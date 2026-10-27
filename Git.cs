using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using GitBlameForVs.GitBlame;

namespace GitBlameForVs;
public class Git
{
    private static readonly TimeSpan SingleLineBlameTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WholeFileBlameTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommitMessageTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 获取指定文件指定行的 blame 信息。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="lineNumber1Based">行号（从 1 开始）</param>
    /// <returns>返回对应行的 GitBlameInfo；文件不可 blame 时返回 null</returns>
    /// <exception cref="GitCommandException">Git 返回未预期的错误</exception>
    /// <exception cref="TimeoutException">Git 命令超过超时时间</exception>
    public static async Task<BlameEntry?> GetBlameAsync(
        string filePath,
        int lineNumber1Based,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var workingDir = Path.GetDirectoryName(filePath);
        var args = $"blame -L {lineNumber1Based},{lineNumber1Based} --porcelain -- \"{Path.GetFileName(filePath)}\"";
        var result = await RunGitAsync(
            CreateGitStartInfo(args, workingDir),
            "git blame",
            SingleLineBlameTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            if (IsExpectedNoBlameFailure(result.StandardError))
                return null;

            throw new GitCommandException("git blame", result.ExitCode, result.StandardError);
        }

        return ParsePorcelain(result.StandardOutput);
    }

    /// <summary>
    /// 解析 git blame --porcelain 输出，提取出 commit hash、author、author-time、summary 等信息。
    /// </summary>
    /// <param name="output">git blame --porcelain 输出内容</param>
    /// <returns>返回解析后的 GitBlameInfo，如果解析失败则返回 null</returns>
    private static BlameEntry? ParsePorcelain(string output)
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
    public static async Task<string?> GetFullCommitMessageAsync(
        string workingDir,
        string commitHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(commitHash) || IsZeroObjectId(commitHash))
            return null;

        var result = await RunGitAsync(
            CreateGitStartInfo($"log -1 --format=%B {commitHash}", workingDir),
            "git log",
            CommitMessageTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
            throw new GitCommandException("git log", result.ExitCode, result.StandardError);

        return result.StandardOutput.Trim();
    }

    internal static async Task<CommitChangeStats?> GetCommitChangeStatsAsync(
        string workingDir,
        string commitHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(commitHash) || IsZeroObjectId(commitHash))
            return null;

        var result = await RunGitAsync(
            CreateGitStartInfo(
                $"diff-tree --root --first-parent --no-commit-id --numstat -r --no-renames -z {commitHash}",
                workingDir),
            "git diff-tree",
            CommitMessageTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
            throw new GitCommandException("git diff-tree", result.ExitCode, result.StandardError);

        int filesChanged = 0;
        int insertions = 0;
        int deletions = 0;
        foreach (string fileStats in result.StandardOutput.Split('\0'))
        {
            if (string.IsNullOrEmpty(fileStats))
                continue;

            int firstTab = fileStats.IndexOf('\t');
            int secondTab = firstTab < 0 ? -1 : fileStats.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
                continue;

            filesChanged++;
            if (int.TryParse(
                    fileStats.Substring(0, firstTab),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int fileInsertions))
            {
                insertions += fileInsertions;
            }

            if (int.TryParse(
                    fileStats.Substring(firstTab + 1, secondTab - firstTab - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int fileDeletions))
            {
                deletions += fileDeletions;
            }
        }

        return new CommitChangeStats(filesChanged, insertions, deletions);
    }

    /// <summary>
    /// 一次性获取整个文件所有行的 blame 信息。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns></returns>
    public static async Task<Dictionary<int, BlameEntry>?> GetBlameForWholeFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var workingDir = Path.GetDirectoryName(filePath);
        var args = $"blame --porcelain -- \"{Path.GetFileName(filePath)}\"";
        var result = await RunGitAsync(
            CreateGitStartInfo(args, workingDir),
            "git blame",
            WholeFileBlameTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            if (IsExpectedNoBlameFailure(result.StandardError))
                return new Dictionary<int, BlameEntry>();

            throw new GitCommandException("git blame", result.ExitCode, result.StandardError);
        }

        return ParsePorcelainWholeFile(result.StandardOutput);
    }

