using GitBlameForVs.GitBlame;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitBlameForVs.Adornment
{
    internal sealed class LineBlameAdornmentManager
    {
        public const string LayerName = "GitBlameForVsBlameLayer";

        private readonly IWpfTextView _textView;
        private readonly BlameUI _blameUI;
        private readonly GitBlameCache _cache = new();
        private readonly CommitMessageCache _messageCache = new();
        private readonly ITextDocument? _document;
        private ITextSnapshot? _baselineSnapshot;
        private CancellationTokenSource? _debounceCts;
        private DateTimeOffset? _lastEditTime;
        private long _updateGeneration;
        private string? _lastRequestedFilePath;
        private int _lastRequestedLineNumber = -1;
        private string? _lastRequestedLineText;
        private bool _lastRequestedLineIsChanged;

        public LineBlameAdornmentManager(IWpfTextView textView)
        {
            _textView = textView;
            _blameUI = new BlameUI(textView, textView.GetAdornmentLayer(LayerName));
            if (!_blameUI.HasAdornmentLayer)
                return;

            _textView.TextBuffer.Properties.TryGetProperty(
                typeof(ITextDocument), out _document);
            if (_document == null)
                return;

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
            if (_textView.IsClosed || !_blameUI.HasAdornmentLayer)
                return;

            if (!_textView.HasAggregateFocus)
            {
                CancelPendingUpdate();
                _blameUI.Clear();
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
            _blameUI.Clear();
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

        private bool IsLatest(long generation) =>
            generation == Interlocked.Read(ref _updateGeneration);

        private async Task UpdateAdornmentAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLatest(generation) || _textView.IsClosed)
                return;

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();
            var viewLine = _textView.GetTextViewLineContainingBufferPosition(caretPoint);
            if (viewLine == null ||
                IsLineContentChanged(snapshotLine) ||
                string.IsNullOrWhiteSpace(snapshotLine.GetText()))
            {
                return;
            }

            string filePath = GetFilePath();
            if (string.IsNullOrEmpty(filePath))
                return;

            int lineNumber1Based = snapshotLine.LineNumber + 1;
            var blame = await _cache.GetOrFetchAsync(filePath, lineNumber1Based, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (blame == null)
                return;

            var currentCaretPoint = _textView.Caret.Position.BufferPosition;
            var currentSnapshotLine = currentCaretPoint.GetContainingLine();
            if (currentSnapshotLine.LineNumber != snapshotLine.LineNumber ||
                IsLineContentChanged(currentSnapshotLine) ||
                string.IsNullOrWhiteSpace(currentSnapshotLine.GetText()) ||
                !IsLatest(generation))
            {
                return;
            }

            var currentViewLine = _textView.GetTextViewLineContainingBufferPosition(currentCaretPoint);
            if (currentViewLine != null)
                _blameUI.Render(
                    currentViewLine,
                    blame,
                    _lastEditTime,
                    LoadCommitMessageAsync,
                    AuthorAvatarCache.GetOrFetchAsync);
        }

        private void RenderFailureAdornment(long generation, Exception error)
        {
            if (!IsLatest(generation) || _textView.IsClosed || !_blameUI.HasAdornmentLayer)
                return;

            var caretPoint = _textView.Caret.Position.BufferPosition;
            var snapshotLine = caretPoint.GetContainingLine();
            if (IsLineContentChanged(snapshotLine))
                return;

            var viewLine = _textView.GetTextViewLineContainingBufferPosition(caretPoint);
            if (viewLine != null)
                _blameUI.RenderFailure(viewLine, error);
        }

        private bool IsLineContentChanged(ITextSnapshotLine currentLine)
        {
            if (_baselineSnapshot == null || _baselineSnapshot == currentLine.Snapshot)
                return false;

            var trackingPoint = currentLine.Snapshot.CreateTrackingPoint(
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

            return currentLine.GetText() != baselinePoint.GetContainingLine().GetText();
        }

        private async Task<string?> LoadCommitMessageAsync(BlameEntry blame)
        {
            string filePath = GetFilePath();
            string? workingDirectory = string.IsNullOrEmpty(filePath)
                ? null
                : Path.GetDirectoryName(filePath);

            return workingDirectory != null
                ? await _messageCache.GetOrFetchAsync(workingDirectory, blame.CommitHash)
                : null;
        }

        private string GetFilePath() => _document?.FilePath ?? string.Empty;

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
