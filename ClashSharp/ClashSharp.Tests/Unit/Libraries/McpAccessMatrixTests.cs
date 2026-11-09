using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Exhaustive access-policy checks: every scope subset against every operation and tool, escalation ordering of the
/// operation requirements, deny-by-default setting writes over the real registry, and consistency of the tool catalog.
/// All enumerations come from the enum and registry definitions so new members are covered automatically.
/// </summary>
public sealed class McpAccessMatrixTests
{
    private static readonly McpAccessScope[] AllFlags =
        [.. Enum.GetValues<McpAccessScope>().Where(static scope => scope != McpAccessScope.None)];

    private static McpAccessScope Union => AllFlags.Aggregate(McpAccessScope.None, static (accumulated, scope) => accumulated | scope);

    /// <summary>Verifies the policy allows an operation exactly when it is enabled and holds every required scope bit.</summary>
    [Fact]
    public void Policy_AllowsExactlyEnabledSupersetsOfTheRequiredScopes_ForEverySubsetAndOperation()
    {
        int subsets = 1 << AllFlags.Length;
        int checkedPairs = 0;
        for (int mask = 0; mask < subsets; mask++)
        {
            McpAccessScope granted = FromMask(mask);
            foreach (bool enabled in new[] { false, true })
            {
                McpAccessPolicy policy = new(enabled, granted);
                foreach (McpOperation operation in Enum.GetValues<McpOperation>())
                {
                    McpAccessScope required = McpAccessRules.RequiredScopes(operation);
                    bool expected = enabled && (granted & required) == required;
                    Assert.Equal(expected, policy.Allows(required));
                    checkedPairs++;
                }

                Assert.False(policy.Allows(McpAccessScope.None));
                Assert.False(policy.Allows((McpAccessScope)(1 << 20)));
                Assert.False(policy.Allows(granted | (McpAccessScope)(1 << 21)));
            }
        }

        Assert.Equal(subsets * 2 * Enum.GetValues<McpOperation>().Length, checkedPairs);
    }

    /// <summary>Verifies granting more scopes never removes a permission (monotonicity) and disabling removes all of them.</summary>
    [Fact]
    public void Policy_IsMonotonicInScopesAndDisabledDeniesEverything()
    {
        int subsets = 1 << AllFlags.Length;
        for (int mask = 0; mask < subsets; mask++)
        {
            McpAccessPolicy smaller = new(true, FromMask(mask));
            foreach (McpAccessScope extra in AllFlags)
            {
                McpAccessPolicy larger = new(true, smaller.Scopes | extra);
                foreach (int probeMask in new[] { 1, 3, 17, 129, 255 })
                {
                    McpAccessScope probe = FromMask(probeMask);
                    Assert.True(!smaller.Allows(probe) || larger.Allows(probe));
                }
            }

            Assert.All(AllFlags, scope => Assert.False(new McpAccessPolicy(false, smaller.Scopes).Allows(scope)));
        }
    }

