using System.Text;
using ClashSharp.Service;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests the bounded, Installer-owned service association read contract.</summary>
public sealed class MihomoServiceAssociationReaderTests : IDisposable
{
    private const string CurrentSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ClashSharp-association-{Guid.NewGuid():N}");

    public MihomoServiceAssociationReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void Read_WhenAssociationIsValid_ReDerivesProtocolPipeName()
    {
        string path = WriteAssociation(ValidJson(CurrentSid, Token));

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        Assert.True(endpoint.IsProvisioned);
        Assert.Null(endpoint.ProvisioningFailureCode);
        Assert.Equal(CurrentSid, endpoint.UserSid);
        Assert.Equal(Token, endpoint.AuthenticationToken);
        Assert.Equal(MihomoServiceIpcProtocol.BuildPipeName(CurrentSid, Token), endpoint.PipeName);
    }

    [Fact]
    public void Read_WhenAssociationIsMissing_ReturnsFixedMissingSentinel()
    {
        string path = Path.Combine(_temporaryDirectory, "missing.json");

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        AssertSentinel(endpoint, MihomoServiceIpcEndpoint.AssociationMissingCode);
    }

    [Fact]
    public void Read_WhenOwnerDoesNotMatchCurrentUser_ReturnsOwnerMismatchSentinel()
    {
        string path = WriteAssociation(ValidJson(OtherSid, Token));

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        AssertSentinel(endpoint, MihomoServiceIpcEndpoint.OwnerMismatchCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":\"not-a-sid\",\"authenticationToken\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}")]
    [InlineData("{\"schemaVersion\":2,\"ownerSid\":\"S-1-5-18\",\"authenticationToken\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":\"S-1-5-18\",\"authenticationToken\":\"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789\"}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":\"S-1-5-18\",\"authenticationToken\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\",\"pipeName\":\"attacker-controlled\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"ownerSid\":\"S-1-5-18\",\"authenticationToken\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}")]
    public void Read_WhenSchemaOrCredentialIsNotCanonical_ReturnsInvalidSentinel(string json)
    {
        string path = WriteAssociation(json);

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        AssertSentinel(endpoint, MihomoServiceIpcEndpoint.AssociationInvalidCode);
    }

    [Fact]
    public void Read_WhenAssociationExceedsFourKiB_ReturnsInvalidSentinel()
    {
        string path = Path.Combine(_temporaryDirectory, "association.json");
        File.WriteAllBytes(
            path,
            Encoding.UTF8.GetBytes(new string('x', MihomoServiceAssociationReader.MaximumAssociationBytes + 1)));

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        AssertSentinel(endpoint, MihomoServiceIpcEndpoint.AssociationInvalidCode);
    }

    [Fact]
    public void Read_WhenPathIsDirectory_ReturnsInvalidSentinel()
    {
        string path = Path.Combine(_temporaryDirectory, "association.json");
        Directory.CreateDirectory(path);

        MihomoServiceIpcEndpoint endpoint = MihomoServiceAssociationReader.Read(path, CurrentSid);

        AssertSentinel(endpoint, MihomoServiceIpcEndpoint.AssociationInvalidCode);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteAssociation(string json)
    {
        string path = Path.Combine(_temporaryDirectory, "association.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string ValidJson(string ownerSid, string token)
    {
        return $$"""
            {"schemaVersion":1,"ownerSid":"{{ownerSid}}","authenticationToken":"{{token}}"}
            """;
    }

    private static void AssertSentinel(
        MihomoServiceIpcEndpoint endpoint,
        string expectedFailureCode)
    {
        Assert.False(endpoint.IsProvisioned);
        Assert.Equal(expectedFailureCode, endpoint.ProvisioningFailureCode);
        Assert.Equal("S-1-0-0", endpoint.UserSid);
        Assert.Equal(new string('0', 64), endpoint.AuthenticationToken);
        Assert.Equal("ClashSharp.Mihomo.Unprovisioned", endpoint.PipeName);
    }
}
