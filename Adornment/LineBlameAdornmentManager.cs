using GitBlameForVs.GitBlame;
using GitBlameForVs.Localization;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using WpfPath = System.Windows.Shapes.Path;

namespace GitBlameForVs.Adornment
{
    internal sealed class LineBlameAdornmentManager
    {
        public const string LayerName = "GitBlameForVsBlameLayer";
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
        private readonly GitBlameCache _cache = new();
        private readonly CommitMessageCache _messageCache = new();
        private readonly ITextDocument? _document;

        // 基准快照：代表"最后一次已知与磁盘内容一致"的版本。
        // 文件打开时、每次保存后都会更新为当时的快照。
        // 判断某一行是否为脏行，靠把当前行位置映射回这个快照做内容比较，
        // 而不是记录"有没有发生过编辑"，这样撤销、手动改回原样等
        // 任何让内容恢复一致的方式都能被正确识别。
        private ITextSnapshot? _baselineSnapshot;
        private CancellationTokenSource? _debounceCts;
        private DateTimeOffset? _lastEditTime;
        private long _updateGeneration = 0;
        private string? _lastRequestedFilePath;
        private int _lastRequestedLineNumber = -1;
        private string? _lastRequestedLineText;
        private bool _lastRequestedLineIsChanged;

        public LineBlameAdornmentManager(IWpfTextView textView)
        {
            _textView = textView;
            _layer = textView.GetAdornmentLayer(LayerName);
            if (_layer == null)
            {
                return;
            }

            _textView.TextBuffer.Properties.TryGetProperty(
                typeof(ITextDocument), out _document);

            if (_document == null)
            {
                return;
            }

            _baselineSnapshot = _textView.TextBuffer.CurrentSnapshot;
            _textView.Caret.PositionChanged += OnCaretPositionChanged;
            _textView.LayoutChanged += OnLayoutChanged;
            _textView.GotAggregateFocus += OnAggregateFocusChanged;
            _textView.LostAggregateFocus += OnAggregateFocusChanged;
            _textView.TextBuffer.Changed += OnBufferChanged;
            _textView.Closed += OnClosed;
            _document.FileActionOccurred += OnFileActionOccurred;

            RequestUpdate();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _lastEditTime = DateTimeOffset.Now;
            RequestUpdate();
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk ||
                e.FileActionType == FileActionTypes.DocumentRenamed)
            {
                _cache.InvalidateFile(e.FilePath);
                _baselineSnapshot = _textView.TextBuffer.CurrentSnapshot;
                RequestUpdate(force: true);
            }
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            RequestUpdate();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            RequestUpdate();
        }

        private void OnAggregateFocusChanged(object sender, EventArgs e)
        {
            RequestUpdate();
        }

