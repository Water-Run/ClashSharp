using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the Phase 04 trigger persistence, lifetime, and presentation boundaries.</summary>
public sealed class TriggerArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>Restricts the legacy JSON authority to read-only migration composition.</summary>
    [Fact]
    public void LegacyTriggerJson_IsOnlyAddressedByMigrationComposition()
    {
        string[] sourcesWithLegacyPath = ReadProductionSources()
            .Where(source => source.Text.Contains("Triggers.json", StringComparison.Ordinal))
            .Select(static source => source.RelativePath)
            .ToArray();

        Assert.Equal(
            [
                "ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerMigrationCoordinator.cs",
                "ClashSharp/ClashSharp.TriggerProbe/Program.cs",
                "ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs",
            ],
            sourcesWithLegacyPath);

        string reader = ReadSource(
            "ClashSharp/ClashSharp.Infrastructure/Triggers/LegacyTriggerMigrationReader.cs");
        Assert.Contains("FileMode.Open", reader, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read", reader, StringComparison.Ordinal);
        Assert.Contains("JsonDocument.Parse", reader, StringComparison.Ordinal);
        Assert.False(ContainsLegacyJsonWrite(reader));
        Assert.DoesNotMatch(@"\bJsonSerializer\.Serialize\b", reader);

        string coordinator = ReadSource(
            "ClashSharp/ClashSharp.Infrastructure/Triggers/TriggerMigrationCoordinator.cs");
        Assert.Contains(
            "LegacyTriggerMigrationReader.ReadAsync(",
            coordinator,
            StringComparison.Ordinal);
        Assert.False(ContainsLegacyJsonWrite(coordinator));
    }

    /// <summary>Keeps file-system mutation inside the Infrastructure trigger implementation.</summary>
    [Fact]
    public void TriggerApplicationAndPresentationSources_DoNotPerformDirectFileIo()
    {
        const string directFileIoPattern =
            @"\b(?:File|Directory)\.(?:Create|Delete|Move|Replace|Write|Append|Open)"
            + @"|\bnew\s+(?:FileStream|StreamReader|StreamWriter)\s*\(";

        ProductionSource[] offenders = ReadTriggerSources()
            .Where(source => !source.RelativePath.StartsWith(
                "ClashSharp/ClashSharp.Infrastructure/",
                StringComparison.Ordinal))
            .Where(source => Regex.IsMatch(source.Text, directFileIoPattern))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>Rejects timer callbacks and detached asynchronous work in every trigger runtime layer.</summary>
    [Fact]
    public void TriggerRuntimeSources_UseOwnedAwaitedWorkInsteadOfTimersOrDetachedTasks()
    {
        ProductionSource[] runtimeSources = ReadTriggerSources()
            .Where(source => !source.RelativePath.EndsWith(
                "/View/Triggers.xaml.cs",
                StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(
            runtimeSources,
            source => ContainsDetachedWork(source.Text));
        Assert.DoesNotContain(
            runtimeSources,
            source => Regex.IsMatch(source.Text, @"\basync\s+void\b"));

        foreach (ProductionSource source in runtimeSources)
        {
            string[] taskRunLines = source.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(static line => line.Contains("Task.Run(", StringComparison.Ordinal))
                .ToArray();
            Assert.All(
                taskRunLines,
                line => Assert.Contains("await Task.Run(", line.Trim(), StringComparison.Ordinal));
        }
    }

    /// <summary>Requires context and storage boundaries to remain asynchronous and cancellation-aware.</summary>
    [Fact]
    public void TriggerContextAndStorageContracts_AreAsyncAndCancellationAware()
    {
        AssertAsyncCancellationContract(typeof(ITriggerContextProvider));
        AssertAsyncCancellationContract(typeof(ITriggerRepository));
        AssertAsyncCancellationContract(typeof(ITriggerDefinitionStore));

        Assert.DoesNotContain(
            ReadTriggerSources(),
            source => ContainsSynchronousWait(source.Text));
    }

    /// <summary>Locks the detectors against representative forms that previously escaped text gates.</summary>
    [Fact]
    public void ForbiddenConstructDetectors_RejectKnownBadMutationsAndIgnoreDeadRegistrationText()
    {
        string[] synchronousWaits =
        [
            "return provider.AcquireAsync(request, token).Result;",
            "provider.AcquireAsync(request, token).Wait();",
            "return provider.AcquireAsync(request, token).GetAwaiter().GetResult();",
            "Task.WaitAll(first, second);",
        ];
        Assert.All(synchronousWaits, sample => Assert.True(ContainsSynchronousWait(sample), sample));

        string[] detachedWork =
        [
            "_ = RunAsync(token);",
            "_ = service.RunAsync(token);",
            "_ = Task.Run(() => RunAsync(token));",
            "Task.Factory.StartNew(() => RunAsync(token));",
            "work.ContinueWith(Observe);",
        ];
        Assert.All(detachedWork, sample => Assert.True(ContainsDetachedWork(sample), sample));

        string[] legacyWrites =
        [
            "File.Create(_legacyPath);",
            "File.OpenWrite(_legacyPath);",
            "File.WriteAllText(_legacyPath, payload);",
            "new FileStream(_legacyPath, FileMode.Create, FileAccess.Write);",
            "new StreamWriter(_legacyPath);",
        ];
        Assert.All(legacyWrites, sample => Assert.True(ContainsLegacyJsonWrite(sample), sample));

        string deadRegistrationText =
            "// AddSingleton<ITriggerRepository>(repository)\n"
            + "const string marker = \"new TriggerScheduler(repository)\";";
        string executableText = RemoveCommentsAndLiterals(deadRegistrationText);
        Assert.DoesNotContain("AddSingleton<ITriggerRepository>", executableText, StringComparison.Ordinal);
        Assert.DoesNotContain("new TriggerScheduler(", executableText, StringComparison.Ordinal);
        Assert.False(ContainsSynchronousWait(
            "// task.Result\nconst string marker = \"task.Wait()\";"));
        Assert.False(ContainsDetachedWork(
            "/* _ = service.RunAsync(); */\nconst string marker = \"Task.Factory.StartNew\";"));
        Assert.False(ContainsLegacyJsonWrite(
            "// File.Create(_legacyPath)\nconst string marker = \"File.OpenWrite(_legacyPath)\";"));
    }

    /// <summary>Prevents the deleted monolith, test source links, and trigger-specific test forks from returning.</summary>
    [Fact]
    public void LegacyTriggerMonolithSourceLinksAndTestForks_AreAbsent()
    {
        string[] removedPaths =
        [
            "ClashSharp/ClashSharp/Model/TriggerTask.cs",
            "ClashSharp/ClashSharp/Service/TriggerEvaluationContextFactory.cs",
            "ClashSharp/ClashSharp/Service/TriggerService.cs",
            "ClashSharp/ClashSharp/Service/TriggerTaskNormalizer.cs",
            "ClashSharp/ClashSharp.Tests/Unit/Services/TriggerServiceTests.cs",
            "ClashSharp/ClashSharp.Tests/Unit/Services/TriggerTaskNormalizerTests.cs",
        ];
        Assert.All(
            removedPaths,
            path => Assert.False(File.Exists(AbsolutePath(path)), path));

        XDocument testProject = XDocument.Load(AbsolutePath(
            "ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj"));
        string[] triggerSourceLinks = testProject.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Where(include => include.Contains("Trigger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(triggerSourceLinks);

        Assert.DoesNotContain(
            ReadTriggerSources(),
            source => source.Text.Contains("#if UNIT_TESTS", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ReadProductionSources(),
            source => Regex.IsMatch(
                source.Text,
                @"\b(?:class|record)\s+(?:TriggerService|TriggerTaskNormalizer|TriggerTask)\b"));
    }

    /// <summary>Keeps persisted definitions immutable and removes duplicate mutable presentation models.</summary>
    [Fact]
    public void TriggerDefinitions_AreImmutableCoreValuesWithViewModelOwnedDrafts()
    {
        AssertImmutableDefinition(typeof(ClashSharp.Model.Triggers.TriggerTaskDefinition));
        AssertImmutableDefinition(typeof(ClashSharp.Model.Triggers.TriggerCondition));
        AssertImmutableDefinition(typeof(ClashSharp.Model.Triggers.TriggerAction));

        string presentationModelRoot = AbsolutePath("ClashSharp/ClashSharp/Model");
        Assert.Empty(Directory.EnumerateFiles(
            presentationModelRoot,
            "Trigger*.cs",
            SearchOption.TopDirectoryOnly));

        string codeBehind = ReadSource("ClashSharp/ClashSharp/View/Triggers.xaml.cs");
        Assert.DoesNotMatch(
            @"new\s+Trigger(?:TaskDefinition|Condition|Action)\s*\(",
            codeBehind);
        Assert.DoesNotContain("TimeSpan.From", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("TryParse(", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>Freezes remaining trigger singleton access at named compatibility adapters.</summary>
    [Fact]
    public void TriggerServiceLocatorDebt_IsFrozenAtCompatibilityAdapters()
    {
        Dictionary<string, int> expected = new(StringComparer.Ordinal)
        {
            ["ClashSharp/ClashSharp/AppHost/Compatibility/TriggerPresentationCompatibilityFactory.cs"] = 1,
            ["ClashSharp/ClashSharp/Service/TriggerSchedulerAdapters.cs"] = 2,
        };
        Dictionary<string, int> actual = ReadTriggerSources()
            .Where(source => source.RelativePath.StartsWith(
                "ClashSharp/ClashSharp/",
                StringComparison.Ordinal))
            .Select(source => new
            {
                source.RelativePath,
                Count = Regex.Count(source.Text, @"\.Instance\b"),
            })
            .Where(static source => source.Count > 0)
            .ToDictionary(
                static source => source.RelativePath,
                static source => source.Count,
                StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies the composition root owns every durable trigger runtime adapter.</summary>
    [Fact]
    public void AppHost_ComposesRepositorySchedulerExecutorAndLifecycleBoundaries()
    {
        string host = ReadSource(
            "ClashSharp/ClashSharp/AppHost/ClashSharpAppHostFactory.cs");
        string executableHost = RemoveCommentsAndLiterals(host);

        string[] requiredRegistrations =
        [
            "new SqliteTriggerRepository(triggerDatabasePath)",
            "AddSingleton<ITriggerRepository>",
            "AddSingleton<ITriggerDefinitionStore>",
            "AddSingleton<ITriggerFiredNotificationSink>",
            "AddSingleton<ITriggerContextProvider>",
            "AddSingleton<ITriggerActionRuntime>",
            "AddSingleton<TriggerActionExecutor>()",
            "AddSingleton<ITriggerExecutionDispatcher>",
            "new TriggerLifecycleHandoffCoordinator(",
            "AddSingleton<ITriggerLifecycleHandoff>",
            "new TriggerScheduler(",
            "GetRequiredService<TriggerScheduler>()",
            "AddSingleton<ITriggerStartupInitializer>",
        ];

        Assert.All(
            requiredRegistrations,
            registration => Assert.Contains(registration, executableHost, StringComparison.Ordinal));
        Assert.DoesNotContain(
            "NullTriggerFiredNotificationSink.Instance",
            executableHost,
            StringComparison.Ordinal);
    }

    private static bool ContainsSynchronousWait(string source)
    {
        string executableSource = RemoveCommentsAndLiterals(source);
        return Regex.IsMatch(
            executableSource,
            @"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(\s*\)"
            + @"|\.Result\b"
            + @"|\.Wait\s*\("
            + @"|\bTask\.Wait(?:All|Any)\s*\(");
    }

    private static bool ContainsDetachedWork(string source)
    {
        string executableSource = RemoveCommentsAndLiterals(source);
        return Regex.IsMatch(
            executableSource,
            @"\b(?:System\.Threading\.)?Timer\b"
            + @"|\bPeriodicTimer\b"
            + @"|\bThreadPool\.(?:QueueUserWorkItem|UnsafeQueueUserWorkItem)\b"
            + @"|\bTask\.Factory\.StartNew\s*\("
            + @"|\.ContinueWith\s*\("
            + @"|_\s*=\s*[^;{}]*\b\w+Async\s*\("
            + @"|_\s*=\s*[^;{}]*\bTask\.Run\s*\(");
    }

    private static bool ContainsLegacyJsonWrite(string source)
    {
        const string legacyPath = @"(?:_legacyPath|legacyPath)";
        string executableSource = RemoveCommentsAndLiterals(source);
        return Regex.IsMatch(
            executableSource,
            $@"\bFile\.(?:Create|OpenWrite|Write\w*|Append\w*)\s*\(\s*{legacyPath}\b"
            + $@"|\bFile\.Open\s*\(\s*{legacyPath}\b[^;]*"
            + @"(?:FileMode\.(?:Create|CreateNew|OpenOrCreate|Truncate|Append)"
            + @"|FileAccess\.(?:Write|ReadWrite))"
            + $@"|\bnew\s+FileStream\s*\(\s*{legacyPath}\b[^;]*"
            + @"(?:FileMode\.(?:Create|CreateNew|OpenOrCreate|Truncate|Append)"
            + @"|FileAccess\.(?:Write|ReadWrite))"
            + $@"|\bnew\s+StreamWriter\s*\(\s*{legacyPath}\b");
    }

    private static string RemoveCommentsAndLiterals(string source)
    {
        char[] code = source.ToCharArray();
        int index = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    int end = source.IndexOfAny(['\r', '\n'], index + 2);
                    end = end < 0 ? source.Length : end;
                    Blank(code, index, end);
                    index = end;
                    continue;
                }

                if (source[index + 1] == '*')
                {
                    int terminator = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    int end = terminator < 0 ? source.Length : terminator + 2;
                    Blank(code, index, end);
                    index = end;
                    continue;
                }
            }

            if (TryReadStringLiteral(source, index, out int stringEnd))
            {
                Blank(code, index, stringEnd);
                index = stringEnd;
                continue;
            }

            if (source[index] == '\'')
            {
                int characterEnd = ReadEscapedLiteral(source, index, '\'');
                Blank(code, index, characterEnd);
                index = characterEnd;
                continue;
            }

            index++;
        }

        return new string(code);
    }

    private static bool TryReadStringLiteral(string source, int start, out int end)
    {
        end = start;
        int quoteIndex = start;
        bool isVerbatim = false;
        if (source[start] == '@' && start + 1 < source.Length && source[start + 1] == '"')
        {
            isVerbatim = true;
            quoteIndex = start + 1;
        }
        else if (source[start] == '$')
        {
            int cursor = start;
            while (cursor < source.Length && source[cursor] == '$')
            {
                cursor++;
            }

            if (cursor < source.Length && source[cursor] == '@')
            {
                isVerbatim = true;
                cursor++;
            }

            if (cursor >= source.Length || source[cursor] != '"')
            {
                return false;
            }

            quoteIndex = cursor;
        }
        else if (source[start] != '"')
        {
            return false;
        }

        int delimiterLength = 1;
        while (quoteIndex + delimiterLength < source.Length
            && source[quoteIndex + delimiterLength] == '"')
        {
            delimiterLength++;
        }

        if (delimiterLength >= 3)
        {
            int terminator = source.IndexOf(
                new string('"', delimiterLength),
                quoteIndex + delimiterLength,
                StringComparison.Ordinal);
            end = terminator < 0
                ? source.Length
                : terminator + delimiterLength;
            return true;
        }

        end = isVerbatim
            ? ReadVerbatimString(source, quoteIndex)
            : ReadEscapedLiteral(source, quoteIndex, '"');
        return true;
    }

    private static int ReadVerbatimString(string source, int quoteIndex)
    {
        int cursor = quoteIndex + 1;
        while (cursor < source.Length)
        {
            if (source[cursor] != '"')
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < source.Length && source[cursor + 1] == '"')
            {
                cursor += 2;
                continue;
            }

            return cursor + 1;
        }

        return source.Length;
    }

    private static int ReadEscapedLiteral(string source, int delimiterIndex, char delimiter)
    {
        int cursor = delimiterIndex + 1;
        while (cursor < source.Length)
        {
            if (source[cursor] == '\\')
            {
                cursor = Math.Min(source.Length, cursor + 2);
                continue;
            }

            if (source[cursor] == delimiter)
            {
                return cursor + 1;
            }

            cursor++;
        }

        return source.Length;
    }

    private static void Blank(char[] code, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            if (code[index] is not ('\r' or '\n'))
            {
                code[index] = ' ';
            }
        }
    }

    private static void AssertAsyncCancellationContract(Type contract)
    {
        MethodInfo[] methods = contract
            .GetMethods()
            .Where(static method => !method.IsSpecialName)
            .ToArray();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            Assert.True(
                typeof(Task).IsAssignableFrom(method.ReturnType),
                $"{contract.Name}.{method.Name} must return Task.");
            ParameterInfo[] parameters = method.GetParameters();
            ParameterInfo cancellationToken = Assert.Single(
                parameters,
                parameter => parameter.ParameterType == typeof(CancellationToken));
            Assert.Equal(parameters.Length - 1, cancellationToken.Position);
        });
    }

    private static void AssertImmutableDefinition(Type type)
    {
        Assert.True(type.IsSealed, type.FullName);
        Assert.All(
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property =>
            {
                MethodInfo? setter = property.SetMethod;
                bool isInitOnly = setter?.ReturnParameter
                    .GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
                Assert.True(
                    setter is null || isInitOnly,
                    $"{type.Name}.{property.Name} exposes a mutable setter.");
            });
    }

    private static IReadOnlyList<ProductionSource> ReadTriggerSources()
    {
        string[] directoryRoots =
        [
            "ClashSharp/ClashSharp.Core/Domain/Triggers",
            "ClashSharp/ClashSharp.Application/Triggers",
            "ClashSharp/ClashSharp.Infrastructure/Triggers",
            "ClashSharp/ClashSharp.TriggerProbe",
        ];
        IEnumerable<string> paths = directoryRoots
            .Select(AbsolutePath)
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.cs",
                SearchOption.AllDirectories));
        string appRoot = AbsolutePath("ClashSharp/ClashSharp");
        paths = paths.Concat(Directory
            .EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(
                "Trigger",
                StringComparison.OrdinalIgnoreCase)));

        return paths
            .Where(path => !HasGeneratedSegment(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ReadProductionSource)
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ProductionSource> ReadProductionSources()
    {
        string[] roots =
        [
            "ClashSharp/ClashSharp",
            "ClashSharp/ClashSharp.Application",
            "ClashSharp/ClashSharp.Core",
            "ClashSharp/ClashSharp.Infrastructure",
            "ClashSharp/ClashSharp.MihomoService",
            "ClashSharp/ClashSharp.ProcessProbe",
            "ClashSharp/ClashSharp.StartupProbe",
            "ClashSharp/ClashSharp.TriggerProbe",
        ];

        return roots
            .Select(AbsolutePath)
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !HasGeneratedSegment(path))
            .Select(ReadProductionSource)
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProductionSource ReadProductionSource(string path)
    {
        return new ProductionSource(
            Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'),
            File.ReadAllText(path));
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(AbsolutePath(relativePath));

    private static string AbsolutePath(string relativePath) =>
        Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool HasGeneratedSegment(string path)
    {
        string relativePath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');
        return relativePath.Contains("/bin/", StringComparison.Ordinal)
            || relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                directory.FullName,
                "ClashSharp",
                "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the ClashSharp repository root.");
    }

    private sealed record ProductionSource(string RelativePath, string Text);
}
