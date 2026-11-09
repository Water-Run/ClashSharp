using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Contracts of the XML data package: round-trip identity, strict scope and path safety, hostile input
/// rejection without side effects, and all-or-nothing behaviour at every transaction checkpoint.
/// </summary>
public sealed class ClashDataPackageContractTests
{
    private const int Seed = 20261109;
    private const string TransactionRoot = ".clashsharp-data-package-transaction";

    private static readonly ClashDataPackageScope[] Scopes = [ClashDataPackageScope.Settings, ClashDataPackageScope.SettingsAndProxyConfiguration];

    // -------------------------------------------------------------------- state capture

    private sealed record State(Dictionary<string, string> Settings, Dictionary<string, string> Files)
    {
        public bool SameAs(State other) =>
            Settings.OrderBy(static pair => pair.Key, StringComparer.Ordinal).SequenceEqual(other.Settings.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            && Files.OrderBy(static pair => pair.Key, StringComparer.Ordinal).SequenceEqual(other.Files.OrderBy(static pair => pair.Key, StringComparer.Ordinal));

        public string Describe(State other)
        {
            List<string> differences = [];
            foreach (string key in Settings.Keys.Union(other.Settings.Keys).Order(StringComparer.Ordinal))
            {
                Settings.TryGetValue(key, out string? left);
                other.Settings.TryGetValue(key, out string? right);
                if (left != right) { differences.Add($"setting {key}: '{left}' vs '{other.Settings.GetValueOrDefault(key)}'"); }
            }

            foreach (string key in Files.Keys.Union(other.Files.Keys).Order(StringComparer.Ordinal))
            {
                if (Files.GetValueOrDefault(key) != other.Files.GetValueOrDefault(key)) { differences.Add($"file {key}: {Files.GetValueOrDefault(key)?[..8] ?? "<absent>"} vs {other.Files.GetValueOrDefault(key)?[..8] ?? "<absent>"}"); }
            }

            return string.Join("; ", differences);
        }
    }

    private static State Capture(string directory, PackageSettingsFake settings, params string[] ignoredFiles)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative.StartsWith(TransactionRoot, StringComparison.OrdinalIgnoreCase) || ignoredFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }

