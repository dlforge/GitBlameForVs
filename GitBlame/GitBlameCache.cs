using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GitBlameForVs.GitBlame
{
    public class GitBlameCache
    {
        // 按文件路径存放"整份文件的行号 -> blame 信息"映射
        private readonly ConcurrentDictionary<string, Dictionary<int, GitBlameInfo>> _fileCache = new();

        // 防止同一个文件的预取任务被并发触发多次（比如快速连续移动光标）
        private readonly ConcurrentDictionary<string, Task> _prefetchTasks = new();

        /// <summary>
        /// 获取指定文件指定行的 blame 信息。
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        public async Task<GitBlameInfo?> GetOrFetchAsync(string filePath, int line)
        {
            // 已经预取完成，直接查表，零额外开销
            if (_fileCache.TryGetValue(filePath, out var lineDict))
                return lineDict.TryGetValue(line, out var cached) ? cached : null;

            // 预取还没完成（比如文件刚打开），先确保预取任务已启动，
            // 同时用单行查询作为过渡方案立即返回结果，避免用户等待
            await EnsurePrefetchStartedAsync(filePath);
            return await GitBlameService.GetBlameAsync(filePath, line);
        }

        /// <summary>
        /// 触发整份文件的后台预取。重复调用是安全的——
        /// 同一个文件路径的预取任务只会真正执行一次。
        /// </summary>
        public async Task EnsurePrefetchStartedAsync(string filePath)
        {
            await _prefetchTasks.GetOrAdd(filePath, _ => PrefetchFileAsync(filePath));
        }

        /// <summary>
        /// 后台预取整份文件的 blame 信息，并缓存到 _fileCache 中。
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        private async Task PrefetchFileAsync(string filePath)
        {
            var result = await GitBlameService.GetBlameForWholeFileAsync(filePath);
            if (result != null)
                _fileCache[filePath] = result;

            _prefetchTasks.TryRemove(filePath, out _);
        }

        /// <summary>
        /// 清除指定文件的缓存和预取任务。
        /// </summary>
        /// <param name="filePath"></param>
        public void InvalidateFile(string filePath)
        {
            _fileCache.TryRemove(filePath, out _);
            _prefetchTasks.TryRemove(filePath, out _);
        }
    }
}