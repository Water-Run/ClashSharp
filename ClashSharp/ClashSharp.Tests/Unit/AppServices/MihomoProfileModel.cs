using System.Globalization;
using System.Text;
using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.AppServices;

internal enum YamlStyle
{
    Block,
    Flow,
    Mixed,
}

internal enum LineEnding
{
    Lf,
    CrLf,
    Cr,
}

internal sealed record NodeSpec(string Name, string? Type, string? Server, string? Port);

internal sealed record ProviderSpec(string Name, string? Type, string? Url);

/// <summary>A synthetic profile with a known meaning that can be rendered as many different YAML texts.</summary>
internal sealed record ProfileModel(
    IReadOnlyList<NodeSpec> Nodes,
    IReadOnlyList<ProviderSpec> Providers,
    IReadOnlyList<string> Rules);

internal sealed record RenderOptions(
    YamlStyle Style,
    LineEnding Ending,
    bool Bom,
    bool Comments,
    int Indent,
    int QuoteSeed,
    bool DocumentMarker = false,
    string Poison = "",
    bool SingleQuotesOnly = false);

internal static class MihomoProfileModelGenerator
{
    private static readonly string[] NameAtoms =
    [
        "HK", "Hong Kong", "香港", "JP", "Tokyo", "日本", "US", "Los Angeles", "SG", "Singapore", "新加坡", "TW", "台湾",
        "DE", "Germany", "UK", "London", "KR", "Seoul", "RU", "Moscow", "BR", "CA", "AU", "node", "Node", "relay",
        "01", "02", "7", "x", "premium", "IEPL", "BGP", "0.5x", "-", "_", ".", " ", "|", "[", "]", "(", ")", "😀", "🇭🇰",
        "עברית", "مرحبا", "é", "ß", "#", ":", "'", "\"", "\\", ",", "{", "}", "&", "*", "!", "@", "%", "=", "?", "`",
    ];

    public static readonly string[] NodeTypes =
    [
        "ss", "ssr", "vmess", "vless", "trojan", "hysteria", "hysteria2", "tuic", "wireguard", "socks5", "http",
        "snell", "anytls", "mieru", "ssh", "direct", "dns", "masque", "sudoku", "ShadowSocks", "VMESS", "未知",
    ];

    private static readonly string[] Hosts =
    [
        "example.com", "hk-01.provider.example", "203.0.113.7", "node.jp.example", "xn--mnchen-3ya.example", "localhost",
        "us.relay.example.org", "[2001:db8::1]", "2001:db8::2", "singapore.example",
    ];

    private static readonly string[] Ports = ["443", "80", "1", "65535", "0", "65536", "-1", "abc", "443.0", " 8443 ", "+443", "99999999999", "٤٤٣", "", "0x1bb"];

    private static readonly string[] ProviderTypes = ["http", "file", "inline", "HTTP", "dns", "未知"];

    private static readonly string[] Urls =
    [
        "https://sub.example.com/path?token=AbC", "http://203.0.113.9:8080/p.yaml", "https://hk.provider.example/x",
        "https://[2001:db8::5]/sub", "not a url", "", "ftp://files.example/p.yaml", "https://jp.example/%E8%AE%A2",
    ];

    public static readonly string[] RuleSamples =
    [
        "DOMAIN,example.com,PROXY",
        "DOMAIN-SUFFIX,google.com,PROXY",
        "DOMAIN-KEYWORD,ads,REJECT",
        "DOMAIN-REGEX,^(ads?|track(er)?)\\.example\\.com$,REJECT",
        "DOMAIN-REGEX,^a{1,3}\\.example$,REJECT",
        "GEOIP,CN,DIRECT,no-resolve",
        "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
        "IP-CIDR6,2001:db8::/32,DIRECT",
        "SRC-IP-CIDR,10.0.0.0/8,DIRECT",
        "SRC-PORT,1000-2000,DIRECT",
        "DST-PORT,443,PROXY",
        "PROCESS-NAME,chrome.exe,PROXY",
        "PROCESS-PATH,C:\\Program Files (x86)\\Vendor (beta)\\app.exe,DIRECT",
        "PROCESS-PATH-REGEX,(?i)^c:\\\\users\\\\[^\\\\]+\\\\app\\.exe$,DIRECT",
        "RULE-SET,telegram,PROXY,no-resolve",
        "NETWORK,UDP,REJECT",
        "AND,((DOMAIN,example.com),(NETWORK,UDP)),REJECT",
        "OR,((DOMAIN-SUFFIX,a.com),(DOMAIN-SUFFIX,b.com)),PROXY",
        "NOT,((DOMAIN,example.com)),DIRECT",
        "AND,((OR,((DOMAIN,a),(DOMAIN,b))),(NOT,((NETWORK,TCP)))),REJECT",
        "SUB-RULE,(NETWORK,TCP),my-sub",
        "AND,((DOMAIN-REGEX,^[a,b]+$),(DST-PORT,80)),REJECT",
        "MATCH,DIRECT",
        "MATCH,PROXY",
        "DOMAIN,example.com,Auto Select",
        "DOMAIN,example.com,香港 节点 01",
    ];

