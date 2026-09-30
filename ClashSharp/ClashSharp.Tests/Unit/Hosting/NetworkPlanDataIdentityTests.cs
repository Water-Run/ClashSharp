extern alias ClashSharpUi;
using System.Text.Json.Nodes;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using Persistence = ClashSharpUi::ClashSharp.Hosting.Compatibility.LegacyNetworkPlanPersistence;

namespace ClashSharp.Tests.Unit.Hosting;

public sealed class NetworkPlanDataIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NetworkDataIdentity_CurrentPayloadRoundTripsTheExactNamespace(bool managed)
    {
        Guid? id = managed ? Guid.NewGuid() : null;
        var restored = Persistence.Deserialize(CreatePayload(id));
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(id, restored.DataGenerationId);
        Persistence.RequireDataGeneration(restored, id);
    }

    [Theory]
    [InlineData("another-directory")]
    [InlineData("managed-to-legacy")]
    [InlineData("legacy-to-managed")]
    public void NetworkDataIdentity_EqualNetworkValuesCannotAuthorizeAnotherNamespace(string mismatch)
    {
        Guid? saved = mismatch == "legacy-to-managed" ? null : Guid.NewGuid();
        Guid? current = mismatch == "managed-to-legacy" ? null : Guid.NewGuid();
        var restored = Persistence.Deserialize(CreatePayload(saved));
        Assert.Throws<InvalidOperationException>(() => Persistence.RequireDataGeneration(restored, current));
    }

    [Fact]
    public void NetworkDataIdentity_VersionOneIsOnlyAcceptedBeforeManagedStorageBinding()
    {
        JsonNode old = JsonNode.Parse(CreatePayload(null))!;
        old["schemaVersion"] = 1;
        old.AsObject().Remove("dataGenerationId");
        var restored = Persistence.Deserialize(old.ToJsonString());
        Persistence.RequireDataGeneration(restored, null);
        Assert.Throws<InvalidOperationException>(() => Persistence.RequireDataGeneration(restored, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("legacy-with-managed-id")]
    [InlineData("empty-id")]
    [InlineData("unknown-schema")]
    public void NetworkDataIdentity_RejectsAmbiguousOrUnsupportedPayloads(string invalid)
    {
        JsonNode payload = JsonNode.Parse(CreatePayload(Guid.NewGuid()))!;
        if (invalid == "legacy-with-managed-id") { payload["schemaVersion"] = 1; }
        if (invalid == "empty-id") { payload["dataGenerationId"] = Guid.Empty; }
        if (invalid == "unknown-schema") { payload["schemaVersion"] = 3; }
        Assert.Throws<InvalidOperationException>(() => Persistence.Deserialize(payload.ToJsonString()));
    }

    private static string CreatePayload(Guid? id)
    {
        NetworkIntent intent = NetworkIntent.Shutdown(ClashSharpMode.Disabled, false, 10000);
        NetworkStateSnapshot state = new(ClashSharpMode.Disabled, false, false, false, 10000, "state");
        return Persistence.Serialize(intent, state, state, "baseline", "desired", string.Empty, string.Empty,
            ClashSharpMode.RuleTakeover, false, 10000, id);
    }
}
