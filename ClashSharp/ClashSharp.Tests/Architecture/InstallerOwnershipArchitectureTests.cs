namespace ClashSharp.Tests.Architecture;

/// <summary>Guards the traditional Installer/App ownership boundary.</summary>
public sealed class InstallerOwnershipArchitectureTests
{
    private static readonly string ApplicationRoot = Path.Combine(
        FindRepositoryRoot(),
        "ClashSharp",
        "ClashSharp");

    /// <summary>
    /// Keeps service installation, repair, migration, and removal out of the production settings
    /// surface. The App may observe service status because TUN availability depends on it.
    /// </summary>
    [Fact]
    public void SettingsServiceSurface_IsReadOnlyAndHasNoLifecycleCommands()
    {
        string contract = ReadApplicationSource("ViewModel/IMihomoServiceController.cs");
        string adapter = ReadApplicationSource(
            "Presentation/Adapters/MihomoServiceControllerAdapter.cs");
        string viewModel = ReadApplicationSource("ViewModel/SettingsViewModel.cs");
        string view = ReadApplicationSource("View/Settings.xaml");
        string manager = ReadApplicationSource("Service/MihomoServiceManager.cs");

        Assert.Contains("GetLatestStatus()", contract, StringComparison.Ordinal);
        Assert.Contains("RefreshStatusAsync", contract, StringComparison.Ordinal);
        Assert.Contains("_manager.GetStatusAsync", adapter, StringComparison.Ordinal);
        Assert.Contains("MihomoServiceStatusText", view, StringComparison.Ordinal);
        Assert.DoesNotContain("DeployAsync(", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("UninstallAsync(", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("DeployAsync(", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("UninstallAsync(", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public async Task<MihomoServiceStatus> DeployAsync",
            manager,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public async Task<MihomoServiceStatus> UninstallAsync",
            manager,
            StringComparison.Ordinal);

        string serviceRow = ReadElement(view, "TransparentProxyServiceRow");
        Assert.DoesNotContain("<Button", serviceRow, StringComparison.Ordinal);

        string[] lifecycleTokens =
        [
            "DeployMihomoService",
            "UninstallMihomoService",
            "_mihomoServiceController.DeployAsync",
            "_mihomoServiceController.UninstallAsync",
        ];

        foreach (string token in lifecycleTokens)
        {
            Assert.DoesNotContain(token, contract, StringComparison.Ordinal);
            Assert.DoesNotContain(token, adapter, StringComparison.Ordinal);
            Assert.DoesNotContain(token, viewModel, StringComparison.Ordinal);
            Assert.DoesNotContain(token, view, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("_manager.DeployAsync", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("_manager.UninstallAsync", adapter, StringComparison.Ordinal);
    }

    /// <summary>The App consumes only the Installer-owned ProgramData association.</summary>
    [Fact]
    public void ProductionServiceEndpoint_HasNoAppSettingsCredentialFallback()
    {
        string factory = ReadApplicationSource("Service/MihomoServiceManagerFactory.cs");
        string settings = ReadApplicationSource("Service/AppSettingsService.cs");
        string actions = ReadApplicationSource("Service/ApplicationActionService.cs");

        Assert.Contains(
            "MihomoServiceIpcEndpoint.LoadForCurrentUser()",
            factory,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoServiceIpcToken", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoServiceIpcToken", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoServiceIpcToken", actions, StringComparison.Ordinal);
    }

    /// <summary>Ensures every shipped language directs lifecycle work to the Installer.</summary>
    [Theory]
    [InlineData("English", "ClashSharp Installer", "Repair")]
    [InlineData("SimplifiedChinese", "ClashSharp 安装器", "修复")]
    [InlineData("TraditionalChinese", "ClashSharp 安裝程式", "修復")]
    [InlineData("Russian", "установщик ClashSharp", "Восстановить")]
    [InlineData("French", "programme d’installation de ClashSharp", "Réparer")]
    [InlineData("German", "ClashSharp-Installationsprogramm", "Reparieren")]
    public void ServiceDescription_DirectsLifecycleWorkToInstaller(
        string catalog,
        string installerText,
        string repairText)
    {
        string resources = ReadApplicationSource(
            $"Strings/Catalogs/LocalizationResources.{catalog}.cs");

        string description = ReadResourceValue(
            resources,
            "Settings.TransparentProxy.Service.Description");

        Assert.Contains(installerText, description, StringComparison.Ordinal);
        Assert.Contains(repairText, description, StringComparison.Ordinal);
    }

    /// <summary>The App observes the public transaction marker only after acquiring its lifetime barrier.</summary>
    [Fact]
    public void InstallerTransactionGate_IsReadOnlyAndPrecedesRuntimeMutation()
    {
        string app = ReadApplicationSource("App.xaml.cs");
        string reader = ReadApplicationSource("Service/InstallerTransactionStateReader.cs");
        string gate = ReadApplicationSource("AppHost/Startup/InstallerTransactionStartupGate.cs");
        string hostFactory = ReadApplicationSource("AppHost/ClashSharpAppHostFactory.cs");
        string mainWindow = ReadApplicationSource("MainWindow.xaml.cs");

        int barrierAcquire = app.IndexOf(".AcquireAsync(cancellationToken)", StringComparison.Ordinal);
        int markerRead = app.IndexOf("_installerTransactionStateReader.Read()", StringComparison.Ordinal);
        int watchdogArm = app.IndexOf("_recoveryWatchdog.TryArm()", StringComparison.Ordinal);
        Assert.True(barrierAcquire >= 0 && markerRead > barrierAcquire && watchdogArm > markerRead);
        Assert.Contains(
            "_installerTransactionState == InstallerTransactionState.Clear",
            app,
            StringComparison.Ordinal);

        Assert.Contains("CommonApplicationData", reader, StringComparison.Ordinal);
        Assert.Contains("ProductDirectoryName = \"ClashSharp\"", reader, StringComparison.Ordinal);
        Assert.Contains("InstallerDirectoryName = \"Installer\"", reader, StringComparison.Ordinal);
        Assert.Contains("PublicMarkerFileName = \"transaction.json\"", reader, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Create", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", reader, StringComparison.Ordinal);

        Assert.Contains("public int Order => 125", gate, StringComparison.Ordinal);
        Assert.Contains("MutationAdmissionClosure.Shutdown", gate, StringComparison.Ordinal);
        Assert.Contains("StartupStepResult.Fatal", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("RepairAsync", gate, StringComparison.Ordinal);
        Assert.Contains("new InstallerTransactionStartupGate", hostFactory, StringComparison.Ordinal);
        Assert.Contains("Startup.Shell.InstallerTransactionPending", mainWindow, StringComparison.Ordinal);
    }

    /// <summary>Every shipped language directs a blocked transaction to manual Installer Repair.</summary>
    [Theory]
    [InlineData("English", "ClashSharp Installer", "Repair", "transparent proxy (TUN)")]
    [InlineData("SimplifiedChinese", "ClashSharp 安装器", "修复", "透明代理（TUN）")]
    [InlineData("TraditionalChinese", "ClashSharp 安裝程式", "修復", "透明代理（TUN）")]
    [InlineData("Russian", "установщика ClashSharp", "Восстановить", "прозрачный прокси (TUN)")]
    [InlineData("French", "programme d’installation de ClashSharp", "Réparer", "proxy transparent (TUN)")]
    [InlineData("German", "ClashSharp-Installationsprogramms", "Reparieren", "transparenten Proxy (TUN)")]
    public void InstallerTransactionPrompt_DirectsRepairWithoutOfferingAppRepair(
        string catalog,
        string installerText,
        string repairText,
        string transparentProxyText)
    {
        string resources = ReadApplicationSource(
            $"Strings/Catalogs/LocalizationResources.{catalog}.cs");

        string prompt = ReadResourceValue(
            resources,
            "Startup.Shell.InstallerTransactionPending");

        Assert.Contains(installerText, prompt, StringComparison.Ordinal);
        Assert.Contains(repairText, prompt, StringComparison.Ordinal);
        Assert.Contains(transparentProxyText, prompt, StringComparison.Ordinal);
    }

    private static string ReadResourceValue(string catalogSource, string key)
    {
        string prefix = $"[\"{key}\"] = \"";
        int valueStart = catalogSource.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(valueStart >= 0, $"Missing localization key: {key}");
        valueStart += prefix.Length;
        int valueEnd = catalogSource.IndexOf("\",", valueStart, StringComparison.Ordinal);
        Assert.True(valueEnd >= valueStart, $"Malformed localization value: {key}");
        return catalogSource[valueStart..valueEnd];
    }

    private static string ReadElement(string xaml, string elementName)
    {
        string nameToken = $"x:Name=\"{elementName}\"";
        int nameIndex = xaml.IndexOf(nameToken, StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Missing XAML element: {elementName}");
        int elementStart = xaml.LastIndexOf("<components:SettingRow", nameIndex, StringComparison.Ordinal);
        Assert.True(elementStart >= 0, $"Missing SettingRow start for: {elementName}");
        int elementEnd = xaml.IndexOf("</components:SettingRow>", nameIndex, StringComparison.Ordinal);
        Assert.True(elementEnd >= nameIndex, $"Missing SettingRow end for: {elementName}");
        elementEnd += "</components:SettingRow>".Length;
        return xaml[elementStart..elementEnd];
    }

    private static string ReadApplicationSource(string relativePath)
    {
        string path = Path.Combine(
            ApplicationRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing application source: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }
}