        return new State(settings.Snapshot(), files);
    }

    private static void AssertTransactionClean(string directory)
    {
        string root = Path.Combine(directory, TransactionRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        Assert.False(File.Exists(Path.Combine(root, "manifest.json")), "manifest left behind");
        Assert.Empty(Directory.GetDirectories(root));
    }

    private static void WriteFile(string directory, string relative, byte[] content)
    {
        string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static byte[] Bytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    // -------------------------------------------------------------------- round trip

    [Fact]
    public async Task ExportThenImport_ReproducesEverySettingAndEveryPortableFileByteForByte()
    {
        Random random = new(Seed);
        for (int iteration = 0; iteration < 25; iteration++)
        {
            using ScratchDirectory source = new("src");
            using ScratchDirectory target = new("dst");
            PackageSettingsFake exported = PackageSettingsFake.Random(random);
            WriteFile(source.Path, "ProfileCatalog.json", Bytes(random, random.Next(0, 4000)));
            string[] portable =
            [
                "mihomo/profiles/p1.yaml", "mihomo/profiles/dir with space/\u540d\u524d.yaml", "mihomo/\U0001F600.bin", "mihomo/a/b/c/d/e.dat",
                "mihomo/.hidden", "mihomo/noextension", "mihomo/UPPER.YAML", "mihomo/history/0.yaml",
            ];
            foreach (string relative in portable.Where(_ => random.Next(4) != 0))
            {
                WriteFile(source.Path, relative, Bytes(random, random.Next(0, 70_000)));
            }

            // Runtime-owned and unrelated files must never travel.
            string[] excluded =
            [
                "mihomo/config.yaml", "mihomo/config.runtime-state.json", "mihomo/cache.db", "mihomo/cache.db-wal",
                "mihomo/runtime-generations/g1/config.yaml", "mihomo/config.yaml.staging.abc", "logs.sqlite3", "settings.json", "other/readme.txt",
            ];
            foreach (string relative in excluded)
            {
                WriteFile(source.Path, relative, Bytes(random, 50));
            }

            string package = Path.Combine(source.Path, "backup.xml");
            await new ClashDataPackageService(exported, source.Path).ExportAsync(package, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);

            PackageSettingsFake imported = PackageSettingsFake.Random(random);
            WriteFile(target.Path, "mihomo/config.yaml", "target-owned"u8.ToArray());
            await new ClashDataPackageService(imported, target.Path).ImportAsync(package, CancellationToken.None);

            Assert.True(exported.Snapshot().OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(imported.Snapshot().OrderBy(static pair => pair.Key, StringComparer.Ordinal)), $"#{iteration} settings differ");
            State sourceState = Capture(source.Path, exported, "backup.xml");
            State targetState = Capture(target.Path, imported, "mihomo/config.yaml");
            foreach ((string relative, string hash) in sourceState.Files)
            {
                bool travels = relative == "ProfileCatalog.json" || relative.StartsWith("mihomo/", StringComparison.Ordinal) && !excluded.Contains(relative, StringComparer.Ordinal);
                if (travels)
                {
                    Assert.True(targetState.Files.TryGetValue(relative, out string? actual) && actual == hash, $"#{iteration} {relative} not reproduced");
                }
                else
                {
                    Assert.False(targetState.Files.ContainsKey(relative), $"#{iteration} {relative} must not travel");
                }
            }

            Assert.Equal("target-owned", await File.ReadAllTextAsync(Path.Combine(target.Path, "mihomo", "config.yaml")));
            AssertTransactionClean(target.Path);
        }
    }

    [Fact]
    public async Task Export_IsDeterministicSortedAndLeavesSourcesAndStagingUntouched()
    {
        using ScratchDirectory directory = new();
        Random random = new(Seed + 1);
        PackageSettingsFake settings = PackageSettingsFake.Random(random);
        foreach (string relative in new[] { "mihomo/z.yaml", "mihomo/B.yaml", "mihomo/a.yaml", "mihomo/sub/Z.yaml", "ProfileCatalog.json" })
        {
            WriteFile(directory.Path, relative, Bytes(random, 100));
        }

        State before = Capture(directory.Path, settings);
        string first = Path.Combine(directory.Path, "out", "one.xml");
        string second = Path.Combine(directory.Path, "out", "two.xml");
        ClashDataPackageService service = new(settings, directory.Path);
        await service.ExportAsync(first, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);
        await service.ExportAsync(second, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);

        Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));
        string[] paths = [.. XDocument.Load(first).Root!.Element("Files")!.Elements("File").Select(static file => file.Attribute("Path")!.Value)];
        Assert.Equal(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), paths);
        Assert.Equal(5, paths.Length);
        Assert.True(before.SameAs(Capture(directory.Path, settings, "out/one.xml", "out/two.xml")));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "out"), "*.staging.*"));
    }

    [Fact]
    public async Task Export_SettingsScopeNeverContainsFilesEvenWhenTheyExist()
    {
        using ScratchDirectory directory = new();
        WriteFile(directory.Path, "ProfileCatalog.json", "x"u8.ToArray());
        WriteFile(directory.Path, "mihomo/p.yaml", "x"u8.ToArray());
        string package = Path.Combine(directory.Path, "s.xml");

        await new ClashDataPackageService(new PackageSettingsFake(), directory.Path).ExportAsync(package, ClashDataPackageScope.Settings, CancellationToken.None);

        Assert.Empty(XDocument.Load(package).Root!.Element("Files")!.Elements());
        Assert.Equal(PackageSettingsFake.Names.Count, XDocument.Load(package).Root!.Element("Settings")!.Elements("Setting").Count());
    }

    [Fact]
    public async Task Export_RejectsAnUnrepresentableSettingWithoutDamagingTheExistingBackup()
    {
        using ScratchDirectory directory = new();
        string package = Path.Combine(directory.Path, "backup.xml");
        await File.WriteAllTextAsync(package, "precious existing backup");
        PackageSettingsFake settings = new() { ActiveProfileId = "bad\u0001id" };

        await Assert.ThrowsAnyAsync<Exception>(() => new ClashDataPackageService(settings, directory.Path)
            .ExportAsync(package, ClashDataPackageScope.Settings, CancellationToken.None));

        Assert.Equal("precious existing backup", await File.ReadAllTextAsync(package));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.staging.*"));
    }

    [Fact]
    public async Task Export_CancellationLeavesNoStagingFileAndKeepsTheExistingBackup()
    {
        using ScratchDirectory directory = new();
        string package = Path.Combine(directory.Path, "backup.xml");
        await File.WriteAllTextAsync(package, "previous");
        WriteFile(directory.Path, "ProfileCatalog.json", "x"u8.ToArray());
        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClashDataPackageService(new PackageSettingsFake(), directory.Path)
            .ExportAsync(package, ClashDataPackageScope.SettingsAndProxyConfiguration, canceled.Token));

        Assert.Equal("previous", await File.ReadAllTextAsync(package));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.staging.*"));
    }

    // -------------------------------------------------------------------- hostile and malformed input

    private static string BuildPackage(
        string directory,
        ClashDataPackageScope scope,
        IEnumerable<(string Name, string Value)> settings,
        IEnumerable<(string Path, byte[] Content)> files)
    {
        string path = Path.Combine(directory, "pkg-" + Guid.NewGuid().ToString("N") + ".xml");
        XDocument document = new(new XElement("ClashSharpDataPackage",
            new XAttribute("Format", "ClashSharp.XmlDataPackage"),
            new XAttribute("Version", "1"),
            new XAttribute("Scope", scope.ToString()),
            new XElement("Settings", settings.Select(static setting => new XElement("Setting", new XAttribute("Name", setting.Name), new XAttribute("Value", setting.Value)))),
            new XElement("Files", files.Select(static file => new XElement("File", new XAttribute("Path", file.Path), Convert.ToBase64String(file.Content))))));
        File.WriteAllText(path, document.ToString(SaveOptions.DisableFormatting));
        return path;
    }

    private static readonly (string Name, string Value)[] ChangeLanguage = [(nameof(IClashDataPackageSettings.DisplayLanguage), nameof(AppLanguage.French))];

    private static readonly byte[] Payload = "payload"u8.ToArray();

    public static TheoryData<string> UnsafeTargets() =>
    [
        "../x", "..\\x", "mihomo/../x", "mihomo/../../x", "mihomo\\..\\ProfileCatalog.json", "MIHOMO/../x", "/abs", "\\abs", "C:\\abs.txt", "C:/abs",
        "c:abs", "\\\\server\\share\\x", "//server/share/x", "\\\\?\\C:\\x", "\\\\.\\pipe\\x", "mihomo/a:stream", "mihomo/a::$DATA", "mihomo/CON",
        "mihomo/aux.txt", "mihomo/nul.yaml", "mihomo/prn", "mihomo/com1", "mihomo/COM9.yaml", "mihomo/lpt1", "mihomo/LPT9.txt", "mihomo/trailing.",
        "mihomo/trailing ", "mihomo/a?b", "mihomo/a*b", "mihomo/a<b", "mihomo/a>b", "mihomo/a|b", "mihomo/a\"b", "mihomo//x", "mihomo/./x", ".", "..",
        "", " ", "mihomo/", "mihomo\\", "mihomo/...", "mihomo/.. ", "ProfileCatalog.json/child", ".clashsharp-data-package-transaction/x",
        ".clashsharp-data-package-transaction/manifest.json", ".CLASHSHARP-DATA-PACKAGE-TRANSACTION.LOCK", ".clashsharp-data-package-transaction.lock",
        "other/file.txt", "logs.sqlite3", "settings.json", "mihomo.yaml", "mihomox/y.yaml",
    ];

    [Theory]
    [MemberData(nameof(UnsafeTargets))]
    public async Task UnsafeFileTarget_IsRejectedBeforeAnyEffectInEveryScope(string target)
    {
        foreach (ClashDataPackageScope scope in Scopes)
        {
            using ScratchDirectory directory = new();
            WriteFile(directory.Path, "ProfileCatalog.json", "keep"u8.ToArray());
            WriteFile(directory.Path, "mihomo/keep.yaml", "keep"u8.ToArray());
            PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
            State before = Capture(directory.Path, settings);
            string package = BuildPackage(directory.Path, scope, ChangeLanguage, [("mihomo/ok.yaml", Payload), (target, Payload)]);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => new ClashDataPackageService(settings, directory.Path)
                .ImportAsync(package, CancellationToken.None));

            Assert.True(failure is InvalidDataException or XmlException or ArgumentException, failure.GetType().Name);
            Assert.True(before.SameAs(Capture(directory.Path, settings, Path.GetFileName(package))), before.Describe(Capture(directory.Path, settings, Path.GetFileName(package))));
            AssertTransactionClean(directory.Path);
        }
    }

    public static TheoryData<string> SafeTargets() =>
    [
        "mihomo/a.yaml", "mihomo/dir with space/\u540d\u524d.yaml", "mihomo/\U0001F600.yaml", "mihomo/a.b.c", "mihomo/.hidden", "ProfileCatalog.json",
        "mihomo/profiles/x/y/z.yaml", "MIHOMO/Upper.yaml", "mihomo/\u05e2\u05d1\u05e8\u05d9\u05ea.yaml", "mihomo/ leading.yaml", "mihomo/with#hash%percent&amp.yaml",
        "mihomo/profiles/config.yaml", "mihomo\\win\\sep.yaml",
    ];

    [Theory]
    [MemberData(nameof(SafeTargets))]
    public async Task SafeFileTarget_IsWrittenExactlyWhereDeclaredAndNowhereElse(string target)
    {
        using ScratchDirectory directory = new();
        PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
        string package = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, [(target, Payload)]);

        await new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None);

        string expected = Path.GetFullPath(Path.Combine(directory.Path, target.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(expected), expected);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(expected));
        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        string[] written = [.. Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(TransactionRoot, StringComparison.OrdinalIgnoreCase) && path != package)];
        Assert.Equal([expected], written);
    }

    public static TheoryData<string> RuntimeOwnedTargets() =>
    [
        "mihomo/config.yaml", "mihomo/CONFIG.YAML", "mihomo/config.runtime-state.json", "mihomo/cache.db", "mihomo/cache.db-wal", "mihomo/cache.db-shm",
        "mihomo/runtime-generations/g/x.yaml", "mihomo/config.yaml.staging.1", "mihomo/config.yaml.backup.1", "mihomo/config.yaml.restore.1",
        "mihomo/config.yaml.runtime-staging.1", "mihomo/config.yaml.runtime-backup.1", "mihomo/config.runtime-state.json.tmp.1",
    ];

    [Theory]
    [MemberData(nameof(RuntimeOwnedTargets))]
    public async Task RuntimeOwnedTarget_IsSilentlySkippedAndTheLiveFileIsNeverReplaced(string target)
    {
        using ScratchDirectory directory = new();
        string live = Path.Combine(directory.Path, target.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        await File.WriteAllTextAsync(live, "live");
        PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
        string package = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, [(target, Payload), ("mihomo/ok.yaml", Payload)]);

        await new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None);

        Assert.Equal("live", await File.ReadAllTextAsync(live));
        Assert.True(File.Exists(Path.Combine(directory.Path, "mihomo", "ok.yaml")));
        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
    }

    [Fact]
    public async Task DuplicateTargets_AreRejectedEvenWhenTheyDifferOnlyByCaseOrSeparator()
    {
        string[][] pairs =
        [
            ["mihomo/a.yaml", "mihomo/a.yaml"], ["mihomo/a.yaml", "MIHOMO/A.YAML"], ["mihomo/a.yaml", "mihomo\\a.yaml"], ["ProfileCatalog.json", "profilecatalog.JSON"],
        ];
        foreach (string[] pair in pairs)
        {
            using ScratchDirectory directory = new();
            PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
            string package = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, [(pair[0], Payload), (pair[1], Payload)]);

            await Assert.ThrowsAsync<InvalidDataException>(() => new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None));

            Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
            Assert.False(Directory.Exists(Path.Combine(directory.Path, "mihomo")));
        }
    }

    [Fact]
    public async Task EntryAndCountLimits_AreExactAndEnforcedBeforeAnyMutation()
    {
        using ScratchDirectory directory = new();
        PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService service = new(settings, directory.Path);
        const int EntryLimit = 16 * 1024 * 1024;

        byte[] atLimit = new byte[EntryLimit];
        string accepted = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, [("mihomo/big.bin", atLimit)]);
        await service.ImportAsync(accepted, CancellationToken.None);
        Assert.Equal(EntryLimit, new FileInfo(Path.Combine(directory.Path, "mihomo", "big.bin")).Length);
        File.Delete(Path.Combine(directory.Path, "mihomo", "big.bin"));
        settings.DisplayLanguage = AppLanguage.English;

        string tooBig = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, [("mihomo/big.bin", new byte[EntryLimit + 1])]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(tooBig, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory.Path, "mihomo", "big.bin")));
        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);

        // Entries that are skipped as runtime-owned still count against the entry budget.
        string[] owned(int count) => [.. Enumerable.Range(0, count).Select(static index => "mihomo/cache.db-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        string manyAccepted = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, owned(4096).Select(static path => (path, Payload)));
        await service.ImportAsync(manyAccepted, CancellationToken.None);
        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        settings.DisplayLanguage = AppLanguage.English;
        string tooMany = BuildPackage(directory.Path, ClashDataPackageScope.SettingsAndProxyConfiguration, ChangeLanguage, owned(4097).Select(static path => (path, Payload)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(tooMany, CancellationToken.None));
        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
    }

    public static TheoryData<string> MalformedDocuments() =>
    [
        "doctype-entity-expansion", "doctype-external-entity", "not-xml", "empty-file", "wrong-root", "wrong-format", "wrong-version", "scope-lowercase",
        "scope-undefined-number", "scope-missing", "duplicate-setting", "invalid-base64", "base64-with-garbage", "truncated-document", "file-without-path",
        "file-with-empty-path", "utf16-document-with-wrong-declaration",
    ];

    private static string MalformedText(string name, string directory)
    {
        const string Open = "<ClashSharpDataPackage Format=\"ClashSharp.XmlDataPackage\" Version=\"1\" Scope=\"SettingsAndProxyConfiguration\">";
        string setting = "<Setting Name=\"DisplayLanguage\" Value=\"French\"/>";
        return name switch
        {
            "doctype-entity-expansion" => "<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;\">]>"
                + Open + "<Settings>" + setting + "</Settings><Files><File Path=\"mihomo/x.yaml\">&c;</File></Files></ClashSharpDataPackage>",
            "doctype-external-entity" => "<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY x SYSTEM \"file:///" + directory.Replace('\\', '/') + "/secret.txt\">]>"
                + Open + "<Settings/><Files><File Path=\"mihomo/x.yaml\">&x;</File></Files></ClashSharpDataPackage>",
            "not-xml" => "this is not xml",
            "empty-file" => string.Empty,
            "wrong-root" => "<Other Format=\"ClashSharp.XmlDataPackage\" Version=\"1\" Scope=\"Settings\"/>",
            "wrong-format" => "<ClashSharpDataPackage Format=\"x\" Version=\"1\" Scope=\"Settings\"/>",
            "wrong-version" => "<ClashSharpDataPackage Format=\"ClashSharp.XmlDataPackage\" Version=\"2\" Scope=\"Settings\"/>",
            "scope-lowercase" => "<ClashSharpDataPackage Format=\"ClashSharp.XmlDataPackage\" Version=\"1\" Scope=\"settings\"/>",
            "scope-undefined-number" => "<ClashSharpDataPackage Format=\"ClashSharp.XmlDataPackage\" Version=\"1\" Scope=\"77\"/>",
            "scope-missing" => "<ClashSharpDataPackage Format=\"ClashSharp.XmlDataPackage\" Version=\"1\"/>",
            "duplicate-setting" => Open + "<Settings>" + setting + setting + "</Settings></ClashSharpDataPackage>",
            "invalid-base64" => Open + "<Settings>" + setting + "</Settings><Files><File Path=\"mihomo/x.yaml\">***not base64***</File></Files></ClashSharpDataPackage>",
            "base64-with-garbage" => Open + "<Settings>" + setting + "</Settings><Files><File Path=\"mihomo/x.yaml\">cGF5bG9hZA==!!</File></Files></ClashSharpDataPackage>",
            "truncated-document" => Open + "<Settings>" + setting + "</Settings><Files><File Path=\"mihomo/x.yaml\">cGF5",
            "file-without-path" => Open + "<Settings>" + setting + "</Settings><Files><File>cGF5bG9hZA==</File></Files></ClashSharpDataPackage>",
            "file-with-empty-path" => Open + "<Settings>" + setting + "</Settings><Files><File Path=\"\">cGF5bG9hZA==</File></Files></ClashSharpDataPackage>",
            "utf16-document-with-wrong-declaration" => "<?xml version=\"1.0\" encoding=\"utf-16\"?>" + Open + "<Settings>" + setting + "</Settings></ClashSharpDataPackage>",
            _ => throw new NotSupportedException(name),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public async Task MalformedPackage_IsRejectedWithoutSideEffectsOrDisclosure(string name)
    {
        using ScratchDirectory directory = new();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "secret.txt"), "TOP-SECRET-CONTENT");
        PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English };
        string package = Path.Combine(directory.Path, "malformed.xml");
        await File.WriteAllTextAsync(package, MalformedText(name, directory.Path));
        State before = Capture(directory.Path, settings, "malformed.xml");

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None));

        Assert.DoesNotContain("TOP-SECRET-CONTENT", failure.ToString(), StringComparison.Ordinal);
        Assert.True(before.SameAs(Capture(directory.Path, settings, "malformed.xml")));
        AssertTransactionClean(directory.Path);
        if (name.StartsWith("scope", StringComparison.Ordinal) || name.StartsWith("wrong", StringComparison.Ordinal))
        {
            Assert.Throws<InvalidDataException>(() => ClashDataPackageService.ReadImportScope(package));
        }
    }

    [Fact]
    public async Task MissingPackage_AndOversizedPackageAreRejectedBeforeAnyRead()
    {
        using ScratchDirectory directory = new();
        PackageSettingsFake settings = new();
        await Assert.ThrowsAsync<FileNotFoundException>(() => new ClashDataPackageService(settings, directory.Path)
            .ImportAsync(Path.Combine(directory.Path, "missing.xml"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new ClashDataPackageService(settings, directory.Path).ImportAsync(" ", CancellationToken.None));
        Assert.Throws<FileNotFoundException>(() => ClashDataPackageService.ReadImportScope(Path.Combine(directory.Path, "missing.xml")));
    }

    // -------------------------------------------------------------------- setting validation universe

    private static IEnumerable<(string Name, string Invalid)> InvalidSettingValues()
    {
        foreach (string name in PackageSettingsFake.Names)
        {
            Type type = PackageSettingsFake.TypeOf(name);
            if (type == typeof(bool))
            {
                foreach (string value in new[] { "yes", "1", "", "tru" }) { yield return (name, value); }
            }
            else if (type == typeof(int))
            {
                foreach (string value in new[] { "abc", "", "0", "-1", "2", "65536", "301", "99999999999", "1.5", "0x10" })
                {
                    bool valid = name switch
                    {
                        nameof(IClashDataPackageSettings.MixedPort) => int.TryParse(value, out int port) && port is >= 1 and <= 65535,
                        nameof(IClashDataPackageSettings.ConnectionSamplingIntervalSeconds) => int.TryParse(value, out int seconds) && seconds is >= 3 and <= 300,
                        _ => false,
                    };
                    if (!valid) { yield return (name, value); }
                }
            }
            else if (type.IsEnum)
            {
                foreach (string value in new[] { "NotAMember", "", "999", "-1", Enum.GetNames(type)[0].ToLowerInvariant() })
                {
                    if (!Enum.GetNames(type).Contains(value, StringComparer.Ordinal)) { yield return (name, value); }
                }

                if (type == typeof(ClashSharpMode)) { yield return (name, nameof(ClashSharpMode.Faulted)); }
            }
        }

        yield return (nameof(IClashDataPackageSettings.MasterHeroStatusLayout) + "x", "ignored-unknown");
        yield return (nameof(IClashDataPackageSettings.MasterInfoTileLayout), "core,../unknown");
        yield return (nameof(IClashDataPackageSettings.MasterInfoTileLayout), string.Join(",", Enumerable.Range(1, 129).Select(static index => "tile-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task InvalidSettingValue_IsRejectedForEverySettingWithoutPartialApplication()
    {
        int checkedCases = 0;
        foreach ((string name, string invalid) in InvalidSettingValues())
        {
            using ScratchDirectory directory = new();
            PackageSettingsFake settings = new() { DisplayLanguage = AppLanguage.English, MixedPort = 12345 };
            State before = Capture(directory.Path, settings);
            string package = BuildPackage(
                directory.Path,
                ClashDataPackageScope.SettingsAndProxyConfiguration,
                [(nameof(IClashDataPackageSettings.DisplayLanguage), nameof(AppLanguage.French)), (nameof(IClashDataPackageSettings.MixedPort), "23456"), (name, invalid)],
                [("mihomo/p.yaml", Payload)]);
            bool unknownName = !PackageSettingsFake.Names.Contains(name, StringComparer.Ordinal);

            if (unknownName)
            {
                // Unknown names are ignored for forward compatibility; known ones in the same package still apply.
                await new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None);
                Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
                continue;
            }

            Exception? rejected = await Record.ExceptionAsync(() => new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None));
            Assert.True(rejected is not null, $"{name}='{invalid}' was accepted");
            Assert.True(before.SameAs(Capture(directory.Path, settings, Path.GetFileName(package))), $"{name}='{invalid}' leaked state: {before.Describe(Capture(directory.Path, settings, Path.GetFileName(package)))}");
            AssertTransactionClean(directory.Path);
            checkedCases++;
        }

        Assert.True(checkedCases > 60, checkedCases.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MissingSettingsAreLeftAloneAndUnknownSettingsAreIgnored()
    {
        using ScratchDirectory directory = new();
        Random random = new(Seed + 2);
        PackageSettingsFake settings = PackageSettingsFake.Random(random);
        Dictionary<string, string> before = settings.Snapshot();
        string package = BuildPackage(directory.Path, ClashDataPackageScope.Settings, [("FutureSetting", "x"), (nameof(IClashDataPackageSettings.MixedPort), "4242")], []);

        await new ClashDataPackageService(settings, directory.Path).ImportAsync(package, CancellationToken.None);

        Dictionary<string, string> after = settings.Snapshot();
        Assert.Equal("4242", after[nameof(IClashDataPackageSettings.MixedPort)]);
        foreach ((string key, string value) in before.Where(pair => pair.Key != nameof(IClashDataPackageSettings.MixedPort)))
        {
            Assert.Equal(value, after[key]);
        }
    }

    // -------------------------------------------------------------------- crash and fault at every checkpoint

    public enum Flow
    {
        ImportCommit,
        ImportRollback,
        Reset,
    }

    public enum Fault
    {
        Crash,
        Exception,
    }

    private sealed class World : IDisposable
    {
        private readonly ScratchDirectory _directory = new("txn");

        public World()
        {
            Settings = PackageSettingsFake.Random(new Random(Seed + 10));
            Directory = _directory.Path;
            WriteFile(Directory, "ProfileCatalog.json", "old-catalog"u8.ToArray());
            WriteFile(Directory, "mihomo/profiles/p1.yaml", "old-1"u8.ToArray());
            WriteFile(Directory, "mihomo/profiles/p2.yaml", "old-2"u8.ToArray());
            WriteFile(Directory, "mihomo/extra/old only.yaml", "old-only"u8.ToArray());
            WriteFile(Directory, "mihomo/config.yaml", "live"u8.ToArray());
            WriteFile(Directory, "mihomo/cache.db", "cache"u8.ToArray());
        }

        public string Directory { get; }

        public PackageSettingsFake Settings { get; }

        public string? Package { get; private set; }

        public async Task BuildPackageAsync()
        {
            using ScratchDirectory source = new("pkg");
            PackageSettingsFake packageSettings = PackageSettingsFake.Random(new Random(Seed + 11));
            WriteFile(source.Path, "ProfileCatalog.json", "new-catalog"u8.ToArray());
            WriteFile(source.Path, "mihomo/profiles/p1.yaml", "new-1"u8.ToArray());
            WriteFile(source.Path, "mihomo/profiles/p3.yaml", "new-3"u8.ToArray());
            WriteFile(source.Path, "mihomo/dir with space/\u65b0.yaml", "new-space"u8.ToArray());
            string path = Path.Combine(Path.GetTempPath(), "clashsharp-appsvc-pkg-" + Guid.NewGuid().ToString("N") + ".xml");
            await new ClashDataPackageService(packageSettings, source.Path).ExportAsync(path, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);
            Package = path;
            PackageSettings = packageSettings;
        }

        public PackageSettingsFake? PackageSettings { get; private set; }

        public State Capture() => ClashDataPackageContractTests.Capture(Directory, Settings);

        public void Dispose()
        {
            if (Package is not null) { File.Delete(Package); }
            _directory.Dispose();
        }
    }

    private static async Task<State> ExpectedNewAsync(Flow flow)
    {
        using World world = new();
        await world.BuildPackageAsync();
        ClashDataPackageService service = new(world.Settings, world.Directory);
        if (flow == Flow.Reset)
        {
            await service.ResetSettingsAsync(CancellationToken.None);
        }
        else
        {
            await service.ImportAsync(world.Package!, CancellationToken.None);
        }

        return world.Capture();
    }

    private sealed class Plan(int position, Fault fault)
    {
        public List<DataPackageTransactionCheckpoint> Seen { get; } = [];

        public void Observe(DataPackageTransactionCheckpoint checkpoint)
        {
            int index = Seen.Count;
            Seen.Add(checkpoint);
            if (index == position)
            {
                throw fault == Fault.Crash
                    ? new DataPackageSimulatedCrashException("simulated process stop at " + checkpoint)
                    : new IOException("injected failure at " + checkpoint);
            }
        }
    }

    private static async Task<(bool ReceiptIssued, Exception? Failure)> RunFlowAsync(Flow flow, ClashDataPackageService service, string package)
    {
        bool receiptIssued = false;
        try
        {
            if (flow == Flow.Reset)
            {
                await service.ResetSettingsAsync(CancellationToken.None);
                return (false, null);
            }

            DataPackageTransactionReceipt receipt = await service.BeginImportAsync(package, CancellationToken.None);
            receiptIssued = true;
            await using (receipt)
            {
                Func<CancellationToken, Task> complete = flow == Flow.ImportCommit ? receipt.CommitAsync : receipt.RollbackAsync;
                try
                {
                    await complete(CancellationToken.None);
                }
                catch (IOException)
                {
                    // The decision is durable; the same decision may be retried on the same receipt.
                    await complete(CancellationToken.None);
                }
            }

            return (true, null);
        }
        catch (Exception failure)
        {
            return (receiptIssued, failure);
        }
    }

    public static TheoryData<Flow, Fault> FlowFaultMatrix()
    {
        TheoryData<Flow, Fault> data = [];
        foreach (Flow flow in Enum.GetValues<Flow>())
        {
            foreach (Fault fault in Enum.GetValues<Fault>())
            {
                data.Add(flow, fault);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FlowFaultMatrix))]
    public async Task EveryCheckpointOccurrence_LeavesExactlyTheOldOrTheNewGenerationAfterRecovery(Flow flow, Fault fault)
    {
        // Phase 1: record the checkpoint sequence of a healthy run, and the two valid end states.
        List<DataPackageTransactionCheckpoint> sequence;
        State oldState;
        State newState = await ExpectedNewAsync(flow);
        using (World recording = new())
        {
            await recording.BuildPackageAsync();
            oldState = recording.Capture();
            Plan recorder = new(-1, fault);
            ClashDataPackageService service = new(recording.Settings, recording.Directory, recorder.Observe);
            (bool _, Exception? healthy) = await RunFlowAsync(flow, service, recording.Package!);
            Assert.Null(healthy);
            sequence = recorder.Seen;
        }

        Assert.False(oldState.SameAs(newState), "the scenario must actually change something");
        Assert.True(sequence.Count >= 2, "no checkpoints recorded");
        if (flow != Flow.Reset)
        {
            Assert.True(sequence.Count(static checkpoint => checkpoint == DataPackageTransactionCheckpoint.ImportFileApplied) >= 2, "multiple file applications expected");
        }

        int resetCommitted = sequence.IndexOf(DataPackageTransactionCheckpoint.ResetSettingsApplied);
        int rollbackStart = sequence.IndexOf(DataPackageTransactionCheckpoint.RollbackPersisted);
        int applied = sequence.IndexOf(DataPackageTransactionCheckpoint.TransactionApplied);

        // Phase 2: fail at every position of that sequence.
        for (int position = 0; position < sequence.Count; position++)
        {
            if (fault == Fault.Exception && sequence[position] == DataPackageTransactionCheckpoint.ManifestPersisted)
            {
                // This checkpoint sits between the durable forward decision and the recoverable region: it models
                // an abrupt stop by contract, never an in-process failure, so only the crash kind applies to it.
                continue;
            }

            string context = $"{flow}/{fault} at #{position} {sequence[position]}";
            using World world = new();
            await world.BuildPackageAsync();
            Plan plan = new(position, fault);
            ClashDataPackageService failing = new(world.Settings, world.Directory, plan.Observe);

            (bool receiptIssued, Exception? failure) = await RunFlowAsync(flow, failing, world.Package!);

            State afterRun = world.Capture();
            if (fault == Fault.Crash)
            {
                Assert.IsType<DataPackageSimulatedCrashException>(failure);
            }
            else
            {
                // In-process failures are handled by the transaction itself: before the receipt is issued they roll back.
                if (failure is not null && !receiptIssued && flow != Flow.Reset)
                {
                    Assert.True(afterRun.SameAs(oldState), $"{context}: in-process failure must restore the old generation; {afterRun.Describe(oldState)}");
                }
            }

            ClashDataPackageService recovering = new(world.Settings, world.Directory);
            await recovering.ReconcilePendingTransactionAsync(CancellationToken.None);
            State recovered = world.Capture();
            bool isOld = recovered.SameAs(oldState);
            bool isNew = recovered.SameAs(newState);
            Assert.True(isOld || isNew, $"{context}: mixed generation after recovery; vs old: {recovered.Describe(oldState)}; vs new: {recovered.Describe(newState)}");
            AssertTransactionClean(world.Directory);

            // Recovery is idempotent and never flips an outcome that was already decided.
            await recovering.ReconcilePendingTransactionAsync(CancellationToken.None);
            Assert.True(world.Capture().SameAs(recovered), $"{context}: second recovery changed the state");
            if (fault == Fault.Exception)
            {
                Assert.True(recovered.SameAs(afterRun), $"{context}: recovery changed an in-process outcome; {recovered.Describe(afterRun)}");
            }

            // The decision a crash leaves behind follows the durable phase that was reached.
            if (fault == Fault.Crash)
            {
                bool expectNew = flow switch
                {
                    Flow.ImportCommit => true,
                    Flow.ImportRollback => rollbackStart < 0 || position < rollbackStart,
                    _ => resetCommitted >= 0 && position >= resetCommitted,
                };
                Assert.True(expectNew ? isNew : isOld, $"{context}: expected {(expectNew ? "new" : "old")} generation");
            }
            else if (flow == Flow.ImportCommit && receiptIssued)
            {
                Assert.True(isNew, $"{context}: a retried commit must end on the new generation");
            }
            else if (flow == Flow.ImportRollback && receiptIssued && failure is null)
            {
                Assert.True(isOld, $"{context}: a retried rollback must end on the old generation");
            }

            _ = applied;
        }
    }

    [Fact]
    public async Task Import_ConcurrentAttemptsNeverInterleaveAndALostRaceChangesNothing()
    {
        using World world = new();
        await world.BuildPackageAsync();
        ClashDataPackageService first = new(world.Settings, world.Directory);
        ClashDataPackageService second = new(world.Settings, world.Directory);
        State oldState = world.Capture();

        DataPackageTransactionReceipt receipt = await first.BeginImportAsync(world.Package!, CancellationToken.None);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => second.BeginImportAsync(world.Package!, CancellationToken.None));

        Assert.True(failure is IOException or InvalidOperationException or TimeoutException, failure.GetType().Name);
        await receipt.RollbackAsync(CancellationToken.None);
        await receipt.DisposeAsync();
        Assert.True(world.Capture().SameAs(oldState));
        AssertTransactionClean(world.Directory);

        await second.ImportAsync(world.Package!, CancellationToken.None);
        Assert.False(world.Capture().SameAs(oldState));
    }
}
