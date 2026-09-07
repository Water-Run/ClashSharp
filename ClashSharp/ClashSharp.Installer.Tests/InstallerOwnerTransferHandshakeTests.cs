using System.Buffers.Binary;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferHandshakeTests
{
    private static readonly InstallerOwnerTransferBootstrap Bootstrap = new(new string('b', 64), new string('d', 64), 4242);
    private static readonly InstallerOwnerTransferRequest Request = new(Bootstrap.SessionId, InstallerOperation.Install, "1.0.0.0", Bootstrap.InstallerPayloadSha256);
    private static readonly InstallerOwnerTransferOffer Offer = new(Request, new string('e', 64),
        "S-1-5-21-100-200-300-1001", "S-1-5-21-100-200-300-1002", false);

    [Fact]
    public void DedicatedBootstrapHasAnIndependentNamespaceAndExactRoundTrip()
    {
        Assert.Equal(Bootstrap, InstallerOwnerTransferBootstrap.Parse(Bootstrap.ToArguments()));
        Assert.StartsWith("ClashSharp.Installer.OwnerTransfer.", Bootstrap.BuildSessionPipeName());
        Assert.DoesNotContain(Bootstrap.ToArguments(), value => value.Contains("S-1-", StringComparison.Ordinal));
        Assert.Null(InstallerOwnerTransferBootstrap.Parse([]));
        Assert.Null(InstallerOwnerTransferBootstrap.Parse(["--machine-helper"]));
        Assert.NotEqual(InstallerOwnerTransferBootstrap.Create(Bootstrap.InstallerPayloadSha256, 4242).SessionId,
            InstallerOwnerTransferBootstrap.Create(Bootstrap.InstallerPayloadSha256, 4242).SessionId);
    }

    [Theory]
    [InlineData("--OWNER-TRANSFER-HELPER")]
    [InlineData("--owner-transfer-helper=1")]
    [InlineData("ui", "--owner-transfer-helper")]
    [InlineData("--transfer-session", "bad")]
    [InlineData("--owner-transfer-helper")]
    [InlineData("--owner-transfer-helper", "--machine-helper")]
    public void ReservedTransferGrammarNeverFallsBackToUi(params string[] arguments) =>
        Assert.Throws<InstallerProtocolException>(() => InstallerOwnerTransferBootstrap.Parse(arguments));

    [Theory]
    [InlineData("04242")]
    [InlineData("+4242")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("2147483648")]
    public void ParentPidMustBeCanonicalAndPositive(string value)
    {
        string[] arguments = Bootstrap.ToArguments().ToArray();
        arguments[^1] = value;
        Assert.Throws<InstallerProtocolException>(() => InstallerOwnerTransferBootstrap.Parse(arguments));
    }

    [Fact]
    public async Task HandshakeFramesRoundTripWithoutConsumingTheFollowingOrdinaryFrame()
    {
        using var stream = new MemoryStream();
        var decision = new InstallerOwnerTransferDecision(Request.SessionId, Offer.OfferId, true);
        await InstallerOwnerTransferFraming.WriteRequestAsync(stream, Request, default);
        await InstallerOwnerTransferFraming.WriteOfferAsync(stream, Offer, default);
        await InstallerOwnerTransferFraming.WriteDecisionAsync(stream, decision, default);
        byte[] suffix = [91, 92, 93];
        await stream.WriteAsync(suffix);
        stream.Position = 0;

        Assert.Equal(Request, await InstallerOwnerTransferFraming.ReadRequestAsync(stream, default));
        Assert.Equal(Offer, await InstallerOwnerTransferFraming.ReadOfferAsync(stream, default));
        Assert.Equal(decision, await InstallerOwnerTransferFraming.ReadDecisionAsync(stream, default));
        Assert.Equal(suffix, stream.ToArray().AsSpan((int)stream.Position).ToArray());
        Assert.True(stream.CanRead);
        Assert.DoesNotContain("authenticationToken", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("whitespace")]
    [InlineData("case")]
    [InlineData("schema")]
    [InlineData("kind")]
    [InlineData("uninstall")]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("escaped")]
    [InlineData("trailing")]
    [InlineData("private")]
    public async Task NoncanonicalOrPrivateFieldsAreRejectedBeforeAnyAuthority(string mutation)
    {
        using var canonical = new MemoryStream();
        await InstallerOwnerTransferFraming.WriteRequestAsync(canonical, Request, default);
        string json = Encoding.UTF8.GetString(canonical.ToArray().AsSpan(4));
        string modified = mutation switch
        {
            "unknown" => json.Replace("}", ",\"extra\":true}", StringComparison.Ordinal),
            "duplicate" => json.Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal),
            "whitespace" => " " + json,
            "case" => json.Replace("\"install\"", "\"Install\"", StringComparison.Ordinal),
            "schema" => json.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            "kind" => json.Replace("\"request\"", "\"offer\"", StringComparison.Ordinal),
            "uninstall" => json.Replace("\"install\"", "\"uninstall\"", StringComparison.Ordinal),
            "null" => json.Replace("\"install\"", "null", StringComparison.Ordinal),
            "missing" => json.Replace("\"operation\":\"install\",", "", StringComparison.Ordinal),
            "escaped" => json.Replace("\"install\"", "\"\\u0069nstall\"", StringComparison.Ordinal),
            "trailing" => json + "{}",
            "private" => json.Replace("}", ",\"previousOwner\":{\"authenticationToken\":\"secret\"}}", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        using MemoryStream frame = Frame(Encoding.UTF8.GetBytes(modified));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => InstallerOwnerTransferFraming.ReadRequestAsync(frame, default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public async Task LengthIsBoundedBeforeAllocationOrBodyRead(int length)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => InstallerOwnerTransferFraming.ReadOfferAsync(stream, default));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void RecoveryCanRetainItsOriginalOperationButCannotChangeReleaseAccountOrSession()
    {
        InstallerOwnerTransferOffer recovery = Offer with { IsRecovery = true, Request = Request with { Operation = InstallerOperation.Repair } };
        recovery.ValidateAgainst(Request, Offer.NextOwnerSid);
        Assert.Throws<InstallerProtocolException>(() => (recovery with { IsRecovery = false }).ValidateAgainst(Request, Offer.NextOwnerSid));
        Assert.Throws<InstallerProtocolException>(() => recovery.ValidateAgainst(Request with { SessionId = new string('f', 64) }, Offer.NextOwnerSid));
        Assert.Throws<InstallerProtocolException>(() => recovery.ValidateAgainst(Request with { ExpectedPackageVersion = "2.0.0.0" }, Offer.NextOwnerSid));
        Assert.Throws<InstallerProtocolException>(() => recovery.ValidateAgainst(Request, Offer.PreviousOwnerSid));
        Assert.Throws<InstallerProtocolException>(() => new InstallerOwnerTransferDecision(Request.SessionId, new string('f', 64), true).ValidateAgainst(Offer));
    }

    [Fact]
    public async Task TruncatedAndCancelledFramesNeverProduceAnOffer()
    {
        using var stream = Frame([123, 125]);
        stream.SetLength(stream.Length - 1);
        await Assert.ThrowsAsync<EndOfStreamException>(() => InstallerOwnerTransferFraming.ReadOfferAsync(stream, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var empty = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallerOwnerTransferFraming.ReadOfferAsync(empty, cancellation.Token));
    }

    private static MemoryStream Frame(byte[] payload)
    {
        byte[] bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return new(bytes);
    }
}
