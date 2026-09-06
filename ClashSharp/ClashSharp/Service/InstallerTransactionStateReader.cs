using System;
using System.IO;
using System.Security;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Service;

/// <summary>Classifies Installer ownership and transaction state observed at App startup.</summary>
internal enum InstallerTransactionState
{
    /// <summary>Neither a current journal nor a legacy transaction marker exists.</summary>
    Clear,

    /// <summary>An ordinary readable journal or legacy transaction marker exists.</summary>
    Pending,

    /// <summary>The fixed marker path cannot be inspected safely or unambiguously.</summary>
    Invalid,

    /// <summary>Installer lifetime ownership was unavailable, so journal inspection was not admitted.</summary>
    OwnershipUnavailable,
}

/// <summary>Reads Installer-owned transaction presence without changing machine state.</summary>
internal interface IInstallerTransactionStateReader
{
    /// <summary>Observes the fixed current and legacy transaction paths once.</summary>
    InstallerTransactionState Read();
}

/// <summary>
/// Observes fixed ProgramData Installer transaction paths without parsing or repairing their content.
/// </summary>
internal sealed class InstallerTransactionStateReader : IInstallerTransactionStateReader
{
    private readonly string? _commonApplicationDataRoot;

    /// <summary>Creates the production reader whose root comes from the Windows well-known folder.</summary>
    internal InstallerTransactionStateReader()
    {
    }

    /// <summary>Creates a reader rooted at an explicit absolute directory for isolated verification.</summary>
    internal InstallerTransactionStateReader(string commonApplicationDataRoot)
    {
        _commonApplicationDataRoot = commonApplicationDataRoot;
    }

    /// <inheritdoc />
    public InstallerTransactionState Read()
    {
        try
        {
            string root = _commonApplicationDataRoot
                ?? Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                return InstallerTransactionState.Invalid;
            }

            string fullRoot = Path.GetFullPath(root);
            if (!Path.IsPathFullyQualified(fullRoot)
                || ObservePath(fullRoot) != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            string productRoot = Path.Combine(fullRoot, InstallerStateLayout.ProductDirectoryName);
            ObservedPathKind productKind = ObservePath(productRoot);
            if (productKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (productKind != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            string installerRoot = Path.Combine(productRoot, InstallerStateLayout.InstallerDirectoryName);
            ObservedPathKind installerKind = ObservePath(installerRoot);
            if (installerKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (installerKind != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            InstallerTransactionState legacyState = ObserveMarker(Path.Combine(
                installerRoot,
                InstallerStateLayout.LegacyMarkerFileName));
            if (legacyState != InstallerTransactionState.Clear)
            {
                return legacyState;
            }

            string versionRoot = Path.Combine(installerRoot, InstallerStateLayout.VersionDirectoryName);
            ObservedPathKind versionKind = ObservePath(versionRoot);
            if (versionKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (versionKind != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            return ObserveMarker(Path.Combine(versionRoot, InstallerStateLayout.JournalFileName));
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException or
            PlatformNotSupportedException)
        {
            return InstallerTransactionState.Invalid;
        }
    }

    private static InstallerTransactionState ObserveMarker(string markerPath)
    {
        ObservedPathKind markerKind = ObservePath(markerPath);
        if (markerKind == ObservedPathKind.Missing)
        {
            return InstallerTransactionState.Clear;
        }

        if (markerKind != ObservedPathKind.OrdinaryFile)
        {
            return InstallerTransactionState.Invalid;
        }

        // Presence is sufficient, including malformed or Verified-but-not-cleared journals.
        // Do not obstruct the authority's atomic replacement while observing that presence.
        using FileStream marker = new(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);
        _ = marker.Length;
        return InstallerTransactionState.Pending;
    }

    private static ObservedPathKind ObservePath(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return ObservedPathKind.Invalid;
            }

            return (attributes & FileAttributes.Directory) != 0
                ? ObservedPathKind.OrdinaryDirectory
                : ObservedPathKind.OrdinaryFile;
        }
        catch (FileNotFoundException)
        {
            return ObservedPathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ObservedPathKind.Missing;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            return ObservedPathKind.Invalid;
        }
    }

    private enum ObservedPathKind
    {
        Missing,
        OrdinaryDirectory,
        OrdinaryFile,
        Invalid,
    }
}
