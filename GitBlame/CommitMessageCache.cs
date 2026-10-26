using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace GitBlameForVs.GitBlame
{
    /// <summary>
    /// 按 commit hash 缓存完整提交说明。
    /// 与按行缓存的 GitBlameCache 不同，这里的 key 是 commit hash，
    /// 同一个 commit 的完整 message 内容永远不变，所以这个缓存不需要任何失效逻辑，
    /// 用 ConcurrentDictionary 是因为 Tooltip 懒加载可能在 UI 线程之外的异步上下文触发。
    /// </summary>
    public class CommitMessageCache
    {
        private readonly ConcurrentDictionary<string, string?> _cache = new();

        public async Task<string?> GetOrFetchAsync(string workingDir, string? commitHash)
        {
            if (commitHash == null)
                return null;

            if (_cache.TryGetValue(commitHash, out var cached))
                return cached;

            var message = await Git.GetFullCommitMessageAsync(workingDir, commitHash);
            _cache[commitHash] = message; // 即使为 null 也缓存，避免重复失败调用
            return message;
        }
    }
}