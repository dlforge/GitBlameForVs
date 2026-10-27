using System;
using System.Globalization;
using System.Text;
using GitBlameForVs.GitBlame;

namespace GitBlameForVs.Localization
{
    internal static class BlameText
    {
        public static string TooltipLoading => "Loading...";
        public static string TooltipLoadFailed => "Failed to load commit info";
        public static string CopyCommitHashMenuItem => "Copy commit SHA";
        public static string CopyShaText => "Copy SHA";
        public static string CopyShaCopied => "Copied SHA";
        public static string CopyShaFailed => "Copy failed";
        public static string ViewCommitDetailsText => "View commit details";
        public static string BlameTimedOut => "Git blame timed out";
        public static string BlameFailed => "Git blame failed";

        public static string FormatRelativeTime(DateTimeOffset time)
        {
            var elapsedTime = DateTimeOffset.Now - time;
            if (elapsedTime.TotalSeconds < 60) return "just now";
            if (elapsedTime.TotalMinutes < 60) return Plural((int)elapsedTime.TotalMinutes, "minute");
            if (elapsedTime.TotalHours < 24) return Plural((int)elapsedTime.TotalHours, "hour");
            if (elapsedTime.TotalDays < 30) return Plural((int)elapsedTime.TotalDays, "day");
            if (elapsedTime.TotalDays < 365) return Plural((int)(elapsedTime.TotalDays / 30), "month");
            return Plural((int)(elapsedTime.TotalDays / 365), "year");
        }

        public static string FormatInline(
            BlameEntry blame,
            DateTimeOffset? lastEditTime,
            int maximumAuthorCharacters)
        {
            if (blame.IsUncommitted)
            {
                var relativeTime = FormatRelativeTime(lastEditTime ?? DateTimeOffset.Now);
                return $"You, {relativeTime} · Uncommitted changes";
            }

            var summary = string.IsNullOrWhiteSpace(blame.Summary) ? string.Empty : $" - {blame.Summary}";
            var author = TruncateAuthor(blame.Author, maximumAuthorCharacters);
            return $"{author}, {FormatRelativeTime(blame.AuthorTime)}{summary}";
        }

        public static string FormatAbsoluteTime(DateTimeOffset time) =>
            time.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

        public static string FormatTooltip(BlameEntry blame, string? fullMessage)
        {
            if (blame.IsUncommitted)
                return "Uncommitted changes";

            var messageBuilder = new StringBuilder();
            messageBuilder.AppendLine($"Commit: {blame.CommitHash}");
            messageBuilder.AppendLine($"Author: {blame.Author}");
            messageBuilder.AppendLine($"Date: {blame.AuthorTime.ToString("yyyy-MM-dd HH:mm:ss")}");
            messageBuilder.AppendLine();
            messageBuilder.Append(!string.IsNullOrWhiteSpace(fullMessage) ? fullMessage : blame.Summary);
            return messageBuilder.ToString();
        }

        public static string TruncateAuthor(string? author, int maximumCharacters)
        {
            if (string.IsNullOrEmpty(author) || maximumCharacters <= 0)
                return string.Empty;

            var textElements = StringInfo.GetTextElementEnumerator(author);
            var truncatedAuthor = new StringBuilder();
            for (int count = 0; count < maximumCharacters && textElements.MoveNext(); count++)
            {
                truncatedAuthor.Append(textElements.GetTextElement());
            }

            if (textElements.MoveNext())
                truncatedAuthor.Append('…');

            return truncatedAuthor.ToString();
        }

        private static string Plural(int count, string unit) =>
            $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }
}
