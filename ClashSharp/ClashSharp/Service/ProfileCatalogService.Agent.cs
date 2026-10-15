using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProfileCatalogService
{
    /// <summary>Reads one catalog-owned source or retained version without accepting a filesystem path.</summary>
    internal async Task<string> ReadAgentSourceAsync(string profileId, string? versionId, CancellationToken cancellationToken)
    {
        using IDisposable operation = _operations.Enter();
        await _profileMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProfileHistoryEntry? history = null;
            lock (_syncLock)
            {
                ProfileCatalogDocument document = LoadDocument();
                if (!TryFindProfile(document, profileId, out _)) { throw new ArgumentException("Unknown profile."); }
                if (versionId is not null)
                {
                    history = document.History.Where(entry => entry.ProfileId == profileId && entry.VersionId == versionId)
                        .Select(entry => (ProfileHistoryEntry?)entry).SingleOrDefault()
                        ?? throw new ArgumentException("Unknown profile version.");
                }
            }
            if (history is { } entry)
            {
                string retained = await ReadBoundedUtf8FileAsync(GetProfileHistoryPath(entry.ProfileId, entry.VersionId), cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(AgentSourceHash(retained), entry.ContentSha256))
                {
                    throw new InvalidDataException("Profile history verification failed.");
                }
                return retained;
            }
            return await _coreConfiguration.ReadImportedProfileConfigurationAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The profile has no imported source.");
        }
        finally { _profileMutationGate.Release(); }
    }

    /// <summary>Validates and commits an agent edit through the same admission, source, history and compensation owners as UI edits.</summary>
    /// <remarks>The live-edit permission is checked after acquiring both mutation gates, because profile activation may have changed while waiting.</remarks>
    internal async Task<ProfileImportResult> SaveAgentSourceAsync(Guid operationId, string? existingId, string name,
        string content, string? expectedHash, Func<bool> mayEditActive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mayEditActive);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (operationId == Guid.Empty || name.Length > 80 || Encoding.UTF8.GetByteCount(content) > MaxProfileConfigurationBytes)
        {
            throw new ArgumentException("Invalid profile edit.");
        }
        using IDisposable operation = _operations.Enter();
        return await _mutationCoordinator.ExecuteAsync(operationId, async (_, token) =>
        {
            await _profileMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string id = existingId ?? $"local-{operationId:N}";
                string sourceName = "Agent";
                bool exists;
                lock (_syncLock)
                {
                    exists = TryFindProfile(LoadDocument(), id, out ConfigurationProfile profile);
                    if (existingId is not null && !exists || id == ProfileCatalogIds.BuiltInDirect)
                    {
                        throw new ArgumentException("The profile cannot be edited.");
                    }
                    if (exists) { sourceName = profile.SourceName; }
                }
                string? previous = exists ? await _coreConfiguration.ReadImportedProfileConfigurationAsync(id, token).ConfigureAwait(false) : null;
                if (exists && (previous is null || expectedHash is null
                    || !StringComparer.OrdinalIgnoreCase.Equals(AgentSourceHash(previous), expectedHash)))
                {
                    throw new InvalidOperationException("The profile source changed; read it before editing.");
                }
                bool active = StringComparer.Ordinal.Equals(GetActiveProfileId(), id);
                if (active && !mayEditActive()) { throw new UnauthorizedAccessException("Active profile editing requires proxy control."); }
                token.ThrowIfCancellationRequested();
                return await CommitProfileContentAsync(id, name.Trim(), sourceName, content, previous, active,
                    active ? ProfileHistoryApplyOutcome.Applied : ProfileHistoryApplyOutcome.Stored, token).ConfigureAwait(false);
            }
            finally { _profileMutationGate.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string AgentSourceHash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
