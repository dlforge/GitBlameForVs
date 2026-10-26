using System;
using System.Text;
using GitBlameForVs.GitBlame;

namespace GitBlameForVs.Localization
{
    internal sealed class EnglishBlameTextProvider : IBlameTextProvider
    {
        public string TooltipLoading => "Loading...";
        public string TooltipLoadFailed => "Failed to load commit info";
        public string BlameTimedOut => "Git blame timed out";
        public string BlameFailed => "Git blame failed";

        public string FormatRelativeTime(DateTimeOffset time)
        {
            var span = DateTimeOffset.Now - time;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return Plural((int)span.TotalMinutes, "minute");
            if (span.TotalHours < 24) return Plural((int)span.TotalHours, "hour");
            if (span.TotalDays < 30) return Plural((int)span.TotalDays, "day");
            if (span.TotalDays < 365) return Plural((int)(span.TotalDays / 30), "month");
            return Plural((int)(span.TotalDays / 365), "year");
        }

        public string FormatInline(BlameEntry blame, DateTimeOffset? lastEditTime)
        {
            if (blame.IsUncommitted)
            {
                var when = FormatRelativeTime(lastEditTime ?? DateTimeOffset.Now);
                return $"You, {when} · Uncommitted changes";
            }
            var summary = string.IsNullOrWhiteSpace(blame.Summary) ? string.Empty : $" - {blame.Summary}";
            return $"{blame.Author}, {FormatRelativeTime(blame.AuthorTime)}{summary}";
        }

        public string FormatAbsoluteTime(DateTimeOffset time) =>
            time.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

        public string FormatTooltip(BlameEntry blame, string? fullMessage)
        {
            if (blame.IsUncommitted) return "Uncommitted changes";

            var sb = new StringBuilder();
            sb.AppendLine($"Commit: {blame.CommitHash}");
            sb.AppendLine($"Author: {blame.Author}");
            sb.AppendLine($"Date: {blame.AuthorTime.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();
            sb.Append(!string.IsNullOrWhiteSpace(fullMessage) ? fullMessage : blame.Summary);
            return sb.ToString();
        }

        private static string Plural(int count, string unit) =>
            $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}