    public static string RandomName(Random random)
    {
        int count = random.Next(1, 6);
        StringBuilder name = new();
        for (int index = 0; index < count; index++)
        {
            name.Append(NameAtoms[random.Next(NameAtoms.Length)]);
        }

        return name.ToString();
    }

    public static ProfileModel RandomModel(Random random, bool safeAlphabet)
    {
        List<NodeSpec> nodes = [];
        int nodeCount = random.Next(0, 9);
        for (int index = 0; index < nodeCount; index++)
        {
            string name = safeAlphabet ? SafeName(random) : RandomName(random);
            nodes.Add(new NodeSpec(
                name,
                random.Next(8) == 0 ? null : NodeTypes[random.Next(NodeTypes.Length)],
                random.Next(8) == 0 ? null : Hosts[random.Next(Hosts.Length)],
                random.Next(6) == 0 ? null : Ports[random.Next(Ports.Length)]));
        }

        List<ProviderSpec> providers = [];
        int providerCount = random.Next(0, 4);
        for (int index = 0; index < providerCount; index++)
        {
            providers.Add(new ProviderSpec(
                (safeAlphabet ? SafeName(random) : RandomName(random)) + index.ToString(CultureInfo.InvariantCulture),
                random.Next(6) == 0 ? null : ProviderTypes[random.Next(ProviderTypes.Length)],
                random.Next(5) == 0 ? null : Urls[random.Next(Urls.Length)]));
        }

        List<string> rules = [];
        int ruleCount = random.Next(0, 12);
        for (int index = 0; index < ruleCount; index++)
        {
            rules.Add(RuleSamples[random.Next(RuleSamples.Length)]);
        }

        return new ProfileModel(nodes, providers, rules);
    }

    private static readonly string[] SafeAtoms =
    [
        "HK", "Hong Kong", "香港", "JP", "Tokyo", "日本", "US", "SG", "新加坡", "node", "relay", "01", "premium", "BGP", "-", "_", ".", "😀",
        "עברית", "é",
    ];

    private static string SafeName(Random random)
    {
        StringBuilder name = new();
        name.Append(SafeAtoms[random.Next(SafeAtoms.Length)]);
        int more = random.Next(0, 3);
        for (int index = 0; index < more; index++)
        {
            name.Append(' ').Append(SafeAtoms[random.Next(SafeAtoms.Length)]);
        }

        return name.ToString();
    }

    // -------------------------------------------------------------------- rendering

    public static string Render(ProfileModel model, RenderOptions options, Random random)
    {
        string eol = "\n";
        StringBuilder text = new();
        if (options.DocumentMarker)
        {
            text.Append("---\n");
        }

        if (options.Comments)
        {
            text.Append("# generated profile\n");
        }

        text.Append("mixed-port: 7890\nallow-lan: false\n");
        if (model.Nodes.Count > 0 || random.Next(2) == 0)
        {
            text.Append(model.Nodes.Count == 0 ? "proxies: []\n" : "proxies:\n");
            foreach (NodeSpec node in model.Nodes)
            {
                AppendNode(text, node, options, random);
            }
        }

        text.Append("proxy-groups:\n  - name: PROXY\n    type: select\n    proxies: [DIRECT]\n");
        if (model.Providers.Count > 0)
        {
            text.Append("proxy-providers:\n");
            foreach (ProviderSpec provider in model.Providers)
            {
                AppendProvider(text, provider, options);
            }
        }

        if (options.Comments)
        {
            text.Append("\n# rules follow\n");
        }

        text.Append(model.Rules.Count == 0 ? "rules: []\n" : "rules:\n");
        foreach (string rule in model.Rules)
        {
            text.Append(new string(' ', options.Indent)).Append("- ").Append(Q(options, rule.Length, rule, force: true)).Append('\n');
        }

        text.Append(options.Poison);
        string result = text.ToString();
        result = options.Ending switch
        {
            LineEnding.CrLf => result.Replace("\n", "\r\n", StringComparison.Ordinal),
            LineEnding.Cr => result.Replace("\n", "\r", StringComparison.Ordinal),
            _ => result,
        };
        _ = eol;
        return (options.Bom ? "\uFEFF" : string.Empty) + result;
    }

