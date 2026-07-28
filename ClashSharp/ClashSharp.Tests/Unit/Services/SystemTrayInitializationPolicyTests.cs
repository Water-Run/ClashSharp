extern alias ClashSharpUi;

using SystemTrayInitializationPolicy =
    ClashSharpUi::ClashSharp.Service.SystemTrayInitializationPolicy;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies partially initialized tray resources are released before startup degrades.</summary>
public sealed class SystemTrayInitializationPolicyTests
{
    [Fact]
    public void Complete_RefreshFailure_RemovesRegistrationAndDisposesResource()
    {
        DisposableResource resource = new();
        List<string> operations = [];
        InvalidOperationException expected = new("tray refresh unavailable");

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => SystemTrayInitializationPolicy.Complete(
                resource,
                () =>
                {
                    operations.Add("add");
                    return true;
                },
                () =>
                {
                    operations.Add("refresh");
                    throw expected;
                },
                () => operations.Add("remove")));

        Assert.Same(expected, actual);
        Assert.Equal(["add", "refresh", "remove"], operations);
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void Complete_NativeAddReturnsFalse_RemovesRegistrationAndDisposesResource()
    {
        DisposableResource resource = new();
        List<string> operations = [];

        Assert.Throws<InvalidOperationException>(
            () => SystemTrayInitializationPolicy.Complete(
                resource,
                () =>
                {
                    operations.Add("add");
                    return false;
                },
                () =>
                {
                    operations.Add("refresh");
                    return true;
                },
                () => operations.Add("remove")));

        Assert.Equal(["add", "remove"], operations);
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void Complete_NativeModifyReturnsFalse_RemovesRegistrationAndDisposesResource()
    {
        DisposableResource resource = new();
        List<string> operations = [];

        Assert.Throws<InvalidOperationException>(
            () => SystemTrayInitializationPolicy.Complete(
                resource,
                () =>
                {
                    operations.Add("add");
                    return true;
                },
                () =>
                {
                    operations.Add("refresh");
                    return false;
                },
                () => operations.Add("remove")));

        Assert.Equal(["add", "refresh", "remove"], operations);
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void Complete_Success_LeavesOwnedResourceRegistered()
    {
        DisposableResource resource = new();
        List<string> operations = [];

        SystemTrayInitializationPolicy.Complete(
            resource,
            () =>
            {
                operations.Add("add");
                return true;
            },
            () =>
            {
                operations.Add("refresh");
                return true;
            },
            () => operations.Add("remove"));

        Assert.Equal(["add", "refresh"], operations);
        Assert.False(resource.IsDisposed);
    }

    [Fact]
    public void Complete_RecoverableCleanupFailure_DoesNotReplaceInitializationFailure()
    {
        DisposableResource resource = new();
        InvalidOperationException expected = new("tray refresh unavailable");

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => SystemTrayInitializationPolicy.Complete(
                resource,
                static () => true,
                () => throw expected,
                () => throw new InvalidOperationException("tray removal unavailable")));

        Assert.Same(expected, actual);
        Assert.True(resource.IsDisposed);
    }

    private sealed class DisposableResource : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
