using System.Text;
using ClashSharp.Model.Rules;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>Contracts of the outer-field split of one native mihomo rule (type, complete payload, action).</summary>
public sealed class MihomoRuleSyntaxContractTests
{
    private const int Seed = 20261109;

    private static readonly string[] OrdinaryTypes =
    [
        "DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD", "GEOSITE", "GEOIP", "IP-CIDR", "IP-CIDR6",
        "IP-SUFFIX", "IP-ASN", "SRC-GEOIP", "SRC-IP-ASN", "SRC-IP-CIDR", "SRC-IP-SUFFIX", "DST-PORT", "SRC-PORT", "IN-PORT",
        "IN-TYPE", "IN-USER", "IN-NAME", "PROCESS-PATH", "PROCESS-PATH-REGEX", "PROCESS-NAME", "PROCESS-NAME-REGEX", "UID",
        "NETWORK", "DSCP", "RULE-SET", "dOmAiN", " DOMAIN ", "FUTURE-RULE-TYPE",
    ];

    private static readonly string[] Payloads =
    [
        "example.com", "a,b", "a,b,c", ",", ",,", "^(ads?|track(er)?)\\.example\\.com$", "^a{1,3}\\.example$", "^[a,b]+$",
        "C:\\Program Files (x86)\\Vendor (beta)\\app.exe", "C:\\Program Files\\App (unbalanced\\app.exe", "(?i)^c:\\\\users\\\\[^\\\\]+\\\\app\\.exe$",
        "192.168.0.0/16", "2001:db8::/32", "1000-2000", "香港,节点", "😀", "a b c", "no-resolve-but-not-last", "x,no-resolve,y", "src-ish",
        "((", "))", "[", "]", "{", "}", "a)b(c", "\\", "\\,", "a\\", "\"quoted,text\"", "'single,quote'", "ünï", "מילה",
    ];

    private static readonly string[] Actions =
    [
        "DIRECT", "PROXY", "REJECT", "REJECT-DROP", "PASS", "Auto Select", "香港 节点 01", "😀", "name-with-dash", "a.b", "(paren)", "[bracket]",
    ];

    private static readonly string[][] Flags = [[], ["no-resolve"], ["src"], ["no-resolve", "src"], ["src", "no-resolve"]];

    private static string Join(string type, string payload, string action, string[] flags) =>
        string.Join(',', new[] { type, payload, action }.Concat(flags));

    [Fact]
    public void OrdinaryRules_EveryTypePayloadActionAndFlagCombinationSplitsBackIntoItsParts()
    {
        int combinations = 0;
        foreach (string type in OrdinaryTypes)
        {
            foreach (string payload in Payloads)
            {
                foreach (string action in Actions)
                {
                    foreach (string[] flags in Flags)
                    {
                        string text = Join(type, payload, action, flags);
                        Assert.True(MihomoRuleSyntax.TryParse(text, out ParsedMihomoRule rule), text);
                        Assert.Equal(type.Trim(), rule.Type);
                        Assert.Equal(payload.Trim(), rule.Payload);
                        Assert.Equal(action, rule.Action);
                        combinations++;
                    }
                }
            }
        }

        Assert.True(combinations > 20_000);
    }

    [Fact]
    public void Match_HasAnAsteriskPayloadAndNeverAcceptsAPayload()
    {
        foreach (string action in Actions)
        {
            Assert.True(MihomoRuleSyntax.TryParse("MATCH," + action, out ParsedMihomoRule rule));
            Assert.Equal(("MATCH", "*", action), (rule.Type, rule.Payload, rule.Action));
            Assert.True(MihomoRuleSyntax.TryParse("match," + action + ",no-resolve", out ParsedMihomoRule withFlag));
            Assert.Equal(action, withFlag.Action);
            Assert.Equal("*", withFlag.Payload);
        }

        Assert.False(MihomoRuleSyntax.TryParse("DOMAIN,PROXY", out _));
        Assert.False(MihomoRuleSyntax.TryParse("DOMAIN,example.com,no-resolve", out _));
    }

    private static string RandomCondition(Random random, int depth)
    {
        string[] leafTypes = ["DOMAIN", "DOMAIN-SUFFIX", "NETWORK", "DST-PORT", "DOMAIN-REGEX", "PROCESS-NAME", "IP-CIDR"];
        string[] leafPayloads = ["example.com", "UDP", "443", "^a(b|c){1,3}$", "^[a,b]+$", "x.exe", "10.0.0.0/8", "a\\,b", "[)]", "\\)"];
        if (depth == 0 || random.Next(3) == 0)
        {
            return "(" + leafTypes[random.Next(leafTypes.Length)] + "," + leafPayloads[random.Next(leafPayloads.Length)] + ")";
        }

        string[] connectives = ["AND", "OR", "NOT"];
        string kind = connectives[random.Next(connectives.Length)];
        int arity = kind == "NOT" ? 1 : random.Next(2, 4);
        List<string> children = [];
        for (int index = 0; index < arity; index++)
        {
            children.Add(RandomCondition(random, depth - 1));
        }

        return "(" + kind + ",(" + string.Join(',', children) + "))";
    }