    private static ProcessStartInfo CreateGitStartInfo(string arguments, string? workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static async Task<GitProcessResult> RunGitAsync(
        ProcessStartInfo startInfo,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {operation}.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var exitTask = WaitForExitAsync(process);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(timeout, timeoutSource.Token);
        var completedTask = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);

        if (completedTask != exitTask && !exitTask.IsCompleted)
        {
            await TerminateProcessAsync(process, exitTask).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{operation} exceeded the {timeout.TotalSeconds:0}-second timeout.");
        }

        timeoutSource.Cancel();
        await exitTask.ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new GitProcessResult(standardOutput, standardError, process.ExitCode);
    }

    private static Task WaitForExitAsync(Process process)
    {
        var completionSource = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler exitedHandler = null!;
        exitedHandler = (_, _) =>
        {
            process.Exited -= exitedHandler;
            completionSource.TrySetResult(true);
        };

        process.Exited += exitedHandler;
        if (process.HasExited)
            exitedHandler(process, EventArgs.Empty);

        return completionSource.Task;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "VSTHRD003",
        Justification = "The process exit task is completed by the Process.Exited event and does not capture the UI context.")]
    private static async Task TerminateProcessAsync(Process process, Task exitTask)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        await exitTask.ConfigureAwait(false);
    }

    private static bool IsExpectedNoBlameFailure(string standardError)
    {
        var message = standardError.ToLowerInvariant();
        return message.Contains("not a git repository") ||
               message.Contains("no such path") ||
               message.Contains("no such ref: head") ||
               message.Contains("no commits yet");
    }

    private static bool IsZeroObjectId(string objectId)
    {
        foreach (char character in objectId)
        {
            if (character != '0')
                return false;
        }

        return true;
    }

    private sealed class GitProcessResult
    {
        public GitProcessResult(string standardOutput, string standardError, int exitCode)
        {
            StandardOutput = standardOutput;
            StandardError = standardError;
            ExitCode = exitCode;
        }

        public string StandardOutput { get; }
        public string StandardError { get; }
        public int ExitCode { get; }
    }

    private static Dictionary<int, BlameEntry> ParsePorcelainWholeFile(string output)
    {
        var result = new Dictionary<int, BlameEntry>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        // Git omits repeated commit metadata in porcelain output.
        var commitCache = new Dictionary<string, BlameEntry>();
        var lines = output.Split('\n');
        BlameEntry? currentBlame = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (TryParseHeader(line, out var commitHash, out var originalLineNumber, out var finalLineNumber, out var groupLineCount))
            {
                CompleteBlame(currentBlame, result, commitCache);

                currentBlame = new BlameEntry
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

    private static void ParseMetadata(BlameEntry blame, string line)
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

    private static void ParsePrevious(BlameEntry blame, string value)
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
        BlameEntry? blame,
        Dictionary<int, BlameEntry> result,
        Dictionary<string, BlameEntry> commitCache)
    {
        if (blame == null || blame.CommitHash == null)
            return;

        if (commitCache.TryGetValue(blame.CommitHash, out var cachedMetadata))
            CopyCommitMetadata(cachedMetadata, blame);
        else
            commitCache[blame.CommitHash] = blame;

        result[blame.FinalLineNumber] = blame;
    }

    private static void CopyCommitMetadata(BlameEntry source, BlameEntry destination)
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

internal sealed class GitCommandException : Exception
{
    public GitCommandException(string operation, int exitCode, string standardError)
        : base($"{operation} exited with code {exitCode}: {standardError.Trim()}")
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }
    public string StandardError { get; }
}
