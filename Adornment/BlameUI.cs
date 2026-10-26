using GitBlameForVs.GitBlame;
using GitBlameForVs.Localization;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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

        private enum AccentState
        {
            Disabled = 0,
            EnableAcrylicBlurBehind = 4
        }

        private enum WindowCompositionAttribute
        {
            AccentPolicy = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState State;
            public uint Flags;
            public uint GradientColor;
            public uint AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

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
            Func<BlameEntry, Task<string?>> loadCommitMessage,
            Func<string?, Task<byte[]?>> loadAuthorAvatar)
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

            CreateTooltipPopup(inlineBlameBorder, blame, loadCommitMessage, loadAuthorAvatar);
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowCompositionAttribute(
            IntPtr windowHandle,
            ref WindowCompositionAttributeData data);

        private Brush CreateHoverBackgroundBrush()
        {
            var foreground = _textView.FormattedLineSource.DefaultTextProperties.ForegroundBrush;
            var hoverBackground = (Brush)foreground.CloneCurrentValue();
            hoverBackground.Opacity *= InlineBlameHoverOpacity;
            return hoverBackground;
        }

        private static FrameworkElement CreateAuthorAvatar(
            BlameEntry blame,
            double size,
            double gap,
            out Image avatarImage)
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

            var avatar = new Grid
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, gap, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            avatar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(size / 2),
                Background = background,
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
            });
            avatarImage = new Image
            {
                Width = size,
                Height = size,
                Stretch = Stretch.UniformToFill,
                Clip = new EllipseGeometry(new Rect(0, 0, size, size)),
                Visibility = Visibility.Collapsed
            };
            avatar.Children.Add(avatarImage);
            return avatar;
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
            Func<BlameEntry, Task<string?>> loadCommitMessage,
            Func<string?, Task<byte[]?>> loadAuthorAvatar)
        {
            var tooltipContent = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                MaxWidth = 500,
                Child = CreateTooltipTextBlock(BlameText.TooltipLoading),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromArgb(0x50, 0, 0, 0),
                    BlurRadius = 16,
                    ShadowDepth = 2,
                    Opacity = 0.55
                }
            };
            tooltipContent.Background = Brushes.Transparent;
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
            bool themeChangedSubscribed = false;
            ThemeChangedEventHandler themeChangedHandler = _ => ApplyPopupAcrylic(popup, tooltipContent);

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
                ApplyPopupAcrylic(popup, tooltipContent);
                if (!themeChangedSubscribed)
                {
                    VSColorTheme.ThemeChanged += themeChangedHandler;
                    themeChangedSubscribed = true;
                }

                if (tooltipContentRequested)
                    return;

                tooltipContentRequested = true;
                _ = LoadTooltipContentAsync(
                    tooltipContent, blame, loadCommitMessage, loadAuthorAvatar);
            };
            popup.Closed += (_, _) =>
            {
                if (!themeChangedSubscribed)
                    return;

                VSColorTheme.ThemeChanged -= themeChangedHandler;
                themeChangedSubscribed = false;
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

        private static void ApplyPopupAcrylic(Popup popup, Border tooltipContent)
        {
            try
            {
                if (popup.Child is not Visual popupVisual ||
                    PresentationSource.FromVisual(popupVisual) is not HwndSource source)
                {
                    SetFallbackTooltipBackground(tooltipContent);
                    return;
                }

                var themeColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                var accentColor = VSColorTheme.GetThemedColor(
                    EnvironmentColors.AccentPaleColorKey);
                var accentPolicy = new AccentPolicy
                {
                    State = AccentState.EnableAcrylicBlurBehind,
                    GradientColor = (0xC8U << 24) |
                        ((uint)themeColor.B << 16) |
                        ((uint)themeColor.G << 8) |
                        themeColor.R
                };
                int accentPolicySize = Marshal.SizeOf(typeof(AccentPolicy));
                IntPtr accentPolicyPointer = Marshal.AllocHGlobal(accentPolicySize);
                try
                {
                    Marshal.StructureToPtr(accentPolicy, accentPolicyPointer, false);
                    var compositionData = new WindowCompositionAttributeData
                    {
                        Attribute = WindowCompositionAttribute.AccentPolicy,
                        Data = accentPolicyPointer,
                        SizeOfData = accentPolicySize
                    };
                    if (SetWindowCompositionAttribute(source.Handle, ref compositionData) != 0)
                    {
                        tooltipContent.Background = CreateTooltipGradient(
                            ToMediaColor(themeColor), ToMediaColor(accentColor), acrylic: true);
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(accentPolicyPointer);
                }
            }
            catch (Exception error)
            {
                Trace.TraceInformation($"Acrylic blame popup is unavailable: {error.Message}");
            }

            SetFallbackTooltipBackground(tooltipContent);
        }

        private static void SetFallbackTooltipBackground(Border tooltipContent)
        {
            var themeColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var accentColor = VSColorTheme.GetThemedColor(EnvironmentColors.AccentPaleColorKey);
            tooltipContent.Background = CreateTooltipGradient(
                ToMediaColor(themeColor), ToMediaColor(accentColor), acrylic: false);
        }

        private static Color ToMediaColor(System.Drawing.Color color) =>
            Color.FromArgb(color.A, color.R, color.G, color.B);

        private static LinearGradientBrush CreateTooltipGradient(
            Color themeColor,
            Color accentColor,
            bool acrylic)
        {
            byte topOpacity = acrylic ? (byte)0x52 : (byte)0xFF;
            byte middleOpacity = acrylic ? (byte)0x38 : (byte)0xFF;
            byte bottomOpacity = acrylic ? (byte)0x60 : (byte)0xFF;
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            gradient.GradientStops.Add(new GradientStop(
                BlendThemeColor(themeColor, accentColor, 0.22, topOpacity), 0));
            gradient.GradientStops.Add(new GradientStop(
                BlendThemeColor(themeColor, accentColor, 0.06, middleOpacity), 0.48));
            gradient.GradientStops.Add(new GradientStop(
                BlendThemeColor(themeColor, accentColor, 0.16, bottomOpacity), 1));
            return gradient;
        }

        private static Color BlendThemeColor(Color themeColor, Color accentColor, double accentWeight, byte opacity)
        {
            byte BlendChannel(byte themeChannel, byte accentChannel) =>
                (byte)Math.Round(themeChannel * (1 - accentWeight) + accentChannel * accentWeight);

            return Color.FromArgb(
                opacity,
                BlendChannel(themeColor.R, accentColor.R),
                BlendChannel(themeColor.G, accentColor.G),
                BlendChannel(themeColor.B, accentColor.B));
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
            Func<BlameEntry, Task<string?>> loadCommitMessage,
            Func<string?, Task<byte[]?>> loadAuthorAvatar)
        {
            try
            {
                Task<byte[]?>? avatarBytesTask = !blame.IsUncommitted &&
                    !string.IsNullOrWhiteSpace(blame.AuthorMail)
                        ? loadAuthorAvatar(blame.AuthorMail)
                        : null;
                string? fullMessage = blame.IsUncommitted
                    ? null
                    : await loadCommitMessage(blame);

                if (blame.IsUncommitted)
                {
                    tooltipContent.Child = CreateTooltipTextBlock(
                        BlameText.FormatTooltip(blame, fullMessage));
                    return;
                }

                var commitTooltip = CreateCommitTooltipContent(blame, fullMessage, out var avatarImage);
                tooltipContent.Child = commitTooltip;
                if (avatarBytesTask != null)
                {
                    var avatarBytes = await avatarBytesTask;
                    var avatarSource = DecodeAvatarImage(avatarBytes);
                    if (avatarSource != null)
                    {
                        avatarImage.Source = avatarSource;
                        avatarImage.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception error)
            {
                Trace.TraceError($"Failed to load Git commit message: {error}");
                tooltipContent.Child = CreateTooltipTextBlock(BlameText.TooltipLoadFailed);
            }
        }

        private static BitmapImage? DecodeAvatarImage(byte[]? avatarBytes)
        {
            if (avatarBytes == null)
                return null;

            try
            {
                using var stream = new MemoryStream(avatarBytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception error)
            {
                Trace.TraceWarning($"Failed to decode author avatar: {error.Message}");
                return null;
            }
        }

        private FrameworkElement CreateCommitTooltipContent(
            BlameEntry blame,
            string? fullMessage,
            out Image avatarImage)
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
                blame,
                remSize * GitBlameAvatarSizeRem,
                remSize * GitBlameGutterGapRem,
                out avatarImage));
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
            Border? copyFeedback = null;
            if (!string.IsNullOrWhiteSpace(commitHash))
            {
                copyFeedback = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = BlameText.CopyShaText,
                        FontFamily = font.Typeface.FontFamily,
                        FontSize = font.FontRenderingEmSize,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                copyFeedback.SetResourceReference(
                    Border.BorderBrushProperty, EnvironmentColors.AccentBorderBrushKey);
                ((TextBlock)copyFeedback.Child).SetResourceReference(
                    TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

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

                    copyFeedback.Visibility = Visibility.Visible;
                };
                hashControls.Children.Add(copyButton);
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
            if (copyFeedback != null)
                content.Children.Add(copyFeedback);

            return content;
        }
    }
}
