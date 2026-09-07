using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Retains legacy account ownership while adding independently journaled machine MSIX trust.
/// Each capability reconciles its own cut points; the helper verifies both before advancing.
/// </summary>
internal sealed class WindowsInstallerCertificateMutations : IInstallerCertificateMutation, IInstallerCertificateMutationVerifier, IDisposable
{
    private readonly DurableInstallerCertificateMutation _user;
    private readonly DurableInstallerMachineCertificateMutation _machine;
    private readonly IDisposable _lifetime;

    internal WindowsInstallerCertificateMutations(DurableInstallerCertificateMutation user,
        DurableInstallerMachineCertificateMutation machine, IDisposable lifetime)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(lifetime);
        _user = user;
        _machine = machine;
        _lifetime = lifetime;
    }

    public async Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        await _machine.ApplyAsync(request, release, cancellationToken).ConfigureAwait(false);
        await _user.ApplyAsync(request, release, cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyAppliedAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        await _machine.VerifyAppliedAsync(request, release, cancellationToken).ConfigureAwait(false);
        await _user.VerifyAppliedAsync(request, release, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _lifetime.Dispose();
}
