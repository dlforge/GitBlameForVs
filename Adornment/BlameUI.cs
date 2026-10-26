using GitBlameForVs.GitBlame;
using GitBlameForVs.Localization;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using WpfPath = System.Windows.Shapes.Path;

namespace GitBlameForVs.Adornment
{
    internal sealed class BlameUI
    {
        private const int GitBlameMaxAuthorCharsDisplayed = 20;
        private const double GitBlameGutterMarginRem = 6;
        private const double GitBlameGutterGapRem = 0.5;
        private const double GitBlameAvatarSizeRem = 1.0;
        private const double InlineBlameHoverOpacity = 0.08;
        private static readonly Color[] AuthorAvatarColors =
        {
            Color.FromRgb(0x6B, 0x8E, 0xC1),
            Color.FromRgb(0x8A, 0x6B, 0xB1),
            Color.FromRgb(0x4F, 0x91, 0x8A),
            Color.FromRgb(0xB0, 0x7A, 0x5A),
            Color.FromRgb(0xA4, 0x67, 0x7E),
            Color.FromRgb(0x6C, 0x87, 0x58),
            Color.FromRgb(0x5D, 0x83, 0xA1),
            Color.FromRgb(0x9A, 0x7C, 0x4F)
        };

        private readonly IWpfTextView _textView;
        private readonly IAdornmentLayer? _layer;

        public BlameUI(IWpfTextView textView, IAdornmentLayer? layer)
        {
            _textView = textView;
            _layer = layer;
        }

        public bool HasAdornmentLayer => _layer != null;

        public void Clear()
        {
            _layer?.RemoveAllAdornments();
        }

        public void RenderFailure(ITextViewLine line, Exception error)
        {
            var message = error is TimeoutException
                ? BlameText.BlameTimedOut
                : BlameText.BlameFailed;
            var textBlock = new TextBlock
            {
                Text = message,
                Foreground = Brushes.DarkOrange,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                Cursor = Cursors.Arrow,
                Background = Brushes.Transparent,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                ToolTip = error.Message
            };

            AddAdornment(line, textBlock);
        }

        public void Render(
            ITextViewLine line,
            BlameEntry blame,
            DateTimeOffset? lastEditTime,
            Func<BlameEntry, Task<string?>> loadCommitMessage)
        {
            double remSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            double gutterGap = remSize * GitBlameGutterGapRem;
            var foreground = CreateMutedForegroundBrush();
            var textBlock = new TextBlock
            {
                Text = BlameText.FormatInline(blame, lastEditTime, GitBlameMaxAuthorCharsDisplayed),
                Foreground = foreground,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Cursor = Cursors.Arrow,
                Background = Brushes.Transparent,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily
            };
            var leadingMark = CreateBlameIcon(foreground, gutterGap);
            var inlineBlame = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Arrow
            };
            DockPanel.SetDock(leadingMark, Dock.Left);
            inlineBlame.Children.Add(leadingMark);
            inlineBlame.Children.Add(textBlock);

            var inlineBlameBorder = new Border
            {
                Background = Brushes.Transparent,
                Child = inlineBlame,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 1, 3, 1)
            };
            var hoverBackground = CreateHoverBackgroundBrush();
            inlineBlameBorder.MouseEnter += (_, _) => inlineBlameBorder.Background = hoverBackground;
            inlineBlameBorder.MouseLeave += (_, _) => inlineBlameBorder.Background = Brushes.Transparent;

            CreateTooltipPopup(inlineBlameBorder, blame, loadCommitMessage);
            if (!blame.IsUncommitted)
                inlineBlameBorder.ContextMenu = CreateCommitContextMenu(blame);

