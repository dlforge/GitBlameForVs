using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace GitBlameForVs.GitBlame;
public class GitBlameService
{
    /// <summary>
    /// 获取指定文件指定行的 blame 信息。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="lineNumber1Based">行号（从 1 开始）</param>
    /// <returns>返回对应行的 GitBlameInfo，如果文件不存在或发生错误则返回 null</returns>
    public static async Task<GitBlameInfo?> GetBlameAsync(string filePath, int lineNumber1Based)
    {
        if (!File.Exists(filePath)) return null;

        var workingDir = Path.GetDirectoryName(filePath);
        var args = $"blame -L {lineNumber1Based},{lineNumber1Based} --porcelain -- \"{Path.GetFileName(filePath)}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit(3000));
            return ParsePorcelain(output);
        }
        catch
        {
            // git 未安装 / 不是 git 仓库 / 文件未跟踪等，静默失败
            return null;
        }
    }

    /// <summary>
    /// 解析 git blame --porcelain 输出，提取出 commit hash、author、author-time、summary 等信息。
    /// </summary>
    /// <param name="output">git blame --porcelain 输出内容</param>
    /// <returns>返回解析后的 GitBlameInfo，如果解析失败则返回 null</returns>
    private static GitBlameInfo? ParsePorcelain(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var blame in ParsePorcelainWholeFile(output).Values)
            return blame;

        return null;
    }

    /// <summary>
    /// 获取指定 commit 的完整提交信息（commit message）。
    /// </summary>
    /// <param name="workingDir"></param>
    /// <param name="commitHash"></param>
    /// <returns></returns>
    public static async Task<string?> GetFullCommitMessageAsync(string workingDir, string commitHash)
    {
        if (string.IsNullOrEmpty(commitHash) || commitHash.StartsWith("0000000"))
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"log -1 --format=%B {commitHash}",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit(3000));
            return output?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 一次性获取整个文件所有行的 blame 信息。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns></returns>
    public static async Task<Dictionary<int, GitBlameInfo>?> GetBlameForWholeFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var workingDir = Path.GetDirectoryName(filePath);
        var args = $"blame --porcelain -- \"{Path.GetFileName(filePath)}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            // 全文件 blame 比单行慢很多，大文件给更宽松的超时时间
            await Task.Run(() => process.WaitForExit(15000));
            return ParsePorcelainWholeFile(output);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<int, GitBlameInfo> ParsePorcelainWholeFile(string output)
    {
        var result = new Dictionary<int, GitBlameInfo>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        // Git omits repeated commit metadata in porcelain output.
        var commitCache = new Dictionary<string, GitBlameInfo>();
        var lines = output.Split('\n');
        GitBlameInfo? currentBlame = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (TryParseHeader(line, out var commitHash, out var originalLineNumber, out var finalLineNumber, out var groupLineCount))
            {
                CompleteBlame(currentBlame, result, commitCache);

                currentBlame = new GitBlameInfo
                {
                    CommitHash = commitHash,
                    OriginalLineNumber = originalLineNumber,
                    FinalLineNumber = finalLineNumber,
                    GroupLineCount = groupLineCount
                };

                if (commitCache.TryGetValue(commitHash, out var cachedMetadata))
                    CopyCommitMetadata(cachedMetadata, currentBlame);

                continue;
            }

            if (currentBlame == null)
                continue;

            if (line.StartsWith("\t", StringComparison.Ordinal))
            {
                currentBlame.LineContent = line.Substring(1);
                CompleteBlame(currentBlame, result, commitCache);
                currentBlame = null;
                continue;
            }

            ParseMetadata(currentBlame, line);
        }

        CompleteBlame(currentBlame, result, commitCache);
        return result;
    }

    private static bool TryParseHeader(
        string line,
        out string commitHash,
        out int originalLineNumber,
        out int finalLineNumber,
        out int? groupLineCount)
    {
        commitHash = string.Empty;
        originalLineNumber = 0;
        finalLineNumber = 0;
        groupLineCount = null;

        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length != 3 && parts.Length != 4) ||
            !IsObjectId(parts[0]) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out originalLineNumber) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out finalLineNumber))
        {
            return false;
        }

        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedGroupLineCount))
                return false;

            groupLineCount = parsedGroupLineCount;
        }

        commitHash = parts[0];
        return true;
    }

    private static bool IsObjectId(string value)
    {
        if (value.Length != 40 && value.Length != 64)
            return false;

        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static void ParseMetadata(GitBlameInfo blame, string line)
    {
        if (line.StartsWith("author ", StringComparison.Ordinal))
            blame.Author = line.Substring("author ".Length);
        else if (line.StartsWith("author-mail ", StringComparison.Ordinal))
            blame.AuthorMail = line.Substring("author-mail ".Length);
        else if (line.StartsWith("author-time ", StringComparison.Ordinal))
            blame.AuthorTime = ParseUnixTime(line.Substring("author-time ".Length)) ?? blame.AuthorTime;
        else if (line.StartsWith("author-tz ", StringComparison.Ordinal))
            blame.AuthorTimeZone = line.Substring("author-tz ".Length);
        else if (line.StartsWith("committer ", StringComparison.Ordinal))
            blame.Committer = line.Substring("committer ".Length);
        else if (line.StartsWith("committer-mail ", StringComparison.Ordinal))
            blame.CommitterMail = line.Substring("committer-mail ".Length);
        else if (line.StartsWith("committer-time ", StringComparison.Ordinal))
            blame.CommitterTime = ParseUnixTime(line.Substring("committer-time ".Length));
        else if (line.StartsWith("committer-tz ", StringComparison.Ordinal))
            blame.CommitterTimeZone = line.Substring("committer-tz ".Length);
        else if (line.StartsWith("summary ", StringComparison.Ordinal))
            blame.Summary = line.Substring("summary ".Length);
        else if (line.StartsWith("previous ", StringComparison.Ordinal))
            ParsePrevious(blame, line.Substring("previous ".Length));
        else if (line.StartsWith("filename ", StringComparison.Ordinal))
            blame.FileName = line.Substring("filename ".Length);
        else if (line == "boundary")
            blame.IsBoundary = true;
    }

    private static DateTimeOffset? ParseUnixTime(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime))
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ParsePrevious(GitBlameInfo blame, string value)
    {
        var separatorIndex = value.IndexOf(' ');
        if (separatorIndex <= 0)
            return;

        var previousCommitHash = value.Substring(0, separatorIndex);
        if (!IsObjectId(previousCommitHash))
            return;

        blame.PreviousCommitHash = previousCommitHash;
        blame.PreviousFileName = value.Substring(separatorIndex + 1);
    }

    private static void CompleteBlame(
        GitBlameInfo? blame,
        Dictionary<int, GitBlameInfo> result,
        Dictionary<string, GitBlameInfo> commitCache)
    {
        if (blame == null || blame.CommitHash == null)
            return;

        if (commitCache.TryGetValue(blame.CommitHash, out var cachedMetadata))
            CopyCommitMetadata(cachedMetadata, blame);
        else
            commitCache[blame.CommitHash] = blame;

        result[blame.FinalLineNumber] = blame;
    }

    private static void CopyCommitMetadata(GitBlameInfo source, GitBlameInfo destination)
    {
        destination.Author = source.Author;
        destination.AuthorMail = source.AuthorMail;
        destination.AuthorTime = source.AuthorTime;
        destination.AuthorTimeZone = source.AuthorTimeZone;
        destination.Committer = source.Committer;
        destination.CommitterMail = source.CommitterMail;
        destination.CommitterTime = source.CommitterTime;
        destination.CommitterTimeZone = source.CommitterTimeZone;
        destination.Summary = source.Summary;
        destination.PreviousCommitHash = source.PreviousCommitHash;
        destination.PreviousFileName = source.PreviousFileName;
        destination.FileName = source.FileName;
        destination.IsBoundary = source.IsBoundary;
    }
}
