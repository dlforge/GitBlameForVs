using System;

namespace GitBlameForVs.GitBlame
{
    /// <summary>
    /// Git blame porcelain 输出中单行记录的完整数据模型。
    /// 文案格式化统一由 Localization 层的 IBlameTextProvider 负责。
    /// </summary>
    public class BlameInfo
    {
        /// <summary>
        /// SHA-1 仓库中表示未提交更改的提交哈希值。
        /// </summary>
        public const string UncommittedHash = "0000000000000000000000000000000000000000";

        /// <summary>
        /// SHA-256 仓库中表示未提交更改的提交哈希值。
        /// </summary>
        public const string UncommittedHashSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

        /// <summary>
        /// 获取或设置提交的哈希值。如果是未提交的更改，则为 <see cref="UncommittedHash"/> 或
        /// <see cref="UncommittedHashSha256"/>。
        /// </summary>
        public string? CommitHash { get; set; }

        /// <summary>
        /// blame 来源提交中的行号，从 1 开始。
        /// </summary>
        public int OriginalLineNumber { get; set; }

        /// <summary>
        /// 当前文件中的行号，从 1 开始。
        /// </summary>
        public int FinalLineNumber { get; set; }

        /// <summary>
        /// 当前 porcelain 行组包含的行数；输出未提供时为 null。
        /// </summary>
        public int? GroupLineCount { get; set; }

        /// <summary>
        /// 获取或设置提交的作者。如果是未提交的更改，则为 null。
        /// </summary>
        public string? Author { get; set; }

        /// <summary>
        /// 提交作者的邮箱地址。
        /// </summary>
        public string? AuthorMail { get; set; }

        /// <summary>
        /// 获取或设置提交的作者时间。如果是未提交的更改，则为默认值。
        /// </summary>
        public DateTimeOffset AuthorTime { get; set; }

        /// <summary>
        /// Git porcelain 输出中的作者时区偏移量。
        /// </summary>
        public string? AuthorTimeZone { get; set; }

        /// <summary>
        /// 提交者名称。
        /// </summary>
        public string? Committer { get; set; }

        /// <summary>
        /// 提交者邮箱地址。
        /// </summary>
        public string? CommitterMail { get; set; }

        /// <summary>
        /// 提交者时间。
        /// </summary>
        public DateTimeOffset? CommitterTime { get; set; }

        /// <summary>
        /// Git porcelain 输出中的提交者时区偏移量。
        /// </summary>
        public string? CommitterTimeZone { get; set; }

        /// <summary>
        /// 获取或设置提交的摘要。如果是未提交的更改，则为 null。
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// Git porcelain 输出中的前一个提交哈希。
        /// </summary>
        public string? PreviousCommitHash { get; set; }

        /// <summary>
        /// Git porcelain 输出中的前一个文件名。
        /// </summary>
        public string? PreviousFileName { get; set; }

        /// <summary>
        /// blame 来源文件名。
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// blame 对应的源代码行，不包含 porcelain 的前导制表符。
        /// </summary>
        public string? LineContent { get; set; }

        /// <summary>
        /// 指示此记录是否位于 blame 历史边界。
        /// </summary>
        public bool IsBoundary { get; set; }

        /// <summary>
        /// 获取一个值，指示此提交是否为未提交的更改。
        /// </summary>
        public bool IsUncommitted
        {
            get
            {
                if (CommitHash is null ||
                    (CommitHash.Length != 40 && CommitHash.Length != 64))
                {
                    return false;
                }

                foreach (char character in CommitHash)
                {
                    if (character != '0')
                        return false;
                }

                return true;
            }
        }
    }
}
