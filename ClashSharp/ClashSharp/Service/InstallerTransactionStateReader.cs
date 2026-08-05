using System;
using System.IO;
using System.Security;

namespace ClashSharp.Service;

/// <summary>Classifies the Installer-owned public transaction marker observed at App startup.</summary>
internal enum InstallerTransactionState
{
    /// <summary>No public transaction marker or Installer directory exists.</summary>
    Clear,

    /// <summary>An ordinary readable public transaction marker exists.</summary>
    Pending,

    /// <summary>The fixed marker path cannot be inspected safely or unambiguously.</summary>
    Invalid,
}

/// <summary>Reads the Installer-owned public transaction state without changing machine state.</summary>
internal interface IInstallerTransactionStateReader
{
    /// <summary>Observes the fixed public marker once.</summary>
    InstallerTransactionState Read();
}

/// <summary>
/// Observes the fixed ProgramData Installer transaction marker without parsing or repairing it.
/// </summary>
internal sealed class InstallerTransactionStateReader : IInstallerTransactionStateReader
{
    internal const string ProductDirectoryName = "ClashSharp";
    internal const string InstallerDirectoryName = "Installer";
    internal const string PublicMarkerFileName = "transaction.json";

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
            if (string.IsNullOrWhiteSpace(root))
            {
                return InstallerTransactionState.Invalid;
            }

            string fullRoot = Path.GetFullPath(root);
            if (!Path.IsPathFullyQualified(fullRoot)
                || ObservePath(fullRoot) != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            string productRoot = Path.Combine(fullRoot, ProductDirectoryName);
            ObservedPathKind productKind = ObservePath(productRoot);
            if (productKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (productKind != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            string installerRoot = Path.Combine(productRoot, InstallerDirectoryName);
            ObservedPathKind installerKind = ObservePath(installerRoot);
            if (installerKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (installerKind != ObservedPathKind.OrdinaryDirectory)
            {
                return InstallerTransactionState.Invalid;
            }

            string markerPath = Path.Combine(installerRoot, PublicMarkerFileName);
            ObservedPathKind markerKind = ObservePath(markerPath);
            if (markerKind == ObservedPathKind.Missing)
            {
                return InstallerTransactionState.Clear;
            }

            if (markerKind != ObservedPathKind.OrdinaryFile)
            {
                return InstallerTransactionState.Invalid;
            }

            using FileStream marker = new(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
            _ = marker.Length;
            return InstallerTransactionState.Pending;
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