    private static void AppendNode(StringBuilder text, NodeSpec node, RenderOptions options, Random random)
    {
        string indent = new(' ', options.Indent);
        bool flow = options.Style == YamlStyle.Flow || options.Style == YamlStyle.Mixed && random.Next(2) == 0;
        List<(string Key, string Value)> fields = [("name", Q(options, node.Name.Length, node.Name, force: false))];
        if (node.Type is not null) { fields.Add(("type", Q(options, 0, node.Type, force: false))); }
        if (node.Server is not null) { fields.Add(("server", Q(options, 1, node.Server, force: true))); }
        if (node.Port is not null) { fields.Add(("port", PortScalar(node.Port, options))); }
        fields.Add(("udp", "true"));
        if (options.Style == YamlStyle.Mixed && random.Next(2) == 0)
        {
            fields.Reverse();
        }

        if (flow)
        {
            text.Append(indent).Append("- {").Append(string.Join(", ", fields.Select(static field => field.Key + ": " + field.Value))).Append("}\n");
            return;
        }

        for (int index = 0; index < fields.Count; index++)
        {
            text.Append(indent).Append(index == 0 ? "- " : new string(' ', 2)).Append(fields[index].Key).Append(": ").Append(fields[index].Value);
            if (options.Comments && index == 0)
            {
                text.Append("  # first field");
            }

            text.Append('\n');
        }
    }

    private static string PortScalar(string port, RenderOptions options)
    {
        bool plainInteger = port.Length > 0 && port.All(static character => character is >= '0' and <= '9') && port.Length < 10;
        return plainInteger && options.QuoteSeed % 2 == 0 ? port : Q(options, 0, port, force: true);
    }

    private static string Q(RenderOptions options, int offset, string value, bool force) =>
        Quote(value, options.SingleQuotesOnly ? options.QuoteSeed & ~1 : options.QuoteSeed + offset, force);

    private static void AppendProvider(StringBuilder text, ProviderSpec provider, RenderOptions options)
    {
        string indent = new(' ', options.Indent);
        string key = Q(options, 0, provider.Name, force: true);
        if (options.Style == YamlStyle.Flow)
        {
            List<string> fields = [];
            if (provider.Type is not null) { fields.Add("type: " + Q(options, 0, provider.Type, force: false)); }
            if (provider.Url is not null) { fields.Add("url: " + Q(options, 0, provider.Url, force: true)); }
            text.Append(indent).Append(key).Append(": {").Append(string.Join(", ", fields)).Append("}\n");
            return;
        }

        text.Append(indent).Append(key).Append(":\n");
        if (provider.Type is not null) { text.Append(indent).Append(indent).Append("type: ").Append(Q(options, 0, provider.Type, force: false)).Append('\n'); }
        if (provider.Url is not null) { text.Append(indent).Append(indent).Append("url: ").Append(Q(options, 0, provider.Url, force: true)).Append('\n'); }
        text.Append(indent).Append(indent).Append("interval: 3600\n");
    }

    /// <summary>Renders a string as a YAML scalar; plain only when it is provably safe and not forced to be quoted.</summary>
    public static string Quote(string value, int seed, bool force)
    {
        bool plainSafe = !force
            && value.Length > 0
            && value.All(static character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')
            && value[0] != ' ' && value[^1] != ' '
            && !value.Contains("  ", StringComparison.Ordinal)
            && !char.IsDigit(value[0])
            && value[0] is not ('-' or '.')
            && !IsYamlSpecialPlain(value);
        if (plainSafe && seed % 3 == 0)
        {
            return value;
        }

        return seed % 2 == 0
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsYamlSpecialPlain(string value) =>
        value.Trim().ToUpperInvariant() is "NULL" or "TRUE" or "FALSE" or "YES" or "NO" or "ON" or "OFF" or "Y" or "N" or "~";

    // -------------------------------------------------------------------- expectations

    public static int? ExpectedPort(string? port) =>
        port is not null && int.TryParse(port, out int value) && value is >= 1 and <= 65535 ? value : null;

    public static string ExpectedProtocol(string? type) =>
        string.IsNullOrEmpty(type) ? "UNKNOWN" : type.ToUpperInvariant();

    public static string ExpectedHostFromUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : string.Empty;
}
