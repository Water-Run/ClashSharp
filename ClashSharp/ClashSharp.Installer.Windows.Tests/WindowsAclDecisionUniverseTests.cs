using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Decision-universe tests of every ACL policy the installer relies on. Each creator is checked against
/// its own validator through real security descriptors, every exact validator rejects every single-field
/// deviation, and the ancestor-trust policy is checked through metamorphic properties instead of examples.
/// </summary>
public sealed class WindowsAclDecisionUniverseTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string System = WindowsDirectoryAccessPolicy.LocalSystemSid;
    private const string Administrators = WindowsDirectoryAccessPolicy.AdministratorsSid;
    private const string TrustedInstaller = WindowsDirectoryAccessPolicy.TrustedInstallerSid;
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";
    private const string Authenticated = "S-1-5-11";
    private const string Other = "S-1-5-21-9-9-9-500";
    private const int FullControl = (int)FileSystemRights.FullControl;
    private const int ReadOnly = (int)WindowsDirectoryAccessPolicy.OwnerReadOnlyRights;
    private const int GenericAll = 0x1000_0000;
    private const AceFlags DirectoryFlags = AceFlags.ContainerInherit | AceFlags.ObjectInherit;

    private static readonly string[] AllSids = [System, Administrators, TrustedInstaller, Users, Everyone, Authenticated, TargetSid, Other];

    private static readonly int[] Masks =
    [
        0, ReadOnly, (int)FileSystemRights.Write, (int)FileSystemRights.Modify, (int)FileSystemRights.Delete,
        (int)FileSystemRights.DeleteSubdirectoriesAndFiles, (int)FileSystemRights.ChangePermissions,
        (int)FileSystemRights.TakeOwnership, GenericAll, FullControl, unchecked((int)0x8000_0000),
    ];

    private static readonly AceFlags[] FlagSets =
    [
        AceFlags.None, AceFlags.InheritOnly, DirectoryFlags, DirectoryFlags | AceFlags.InheritOnly,
        AceFlags.Inherited, DirectoryFlags | AceFlags.NoPropagateInherit,
    ];

    [Fact]
    public void EveryCreatorProducesADescriptorItsOwnValidatorAccepts()
    {
        WindowsDirectorySecuritySnapshot owner = Snapshot(WindowsDirectoryAccessPolicy.CreateOwnerReadableDirectorySecurity(TargetSid));
        Assert.True(WindowsDirectoryAccessPolicy.HasExactOwnerReadOnlyAccess(owner, TargetSid));
        WindowsInstallerDirectorySecurityPolicy.ValidateProtectedRoot(owner, TargetSid);
        Assert.True(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(owner, TargetSid, directory: true, inherited: false));
        WindowsInstallerDirectorySecurityPolicy.ValidateRenameAnchor(owner);

        WindowsDirectorySecuritySnapshot protectedRoot = Snapshot(WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(TargetSid));
        Assert.True(WindowsDirectoryAccessPolicy.HasExactOwnerReadOnlyAccess(protectedRoot, TargetSid));

        WindowsDirectorySecuritySnapshot privateDirectory = Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity());
        WindowsInstallerPrivateStateSecurity.Validate(privateDirectory, directory: true);
        Assert.True(WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(privateDirectory, service: false));
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPrivateStateSecurity.Validate(privateDirectory, directory: false));

        WindowsDirectorySecuritySnapshot privateFile = Snapshot(WindowsInstallerPrivateStateSecurity.CreateFileSecurity());
        WindowsInstallerPrivateStateSecurity.Validate(privateFile, directory: false);
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPrivateStateSecurity.Validate(privateFile, directory: true));

        WindowsDirectorySecuritySnapshot ledger = Snapshot(WindowsInstallerDirectoryLedgerSecurity.Create());
        WindowsInstallerDirectoryLedgerSecurity.Validate(ledger);
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerSecurity.Validate(privateFile));
    }

    [Fact]
    public void OwnerTransferDescriptorsAgreeWithTheirValidatorForEveryDirectoryAndInheritanceCombination()
    {
        foreach (bool directory in new[] { true, false })
        {
            foreach (bool inherited in new[] { true, false })
            {
                byte[] binary = WindowsOwnerTransferAccessPolicy.CreateDacl(TargetSid, directory, inherited);
                var acl = new RawAcl(binary, 0);
                WindowsDirectoryAce[] entries = acl.Cast<CommonAce>()
                    .Select(static ace => new WindowsDirectoryAce(
                        ace.SecurityIdentifier.Value,
                        WindowsDirectoryAceKind.Allow,
                        ace.AccessMask,
                        ace.AceFlags,
                        IsObjectSpecific: false))
                    .ToArray();
                var snapshot = new WindowsDirectorySecuritySnapshot(Administrators, true, !inherited, entries);

                Assert.True(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(snapshot, TargetSid, directory, inherited));
                Assert.False(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(snapshot, TargetSid, !directory, inherited));
                Assert.False(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(snapshot, TargetSid, directory, !inherited));
                Assert.False(WindowsOwnerTransferAccessPolicy.HasOwnerAccess(snapshot, Other, directory, inherited));
            }
        }
    }

    [Fact]
    public void ExactValidatorsRejectEverySingleFieldDeviationFromTheirCanonicalDescriptor()
    {
        WindowsDirectorySecuritySnapshot owner = Snapshot(WindowsDirectoryAccessPolicy.CreateOwnerReadableDirectorySecurity(TargetSid));
        WindowsDirectorySecuritySnapshot privateDirectory = Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity());
        WindowsDirectorySecuritySnapshot privateFile = Snapshot(WindowsInstallerPrivateStateSecurity.CreateFileSecurity());
        WindowsDirectorySecuritySnapshot ledger = Snapshot(WindowsInstallerDirectoryLedgerSecurity.Create());

        var validators = new (string Name, WindowsDirectorySecuritySnapshot Valid, Func<WindowsDirectorySecuritySnapshot, bool> Accepts)[]
        {
            ("owner-readable", owner, snapshot => WindowsDirectoryAccessPolicy.HasExactOwnerReadOnlyAccess(snapshot, TargetSid)),
            ("owner-access", owner, snapshot => WindowsOwnerTransferAccessPolicy.HasOwnerAccess(snapshot, TargetSid, true, false)),
            ("private-directory", privateDirectory, snapshot => WindowsOwnerTransferAccessPolicy.HasPrivateDirectoryAccess(snapshot, service: false)),
            ("private-state-directory", privateDirectory, snapshot => Accepts(() => WindowsInstallerPrivateStateSecurity.Validate(snapshot, directory: true))),
            ("private-state-file", privateFile, snapshot => Accepts(() => WindowsInstallerPrivateStateSecurity.Validate(snapshot, directory: false))),
            ("ledger-file", ledger, snapshot => Accepts(() => WindowsInstallerDirectoryLedgerSecurity.Validate(snapshot))),
        };

        foreach ((string name, WindowsDirectorySecuritySnapshot valid, Func<WindowsDirectorySecuritySnapshot, bool> accepts) in validators)
        {
            Assert.True(accepts(valid), name + " must accept its own canonical descriptor.");
            int mutants = 0;
            foreach ((string reason, WindowsDirectorySecuritySnapshot mutant) in Mutants(valid))
            {
                mutants++;
                Assert.False(accepts(mutant), $"{name} accepted a descriptor with {reason}.");
            }

            Assert.True(mutants > 100, name + " mutation universe is too small to mean anything.");
        }
    }

    [Fact]
    public void RealWorldAncestorDescriptorsAreClassifiedByTheirDangerousRightsNotTheirShape()
    {
        // The default ACL of a volume root: system principals plus inherit-only Authenticated Users rights.
        var volumeRoot = new WindowsDirectorySecuritySnapshot(
            System,
            true,
            true,
            [
                Ace(System, FullControl, DirectoryFlags),
                Ace(Administrators, FullControl, DirectoryFlags),
                Ace(Authenticated, (int)FileSystemRights.Modify, DirectoryFlags | AceFlags.InheritOnly),
                Ace(Authenticated, (int)(FileSystemRights.AppendData | FileSystemRights.ReadAndExecute), AceFlags.None),
                Ace(Users, ReadOnly, DirectoryFlags),
            ]);
        Assert.True(WindowsDirectoryAccessPolicy.IsTrustedAncestor(volumeRoot, isPhysicalVolumeRoot: true));
        Assert.True(WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(volumeRoot));

        // Users that can delete the directory itself are harmless on a volume root only.
        WindowsDirectorySecuritySnapshot usersDelete = volumeRoot with
        {
            AccessEntries = [.. volumeRoot.AccessEntries, Ace(Users, (int)FileSystemRights.Delete, AceFlags.None)],
        };
        Assert.True(WindowsDirectoryAccessPolicy.IsTrustedAncestor(usersDelete, isPhysicalVolumeRoot: true));
        Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(usersDelete, isPhysicalVolumeRoot: false));

        // Deleting children, rewriting the descriptor, taking ownership or GenericAll are never harmless.
        foreach (int dangerous in new[]
        {
            (int)FileSystemRights.DeleteSubdirectoriesAndFiles, (int)FileSystemRights.ChangePermissions,
            (int)FileSystemRights.TakeOwnership, GenericAll, FullControl,
        })
        {
            WindowsDirectorySecuritySnapshot unsafeAncestor = volumeRoot with
            {
                AccessEntries = [.. volumeRoot.AccessEntries, Ace(Users, dangerous, AceFlags.None)],
            };
            Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(unsafeAncestor, isPhysicalVolumeRoot: true));
            Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(unsafeAncestor, isPhysicalVolumeRoot: false));
        }
    }

    [Fact]
    public void AncestorTrustIsMetamorphicallyStableUnderIrrelevantAndHarmlessChanges()
    {
        var random = new Random(20261109);
        int trusted = 0;
        int untrusted = 0;
        for (int sample = 0; sample < 4_000; sample++)
        {
            WindowsDirectorySecuritySnapshot snapshot = RandomSnapshot(random);
            foreach (bool volumeRoot in new[] { false, true })
            {
                bool decision = WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot, volumeRoot);
                _ = decision ? trusted++ : untrusted++;

                // A deny entry only removes access.
                Assert.Equal(decision, WindowsDirectoryAccessPolicy.IsTrustedAncestor(
                    snapshot with { AccessEntries = [.. snapshot.AccessEntries, Ace(Other, GenericAll, AceFlags.None, WindowsDirectoryAceKind.Deny)] },
                    volumeRoot));
                // An inherit-only entry never applies to the directory itself.
                Assert.Equal(decision, WindowsDirectoryAccessPolicy.IsTrustedAncestor(
                    snapshot with { AccessEntries = [.. snapshot.AccessEntries, Ace(Everyone, FullControl, DirectoryFlags | AceFlags.InheritOnly)] },
                    volumeRoot));
                // Rights held by a trusted principal are always acceptable.
                Assert.Equal(decision, WindowsDirectoryAccessPolicy.IsTrustedAncestor(
                    snapshot with { AccessEntries = [.. snapshot.AccessEntries, Ace(TrustedInstaller, FullControl, AceFlags.None)] },
                    volumeRoot));
                // Order is not significant.
                Assert.Equal(decision, WindowsDirectoryAccessPolicy.IsTrustedAncestor(
                    snapshot with { AccessEntries = [.. snapshot.AccessEntries.Reverse()] },
                    volumeRoot));
                // Removing the DACL can never make an ancestor trustworthy.
                Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot with { HasDacl = false }, volumeRoot));
                // An untrusted owner can rewrite the DACL regardless of its entries.
                Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot with { OwnerSid = Users }, volumeRoot));
                Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot with { OwnerSid = null }, volumeRoot));
                // An entry the parser could not classify fails closed.
                Assert.False(WindowsDirectoryAccessPolicy.IsTrustedAncestor(
                    snapshot with { AccessEntries = [.. snapshot.AccessEntries, Ace(System, 0, AceFlags.None, WindowsDirectoryAceKind.Unsupported)] },
                    volumeRoot));
            }

            // A volume root is the only relaxation; anything trusted as an ordinary anchor is trusted on a root.
            if (WindowsDirectoryAccessPolicy.IsTrustedRenameAnchor(snapshot))
            {
                Assert.True(WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot, isPhysicalVolumeRoot: true));
            }
        }

        Assert.True(trusted > 300 && untrusted > 300, $"Property is vacuous: {trusted} trusted, {untrusted} untrusted.");
    }

    [Fact]
    public void SinglePrincipalAncestorDecisionMatchesTheDocumentedDangerousRightsForEveryCombination()
    {
        const int dangerousOnRoot = (int)(FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | GenericAll;
        const int dangerousOnDirectory = dangerousOnRoot | (int)FileSystemRights.Delete;
        foreach (string owner in AllSids)
        {
            foreach (string sid in AllSids)
            {
                foreach (int mask in Masks)
                {
                    foreach (AceFlags flags in FlagSets)
                    {
                        foreach (WindowsDirectoryAceKind kind in Enum.GetValues<WindowsDirectoryAceKind>())
                        {
                            foreach (bool volumeRoot in new[] { false, true })
                            {
                                var snapshot = new WindowsDirectorySecuritySnapshot(owner, true, true, [Ace(sid, mask, flags, kind)]);
                                bool trustedSid = sid is System or Administrators or TrustedInstaller;
                                bool applies = (flags & AceFlags.InheritOnly) == 0;
                                bool harmful = kind == WindowsDirectoryAceKind.Allow && applies && !trustedSid
                                    && (mask & (volumeRoot ? dangerousOnRoot : dangerousOnDirectory)) != 0;
                                bool expected = owner is System or Administrators or TrustedInstaller
                                    && kind != WindowsDirectoryAceKind.Unsupported
                                    && !harmful;

                                Assert.True(
                                    expected == WindowsDirectoryAccessPolicy.IsTrustedAncestor(snapshot, volumeRoot),
                                    $"owner={owner} sid={sid} mask=0x{mask:X} flags={flags} kind={kind} root={volumeRoot}");
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("S-1-5-21-100-200-300-1001", "S-1-5-21-100-200-300-1002", true)]
    [InlineData("S-1-5-21-100-200-300-1001", "S-1-5-21-100-200-300-1001", false)]
    [InlineData("S-1-5-21-100-200-300-1001", "S-1-5-32-545", false)]
    [InlineData("S-1-5-21-100-200-300-1001", "S-1-5-18", false)]
    [InlineData("S-1-5-21-100-200-300-1001", "S-1-1-0", false)]
    [InlineData("S-1-5-32-544", "S-1-5-21-100-200-300-1001", false)]
    [InlineData("", "S-1-5-21-100-200-300-1001", false)]
    [InlineData("S-1-5-21-100-200-300-1001", "not-a-sid", false)]
    [InlineData("s-1-5-21-100-200-300-1001", "S-1-5-21-100-200-300-1002", false)]
    public void OwnerTransferParticipantsMustBeTwoDistinctCanonicalAccountSids(string previous, string next, bool valid)
    {
        if (valid)
        {
            WindowsOwnerTransferAccessPolicy.ValidateParticipants(previous, next);
            return;
        }

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(
            () => WindowsOwnerTransferAccessPolicy.ValidateParticipants(previous, next));
        Assert.Equal("installer.owner_transfer.access_identity_invalid", failure.DiagnosticCode);
    }

    private static bool Accepts(Action validator)
    {
        try
        {
            validator();
            return true;
        }
        catch (InstallerProtocolException)
        {
            return false;
        }
    }

    private static IEnumerable<(string Reason, WindowsDirectorySecuritySnapshot Mutant)> Mutants(WindowsDirectorySecuritySnapshot valid)
    {
        foreach (string? owner in new[] { null, System, Administrators, TrustedInstaller, Users, Everyone, TargetSid, Other })
        {
            if (owner != valid.OwnerSid)
            {
                yield return ("owner " + owner, valid with { OwnerSid = owner });
            }
        }

        yield return ("no DACL", valid with { HasDacl = false });
        yield return ("DACL protection flipped", valid with { DaclProtected = !valid.DaclProtected });
        for (int index = 0; index < valid.AccessEntries.Count; index++)
        {
            WindowsDirectoryAce entry = valid.AccessEntries[index];
            yield return ($"entry {index} removed", valid with { AccessEntries = valid.AccessEntries.Where((_, position) => position != index).ToArray() });
            yield return ($"entry {index} duplicated", valid with { AccessEntries = [.. valid.AccessEntries, entry] });
            foreach (string sid in AllSids.Where(sid => sid != entry.Sid))
            {
                yield return ($"entry {index} sid {sid}", Replace(valid, index, entry with { Sid = sid }));
            }

            foreach (WindowsDirectoryAceKind kind in Enum.GetValues<WindowsDirectoryAceKind>().Where(kind => kind != entry.Kind))
            {
                yield return ($"entry {index} kind {kind}", Replace(valid, index, entry with { Kind = kind }));
            }

            for (int bit = 0; bit < 32; bit++)
            {
                yield return ($"entry {index} mask bit {bit} flipped", Replace(valid, index, entry with { AccessMask = entry.AccessMask ^ (1 << bit) }));
            }

            foreach (AceFlags flag in new[]
            {
                AceFlags.ObjectInherit, AceFlags.ContainerInherit, AceFlags.NoPropagateInherit, AceFlags.InheritOnly,
                AceFlags.Inherited, AceFlags.SuccessfulAccess, AceFlags.FailedAccess,
            })
            {
                yield return ($"entry {index} flag {flag} flipped", Replace(valid, index, entry with { Flags = entry.Flags ^ flag }));
            }

            yield return ($"entry {index} object specific", Replace(valid, index, entry with { IsObjectSpecific = true }));
        }

        foreach (string sid in AllSids)
        {
            foreach (int mask in new[] { ReadOnly, FullControl, (int)FileSystemRights.Write })
            {
                foreach (WindowsDirectoryAceKind kind in new[] { WindowsDirectoryAceKind.Allow, WindowsDirectoryAceKind.Deny })
                {
                    yield return (
                        $"extra {kind} entry {sid} 0x{mask:X}",
                        valid with { AccessEntries = [.. valid.AccessEntries, Ace(sid, mask, valid.AccessEntries[0].Flags, kind)] });
                }
            }
        }
    }

    private static WindowsDirectorySecuritySnapshot Replace(WindowsDirectorySecuritySnapshot source, int index, WindowsDirectoryAce entry)
    {
        WindowsDirectoryAce[] entries = [.. source.AccessEntries];
        entries[index] = entry;
        return source with { AccessEntries = entries };
    }

    private static WindowsDirectoryAce Ace(
        string sid,
        int mask,
        AceFlags flags,
        WindowsDirectoryAceKind kind = WindowsDirectoryAceKind.Allow) =>
        new(sid, kind, mask, flags, IsObjectSpecific: false);

    private static WindowsDirectorySecuritySnapshot RandomSnapshot(Random random)
    {
        int count = random.Next(0, 5);
        var entries = new List<WindowsDirectoryAce>(count);
        for (int index = 0; index < count; index++)
        {
            // Bias towards principals and rights that make the decision interesting.
            string sid = random.Next(3) == 0 ? AllSids[random.Next(AllSids.Length)] : AllSids[random.Next(3)];
            entries.Add(Ace(
                sid,
                Masks[random.Next(Masks.Length)],
                FlagSets[random.Next(FlagSets.Length)],
                random.Next(6) == 0 ? WindowsDirectoryAceKind.Deny : WindowsDirectoryAceKind.Allow));
        }

        return new WindowsDirectorySecuritySnapshot(
            random.Next(5) == 0 ? Users : AllSids[random.Next(3)],
            HasDacl: true,
            DaclProtected: random.Next(2) == 0,
            entries);
    }

    private static WindowsDirectorySecuritySnapshot Snapshot(FileSystemSecurity security)
    {
        string? owner = ((SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier)))?.Value;
        WindowsDirectoryAce[] entries = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(static rule => new WindowsDirectoryAce(
                rule.IdentityReference.Value,
                rule.AccessControlType == AccessControlType.Allow ? WindowsDirectoryAceKind.Allow : WindowsDirectoryAceKind.Deny,
                (int)rule.FileSystemRights,
                ToAceFlags(rule),
                IsObjectSpecific: false))
            .ToArray();
        return new WindowsDirectorySecuritySnapshot(owner, true, security.AreAccessRulesProtected, entries);
    }

    private static AceFlags ToAceFlags(FileSystemAccessRule rule)
    {
        AceFlags flags = AceFlags.None;
        if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit))
        {
            flags |= AceFlags.ContainerInherit;
        }

        if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit))
        {
            flags |= AceFlags.ObjectInherit;
        }

        if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
        {
            flags |= AceFlags.InheritOnly;
        }

        if (rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit))
        {
            flags |= AceFlags.NoPropagateInherit;
        }

        return rule.IsInherited ? flags | AceFlags.Inherited : flags;
    }
}