    [Fact]
    public void LogicalRules_NestedConditionsKeepTheirCommasAndParenthesesInsideTheOneRawPayload()
    {
        Random random = new(Seed);
        int parsed = 0;
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            string kind = new[] { "AND", "OR", "NOT", "SUB-RULE" }[random.Next(4)];
            string payload = kind == "NOT" || kind == "SUB-RULE"
                ? RandomCondition(random, random.Next(0, 4))
                : "(" + string.Join(',', Enumerable.Range(0, random.Next(2, 4)).Select(_ => RandomCondition(random, random.Next(0, 3)))) + ")";
            string action = Actions[random.Next(Actions.Length)];
            string[] flags = Flags[random.Next(Flags.Length)];
            string text = Join(kind, payload, action, flags);

            Assert.True(MihomoRuleSyntax.TryParse(text, out ParsedMihomoRule rule), text);
            Assert.Equal(kind, rule.Type);
            Assert.Equal(payload, rule.Payload);
            Assert.Equal(action, rule.Action);
            parsed++;
        }

        Assert.Equal(3000, parsed);
    }

    [Theory]
    [InlineData("AND,((DOMAIN,a),(NETWORK,UDP),REJECT")]
    [InlineData("AND,((DOMAIN,a),(NETWORK,UDP))),REJECT")]
    [InlineData("OR,(DOMAIN,a],REJECT")]
    [InlineData("NOT,(DOMAIN,a)")]
    [InlineData("AND,((DOMAIN,a)),")]
    [InlineData(",DOMAIN,example.com,PROXY")]
    [InlineData("DOMAIN")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData("DOMAIN,example.com,")]
    [InlineData("DOMAIN,example.com, ")]
    [InlineData("AND,((DOMAIN,[a),(NETWORK,UDP)),REJECT")]
    public void MalformedRules_AreRejectedInsteadOfBeingSplitAtTheWrongComma(string text)
    {
        Assert.False(MihomoRuleSyntax.TryParse(text, out ParsedMihomoRule rule), text);
        Assert.Equal(default, rule);
    }

    [Fact]
    public void ArbitraryText_NeverThrowsAndEveryAcceptedRuleHasNonBlankTypeAndAction()
    {
        string[] tokens = [",", "(", ")", "[", "]", "{", "}", "\\", "AND", "OR", "NOT", "SUB-RULE", "MATCH", "DOMAIN", "no-resolve", "src", " ", "a", "x.y", "😀", "香港", "\"", "'", "\u0000", "\n"];
        Random random = new(Seed + 1);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            StringBuilder text = new();
            int count = random.Next(0, 30);
            for (int index = 0; index < count; index++)
            {
                text.Append(tokens[random.Next(tokens.Length)]);
            }

            if (MihomoRuleSyntax.TryParse(text.ToString(), out ParsedMihomoRule rule))
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Type));
                Assert.False(string.IsNullOrWhiteSpace(rule.Action));
                Assert.DoesNotContain(',', rule.Type);
            }
            else
            {
                Assert.Equal(default, rule);
            }
        }

        Assert.Throws<ArgumentNullException>(() => MihomoRuleSyntax.TryParse(null!, out _));
    }

    [Fact]
    public void TrailingFlagsAreRemovedOnlyFromTheEndAndOnlyWhenTheyAreWholeTokens()
    {
        Assert.True(MihomoRuleSyntax.TryParse("GEOIP,CN,DIRECT,no-resolve", out ParsedMihomoRule one));
        Assert.Equal("DIRECT", one.Action);
        Assert.True(MihomoRuleSyntax.TryParse("GEOIP,CN,DIRECT,src,no-resolve", out ParsedMihomoRule two));
        Assert.Equal("DIRECT", two.Action);
        Assert.True(MihomoRuleSyntax.TryParse("GEOIP,CN,DIRECT,no-resolve-ish", out ParsedMihomoRule three));
        Assert.Equal("no-resolve-ish", three.Action);
        Assert.Equal("CN,DIRECT", three.Payload);
        Assert.True(MihomoRuleSyntax.TryParse("DOMAIN,no-resolve,PROXY", out ParsedMihomoRule four));
        Assert.Equal("no-resolve", four.Payload);
        Assert.True(MihomoRuleSyntax.TryParse("DOMAIN, example.com , PROXY , no-resolve ", out ParsedMihomoRule five));
        Assert.Equal(("DOMAIN", "example.com", "PROXY"), (five.Type, five.Payload, five.Action));
    }
}