    /// <summary>Verifies the constructor rejects every scope bit outside the known set and the default policy is deny-all.</summary>
    [Fact]
    public void Policy_RejectsUnknownBitsAndDefaultsToDisabled()
    {
        Assert.Equal(McpAccessPolicy.KnownScopes, Union);
        for (int bit = 8; bit < 31; bit++)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new McpAccessPolicy(true, (McpAccessScope)(1 << bit)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new McpAccessPolicy(false, McpAccessScope.ReadStatus | (McpAccessScope)(1 << bit)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new McpAccessPolicy(true, (McpAccessScope)(-1)));
        Assert.False(McpAccessPolicy.Default.Enabled);
        Assert.False(McpAccessPolicy.Default.Allows(McpAccessScope.ReadStatus));
    }

    /// <summary>Verifies operation requirements: nonempty, known, escalating for sensitive/live variants, and no orphan scope.</summary>
    [Fact]
    public void OperationRequirements_AreKnownEscalatingAndCoverEveryScope()
    {
        Dictionary<McpOperation, McpAccessScope> required = Enum.GetValues<McpOperation>()
            .ToDictionary(static operation => operation, McpAccessRules.RequiredScopes);

        Assert.All(required, pair => Assert.NotEqual(McpAccessScope.None, pair.Value));
        Assert.All(required, pair => Assert.Equal(McpAccessScope.None, pair.Value & ~McpAccessPolicy.KnownScopes));
        Assert.All(AllFlags, scope => Assert.Contains(required, pair => (pair.Value & scope) == scope));
        AssertStrictlyMore(required, McpOperation.ReadSensitiveConfiguration, McpOperation.ReadConfiguration, McpAccessScope.ReadSensitiveConfiguration);
        AssertStrictlyMore(required, McpOperation.ModifyLiveConfiguration, McpOperation.ModifyConfiguration, McpAccessScope.ChangeProxyState);
        AssertStrictlyMore(required, McpOperation.ReadSensitiveHistory, McpOperation.ReadHistoryDetails, McpAccessScope.ReadSensitiveConfiguration);
        Assert.Equal(McpAccessScope.None, required[McpOperation.ChangeProxyState] & (McpAccessScope.ModifyConfiguration | McpAccessScope.ReadSensitiveConfiguration));
        Assert.Equal(
            Enum.GetValues<McpOperation>().Length,
            required.Values.Distinct().Count());

        foreach (int undefined in new[] { -1, 99, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => McpAccessRules.RequiredScopes((McpOperation)undefined));
        }
    }

    /// <summary>Verifies setting writes are deny-by-default: never the agent's own permissions, never sensitive, always at least modify access.</summary>
    [Fact]
    public void SettingWrites_AreDeniedByDefaultAndNeverCoverAgentSelfConfiguration()
    {
        int writable = 0;
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions)
        {
            McpAccessScope? required = McpAccessRules.RequiredSettingWriteScopes(definition.Key);
            if (definition.ApplicationKind == SettingApplicationKind.McpServer || definition.IsSensitive)
            {
                Assert.Null(required);
                continue;
            }

            if (required is null)
            {
                continue;
            }

            writable++;
            Assert.Equal(McpAccessScope.None, required.Value & ~McpAccessPolicy.KnownScopes);
            Assert.Equal(McpAccessScope.ModifyConfiguration, required.Value & McpAccessScope.ModifyConfiguration);
            Assert.Equal(McpAccessScope.None, required.Value & McpAccessScope.ReadSensitiveConfiguration);
            Assert.True(required.Value is McpAccessScope.ModifyConfiguration or (McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState));
        }

        Assert.InRange(writable, 10, SettingsRegistry.Default.Definitions.Count - 3);
        Assert.Null(McpAccessRules.RequiredSettingWriteScopes(new SettingKey("NoSuchSetting")));
        foreach (SettingKey alias in SettingsRegistry.Default.Definitions.SelectMany(static definition => definition.Aliases))
        {
            Assert.Null(McpAccessRules.RequiredSettingWriteScopes(alias));
        }

        Assert.Null(McpAccessRules.RequiredSettingWriteScopes(SettingsRegistry.Keys.McpServerEnabled));
        Assert.Null(McpAccessRules.RequiredSettingWriteScopes(SettingsRegistry.Keys.McpServerPort));
        Assert.Null(McpAccessRules.RequiredSettingWriteScopes(SettingsRegistry.Keys.McpAllowedScopes));
        Assert.Throws<ArgumentNullException>(() => McpAccessRules.RequiredSettingWriteScopes(null!));
    }

