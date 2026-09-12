using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Contracts;

/// <summary>Binds a cleared journal receipt to optional verified directory cleanup observations.</summary>
/// <param name="State">The exact immutable Verified journal that was cleared.</param>
/// <param name="DirectoryCleanupReport">Directory observations for a completed ordinary uninstall.</param>
public sealed record InstallerClearReceipt(
    InstallerTransactionSnapshot State,
    InstallerDirectoryCleanupReport? DirectoryCleanupReport = null);
