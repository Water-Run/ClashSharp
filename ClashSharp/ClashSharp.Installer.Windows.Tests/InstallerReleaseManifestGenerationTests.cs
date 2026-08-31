using System.Diagnostics;
using System.Text;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class InstallerReleaseManifestGenerationTests
{
    [Fact]
    public async Task PowerShellGeneratorAndProductionCodecAgreeOnTheExactPayloadContract()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        string modulePath = SourcePath("Installer", "PackagingContract.psm1");
        string outputPath = Path.Combine(fixture.RootDirectory, "installer-release-manifest.json");
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module -Name {{Quote(modulePath)}} -Force
            $primary = Get-ClashSharpMsixIdentity -LiteralPath {{Quote(fixture.PrimaryPath)}}
            $dependency = Get-ClashSharpMsixIdentity -LiteralPath {{Quote(fixture.DependencyPath)}}
            $dependencyContract = [PSCustomObject]@{
                Path = 'dependencies/x64/microsoft.windowsappruntime.1.8.msix'
                MinimumVersion = '8000.806.2252.0'
                Identity = $dependency
            }
            $null = New-ClashSharpInstallerReleaseManifest `
                -PayloadRoot {{Quote(fixture.PayloadRoot)}} `
                -PrimaryIdentity $primary `
                -PrimaryRelativePath 'clashsharp_1.2.3.4_x64.msix' `
                -DependencyContracts @($dependencyContract) `
                -CertificateRelativePath 'clashsharp_temporarykey.cer' `
                -AuthenticodeCertificateThumbprint '{{fixture.Manifest.AuthenticodeCertificateThumbprint}}' `
                -CertificateThumbprint '{{fixture.Manifest.PackageCertificateThumbprint}}' `
                -OutputPath {{Quote(outputPath)}}
            [Console]::Out.Write('OK')
            """;

        ProcessResult result = await RunPowerShellAsync(command, TimeSpan.FromSeconds(20));

        Assert.True(
            result.ExitCode == 0,
            $"PowerShell generator exited with {result.ExitCode}. "
            + $"stdout: {result.StandardOutput}; stderr: {result.StandardError}");
        Assert.Equal("OK", result.StandardOutput);
        Assert.True(
            string.IsNullOrWhiteSpace(result.StandardError),
            $"PowerShell generator wrote stderr: {result.StandardError}");
        byte[] bytes = File.ReadAllBytes(outputPath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.DoesNotContain((byte)'\n', bytes);

        InstallerReleaseManifest actual = InstallerReleaseManifestCodec.Parse(bytes);
        Assert.Equal(fixture.Manifest.Schema, actual.Schema);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, actual.ExpectedPackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, actual.InstallerPayloadSha256);
        Assert.Equal(
            fixture.Manifest.AuthenticodeCertificateThumbprint,
            actual.AuthenticodeCertificateThumbprint);
        Assert.Equal(
            fixture.Manifest.PackageCertificateThumbprint,
            actual.PackageCertificateThumbprint);
        Assert.Equal(fixture.Manifest.CertificateSha256, actual.CertificateSha256);
        Assert.Equal(fixture.Manifest.PackageIdentity, actual.PackageIdentity);
        Assert.Equal(fixture.Manifest.Dependencies.ToArray(), actual.Dependencies.ToArray());
        Assert.Equal(fixture.Manifest.MachineFiles.ToArray(), actual.MachineFiles.ToArray());
        Assert.Equal(fixture.Manifest.Files.ToArray(), actual.Files.ToArray());
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string command,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell 7.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("PowerShell release manifest generation timed out.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string SourcePath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "ClashSharp.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
