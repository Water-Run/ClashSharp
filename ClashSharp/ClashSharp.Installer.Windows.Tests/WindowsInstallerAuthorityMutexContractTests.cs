using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Contracts of the global installer authority object that never touch the real named mutex: the descriptor the
/// installer creates is accepted by its own validator and every single-field deviation is refused, and the wait
/// semantics (recursion, exclusion between threads, abandonment, release without ownership, disposal) are checked
/// on a private unnamed kernel mutex wrapped by the same type.
/// </summary>
public sealed class WindowsInstallerAuthorityMutexContractTests
{
    private const int AllAccess = 0x001F_0001;
    private static readonly SecurityIdentifier System = new("S-1-5-18");
    private static readonly SecurityIdentifier Administrators = new("S-1-5-32-544");
    private static readonly SecurityIdentifier Users = new("S-1-5-32-545");
    private static readonly SecurityIdentifier Everyone = new("S-1-1-0");

    [Fact]
    public void TheCreatedDescriptorIsAcceptedByItsOwnValidatorAndNullIsRejected()
    {
        RawSecurityDescriptor security = WindowsInstallerAuthorityMutex.CreateSecurity();

        WindowsInstallerAuthorityMutex.ValidateSecurity(security);
        Assert.Throws<ArgumentNullException>(() => WindowsInstallerAuthorityMutex.ValidateSecurity(null!));
        Assert.Equal(Administrators, security.Owner);
        Assert.Equal(2, security.DiscretionaryAcl!.Count);
    }

    [Fact]
    public void EverySingleFieldDeviationFromTheCanonicalDescriptorIsRefused()
    {
        const ControlFlags canonical = ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
        var mutants = new List<(string Label, RawSecurityDescriptor Descriptor)>
        {
            ("DACL not protected", Build(ControlFlags.DiscretionaryAclPresent, Administrators, Allow(System), Allow(Administrators))),
            ("no DACL flag", Build(ControlFlags.DiscretionaryAclProtected, Administrators, Allow(System), Allow(Administrators))),
            ("owner system", Build(canonical, System, Allow(System), Allow(Administrators))),
            ("owner users", Build(canonical, Users, Allow(System), Allow(Administrators))),
            ("only system", Build(canonical, Administrators, Allow(System))),
            ("only administrators", Build(canonical, Administrators, Allow(Administrators))),
            ("duplicate system", Build(canonical, Administrators, Allow(System), Allow(System))),
            ("duplicate administrators", Build(canonical, Administrators, Allow(Administrators), Allow(Administrators))),
            ("extra users", Build(canonical, Administrators, Allow(System), Allow(Administrators), Allow(Users))),
            ("extra everyone", Build(canonical, Administrators, Allow(System), Allow(Administrators), Allow(Everyone))),
            ("users instead of system", Build(canonical, Administrators, Allow(Users), Allow(Administrators))),
            ("everyone instead of administrators", Build(canonical, Administrators, Allow(System), Allow(Everyone))),
            ("system denied", Build(canonical, Administrators, Ace(System, AceQualifier.AccessDenied, AllAccess, AceFlags.None), Allow(Administrators))),
            ("generic all", Build(canonical, Administrators, Ace(System, AceQualifier.AccessAllowed, 0x1000_0000, AceFlags.None), Allow(Administrators))),
            ("synchronize only", Build(canonical, Administrators, Ace(System, AceQualifier.AccessAllowed, 0x0010_0000, AceFlags.None), Allow(Administrators))),
            ("no access", Build(canonical, Administrators, Ace(System, AceQualifier.AccessAllowed, 0, AceFlags.None), Allow(Administrators))),
            ("audit entry", Build(canonical, Administrators, Ace(System, AceQualifier.SystemAudit, AllAccess, AceFlags.None), Allow(Administrators))),
        };
        foreach (AceFlags flag in new[] { AceFlags.ObjectInherit, AceFlags.ContainerInherit, AceFlags.InheritOnly, AceFlags.Inherited, AceFlags.NoPropagateInherit })
        {
            mutants.Add(("system flag " + flag, Build(canonical, Administrators, Ace(System, AceQualifier.AccessAllowed, AllAccess, flag), Allow(Administrators))));
            mutants.Add(("administrators flag " + flag, Build(canonical, Administrators, Allow(System), Ace(Administrators, AceQualifier.AccessAllowed, AllAccess, flag))));
        }

        for (int bit = 0; bit < 32; bit++)
        {
            int mask = AllAccess ^ (1 << bit);
            mutants.Add(($"system mask bit {bit}", Build(canonical, Administrators, Ace(System, AceQualifier.AccessAllowed, mask, AceFlags.None), Allow(Administrators))));
        }

        foreach ((string label, RawSecurityDescriptor descriptor) in mutants)
        {
            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(
                () => WindowsInstallerAuthorityMutex.ValidateSecurity(descriptor));

            Assert.True(failure.DiagnosticCode == "installer.authority.lock_acl_invalid", label);
        }

        Assert.True(mutants.Count > 50);
    }

