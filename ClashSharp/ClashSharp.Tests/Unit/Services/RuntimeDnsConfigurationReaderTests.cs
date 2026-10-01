using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class RuntimeDnsConfigurationReaderTests
{
    [Fact]
    public void DnsTiles_ReadResolverRolesAndOmitEndpointCredentials()
    {
        RuntimeDnsConfiguration dns = Assert.IsType<RuntimeDnsConfiguration>(RuntimeDnsConfigurationReader.Read("""
            dns:
              enable: true
              enhanced-mode: fake-ip
              listen: '[::1]:1053'
              ipv6: true
              respect-rules: true
              nameserver: ['https://user:password@resolver.example/private-key?token=secret#proxy', '1.1.1.1#RULES']
              fallback: ['tls://8.8.8.8:853']
              default-nameserver: ['223.5.5.5']
              direct-nameserver: [system]
              proxy-server-nameserver: ['https://proxy.example/dns-query']
              nameserver-policy:
                '+.example.org': '192.0.2.1'
            """));
        Assert.True(dns.Enabled);
        Assert.Equal("fake-ip", dns.Mode);
        Assert.Equal("[::1]:1053", dns.Listen);
        Assert.True(dns.Ipv6);
        Assert.True(dns.RespectRules);
        Assert.Equal(["https://resolver.example", "1.1.1.1"], dns.NameServers.ToArray());
        Assert.Equal(["tls://8.8.8.8:853"], dns.FallbackServers.ToArray());
        Assert.Equal(["223.5.5.5"], dns.BootstrapServers.ToArray());
        Assert.Equal(["system"], dns.DirectServers.ToArray());
        Assert.Equal(["https://proxy.example"], dns.ProxyServers.ToArray());
        Assert.Equal(1, dns.PolicyCount);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("dns: {}", false)]
    [InlineData("dns: { enable: true }", true)]
    public void DnsTiles_OmittedServersPreserveCoreDefaultsInsteadOfInventingZero(string text, bool enabled)
    {
        RuntimeDnsConfiguration dns = Assert.IsType<RuntimeDnsConfiguration>(RuntimeDnsConfigurationReader.Read(text));
        Assert.Equal(enabled, dns.Enabled);
        Assert.True(dns.NameServers.IsDefault);
        Assert.True(dns.BootstrapServers.IsDefault);
        Assert.Empty(dns.FallbackServers);
        Assert.False(dns.Ipv6);
        Assert.False(dns.RespectRules);
        Assert.Equal("redir-host", dns.Mode);
    }

    [Theory]
    [InlineData("dns: []")]
    [InlineData("dns: { enable: invalid }")]
    [InlineData("dns: { enhanced-mode: invalid }")]
    [InlineData("dns: { nameserver: dns.example }")]
    [InlineData("dns: { nameserver: [ { server: dns.example } ] }")]
    [InlineData("dns: { nameserver-policy: [] }")]
    [InlineData("dns: { enable: true, enable: false }")]
    [InlineData("dns: [")]
    public void DnsTiles_MalformedDnsStaysUnknown(string text) => Assert.Null(RuntimeDnsConfigurationReader.Read(text));

    [Fact]
    public void DnsTiles_ReadsManagedTunDnsInsteadOfAssumingDirectProfileHasNoResolver()
    {
        string text = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(17890, ClashSharpMode.RuleTakeover, true, new string('a', 64));
        RuntimeDnsConfiguration dns = Assert.IsType<RuntimeDnsConfiguration>(RuntimeDnsConfigurationReader.Read(text));
        Assert.True(dns.Enabled);
        Assert.Equal("fake-ip", dns.Mode);
        Assert.NotEmpty(dns.NameServers);
        Assert.NotEmpty(dns.BootstrapServers);
    }
}
