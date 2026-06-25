/*
 * Installer Build Script Tests
 * Verifies the repository contains a deterministic desktop packaging helper
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Resources/InstallerBuildScriptTests.cs
 * @date: 2026-06-25
 */

namespace ClashSharp.Tests.Unit.Resources;

/// <summary>Tests the installer build script source-level contract.</summary>
public sealed class InstallerBuildScriptTests
{
    /// <summary>Verifies the Python packaging script publishes win-x64, builds an icon, and creates an archive.</summary>
    [Fact]
    public void BuildInstallerScript_PublishesExeWithIconAndZipPackage()
    {
        string scriptPath = FindSourceFile("Tools", "build_installer.py");

        string script = File.ReadAllText(scriptPath);

        Assert.Contains("dotnet", script, StringComparison.Ordinal);
        Assert.Contains("publish", script, StringComparison.Ordinal);
        Assert.Contains("win-x64", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharp.csproj", script, StringComparison.Ordinal);
        Assert.Contains("PIL", script, StringComparison.Ordinal);
        Assert.Contains(".ico", script, StringComparison.Ordinal);
        Assert.Contains("zipfile", script, StringComparison.Ordinal);
        Assert.Contains("ClashSharp-Installer", script, StringComparison.Ordinal);
    }

    private static string FindSourceFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Source file was not found.", Path.Combine(segments));
    }
}