            AddAdornment(line, inlineBlameBorder);
        }

        private Brush CreateMutedForegroundBrush()
        {
            var foreground = _textView.FormattedLineSource.DefaultTextProperties.ForegroundBrush;
            var mutedForeground = (Brush)foreground.CloneCurrentValue();
            mutedForeground.Opacity *= 0.7;
            return mutedForeground;
        }

        private static Brush CreateTooltipMutedForegroundBrush()
        {
            var foreground = Application.Current?.TryFindResource(
                EnvironmentColors.ToolWindowTextBrushKey) as Brush;
            var mutedForeground = foreground != null
                ? (Brush)foreground.CloneCurrentValue()
                : (Brush)SystemColors.ControlTextBrush.CloneCurrentValue();
            mutedForeground.Opacity *= 0.7;
            return mutedForeground;
        }

        private Brush CreateHoverBackgroundBrush()
        {
            var foreground = _textView.FormattedLineSource.DefaultTextProperties.ForegroundBrush;
            var hoverBackground = (Brush)foreground.CloneCurrentValue();
            hoverBackground.Opacity *= InlineBlameHoverOpacity;
            return hoverBackground;
        }

        private static Border CreateAuthorAvatar(BlameEntry blame, double size, double gap)
        {
            string identity = blame.AuthorMail?.Trim().Trim('<', '>') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(identity))
                identity = blame.Author?.Trim() ?? string.Empty;

            uint hash = 2166136261;
            foreach (char character in identity.ToUpperInvariant())
            {
                hash = unchecked((hash ^ character) * 16777619);
            }

            var background = new SolidColorBrush(
                AuthorAvatarColors[(int)(hash % (uint)AuthorAvatarColors.Length)]);
            background.Freeze();

            return new Border
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, gap, 0),
                CornerRadius = new CornerRadius(size / 2),
                Background = background,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = GetAuthorInitials(blame.Author),
                    Foreground = Brushes.White,
                    FontSize = size * 0.48,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        private static string GetAuthorInitials(string? author)
        {
            string nonNullAuthor = author ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nonNullAuthor))
                return "?";

            var words = nonNullAuthor.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return "?";

            if (words.Length > 1)
            {
                return string.Concat(
                    GetFirstTextElement(words[0]),
                    GetFirstTextElement(words[words.Length - 1])).ToUpperInvariant();
            }

            var textElements = StringInfo.GetTextElementEnumerator(words[0]);
            var initials = new StringBuilder();
            for (int count = 0; count < 2 && textElements.MoveNext(); count++)
            {
                initials.Append(textElements.GetTextElement());
            }

            return initials.ToString().ToUpperInvariant();
        }

        private static string GetFirstTextElement(string value)
        {
            var textElements = StringInfo.GetTextElementEnumerator(value);
            return textElements.MoveNext() ? textElements.GetTextElement() : string.Empty;
        }

        private static ContextMenu? CreateCommitContextMenu(BlameEntry blame)
        {
            string commitHash = blame.CommitHash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(commitHash))
                return null;

            var menu = new ContextMenu();
            var copyCommitHash = new MenuItem
            {
                Header = BlameText.CopyCommitHashMenuItem
            };
            copyCommitHash.Click += (_, _) => TryCopyCommitHashToClipboard(commitHash);
            menu.Items.Add(copyCommitHash);
            return menu;
        }

        private static bool TryCopyCommitHashToClipboard(string commitHash)
        {
            try
            {
                Clipboard.SetText(commitHash);
                return true;
            }
            catch (Exception error)
            {
                Trace.TraceError($"Failed to copy Git commit hash: {error}");
                return false;
            }
        }

        private static Canvas CreateCopyIcon(Brush foreground)
        {
            var icon = new Canvas
            {
                Width = 12,
                Height = 12,
                IsHitTestVisible = false
            };
            icon.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M 4,3 L 10,3 L 10,10 L 4,10 Z M 2,8 L 2,2 L 8,2"),
                Stroke = foreground,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            return icon;
        }

        private static Canvas CreateBlameIcon(Brush foreground, double gap)
        {
            var icon = new Canvas
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, gap, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            icon.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M 3,2 L 3,10 M 3,4 C 3,4 9,4 9,8"),
                Stroke = foreground,
                StrokeThickness = 1.25,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            AddBlameIconNode(icon, 1.5, 0.5, foreground);
            AddBlameIconNode(icon, 1.5, 8.5, foreground);
            AddBlameIconNode(icon, 7.5, 6.5, foreground);
            return icon;
        }

        private static void AddBlameIconNode(Canvas icon, double left, double top, Brush foreground)
        {
            var node = new Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = foreground,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(node, left);
            Canvas.SetTop(node, top);
            icon.Children.Add(node);
        }

        private void AddAdornment(ITextViewLine line, FrameworkElement adornment)
        {
            var layer = _layer;
            if (layer == null)
                return;

            double gutterMargin = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize
                * GitBlameGutterMarginRem;
            double startX = Math.Max(line.TextRight, _textView.Caret.Left) + gutterMargin;
            double availableWidth = _textView.ViewportWidth - startX - gutterMargin;
            if (availableWidth <= 0)
                return;

            adornment.MaxWidth = Math.Min(adornment.MaxWidth, availableWidth);
            adornment.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(adornment, startX);
            Canvas.SetTop(adornment, line.TextTop);
            layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(line.Start, line.End),
                tag: null,
                adornment: adornment,
                removedCallback: null);
        }

        private Popup CreateTooltipPopup(
            Border anchor,
            BlameEntry blame,
            Func<BlameEntry, Task<string?>> loadCommitMessage)
        {
            var tooltipContent = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8),
                MaxWidth = 500,
                Child = CreateTooltipTextBlock(BlameText.TooltipLoading)
            };
            tooltipContent.SetResourceReference(
                Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            tooltipContent.SetResourceReference(
                Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            tooltipContent.SetResourceReference(
                TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var popup = new Popup
            {
                AllowsTransparency = true,
                Child = tooltipContent,
                Placement = PlacementMode.RelativePoint,
                PlacementTarget = anchor,
                HorizontalOffset = 12,
                VerticalOffset = 16,
                StaysOpen = true
            };
            var closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            bool tooltipContentRequested = false;

            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                if (!anchor.IsMouseOver && !tooltipContent.IsMouseOver)
                    popup.IsOpen = false;
            };
            anchor.MouseEnter += (_, _) =>
            {
                closeTimer.Stop();
                UpdatePopupPosition();
                popup.IsOpen = true;
            };
            anchor.MouseMove += (_, _) =>
            {
                if (anchor.IsMouseOver)
                    UpdatePopupPosition();
            };
            anchor.MouseLeave += (_, _) => SchedulePopupClose();
            tooltipContent.MouseEnter += (_, _) => closeTimer.Stop();
            tooltipContent.MouseLeave += (_, _) => SchedulePopupClose();
            anchor.Unloaded += (_, _) =>
            {
                closeTimer.Stop();
                popup.IsOpen = false;
            };
            popup.Opened += (_, _) =>
            {
                if (tooltipContentRequested)
                    return;

                tooltipContentRequested = true;
                _ = LoadTooltipContentAsync(tooltipContent, blame, loadCommitMessage);
            };

            return popup;

            void SchedulePopupClose()
            {
                closeTimer.Stop();
                closeTimer.Start();
            }

            void UpdatePopupPosition()
            {
                var pointerPosition = Mouse.GetPosition(anchor);
                popup.PlacementRectangle = new Rect(pointerPosition, new Size(0, 0));
            }
        }

        private TextBlock CreateTooltipTextBlock(string text)
        {
            var font = _textView.FormattedLineSource.DefaultTextProperties;
            return new TextBlock
            {
                Text = text,
                FontFamily = font.Typeface.FontFamily,
                FontSize = font.FontRenderingEmSize,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private async Task LoadTooltipContentAsync(
            Border tooltipContent,
            BlameEntry blame,
            Func<BlameEntry, Task<string?>> loadCommitMessage)
        {
            try
            {
                string? fullMessage = blame.IsUncommitted
                    ? null
                    : await loadCommitMessage(blame);

                tooltipContent.Child = blame.IsUncommitted
                    ? CreateTooltipTextBlock(BlameText.FormatTooltip(blame, fullMessage))
                    : CreateCommitTooltipContent(blame, fullMessage);
            }
            catch (Exception error)
            {
                Trace.TraceError($"Failed to load Git commit message: {error}");
                tooltipContent.Child = CreateTooltipTextBlock(BlameText.TooltipLoadFailed);
            }
        }

        private FrameworkElement CreateCommitTooltipContent(BlameEntry blame, string? fullMessage)
        {
            var font = _textView.FormattedLineSource.DefaultTextProperties;
            var mutedForeground = CreateTooltipMutedForegroundBrush();
            var content = new StackPanel
            {
                MaxWidth = 480,
                Margin = new Thickness(2)
            };

            var authorHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };
            double remSize = font.FontRenderingEmSize;
            authorHeader.Children.Add(CreateAuthorAvatar(
                blame, remSize * GitBlameAvatarSizeRem, remSize * GitBlameGutterGapRem));
            authorHeader.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(blame.Author) ? "-" : blame.Author,
                FontFamily = font.Typeface.FontFamily,
                FontSize = font.FontRenderingEmSize,
                FontWeight = FontWeights.SemiBold
            });

            string? authorMail = blame.AuthorMail?.Trim('<', '>');
            if (!string.IsNullOrWhiteSpace(authorMail))
            {
                authorHeader.Children.Add(new TextBlock
                {
                    Text = $"  {authorMail}",
                    Foreground = mutedForeground,
                    FontFamily = font.Typeface.FontFamily,
                    FontSize = font.FontRenderingEmSize
                });
            }

            content.Children.Add(authorHeader);
            content.Children.Add(new Border
            {
                Height = 1,
                Background = mutedForeground,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var commitMessage = !string.IsNullOrWhiteSpace(fullMessage)
                ? fullMessage
                : !string.IsNullOrWhiteSpace(blame.Summary)
                    ? blame.Summary
                    : BlameText.FormatTooltip(blame, fullMessage);
            content.Children.Add(new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = commitMessage,
                    FontFamily = font.Typeface.FontFamily,
                    FontSize = font.FontRenderingEmSize,
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 240,
                MaxWidth = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            content.Children.Add(new Border
            {
                Height = 1,
                Background = mutedForeground,
                Margin = new Thickness(0, 6, 0, 4)
            });

            var footer = new DockPanel();
            var commitHash = blame.CommitHash ?? string.Empty;
            var shortCommitHash = commitHash.Length > 8
                ? commitHash.Substring(0, 8)
                : commitHash;
            var shortHash = new TextBlock
            {
                Text = shortCommitHash,
                Foreground = mutedForeground,
                Cursor = Cursors.Arrow,
                FontFamily = font.Typeface.FontFamily,
                FontSize = font.FontRenderingEmSize
            };
            var hashControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            hashControls.Children.Add(shortHash);
            if (!string.IsNullOrWhiteSpace(commitHash))
            {
                var copyFeedback = new TextBlock
                {
                    Foreground = mutedForeground,
                    FontFamily = font.Typeface.FontFamily,
                    FontSize = font.FontRenderingEmSize,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                var copyButton = new Button
                {
                    Content = CreateCopyIcon(mutedForeground),
                    ToolTip = BlameText.CopyShaText,
                    Width = 18,
                    Height = 18,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(2),
                    Margin = new Thickness(2, 0, 0, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                copyButton.Click += (_, _) =>
                {
                    if (!TryCopyCommitHashToClipboard(commitHash))
                        return;

                    copyFeedback.Text = BlameText.CopyShaText;
                    copyFeedback.Visibility = Visibility.Visible;
                };
                hashControls.Children.Add(copyButton);
                hashControls.Children.Add(copyFeedback);
            }

            DockPanel.SetDock(hashControls, Dock.Right);
            footer.Children.Add(hashControls);
            footer.Children.Add(new TextBlock
            {
                Text = BlameText.FormatAbsoluteTime(blame.CommitterTime ?? blame.AuthorTime),
                Foreground = mutedForeground,
                FontFamily = font.Typeface.FontFamily,
                FontSize = font.FontRenderingEmSize
            });
            content.Children.Add(footer);

            return content;
        }
    }
}
