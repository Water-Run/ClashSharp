using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

public sealed class InstallationTaskbarRequestStoreTests
{
    private const string RequestId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ReplacementId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("invalid")]
    public void NoncanonicalRequest_IsIgnored(string request)
    {
        Dictionary<string, object> values = new() { [InstallationSetupProtocol.TaskbarRequestKey] = request };
        Assert.Null(InstallationTaskbarRequestStore.ReadPending(values));
        Assert.False(InstallationTaskbarRequestStore.TryConsume(values, request));
        Assert.False(values.ContainsKey(InstallationSetupProtocol.TaskbarReceiptKey));
    }

    [Fact]
    public void ConsumedRequest_IsNotReturnedOrConsumedAgain()
    {
        Dictionary<string, object> values = new() { [InstallationSetupProtocol.TaskbarRequestKey] = RequestId };
        Assert.Equal(RequestId, InstallationTaskbarRequestStore.ReadPending(values));
        Assert.True(InstallationTaskbarRequestStore.TryConsume(values, RequestId));
        Assert.Null(InstallationTaskbarRequestStore.ReadPending(values));
        Assert.False(InstallationTaskbarRequestStore.TryConsume(values, RequestId));
        Assert.Equal(RequestId, values[InstallationSetupProtocol.TaskbarReceiptKey]);
    }

    [Fact]
    public void ReplacedRequest_IsNotAcknowledgedUsingStaleIdentity()
    {
        Dictionary<string, object> values = new() { [InstallationSetupProtocol.TaskbarRequestKey] = ReplacementId };
        Assert.False(InstallationTaskbarRequestStore.TryConsume(values, RequestId));
        Assert.Equal(ReplacementId, InstallationTaskbarRequestStore.ReadPending(values));
        Assert.False(values.ContainsKey(InstallationSetupProtocol.TaskbarReceiptKey));
    }

    [Fact]
    public void ReplacementDuringReceiptWrite_IsPreservedAndRemainsPending()
    {
        IDictionary<string, object> values = new ReplacingDictionary { [InstallationSetupProtocol.TaskbarRequestKey] = RequestId };
        Assert.False(InstallationTaskbarRequestStore.TryConsume(values, RequestId));
        Assert.Equal(ReplacementId, InstallationTaskbarRequestStore.ReadPending(values));
        Assert.Equal(ReplacementId, values[InstallationSetupProtocol.TaskbarRequestKey]);
        Assert.Equal(RequestId, values[InstallationSetupProtocol.TaskbarReceiptKey]);
    }

    private sealed class ReplacingDictionary : Dictionary<string, object>, IDictionary<string, object>
    {
        object IDictionary<string, object>.this[string key]
        {
            get => base[key];
            set
            {
                base[key] = value;
                if (key == InstallationSetupProtocol.TaskbarReceiptKey) { base[InstallationSetupProtocol.TaskbarRequestKey] = ReplacementId; }
            }
        }
    }
}
