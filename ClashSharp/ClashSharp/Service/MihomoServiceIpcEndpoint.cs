using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Contains the immutable owner and deployment identity for service IPC.</summary>
internal sealed record MihomoServiceIpcEndpoint(
    string UserSid,
    string AuthenticationToken,
    string PipeName,
    string? ProvisioningFailureCode = null)
{
    internal const string AssociationInvalidCode = "service.provisioning.association_invalid";
    internal const string AssociationMissingCode = "service.provisioning.association_missing";
    internal const string OwnerMismatchCode = "service.provisioning.owner_mismatch";

    private const string SentinelPipeName = "ClashSharp.Mihomo.Unprovisioned";
    private const string SentinelSid = "S-1-0-0";
    private static readonly string SentinelToken = new('0', 64);

    /// <summary>Gets whether an Installer-provisioned association was validated.</summary>
    internal bool IsProvisioned => ProvisioningFailureCode is null;

    /// <summary>Loads the Installer-owned machine association for the current Windows user.</summary>
    internal static MihomoServiceIpcEndpoint LoadForCurrentUser()
    {
        try
        {
            string commonApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(commonApplicationData))
            {
                return Unprovisioned(AssociationInvalidCode);
            }

            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            string? currentSid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(currentSid))
            {
                return Unprovisioned(OwnerMismatchCode);
            }

            string associationPath = Path.Combine(
                commonApplicationData,
                "ClashSharp",
                "MihomoService",
                "association.json");
            return MihomoServiceAssociationReader.Read(associationPath, currentSid);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            SecurityException or
            InvalidOperationException or
            PlatformNotSupportedException or
            ArgumentException or
            NotSupportedException)
        {
            return Unprovisioned(AssociationInvalidCode);
        }
    }

    /// <summary>Creates and validates one explicit provisioned endpoint.</summary>
    internal static MihomoServiceIpcEndpoint Create(
        string userSid,
        string authenticationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        SecurityIdentifier sid = new(userSid);
        if (!string.Equals(sid.Value, userSid, StringComparison.Ordinal))
        {
            throw new ArgumentException("The service IPC owner SID is not canonical.", nameof(userSid));
        }

        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(authenticationToken))
        {
            throw new ArgumentException(
                "The service IPC credential is not canonical lowercase SHA-256 text.",
                nameof(authenticationToken));
        }

        return new MihomoServiceIpcEndpoint(
            userSid,
            authenticationToken,
            MihomoServiceIpcProtocol.BuildPipeName(userSid, authenticationToken));
    }

    /// <summary>Creates the fixed non-connectable endpoint used when provisioning is invalid.</summary>
    internal static MihomoServiceIpcEndpoint Unprovisioned(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new MihomoServiceIpcEndpoint(
            SentinelSid,
            SentinelToken,
            SentinelPipeName,
            failureCode);
    }
}

/// <summary>Strictly reads the Installer-owned machine association without repairing it.</summary>
internal static class MihomoServiceAssociationReader
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumAssociationBytes = 4096;

    private static readonly HashSet<string> RequiredPropertyNames = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "ownerSid",
        "authenticationToken",
    };

    internal static MihomoServiceIpcEndpoint Read(string associationPath, string currentUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(associationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(associationPath);
            if (!File.Exists(fullPath))
            {
                if (Directory.Exists(fullPath))
                {
                    return MihomoServiceIpcEndpoint.Unprovisioned(
                        MihomoServiceIpcEndpoint.AssociationInvalidCode);
                }

                return MihomoServiceIpcEndpoint.Unprovisioned(
                    MihomoServiceIpcEndpoint.AssociationMissingCode);
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return MihomoServiceIpcEndpoint.Unprovisioned(
                    MihomoServiceIpcEndpoint.AssociationInvalidCode);
            }

            byte[] bytes;
            using (FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: MaximumAssociationBytes,
                FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > MaximumAssociationBytes)
                {
                    return MihomoServiceIpcEndpoint.Unprovisioned(
                        MihomoServiceIpcEndpoint.AssociationInvalidCode);
                }

                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }

            return Parse(bytes, currentUserSid);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            JsonException)
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.AssociationInvalidCode);
        }
    }

    private static MihomoServiceIpcEndpoint Parse(byte[] bytes, string currentUserSid)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.AssociationInvalidCode);
        }

        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!RequiredPropertyNames.Contains(property.Name) || !observed.Add(property.Name))
            {
                return MihomoServiceIpcEndpoint.Unprovisioned(
                    MihomoServiceIpcEndpoint.AssociationInvalidCode);
            }
        }

        if (!observed.SetEquals(RequiredPropertyNames)
            || !root.TryGetProperty("schemaVersion", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int schemaVersion)
            || schemaVersion != CurrentSchemaVersion
            || !TryGetString(root, "ownerSid", out string? ownerSid)
            || !TryGetString(root, "authenticationToken", out string? token))
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.AssociationInvalidCode);
        }

        SecurityIdentifier parsedOwner;
        try
        {
            parsedOwner = new SecurityIdentifier(ownerSid!);
        }
        catch (ArgumentException)
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.AssociationInvalidCode);
        }

        if (!string.Equals(parsedOwner.Value, ownerSid, StringComparison.Ordinal)
            || !MihomoServiceIpcProtocol.IsCanonicalSha256(token))
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.AssociationInvalidCode);
        }

        if (!string.Equals(ownerSid, currentUserSid, StringComparison.Ordinal))
        {
            return MihomoServiceIpcEndpoint.Unprovisioned(
                MihomoServiceIpcEndpoint.OwnerMismatchCode);
        }

        return MihomoServiceIpcEndpoint.Create(ownerSid!, token!);
    }

    private static bool TryGetString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        if (root.TryGetProperty(propertyName, out JsonElement element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }
}
