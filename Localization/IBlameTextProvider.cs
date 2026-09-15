using System;
using GitBlameForVs.GitBlame;

namespace GitBlameForVs.Localization
{
    /// <summary>
    /// 集中提供所有面向用户的 blame 展示文案：
    /// 行内提示、悬停 Tooltip、点击复制的反馈。
    /// 新增语言时实现此接口，并在 <see cref="BlameText"/> 中注册语言映射。
    /// </summary>
    internal interface IBlameTextProvider
    {
        /// <summary>Tooltip 加载中的占位文案。</summary>
        string TooltipLoading { get; }

        /// <summary>Tooltip 加载失败时的文案。</summary>
        string TooltipLoadFailed { get; }

        /// <summary>复制 commit hash 成功的反馈文案。</summary>
        string CopyHashFeedback { get; }

        /// <summary>复制成功反馈的前缀（例如 "  ✓ "）。</summary>
        string CopySuccessPrefix { get; }

        /// <summary>把时间格式化成“多久以前”（刚刚 / N 分钟前 / …）。</summary>
        string FormatRelativeTime(DateTimeOffset time);

        /// <summary>行内 blame 文案。</summary>
        string FormatInline(GitBlameInfo blame, DateTimeOffset? lastEditTime);

        /// <summary>悬停 Tooltip 的完整信息文案。</summary>
        string FormatTooltip(GitBlameInfo blame, string? fullMessage);
    }
}
