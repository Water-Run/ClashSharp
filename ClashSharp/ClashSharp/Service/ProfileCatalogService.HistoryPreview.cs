using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProfileCatalogService
{
    /// <summary>Reads bounded, hash-verified history sources and derives current preview counts off the UI thread.</summary>
    /// <remarks>Does not rewrite archived entries or files. Rollback still verifies the original entry and source.</remarks>
    /// <param name="profileId">Profile whose retained versions are requested.</param>
    /// <param name="cancellationToken">Cancels file reads and preview construction.</param>
    /// <returns>Newest-first previews, including unavailable versions without fabricated counts.</returns>
    public async Task<IReadOnlyList<ProfileHistoryPreview>> GetProfileHistoryPreviewsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable operation = _operations.Enter();
        return await Task.Run(async () =>
        {
            ProfileHistoryEntry[] entries;
            lock (_syncLock)
            {
                entries = [.. LoadDocument().History
                    .Where(entry => StringComparer.Ordinal.Equals(entry.ProfileId, profileId))
                    .OrderByDescending(entry => entry.CreatedAt)
                    .Take(MaxProfileHistoryEntriesPerProfile)];
            }

            List<ProfileHistoryPreview> previews = new(entries.Length);
            foreach (ProfileHistoryEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string content = await ReadBoundedUtf8FileAsync(
                        GetProfileHistoryPath(entry.ProfileId, entry.VersionId), cancellationToken).ConfigureAwait(false);
                    string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                    if (!StringComparer.Ordinal.Equals(hash, entry.ContentSha256))
                    {
                        throw new InvalidDataException("Profile history content differs from its catalog entry.");
                    }
                    int nodes = MihomoProfilePreviewParser.ParseNodes(content, static code => new RegionMetadata(code, code, code)).Count;
                    cancellationToken.ThrowIfCancellationRequested();
                    int rules = MihomoProfilePreviewParser.ParseRules(content, static key => key).Count;
                    previews.Add(new ProfileHistoryPreview(entry, nodes, rules));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException)
                {
                    previews.Add(new ProfileHistoryPreview(entry, null, null));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncLock)
            {
                ProfileCatalogDocument current = LoadDocument();
                if (entries.Any(entry => !current.History.Contains(entry)))
                {
                    throw new InvalidOperationException("Profile history changed while its preview was being read.");
                }
            }
            return (IReadOnlyList<ProfileHistoryPreview>)previews;
        }, cancellationToken).ConfigureAwait(false);
    }
}