    [Fact]
    public void TheSameThreadMayRecurseAndEveryAcquireNeedsItsOwnRelease()
    {
        using var kernel = new Mutex(false);
        using var authority = new WindowsInstallerAuthorityMutex(kernel.SafeWaitHandle);

        Assert.True(authority.TryAcquire());
        Assert.True(authority.TryAcquire());
        authority.Release();
        authority.Release();

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(authority.Release);
        Assert.Equal("installer.authority.lock_release_failed", failure.DiagnosticCode);
        Assert.Equal(288, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
    }

    [Fact]
    public void AnotherThreadIsExcludedWhileHeldAndAdmittedAfterReleaseOrAbandonment()
    {
        using var kernel = new Mutex(false);
        using var authority = new WindowsInstallerAuthorityMutex(kernel.SafeWaitHandle);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool ownerAcquired = false;
        var owner = new Thread(() =>
        {
            ownerAcquired = authority.TryAcquire();
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            authority.Release();
        });
        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(ownerAcquired);

        Assert.False(RunOnNewThread(authority.TryAcquire));

        release.Set();
        owner.Join(TimeSpan.FromSeconds(10));
        Assert.True(RunOnNewThread(() =>
        {
            bool taken = authority.TryAcquire();
            authority.Release();
            return taken;
        }));

        // A holder that ends without releasing abandons the mutex; the next owner is still admitted.
        var abandoner = new Thread(() => _ = authority.TryAcquire());
        abandoner.Start();
        abandoner.Join(TimeSpan.FromSeconds(10));
        Assert.True(RunOnNewThread(() =>
        {
            bool taken = authority.TryAcquire();
            authority.Release();
            return taken;
        }));
    }

    [Fact]
    public void DisposalIsIdempotentAndEndsAllUse()
    {
        using var kernel = new Mutex(false);
        var authority = new WindowsInstallerAuthorityMutex(kernel.SafeWaitHandle);

        authority.Dispose();
        authority.Dispose();

        Assert.ThrowsAny<ObjectDisposedException>(() => authority.TryAcquire());
        Assert.Throws<ArgumentNullException>(() => new WindowsInstallerAuthorityMutex(null!));
    }

    private static bool RunOnNewThread(Func<bool> action)
    {
        bool result = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (InstallerProtocolException exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        return result;
    }

    private static CommonAce Allow(SecurityIdentifier sid) => Ace(sid, AceQualifier.AccessAllowed, AllAccess, AceFlags.None);

    private static CommonAce Ace(SecurityIdentifier sid, AceQualifier qualifier, int mask, AceFlags flags) =>
        new(flags, qualifier, mask, sid, isCallback: false, opaque: null);

    private static RawSecurityDescriptor Build(ControlFlags flags, SecurityIdentifier owner, params CommonAce[] aces)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, aces.Length);
        foreach (CommonAce ace in aces)
        {
            acl.InsertAce(acl.Count, ace);
        }

        return new RawSecurityDescriptor(flags, owner, Administrators, systemAcl: null, acl);
    }
}
