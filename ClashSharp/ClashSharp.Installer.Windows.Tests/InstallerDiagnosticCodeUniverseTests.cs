using System.Text.RegularExpressions;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Every diagnostic code the Windows layer can raise crosses an IPC or presentation boundary that accepts only
/// bounded lowercase dotted codes. This scans the production sources so a malformed or oversized code fails here
/// instead of being replaced by a generic "result invalid" failure at runtime.
/// </summary>
public sealed partial class InstallerDiagnosticCodeUniverseTests
{
    private static readonly string[] ProductionProjects = ["ClashSharp.Installer.Windows", "ClashSharp.Installer"];

    [Fact]
    public void EveryDiagnosticCodeLiteralIsAValidBoundedStableCode()
    {
        var codes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in ProductionSources())
        {
            foreach (Match match in CodeLiteral().Matches(File.ReadAllText(file)))
            {
                string code = match.Groups["code"].Value;
                if (code.EndsWith('.') || code.Contains('{', StringComparison.Ordinal))
                {
                    continue;
                }

                codes.TryAdd(code, Path.GetFileName(file));
            }
        }

        Assert.True(codes.Count > 300, $"Only {codes.Count} codes were found; the scanner is not seeing the sources.");
        foreach ((string code, string file) in codes)
        {
            Exception? failure = Record.Exception(() => InstallerProtocolValidation.ValidateDiagnosticCode(code));

            Assert.True(failure is null, $"'{code}' in {file} is not a valid diagnostic code.");
            Assert.True(code.StartsWith("installer.", StringComparison.Ordinal), $"'{code}' in {file} lacks the installer namespace.");
        }
    }

    [Fact]
    public void EveryCodeSuffixHelperLiteralIsALowercaseIdentifierThatKeepsItsPrefixedCodeValid()
    {
        var suffixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in ProductionSources())
        {
            foreach (Match match in FailureSuffix().Matches(File.ReadAllText(file)))
            {
                suffixes.Add(match.Groups["suffix"].Value);
            }
        }

        Assert.True(suffixes.Count > 50, $"Only {suffixes.Count} suffixes were found.");
        foreach (string suffix in suffixes)
        {
            Assert.Matches("^[a-z0-9_]{1,64}$", suffix);
            // The longest prefix used with a helper still has to leave a valid, bounded code.
            InstallerProtocolValidation.ValidateDiagnosticCode("installer.recovery.original_baseline." + suffix);
        }
    }

    [Fact]
    public void NoDiagnosticCodeLiteralRevealsPathsIdentitiesOrExceptionText()
    {
        foreach (string file in ProductionSources())
        {
            foreach (Match match in CodeLiteral().Matches(File.ReadAllText(file)))
            {
                string code = match.Groups["code"].Value;
                Assert.DoesNotMatch(@"[A-Z\\/: ]|s-1-5|c:", code);
            }
        }
    }

    private static IEnumerable<string> ProductionSources()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.Installer.Windows")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        foreach (string project in ProductionProjects)
        {
            string root = Path.Combine(directory.FullName, "ClashSharp", project);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (!relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    [GeneratedRegex("\"(?<code>installer\\.[^\"\\\\]*)\"")]
    private static partial Regex CodeLiteral();

    [GeneratedRegex("Failure\\(\"(?<suffix>[^\"]*)\"\\)")]
    private static partial Regex FailureSuffix();
}
