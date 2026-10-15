using ClashSharp.Agent;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Agent;

/// <summary>Verifies least-privilege grants and prevents indirect connectivity changes through generic settings.</summary>
public sealed class McpAccessPolicyTests
{
    private static readonly IReadOnlyDictionary<McpOperation, McpAccessScope> Requirements =
        new Dictionary<McpOperation, McpAccessScope>
        {
            [McpOperation.ReadStatus] = McpAccessScope.ReadStatus,
            [McpOperation.ReadConfiguration] = McpAccessScope.ReadConfiguration,
            [McpOperation.ReadSensitiveConfiguration] = McpAccessScope.ReadConfiguration | McpAccessScope.ReadSensitiveConfiguration,
            [McpOperation.ModifyConfiguration] = McpAccessScope.ModifyConfiguration,
            [McpOperation.ModifyLiveConfiguration] = McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState,
            [McpOperation.ReadDiagnostics] = McpAccessScope.ReadDiagnostics,
            [McpOperation.ReadHistorySummary] = McpAccessScope.ReadHistorySummary,
            [McpOperation.ReadHistoryDetails] = McpAccessScope.ReadHistoryDetails,
            [McpOperation.ReadSensitiveHistory] = McpAccessScope.ReadHistoryDetails | McpAccessScope.ReadSensitiveConfiguration,
            [McpOperation.ChangeProxyState] = McpAccessScope.ChangeProxyState,
        };

    /// <summary>The initial user experience cannot start a server or authorize configuration, history, or proxy changes.</summary>
    [Fact]
    public void Default_DisablesEndpointAndPreselectsOnlyStatusRead()
    {
        Assert.False(McpAccessPolicy.Default.Enabled);
        Assert.Equal(McpAccessScope.ReadStatus, McpAccessPolicy.Default.Scopes);
        foreach (McpOperation operation in Enum.GetValues<McpOperation>())
        {
            Assert.False(McpAccessPolicy.Default.Allows(McpAccessRules.RequiredScopes(operation)));
        }
    }

    /// <summary>Every grant combination must satisfy all required bits; endpoint disablement overrides every grant.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllGrantCombinations_PreserveIndependentOperationBoundaries(bool enabled)
    {
        Assert.Equal(Enum.GetValues<McpOperation>().Order(), Requirements.Keys.Order());
        for (int mask = 0; mask <= (int)McpAccessPolicy.KnownScopes; mask++)
        {
            McpAccessPolicy policy = new(enabled, (McpAccessScope)mask);
            foreach ((McpOperation operation, McpAccessScope required) in Requirements)
            {
                bool expected = enabled && ((McpAccessScope)mask & required) == required;
                Assert.Equal(expected, policy.Allows(McpAccessRules.RequiredScopes(operation)));
            }
        }
    }

    /// <summary>Changing network-affecting settings requires proxy control even when general configuration editing is granted.</summary>
    [Theory]
    [InlineData("CurrentMode")]
    [InlineData("ActiveProfileId")]
    [InlineData("TransparentProxyEnabled")]
    [InlineData("MixedPort")]
    [InlineData("LaunchAtStartupEnabled")]
    [InlineData("RestoreProxyOnExit")]
    [InlineData("CheckStaleProxyOnStartup")]
    [InlineData("StartupConflictCheckEnabled")]
    [InlineData("StartupBehaviorMode")]
    [InlineData("CloseBehaviorMode")]
    public void ConfigurationEditing_CannotBypassProxyControl(string key)
    {
        McpAccessScope required = Assert.IsType<McpAccessScope>(McpAccessRules.RequiredSettingWriteScopes(new(key)));
        McpAccessPolicy configurationOnly = new(true, McpAccessScope.ModifyConfiguration);
        McpAccessPolicy proxyOnly = new(true, McpAccessScope.ChangeProxyState);
        McpAccessPolicy both = new(true, McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState);

        Assert.False(configurationOnly.Allows(required));
        Assert.False(proxyOnly.Allows(required));
        Assert.True(both.Allows(required));
    }

    /// <summary>Agent-specific controls and trigger activation cannot grant new authority through generic setting writes.</summary>
    [Theory]
    [InlineData("McpServerEnabled")]
    [InlineData("McpAccessScopes")]
    [InlineData("McpServerPort")]
    [InlineData("McpAccessToken")]
    [InlineData("TriggersEnabled")]
    [InlineData("MainlandChinaDisplayEnabled")]
    [InlineData("FutureProxyControl")]
    [InlineData("currentmode")]
    public void GenericWrites_RejectAgentControlsIndirectActivationAliasesAndUnknownKeys(string key)
    {
        Assert.Null(McpAccessRules.RequiredSettingWriteScopes(new(key)));
    }

    /// <summary>Changing presentation units remains available without permission to affect connectivity.</summary>
    [Fact]
    public void ConfigurationEditing_CanChangeApprovedDisplaySettingsWithoutProxyControl()
    {
        McpAccessPolicy policy = new(true, McpAccessScope.ModifyConfiguration);
        McpAccessScope required = Assert.IsType<McpAccessScope>(
            McpAccessRules.RequiredSettingWriteScopes(SettingsRegistry.Keys.RateUnit));

        Assert.True(policy.Allows(required));
        Assert.False(policy.Allows(McpAccessRules.RequiredScopes(McpOperation.ChangeProxyState)));
    }

    /// <summary>Sensitive-history access requires both the historical-content and sensitive-configuration grants.</summary>
    [Fact]
    public void SensitiveReadGrant_DoesNotImplyConfigurationOrHistoryAccess()
    {
        McpAccessPolicy secretsOnly = new(true, McpAccessScope.ReadSensitiveConfiguration);
        McpAccessPolicy configuration = new(true, McpAccessScope.ReadSensitiveConfiguration | McpAccessScope.ReadConfiguration);
        McpAccessPolicy history = new(true, McpAccessScope.ReadSensitiveConfiguration | McpAccessScope.ReadHistoryDetails);

        Assert.False(secretsOnly.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveConfiguration)));
        Assert.False(secretsOnly.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveHistory)));
        Assert.True(configuration.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveConfiguration)));
        Assert.False(configuration.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveHistory)));
        Assert.True(history.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveHistory)));
        Assert.False(history.Allows(McpAccessRules.RequiredScopes(McpOperation.ReadSensitiveConfiguration)));
    }

    /// <summary>Unknown grant bits cannot be persisted or interpreted as authorization.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1 << 8)]
    [InlineData(1 << 30)]
    public void UnknownScopes_AreRejectedAndNeverAuthorized(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpAccessPolicy(true, (McpAccessScope)value));
        Assert.False(new McpAccessPolicy(true, McpAccessPolicy.KnownScopes).Allows((McpAccessScope)value));
    }

    /// <summary>An operation cannot opt out of permission checking by submitting an empty or undefined requirement.</summary>
    [Fact]
    public void EmptyOrUnknownOperation_CannotAcquireAuthority()
    {
        Assert.False(new McpAccessPolicy(true, McpAccessPolicy.KnownScopes).Allows(McpAccessScope.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpAccessRules.RequiredScopes((McpOperation)999));
    }
}
