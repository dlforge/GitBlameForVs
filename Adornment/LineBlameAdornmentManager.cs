using GitBlameForVs.GitBlame;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GitBlameForVs.Adornment
{
    internal sealed class LineBlameAdornmentManager
    {
        public const string LayerName = "GitBlameForVsBlameLayer";
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
        private DateTime? _lastEditTime;
        private long _updateGeneration = 0;

        public LineBlameAdornmentManager(IWpfTextView textView)
        {
            _layer = textView.GetAdornmentLayer(LayerName);
            if (_layer == null)
            {
                return;
            }

            _textView = textView;
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

            _ = _cache.EnsurePrefetchStartedAsync(_document.FilePath);
            _ = RefreshUncommittedStatusAsync(_document.FilePath);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _lastEditTime = DateTime.Now;
            RequestUpdate();
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk ||
                e.FileActionType == FileActionTypes.DocumentRenamed)
            {
                _cache.InvalidateFile(e.FilePath);
                _baselineSnapshot = _textView.TextBuffer.CurrentSnapshot;
                _ = RefreshUncommittedStatusAsync(e.FilePath);
                RequestUpdate();
            }
        }
        private async Task RefreshUncommittedStatusAsync(string filePath)
        {
            RequestUpdate();
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
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            long generation = Interlocked.Increment(ref _updateGeneration);
            _ = DebouncedUpdateAsync(token, generation);
        }

        private async Task DebouncedUpdateAsync(CancellationToken token, long generation)
        {
            try
            {
                if (token.IsCancellationRequested) return;
                await UpdateAdornmentAsync(generation);
            }
            catch (TaskCanceledException) { }
        }
        private bool IsLatest(long generation) => generation == Interlocked.Read(ref _updateGeneration);

        private async Task UpdateAdornmentAsync(long generation)
        {
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
            var blame = await _cache.GetOrFetchAsync(filePath, lineNumber1Based);
            if (blame == null) return;

            var currentSnapshotLine = _textView.Caret.Position.BufferPosition.GetContainingLine();
            if (currentSnapshotLine.LineNumber != snapshotLine.LineNumber)
                return;

            if (IsLineContentChanged(currentSnapshotLine))
                return;

            if (!IsLatest(generation)) return;

            _layer!.RemoveAllAdornments();
            RenderAdornment(viewLine, blame);
        }

        private static string FormatRelativeTime(DateTime time)
        {
            var elapsed = DateTime.Now - time;
            if (elapsed.TotalSeconds < 60) return "刚刚";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} 分钟前";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} 小时前";
            return $"{(int)elapsed.TotalDays} 天前";
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

        private void RenderAdornment(ITextViewLine line, GitBlameInfo blame)
        {
            string? text = default;
            Cursor? cursor = default;
            if (blame.IsUncommitted)
            {
                string relativeTime = FormatRelativeTime(_lastEditTime ?? DateTime.Now);
                text = $"You, {relativeTime} · 未提交的更改";
                cursor = Cursors.Arrow;
            }
            else
            {
                text = blame.ToShortText();
                cursor = Cursors.Hand;
            }
            var normalBrush = new SolidColorBrush(Colors.Gray);
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = normalBrush,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                Cursor = cursor,
                Background = Brushes.Transparent,
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily
            };

            var toolTip = new ToolTip
            {
                Content = "加载中...",
                FontFamily = _textView.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                FontSize = _textView.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize,
                BorderThickness = new Thickness(1),
            };
            textBlock.ToolTip = toolTip;
            toolTip.Opened += (s, e) => _ = OnToolTipOpenedAsync(toolTip, blame);

            if (!blame.IsUncommitted)
            {
                textBlock.MouseLeftButtonDown += (s, e) => OnBlameLeftClick(textBlock, blame);
            }

            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double startX = Math.Max(line.TextRight, _textView.Caret.Left);
            Canvas.SetLeft(textBlock, startX + 60);
            Canvas.SetTop(textBlock, line.TextTop);
            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(line.Start, line.End),
                tag: null,
                adornment: textBlock,
                removedCallback: null);
        }

        private async Task OnToolTipOpenedAsync(ToolTip toolTip, GitBlameInfo blame)
        {
            try
            {
                await LoadTooltipContentAsync(toolTip, blame);
            }
            catch (Exception ex)
            {
                toolTip.Content = "加载提交信息失败";
            }
        }

        private async Task LoadTooltipContentAsync(ToolTip toolTip, GitBlameInfo blame)
        {
            if (blame.IsUncommitted)
            {
                return;
            }

            string filePath = GetFilePath();
            string? workingDir = string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(filePath);

            string? fullMessage = workingDir != null
                ? await _messageCache.GetOrFetchAsync(workingDir, blame.CommitHash)
                : null;

            var sb = new StringBuilder();
            sb.AppendLine($"提交: {blame.CommitHash}");
            sb.AppendLine($"作者: {blame.Author}");
            sb.AppendLine($"时间: {blame.AuthorTime.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();
            sb.Append(!string.IsNullOrWhiteSpace(fullMessage) ? fullMessage : blame.Summary);
            toolTip.Content = sb.ToString();
        }

        private void OnBlameLeftClick(TextBlock textBlock, GitBlameInfo blame)
        {
            CopyToClipboardWithFeedback(textBlock, blame.CommitHash!, "已复制 hash");
        }

        private void CopyToClipboardWithFeedback(TextBlock textBlock, string content, string feedbackText)
        {
            try
            {
                Clipboard.SetText(content);
            }
            catch
            {
                return;
            }

            string originalText = textBlock.Text;
            var originalBrush = textBlock.Foreground;

            textBlock.Text = "  ✓ " + feedbackText;
            textBlock.Foreground = Brushes.LimeGreen;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                textBlock.Text = originalText;
                textBlock.Foreground = originalBrush;
            };
            timer.Start();
        }

        private string GetFilePath()
        {
            return _document.FilePath;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _textView.Caret.PositionChanged -= OnCaretPositionChanged;
            _textView.LayoutChanged -= OnLayoutChanged;
            _textView.TextBuffer.Changed -= OnBufferChanged;
            _textView.Closed -= OnClosed;

            if (_document != null)
                _document.FileActionOccurred -= OnFileActionOccurred;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
    }
}