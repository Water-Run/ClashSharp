/*
 * Mihomo Profile Shape Validator Tests
 * Verifies minimum mihomo profile section validation before import
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoProfileShapeValidatorTests.cs
 * @date: 2026-06-17
 */

using System;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests minimum mihomo profile section validation behavior.</summary>
/// <remarks>
/// Invariants: Tests inspect in-memory configuration text only and never start mihomo.
/// Thread safety: xUnit may run tests concurrently; validator methods are stateless.
/// Side effects: None.
/// </remarks>
public sealed class MihomoProfileShapeValidatorTests
{
    /// <summary>Verifies inline proxy source, proxy groups, and rules pass shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithProxiesGroupsAndRules_Completes()
    {
        const string Configuration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """;

        MihomoProfileShapeValidator.Validate(Configuration);
    }

    /// <summary>Verifies provider-only profiles pass shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithProvidersGroupsAndRules_Completes()
    {
        const string Configuration = """
            proxy-providers:
              airport:
                type: http
                url: https://example.invalid/sub.yaml
            proxy-groups:
              - name: GLOBAL
                type: select
                use:
                  - airport
            rules:
              - MATCH,GLOBAL
            """;

        MihomoProfileShapeValidator.Validate(Configuration);
    }

    /// <summary>Verifies missing routing rules fail shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithoutRules_Throws()
    {
        const string Configuration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            """;

        Assert.Throws<ArgumentException>(() => MihomoProfileShapeValidator.Validate(Configuration));
    }

    /// <summary>Verifies indented pseudo sections are not accepted as top-level sections.</summary>
    [Fact]
    public void Validate_IndentedPseudoSections_Throws()
    {
        const string Configuration = """
            metadata:
              proxies: []
              proxy-groups: []
              rules: []
            """;

        Assert.Throws<ArgumentException>(() => MihomoProfileShapeValidator.Validate(Configuration));
    }
}
