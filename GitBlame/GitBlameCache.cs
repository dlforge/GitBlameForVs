using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace GitBlameForVs.GitBlame
{
    public class GitBlameCache
    {
        // 按文件路径存放"整份文件的行号 -> blame 信息"映射
        private readonly ConcurrentDictionary<string, Dictionary<int, BlameInfo>> _fileCache = new();

        private readonly Dictionary<string, Task<PrefetchResult>> _prefetchTasks = new();
        private readonly object _prefetchTasksLock = new();
        private readonly CancellationTokenSource _lifetimeCancellationSource = new();

        /// <summary>
        /// 获取指定文件指定行的 blame 信息。
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        public async Task<BlameInfo?> GetOrFetchAsync(
            string filePath,
            int line,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 已经预取完成，直接查表，零额外开销
            if (_fileCache.TryGetValue(filePath, out var lineDict))
                return lineDict.TryGetValue(line, out var cached) ? cached : null;

            var prefetchTask = GetPrefetchTaskAsync(filePath);
            var prefetchResult = await AwaitWithCancellationAsync(prefetchTask, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (prefetchResult.Error != null)
            {
                lock (_prefetchTasksLock)
                {
                    if (_prefetchTasks.TryGetValue(filePath, out var currentTask) &&
                        ReferenceEquals(currentTask, prefetchTask))
                    {
                        _prefetchTasks.Remove(filePath);
                    }
                }

                ExceptionDispatchInfo.Capture(prefetchResult.Error).Throw();
            }

            if (prefetchResult.BlameByLine != null)
            {
                _fileCache[filePath] = prefetchResult.BlameByLine;
                return prefetchResult.BlameByLine.TryGetValue(line, out var cached)
                    ? cached
                    : null;
            }

            return await Git.GetBlameAsync(filePath, line, cancellationToken)
                .ConfigureAwait(false);
        }

        private Task<PrefetchResult> GetPrefetchTaskAsync(string filePath)
        {
            lock (_prefetchTasksLock)
            {
                if (_prefetchTasks.TryGetValue(filePath, out var prefetchTask))
                    return prefetchTask;

                prefetchTask = PrefetchFileAsync(filePath);
                _prefetchTasks[filePath] = prefetchTask;
                return prefetchTask;
            }
        }

        private async Task<PrefetchResult> PrefetchFileAsync(string filePath)
        {
            try
            {
                var blameByLine = await Git.GetBlameForWholeFileAsync(
                    filePath,
                    _lifetimeCancellationSource.Token);
                return new PrefetchResult(blameByLine, null);
            }
            catch (Exception error)
            {
                return new PrefetchResult(null, error);
            }
        }

        public void CancelPendingWork()
        {
            _lifetimeCancellationSource.Cancel();
        }

        [SuppressMessage(
            "Usage",
            "VSTHRD003",
            Justification = "The shared prefetch task performs process I/O without capturing the UI context.")]
        private static async Task<T> AwaitWithCancellationAsync<T>(
            Task<T> task,
            CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
                return await task.ConfigureAwait(false);

            var cancellationSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancellationSource.TrySetResult(true)))
            {
                var completedTask = await Task.WhenAny(task, cancellationSource.Task).ConfigureAwait(false);
                if (completedTask != task)
                    throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }

        private sealed class PrefetchResult
        {
            public PrefetchResult(Dictionary<int, BlameInfo>? blameByLine, Exception? error)
            {
                BlameByLine = blameByLine;
                Error = error;
            }

            public Dictionary<int, BlameInfo>? BlameByLine { get; }
            public Exception? Error { get; }
        }

        /// <summary>
        /// 清除指定文件的缓存和预取任务。
        /// </summary>
        /// <param name="filePath"></param>
        public void InvalidateFile(string filePath)
        {
            _fileCache.TryRemove(filePath, out _);
            lock (_prefetchTasksLock)
                _prefetchTasks.Remove(filePath);
        }
    }
}