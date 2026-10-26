using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GitBlameForVs.GitBlame
{
    internal static class AuthorAvatarCache
    {
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> AvatarRequests =
            new(StringComparer.Ordinal);

        public static Task<byte[]?> GetOrFetchAsync(string? authorMail)
        {
            string normalizedMail = authorMail?.Trim().Trim('<', '>').ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedMail))
                return Task.FromResult<byte[]?>(null);

            string emailHash = CreateEmailHash(normalizedMail);
            return AvatarRequests.GetOrAdd(emailHash, FetchAvatarAsync);
        }

        private static async Task<byte[]?> FetchAvatarAsync(string emailHash)
        {
            try
            {
                var avatarUri = new Uri($"https://www.gravatar.com/avatar/{emailHash}?d=404&s=128");
                using var response = await Client.GetAsync(
                    avatarUri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Trace.TraceWarning($"Failed to load author avatar: {error.Message}");
                return null;
            }
        }

        private static string CreateEmailHash(string normalizedMail)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedMail));
            var hashText = new StringBuilder(hashBytes.Length * 2);
            foreach (byte hashByte in hashBytes)
            {
                hashText.Append(hashByte.ToString("x2", CultureInfo.InvariantCulture));
            }

            return hashText.ToString();
        }
    }
}