    /// <summary>Verifies every network-affecting setting that the agent may write also demands proxy control.</summary>
    [Fact]
    public void NetworkAffectingSettingWrites_RequireProxyControl()
    {
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions)
        {
            McpAccessScope? required = McpAccessRules.RequiredSettingWriteScopes(definition.Key);
            if (required is null)
            {
                continue;
            }

            bool affectsConnectivity = definition.ApplicationKind is SettingApplicationKind.Network or SettingApplicationKind.StartupTask;
            if (affectsConnectivity)
            {
                Assert.True(
                    required.Value.HasFlag(McpAccessScope.ChangeProxyState),
                    $"{definition.Key.Value} changes connectivity or startup behavior and must demand proxy control");
            }
        }
    }

    /// <summary>Verifies the catalog defines every tool id once, with a unique stable name and consistent operation metadata.</summary>
    [Fact]
    public void Catalog_DefinesEveryToolOnceWithConsistentMetadata()
    {
        Assert.Equal(Enum.GetValues<McpToolId>().Order(), McpToolCatalog.All.Select(static tool => tool.Id).Order());
        Assert.Equal(McpToolCatalog.All.Count, McpToolCatalog.All.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (McpToolDefinition tool in McpToolCatalog.All)
        {
            Assert.StartsWith("clashsharp_", tool.Name, StringComparison.Ordinal);
            Assert.Matches("^[a-z_]+$", tool.Name);
            Assert.True(McpToolCatalog.TryGet(tool.Name, out McpToolDefinition? found) && ReferenceEquals(found, tool));
            Assert.Equal(McpAccessRules.RequiredScopes(tool.Operation), tool.RequiredScopes);
            Assert.Equal(
                (tool.RequiredScopes & (McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState)) == 0,
                tool.IsReadOnly);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.False, tool.InputSchema.GetProperty("additionalProperties").ValueKind);
        }

        Assert.False(McpToolCatalog.TryGet(null, out _));
        Assert.False(McpToolCatalog.TryGet(string.Empty, out _));
        Assert.False(McpToolCatalog.TryGet("CLASHSHARP_GET_STATUS", out _));
        Assert.False(McpToolCatalog.TryGet("clashsharp_get_status ", out _));
    }

    /// <summary>Verifies every state-changing tool requires an idempotency identity and no read-only tool accepts one.</summary>
    [Fact]
    public void Catalog_WritesRequireOperationIdentityAndReadsNeverTakeOne()
    {
        foreach (McpToolDefinition tool in McpToolCatalog.All)
        {
            bool hasOperationId = tool.InputSchema.GetProperty("properties").TryGetProperty("operation_id", out _);
            bool required = tool.InputSchema.TryGetProperty("required", out System.Text.Json.JsonElement fields)
                && fields.EnumerateArray().Any(static field => field.GetString() == "operation_id");
            if (tool.IsReadOnly)
            {
                Assert.False(hasOperationId, tool.Name);
            }
            else
            {
                Assert.True(hasOperationId && required, tool.Name);
            }
        }
    }

    /// <summary>Verifies that the tools visible to any scope subset are exactly those whose requirements it satisfies and sensitive tools need extra grants.</summary>
    [Fact]
    public void ToolVisibility_FollowsTheScopeSubsetAndSensitiveToolsNeedExtraGrants()
    {
        int subsets = 1 << AllFlags.Length;
        for (int mask = 0; mask < subsets; mask++)
        {
            McpAccessScope granted = FromMask(mask);
            McpAccessPolicy policy = new(true, granted);
            HashSet<McpToolId> visible = [.. McpToolCatalog.All.Where(tool => policy.Allows(tool.RequiredScopes)).Select(static tool => tool.Id)];

            foreach (McpToolDefinition tool in McpToolCatalog.All)
            {
                Assert.Equal((granted & tool.RequiredScopes) == tool.RequiredScopes, visible.Contains(tool.Id));
            }

            if (!granted.HasFlag(McpAccessScope.ReadSensitiveConfiguration))
            {
                Assert.DoesNotContain(McpToolId.ReadSensitiveProfile, visible);
                Assert.DoesNotContain(McpToolId.ReadSensitiveProfileHistory, visible);
            }

            if (!granted.HasFlag(McpAccessScope.ChangeProxyState))
            {
                Assert.DoesNotContain(McpToolId.SetProxyState, visible);
                Assert.DoesNotContain(McpToolId.SelectProxy, visible);
                Assert.DoesNotContain(McpToolId.ActivateProfile, visible);
            }
        }

        Assert.DoesNotContain(McpToolCatalog.All, tool => new McpAccessPolicy(true, McpAccessScope.None).Allows(tool.RequiredScopes));
        Assert.Equal(McpToolCatalog.All.Count, McpToolCatalog.All.Count(tool => new McpAccessPolicy(true, Union).Allows(tool.RequiredScopes)));
    }

    private static McpAccessScope FromMask(int mask)
    {
        McpAccessScope scopes = McpAccessScope.None;
        for (int index = 0; index < AllFlags.Length; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                scopes |= AllFlags[index];
            }
        }

        return scopes;
    }

    private static void AssertStrictlyMore(
        IReadOnlyDictionary<McpOperation, McpAccessScope> required,
        McpOperation stronger,
        McpOperation weaker,
        McpAccessScope extra)
    {
        Assert.Equal(required[weaker], required[stronger] & required[weaker]);
        Assert.NotEqual(required[weaker], required[stronger]);
        Assert.Equal(extra, required[stronger] & extra);
        Assert.Equal(McpAccessScope.None, required[weaker] & extra);
    }
}
