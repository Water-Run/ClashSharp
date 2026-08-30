using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineAssociationTests
{
    [Fact]
    public void CanonicalAssociationRoundTripsAsExactCompactJson()
    {
        InstallerMachineAssociation expected = Association();

        byte[] bytes = InstallerMachineAssociationCodec.Serialize(expected);
        InstallerMachineAssociation actual = InstallerMachineAssociationCodec.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(
            $$"""{"schemaVersion":1,"ownerSid":"{{InstallerTestData.Sid}}","authenticationToken":"{{InstallerTestData.Hash}}"}""",
            Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,}")]
    [InlineData("{/*comment*/\"schemaVersion\":1}")]
    public void IncompleteOrNoncanonicalJsonIsRejected(string json) =>
        AssertDiagnostic(
            () => InstallerMachineAssociationCodec.Parse(Encoding.UTF8.GetBytes(json)),
            "installer.machine.association_json_invalid");

    [Fact]
    public void UnknownDuplicateAndCaseChangedPropertiesAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Replace(
            "\"ownerSid\":",
            "\"unexpected\":true,\"ownerSid\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"ownerSid\":",
            $"\"ownerSid\":\"{InstallerTestData.Sid}\",\"ownerSid\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"authenticationToken\"",
            "\"AuthenticationToken\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void WhitespaceAndPropertyReorderingAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Insert(1, " "));
        AssertJsonInvalid(canonical.Replace(
            $"{{\"schemaVersion\":1,\"ownerSid\":\"{InstallerTestData.Sid}\",",
            $"{{\"ownerSid\":\"{InstallerTestData.Sid}\",\"schemaVersion\":1,",
            StringComparison.Ordinal));
    }

    [Fact]
    public void PropertyTypesAreStrict()
    {
        JsonObject schema = JsonNode.Parse(CanonicalJson())!.AsObject();
        schema["schemaVersion"] = "1";
        AssertJsonInvalid(schema.ToJsonString());

        JsonObject owner = JsonNode.Parse(CanonicalJson())!.AsObject();
        owner["ownerSid"] = null;
        AssertJsonInvalid(owner.ToJsonString());

        JsonObject token = JsonNode.Parse(CanonicalJson())!.AsObject();
        token["authenticationToken"] = 1;
        AssertJsonInvalid(token.ToJsonString());
    }

    [Fact]
    public void SchemaOwnerTokenAndSizeAreIndependentlyValidated()
    {
        AssertDiagnostic(
            () => (Association() with { SchemaVersion = 2 }).Validate(),
            "installer.machine.association_schema_invalid");
        AssertDiagnostic(
            () => (Association() with { OwnerSid = "S-1-5-18" }).Validate(),
            "installer.request.target_sid_invalid");
        AssertDiagnostic(
            () => (Association() with
            {
                AuthenticationToken = InstallerTestData.Hash.ToUpperInvariant(),
            }).Validate(),
            "installer.machine.authentication_token_invalid");
        AssertDiagnostic(
            () => InstallerMachineAssociationCodec.Parse([]),
            "installer.machine.association_size_invalid");
        AssertDiagnostic(
            () => InstallerMachineAssociationCodec.Parse(
                new byte[InstallerMachineAssociationCodec.MaximumAssociationBytes + 1]),
            "installer.machine.association_size_invalid");
    }

    [Fact]
    public void SameOwnerProvisionRetainsExistingCredential()
    {
        InstallerMachineAssociation existing = Association();

        InstallerMachineProvisionDecision decision =
            InstallerMachineOwnershipPolicy.DecideProvision(
                InstallerTestData.Request(),
                InstallerMachineAssociationObservation.Valid(existing),
                serviceExists: true,
                machineResidueExists: true,
                freshAuthenticationToken: InstallerTestData.OtherHash);

        Assert.Equal(InstallerMachineProvisionDisposition.Provision, decision.Disposition);
        Assert.Equal(existing.AuthenticationToken, decision.AuthenticationToken);
    }

    [Fact]
    public void OrdinaryInstallCannotTakeOverForeignInvalidOrOwnerlessResidue()
    {
        InstallerRequest request = InstallerTestData.Request();
        InstallerMachineAssociation foreign = Association() with
        {
            OwnerSid = "S-1-5-21-100-200-300-1002",
        };
        InstallerMachineAssociationObservation[] observations =
        [
            InstallerMachineAssociationObservation.Valid(foreign),
            InstallerMachineAssociationObservation.Invalid(),
            InstallerMachineAssociationObservation.Missing(),
            InstallerMachineAssociationObservation.Missing(),
        ];
        (bool Service, bool Residue)[] residue =
        [
            (false, false),
            (false, false),
            (true, false),
            (false, true),
        ];

        for (int index = 0; index < observations.Length; index++)
        {
            InstallerMachineProvisionDecision decision =
                InstallerMachineOwnershipPolicy.DecideProvision(
                    request,
                    observations[index],
                    residue[index].Service,
                    residue[index].Residue,
                    InstallerTestData.OtherHash);
            Assert.Equal(
                InstallerMachineProvisionDisposition.RequiresExplicitRepair,
                decision.Disposition);
            Assert.Null(decision.AuthenticationToken);
        }
    }

    [Fact]
    public void CleanInstallAndExplicitRepairUseFreshCredential()
    {
        InstallerMachineProvisionDecision clean =
            InstallerMachineOwnershipPolicy.DecideProvision(
                InstallerTestData.Request(),
                InstallerMachineAssociationObservation.Missing(),
                serviceExists: false,
                machineResidueExists: false,
                freshAuthenticationToken: InstallerTestData.OtherHash);
        InstallerMachineProvisionDecision repair =
            InstallerMachineOwnershipPolicy.DecideProvision(
                InstallerTestData.Request(
                    InstallerOperation.Repair,
                    allowReassociation: true),
                InstallerMachineAssociationObservation.Invalid(),
                serviceExists: true,
                machineResidueExists: true,
                freshAuthenticationToken: InstallerTestData.OtherHash);

        Assert.Equal(InstallerTestData.OtherHash, clean.AuthenticationToken);
        Assert.Equal(InstallerTestData.OtherHash, repair.AuthenticationToken);
        Assert.Equal(InstallerMachineProvisionDisposition.Provision, clean.Disposition);
        Assert.Equal(InstallerMachineProvisionDisposition.Provision, repair.Disposition);
    }

    [Fact]
    public void OwnerCheckedRemovalNeverDeletesMissingInvalidOrForeignResources()
    {
        InstallerMachineAssociation sameOwner = Association();
        InstallerMachineAssociation foreign = sameOwner with
        {
            OwnerSid = "S-1-5-21-100-200-300-1002",
        };

        Assert.True(InstallerMachineOwnershipPolicy.MayRemove(
            InstallerTestData.Sid,
            InstallerMachineAssociationObservation.Valid(sameOwner)));
        Assert.False(InstallerMachineOwnershipPolicy.MayRemove(
            InstallerTestData.Sid,
            InstallerMachineAssociationObservation.Valid(foreign)));
        Assert.False(InstallerMachineOwnershipPolicy.MayRemove(
            InstallerTestData.Sid,
            InstallerMachineAssociationObservation.Missing()));
        Assert.False(InstallerMachineOwnershipPolicy.MayRemove(
            InstallerTestData.Sid,
            InstallerMachineAssociationObservation.Invalid()));
    }

    [Fact]
    public void InvalidObservationDecisionAndOperationAreRejected()
    {
        var inconsistent = new InstallerMachineAssociationObservation(
            InstallerMachineAssociationStatus.Valid,
            null);
        AssertDiagnostic(
            inconsistent.Validate,
            "installer.machine.association_observation_invalid");

        var inconsistentDecision = new InstallerMachineProvisionDecision(
            InstallerMachineProvisionDisposition.Provision,
            null);
        AssertDiagnostic(
            inconsistentDecision.Validate,
            "installer.machine.provision_decision_invalid");

        AssertDiagnostic(
            () => InstallerMachineOwnershipPolicy.DecideProvision(
                InstallerTestData.Request(InstallerOperation.Uninstall),
                InstallerMachineAssociationObservation.Missing(),
                serviceExists: false,
                machineResidueExists: false,
                freshAuthenticationToken: InstallerTestData.OtherHash),
            "installer.machine.provision_operation_invalid");
    }

    [Fact]
    public void GeneratedAuthenticationTokensAreCanonicalAndDistinct()
    {
        string first = InstallerMachineAssociation.GenerateAuthenticationToken();
        string second = InstallerMachineAssociation.GenerateAuthenticationToken();

        InstallerProtocolValidation.ValidateLowerHex256(first, "invalid");
        InstallerProtocolValidation.ValidateLowerHex256(second, "invalid");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ServicePipeDerivationMatchesTheExistingRustAndCSharpProtocol()
    {
        InstallerMachineAssociation association = InstallerMachineAssociation.Create(
            InstallerTestData.Sid,
            InstallerTestData.OtherHash);

        string pipeName = association.BuildServicePipeName();

        Assert.Equal(
            "ClashSharp.Mihomo.889ca1a80c0bd15fb9c7cc8c51e2753d",
            pipeName);
        Assert.DoesNotContain(association.OwnerSid, pipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(
            association.AuthenticationToken,
            pipeName,
            StringComparison.Ordinal);
        Assert.Equal(pipeName, association.BuildServicePipeName());
        Assert.NotEqual(
            pipeName,
            (association with { OwnerSid = "S-1-5-21-100-200-300-1002" })
                .BuildServicePipeName());
        Assert.NotEqual(
            pipeName,
            (association with { AuthenticationToken = InstallerTestData.Hash })
                .BuildServicePipeName());
    }

    private static InstallerMachineAssociation Association() =>
        InstallerMachineAssociation.Create(InstallerTestData.Sid, InstallerTestData.Hash);

    private static string CanonicalJson() => Encoding.UTF8.GetString(
        InstallerMachineAssociationCodec.Serialize(Association()));

    private static void AssertJsonInvalid(string json) => AssertDiagnostic(
        () => InstallerMachineAssociationCodec.Parse(Encoding.UTF8.GetBytes(json)),
        "installer.machine.association_json_invalid");

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
