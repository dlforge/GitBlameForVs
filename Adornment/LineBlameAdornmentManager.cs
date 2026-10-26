using GitBlameForVs.GitBlame;
using GitBlameForVs.Localization;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using WpfPath = System.Windows.Shapes.Path;

namespace GitBlameForVs.Adornment
{
    internal sealed class LineBlameAdornmentManager
    {
        public const string LayerName = "GitBlameForVsBlameLayer";
        private const double InlineBlameHorizontalGap = 8;
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
                RequestUpdate();
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

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            RequestUpdate();
        }

        private void RequestUpdate()
        {
            var cancellationSource = new CancellationTokenSource();
            var previousCancellationSource = _debounceCts;
            _debounceCts = cancellationSource;
            long generation = Interlocked.Increment(ref _updateGeneration);
            previousCancellationSource?.Cancel();
            _ = DebouncedUpdateAsync(cancellationSource, generation);
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
            _layer?.RemoveAllAdornments();

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();

            var viewLine = _textView.GetTextViewLineContainingBufferPosition(caretPoint);
            if (viewLine == null) return;

            if (IsLineContentChanged(snapshotLine))
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

            if (IsLineContentChanged(currentSnapshotLine))
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
                ? BlameText.Current.BlameTimedOut
                : BlameText.Current.BlameFailed;
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
            var foreground = CreateMutedForegroundBrush();
            var textBlock = new TextBlock
            {
                Text = BlameText.Current.FormatInline(blame, _lastEditTime),
                Foreground = foreground,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Cursor = Cursors.Arrow,
                Background = Brushes.Transparent,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily
            };
            var blameIcon = CreateBlameIcon(foreground);
            var inlineBlame = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Arrow
            };
            DockPanel.SetDock(blameIcon, Dock.Left);
            inlineBlame.Children.Add(blameIcon);
            inlineBlame.Children.Add(textBlock);

            var toolTip = new ToolTip
            {
                Content = BlameText.Current.TooltipLoading,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                BorderThickness = new Thickness(1),
            };
            inlineBlame.ToolTip = toolTip;
            toolTip.Opened += (s, e) => _ = OnToolTipOpenedAsync(toolTip, blame);

            AddAdornment(line, inlineBlame);
        }

        private Brush CreateMutedForegroundBrush()
        {
            var foreground = _textView.FormattedLineSource.DefaultTextProperties.ForegroundBrush;
            var mutedForeground = (Brush)foreground.CloneCurrentValue();
            mutedForeground.Opacity *= 0.7;
            return mutedForeground;
        }

        private static Canvas CreateBlameIcon(Brush foreground)
        {
            var icon = new Canvas
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 6, 0),
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

            double startX = Math.Max(line.TextRight, _textView.Caret.Left) + InlineBlameHorizontalGap;
            double availableWidth = _textView.ViewportWidth - startX - InlineBlameHorizontalGap;
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

        private async Task OnToolTipOpenedAsync(ToolTip toolTip, BlameEntry blame)
        {
            try
            {
                await LoadTooltipContentAsync(toolTip, blame);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to load Git commit message: {ex}");
                toolTip.Content = BlameText.Current.TooltipLoadFailed;
            }
        }

        private async Task LoadTooltipContentAsync(ToolTip toolTip, BlameEntry blame)
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

            toolTip.Content = blame.IsUncommitted
                ? BlameText.Current.FormatTooltip(blame, fullMessage)
                : CreateCommitTooltipContent(blame, fullMessage);
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
                    : BlameText.Current.FormatTooltip(blame, fullMessage);
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
            DockPanel.SetDock(shortHash, Dock.Right);
            footer.Children.Add(shortHash);
            footer.Children.Add(new TextBlock
            {
                Text = BlameText.Current.FormatAbsoluteTime(blame.CommitterTime ?? blame.AuthorTime),
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
            _textView.TextBuffer.Changed -= OnBufferChanged;
            _textView.Closed -= OnClosed;

            if (_document != null)
                _document.FileActionOccurred -= OnFileActionOccurred;

            Interlocked.Increment(ref _updateGeneration);
            var cancellationSource = _debounceCts;
            _debounceCts = null;
            cancellationSource?.Cancel();
            _cache.CancelPendingWork();
        }
    }
}