        private void RequestUpdate(bool force = false)
        {
            if (_textView.IsClosed || _layer == null)
                return;

            if (!_textView.HasAggregateFocus)
            {
                CancelPendingUpdate();
                _layer.RemoveAllAdornments();
                ResetLastRequestedLine();
                return;
            }

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();
            string filePath = GetFilePath();
            string lineText = snapshotLine.GetText();
            bool lineIsChanged = IsLineContentChanged(snapshotLine);
            if (!force &&
                string.Equals(_lastRequestedFilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                _lastRequestedLineNumber == snapshotLine.LineNumber &&
                _lastRequestedLineText == lineText &&
                _lastRequestedLineIsChanged == lineIsChanged)
            {
                return;
            }

            _lastRequestedFilePath = filePath;
            _lastRequestedLineNumber = snapshotLine.LineNumber;
            _lastRequestedLineText = lineText;
            _lastRequestedLineIsChanged = lineIsChanged;

            long generation = CancelPendingUpdate();
            _layer.RemoveAllAdornments();
            if (string.IsNullOrWhiteSpace(filePath) ||
                string.IsNullOrWhiteSpace(lineText) ||
                lineIsChanged)
            {
                return;
            }

            var cancellationSource = new CancellationTokenSource();
            _debounceCts = cancellationSource;
            _ = DebouncedUpdateAsync(cancellationSource, generation);
        }

        private long CancelPendingUpdate()
        {
            long generation = Interlocked.Increment(ref _updateGeneration);
            var cancellationSource = _debounceCts;
            _debounceCts = null;
            cancellationSource?.Cancel();
            return generation;
        }

        private void ResetLastRequestedLine()
        {
            _lastRequestedFilePath = null;
            _lastRequestedLineNumber = -1;
            _lastRequestedLineText = null;
            _lastRequestedLineIsChanged = false;
        }

        private async Task DebouncedUpdateAsync(
            CancellationTokenSource cancellationSource,
            long generation)
        {
            var cancellationToken = cancellationSource.Token;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                await UpdateAdornmentAsync(generation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (TimeoutException error)
            {
                Trace.TraceWarning($"Git blame timed out: {error}");
                RenderFailureAdornment(generation, error);
            }
            catch (Exception error)
            {
                Trace.TraceError($"Git blame failed: {error}");
                RenderFailureAdornment(generation, error);
            }
            finally
            {
                if (ReferenceEquals(_debounceCts, cancellationSource))
                    _debounceCts = null;

                cancellationSource.Dispose();
            }
        }
        private bool IsLatest(long generation) => generation == Interlocked.Read(ref _updateGeneration);

        private async Task UpdateAdornmentAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLatest(generation) || _textView.IsClosed)
                return;

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();

            var viewLine = _textView.GetTextViewLineContainingBufferPosition(caretPoint);
            if (viewLine == null) return;

            if (IsLineContentChanged(snapshotLine) ||
                string.IsNullOrWhiteSpace(snapshotLine.GetText()))
            {
                return;
            }

            string filePath = GetFilePath();
            if (string.IsNullOrEmpty(filePath)) return;

            int lineNumber1Based = snapshotLine.LineNumber + 1;
            var blame = await _cache.GetOrFetchAsync(filePath, lineNumber1Based, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (blame == null) return;

            var currentCaretPoint = _textView.Caret.Position.BufferPosition;
            var currentSnapshotLine = currentCaretPoint.GetContainingLine();
            if (currentSnapshotLine.LineNumber != snapshotLine.LineNumber)
                return;

            if (IsLineContentChanged(currentSnapshotLine) ||
                string.IsNullOrWhiteSpace(currentSnapshotLine.GetText()))
                return;

            if (!IsLatest(generation)) return;

            var currentViewLine = _textView.GetTextViewLineContainingBufferPosition(currentCaretPoint);
            if (currentViewLine != null)
                RenderAdornment(currentViewLine, blame);
        }

        private void RenderFailureAdornment(long generation, Exception error)
        {
            if (!IsLatest(generation) || _textView.IsClosed || _layer == null)
                return;

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();
            if (IsLineContentChanged(snapshotLine))
                return;

            var viewLine = _textView.GetTextViewLineContainingBufferPosition(caretPoint);
            if (viewLine == null)
                return;

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

            AddAdornment(viewLine, textBlock);
        }

        /// <summary>
        /// 判断当前行的内容，是否和基准快照（最后一次已知与磁盘一致的版本）
        /// 里"同一位置"的行内容不同。用位置映射而不是裸行号比较，
        /// 这样即使前面插入/删除了整行导致行号偏移，依然能对应到正确的原始行。
        /// </summary>
        private bool IsLineContentChanged(ITextSnapshotLine currentLine)
        {
            if (_baselineSnapshot == null) return false;
            if (_baselineSnapshot == currentLine.Snapshot) return false; // 快照都没变过，必然一致

            var currentSnapshot = currentLine.Snapshot;

            // 把当前行的起始位置，映射到基准快照上对应的位置
            var trackingPoint = currentSnapshot.CreateTrackingPoint(
                currentLine.Start.Position, PointTrackingMode.Negative);

            SnapshotPoint baselinePoint;
            try
            {
                baselinePoint = trackingPoint.GetPoint(_baselineSnapshot);
            }
            catch
            {
                return true;
            }

            var baselineLine = baselinePoint.GetContainingLine();

            string currentText = currentLine.GetText();
            string baselineText = baselineLine.GetText();

            return currentText != baselineText;
        }

        private void RenderAdornment(ITextViewLine line, BlameEntry blame)
        {
            double remSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            double gutterGap = remSize * GitBlameGutterGapRem;
            var foreground = CreateMutedForegroundBrush();
            var textBlock = new TextBlock
            {
                Text = BlameText.FormatInline(
                    blame, _lastEditTime, GitBlameMaxAuthorCharsDisplayed),
                Foreground = foreground,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Cursor = Cursors.Arrow,
                Background = Brushes.Transparent,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily
            };
            FrameworkElement leadingMark = CreateBlameIcon(foreground, gutterGap);
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

            CreateTooltipPopup(inlineBlameBorder, blame);

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

        private Popup CreateTooltipPopup(Border anchor, BlameEntry blame)
        {
            var font = _textView.FormattedLineSource.DefaultTextProperties;
            var tooltipContent = new Border
            {
                Background = SystemColors.InfoBrush,
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8),
                MaxWidth = 500,
                Child = CreateTooltipTextBlock(BlameText.TooltipLoading)
            };
            var popup = new Popup
            {
                AllowsTransparency = true,
                Child = tooltipContent,
                Placement = PlacementMode.Bottom,
                PlacementTarget = anchor,
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
                if (tooltipContentRequested)
                    return;

                tooltipContentRequested = true;
                _ = LoadTooltipContentAsync(tooltipContent, blame);
            };

            return popup;

            void SchedulePopupClose()
            {
                closeTimer.Stop();
                closeTimer.Start();
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

        private async Task LoadTooltipContentAsync(Border tooltipContent, BlameEntry blame)
        {
            try
            {
                string? fullMessage = null;
                if (!blame.IsUncommitted)
                {
                    string filePath = GetFilePath();
                    string? workingDir = string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(filePath);

                    fullMessage = workingDir != null
                        ? await _messageCache.GetOrFetchAsync(workingDir, blame.CommitHash)
                        : null;
                }

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
            var mutedForeground = CreateMutedForegroundBrush();
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

            var authorMail = blame.AuthorMail;
            if (!string.IsNullOrWhiteSpace(authorMail))
            {
                authorHeader.Children.Add(new TextBlock
                {
                    Text = $"  {authorMail?.Trim('<', '>')}",
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

        private string GetFilePath()
        {
            return _document?.FilePath ?? string.Empty;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _textView.Caret.PositionChanged -= OnCaretPositionChanged;
            _textView.LayoutChanged -= OnLayoutChanged;
            _textView.GotAggregateFocus -= OnAggregateFocusChanged;
            _textView.LostAggregateFocus -= OnAggregateFocusChanged;
            _textView.TextBuffer.Changed -= OnBufferChanged;
            _textView.Closed -= OnClosed;

            if (_document != null)
                _document.FileActionOccurred -= OnFileActionOccurred;

            CancelPendingUpdate();
            _cache.CancelPendingWork();
        }
    }
}