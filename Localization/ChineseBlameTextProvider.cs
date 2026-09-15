using System;
using System.Text;
using GitBlameForVs.GitBlame;

namespace GitBlameForVs.Localization
{
    internal sealed class ChineseBlameTextProvider : IBlameTextProvider
    {
        public string TooltipLoading => "加载中...";
        public string TooltipLoadFailed => "加载提交信息失败";
        public string CopyHashFeedback => "已复制 hash";
        public string CopySuccessPrefix => "  ✓ ";

        public string FormatRelativeTime(DateTimeOffset time)
        {
            var span = DateTimeOffset.Now - time;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} 个月前";
            return $"{(int)(span.TotalDays / 365)} 年前";
        }

        public string FormatInline(GitBlameInfo blame, DateTimeOffset? lastEditTime)
        {
            if (blame.IsUncommitted)
            {
                var when = FormatRelativeTime(lastEditTime ?? DateTimeOffset.Now);
                return $"你, {when} · 未提交的更改";
            }
            return $"{blame.Author}, {FormatRelativeTime(blame.AuthorTime)} • {blame.Summary}";
        }

        public string FormatTooltip(GitBlameInfo blame, string? fullMessage)
        {
            if (blame.IsUncommitted) return "未提交的更改";

            var sb = new StringBuilder();
            sb.AppendLine($"提交: {blame.CommitHash}");
            sb.AppendLine($"作者: {blame.Author}");
            sb.AppendLine($"时间: {blame.AuthorTime.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();
            sb.Append(!string.IsNullOrWhiteSpace(fullMessage) ? fullMessage : blame.Summary);
            return sb.ToString();
        }
    }
}
