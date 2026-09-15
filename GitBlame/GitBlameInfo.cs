using System;

namespace GitBlameForVs.GitBlame
{
    /// <summary>
    /// 单行 blame 信息的数据模型，不包含任何展示/格式化逻辑。
    /// 文案格式化统一由 Localization 层的 IBlameTextProvider 负责。
    /// </summary>
    public class GitBlameInfo
    {
        /// <summary>
        /// 表示未提交的更改的提交哈希值。
        /// </summary>
        public const string UncommittedHash = "0000000000000000000000000000000000000000";

        /// <summary>
        /// 获取或设置提交的哈希值。如果是未提交的更改，则为 <see cref="UncommittedHash"/>。
        /// </summary>
        public string? CommitHash { get; set; }

        /// <summary>
        /// 获取或设置提交的作者。如果是未提交的更改，则为 null。
        /// </summary>
        public string? Author { get; set; }

        /// <summary>
        /// 获取或设置提交的作者时间。如果是未提交的更改，则为默认值。
        /// </summary>
        public DateTimeOffset AuthorTime { get; set; }

        /// <summary>
        /// 获取或设置提交的摘要。如果是未提交的更改，则为 null。
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// 获取一个值，指示此提交是否为未提交的更改。
        /// </summary>
        public bool IsUncommitted =>
            CommitHash == UncommittedHash;
    }
}
