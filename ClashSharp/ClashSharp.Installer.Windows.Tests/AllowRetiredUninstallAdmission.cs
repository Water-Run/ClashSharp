using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Pure explicit admission for tests that exercise unrelated authority behavior.</summary>
internal sealed class AllowRetiredUninstallAdmission : IWindowsInstallerRetiredUninstallAdmission
{
    internal Func<CancellationToken, Task>? Inspect { get; init; }

    public Task EnsureNoRetiredUninstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Inspect?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
}
