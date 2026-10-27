using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GitBlameForVs.GitBlame
{
    internal sealed class CommitChangeStats
    {
        public CommitChangeStats(int filesChanged, int insertions, int deletions)
        {
            FilesChanged = filesChanged;
            Insertions = insertions;
            Deletions = deletions;
        }

        public int FilesChanged { get; }
        public int Insertions { get; }
        public int Deletions { get; }
    }

    internal sealed class CommitChangeStatsCache
    {
        private readonly ConcurrentDictionary<string, Task<CommitChangeStats?>> _cache = new();

        public Task<CommitChangeStats?> GetOrFetchAsync(string workingDirectory, string? commitHash)
        {
            if (commitHash == null)
                return Task.FromResult<CommitChangeStats?>(null);

            return _cache.GetOrAdd(commitHash, _ => FetchAsync(workingDirectory, commitHash));
        }

        private static async Task<CommitChangeStats?> FetchAsync(string workingDirectory, string commitHash)
        {
            try
            {
                return await Git.GetCommitChangeStatsAsync(workingDirectory, commitHash);
            }
            catch (Exception error)
            {
                Trace.TraceWarning($"Failed to load Git commit change statistics: {error}");
                return null;
            }
        }
    }
}
