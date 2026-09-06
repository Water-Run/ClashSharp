using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>Exercises native Installer/App sharing against an isolated profile-shaped directory.</summary>
public sealed class WindowsInstallerApplicationLockTests : IDisposable
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp-AppLockTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParentAndHelperIndependentlyExcludeAppUntilBothRelease()
    {
        using IDisposable parent = CreateLock(currentUser: true).Acquire(TargetSid, CancellationToken.None);
        Assert.Equal(0, new FileInfo(LockPath).Length);
        using IDisposable helper = CreateLock(currentUser: false).Acquire(TargetSid, CancellationToken.None);

        Assert.Throws<IOException>(() => OpenAppLock().Dispose());
        parent.Dispose();
        Assert.Throws<IOException>(() => OpenAppLock().Dispose());
        helper.Dispose();
        using FileStream app = OpenAppLock();
        Assert.True(app.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningAppRejectsInstallerBeforeAcquiringLease(bool currentUser)
    {
        Directory.CreateDirectory(LockDirectory);
        using FileStream app = OpenAppLock();

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            CreateLock(currentUser).Acquire(TargetSid, CancellationToken.None));

        Assert.Equal("installer.application_running", failure.DiagnosticCode);
        app.Dispose();
        Directory.Move(Profile, Profile + "-released");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperNeverCreatesMissingUserDirectoriesOrFiles(bool directoryExists)
    {
        Directory.CreateDirectory(directoryExists ? LockDirectory : Profile);

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            CreateLock(currentUser: false).Acquire(TargetSid, CancellationToken.None));

        Assert.Equal("installer.application_lock.unavailable", failure.DiagnosticCode);
        Assert.False(File.Exists(LockPath));
        Assert.Equal(directoryExists, Directory.Exists(LocalData));
        Directory.Move(Profile, Profile + "-released");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingCoordinationFileContentIsNeverChanged(bool currentUser)
    {
        Directory.CreateDirectory(LockDirectory);
        File.WriteAllText(LockPath, "existing-coordination-content");

        using IDisposable lease = CreateLock(currentUser).Acquire(TargetSid, CancellationToken.None);

        Assert.Equal("existing-coordination-content", File.ReadAllText(LockPath));
        Assert.Throws<IOException>(() => File.Delete(LockPath));
        Assert.Throws<IOException>(() => Directory.Move(LockDirectory, LockDirectory + "-moved"));
        lease.Dispose();
        Directory.Move(Profile, Profile + "-released");
    }

    [Fact]
    public void CancellationAfterAdmissionDoesNotReleaseLiveLease()
    {
        using CancellationTokenSource cancellation = new();
        using IDisposable lease = CreateLock(currentUser: true).Acquire(TargetSid, cancellation.Token);

        cancellation.Cancel();

        Assert.Throws<IOException>(() => OpenAppLock().Dispose());
        lease.Dispose();
        using FileStream app = OpenAppLock();
        Assert.True(app.CanWrite);
    }

    [Fact]
    public void PreCancellationDoesNotResolveIdentityOrTouchProfile()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        WindowsInstallerApplicationLock applicationLock = new(
            createCurrentUserLock: true,
            static (_, _) => throw new InvalidOperationException("Profile must not be queried."),
            static () => throw new InvalidOperationException("SID must not be queried."),
            static () => throw new InvalidOperationException("Known folder must not be queried."));

        Assert.Throws<OperationCanceledException>(() => applicationLock.Acquire(TargetSid, cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurrentUserCreationRequiresMatchingSidAndKnownFolder(bool mismatchedSid)
    {
        WindowsInstallerApplicationLock applicationLock = new(
            createCurrentUserLock: true,
            (_, _) => Profile,
            () => mismatchedSid ? "S-1-5-21-100-200-300-1002" : TargetSid,
            () => mismatchedSid ? LocalData : Path.Combine(_root, "different-local-data"));

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            applicationLock.Acquire(TargetSid, CancellationToken.None));

        Assert.Equal(
            mismatchedSid ? "installer.environment.target_user_mismatch" : "installer.application_lock.profile_mismatch",
            failure.DiagnosticCode);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryAtLockPathFailsClosedWithoutLeakingAncestorHandles(bool currentUser)
    {
        Directory.CreateDirectory(LockPath);

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            CreateLock(currentUser).Acquire(TargetSid, CancellationToken.None));

        Assert.Equal("installer.application_lock.unavailable", failure.DiagnosticCode);
        Assert.True(Directory.Exists(LockPath));
        Directory.Move(Profile, Profile + "-released");
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\profile")]
    [InlineData(@"C:\Users\name\..\different")]
    [InlineData(@"C:\Users\name\")]
    public void NoncanonicalProfileFailsBeforeFilesystemAccess(string profile)
    {
        WindowsInstallerApplicationLock applicationLock = new(
            createCurrentUserLock: false,
            (_, _) => profile,
            static () => throw new InvalidOperationException("Helper must not query its own SID."),
            static () => throw new InvalidOperationException("Helper must not query its own folder."));

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            applicationLock.Acquire(TargetSid, CancellationToken.None));

        Assert.Equal("installer.application_lock.profile_invalid", failure.DiagnosticCode);
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WindowsInstallerApplicationLock CreateLock(bool currentUser)
    {
        if (currentUser)
        {
            Directory.CreateDirectory(LocalData);
        }

        return new WindowsInstallerApplicationLock(
            currentUser,
            (sid, _) =>
            {
                Assert.Equal(TargetSid, sid);
                return Profile;
            },
            () => currentUser ? TargetSid : throw new InvalidOperationException("Helper queried its own SID."),
            () => currentUser ? LocalData : throw new InvalidOperationException("Helper queried its own folder."));
    }

    private FileStream OpenAppLock() => new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private string Profile => Path.Combine(_root, "profile");

    private string LocalData => Path.Combine(Profile, "AppData", "Local");

    private string LockDirectory => Path.Combine(LocalData, InstallerStateLayout.ProductDirectoryName);

    private string LockPath => Path.Combine(LockDirectory, InstallerStateLayout.ApplicationMutationLockFileName);
}
