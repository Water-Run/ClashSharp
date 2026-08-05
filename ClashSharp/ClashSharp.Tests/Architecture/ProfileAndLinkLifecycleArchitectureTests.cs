namespace ClashSharp.Tests.Architecture;

/// <summary>Guards page-owned asynchronous lifecycle wiring for profile persistence pages.</summary>
public sealed class ProfileAndLinkLifecycleArchitectureTests
{
    private static readonly string ApplicationRoot = Path.Combine(
        FindRepositoryRoot(),
        "ClashSharp",
        "ClashSharp");

    [Fact]
    public void ProfilesPage_RoutesEveryMutationThroughCancelablePageSession()
    {
        string source = ReadApplicationSource("View/Profiles.xaml.cs");
        string xaml = ReadApplicationSource("View/Profiles.xaml");

        Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
        Assert.Contains("_loadSession.Cancel()", source, StringComparison.Ordinal);
        Assert.Contains("_operationGate.WaitAsync(cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("SetOperationBusy(isBusy: true)", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", source, StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.ImportProfileCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.ValidateProfileCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.SetActiveProfileCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_viewModel.RenameProfileCommand.ExecuteObservedAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_viewModel.DeleteProfileCommand.ExecuteObservedAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_viewModel.RollbackProfileCommand.ExecuteObservedAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Click=\"ValidateProfileButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SetActiveProfileButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RenameProfileButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeleteProfileButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ProfileHistoryButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Command=\"{Binding ValidateProfileCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Command=\"{Binding SetActiveProfileCommand}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinksPage_RoutesDialogAndEveryMutationThroughCancelablePageSession()
    {
        string source = ReadApplicationSource("View/Links.xaml.cs");
        string xaml = ReadApplicationSource("View/Links.xaml");

        Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
        Assert.Contains("_loadSession.Cancel()", source, StringComparison.Ordinal);
        Assert.Contains("_operationGate.WaitAsync(cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("SetOperationBusy(isBusy: true)", source, StringComparison.Ordinal);
        Assert.Contains("dialog.ShowManagedAsync(cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", source, StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.AddLinkCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.CheckLinkCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.UpdateLinkCommand.ExecuteObservedAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"CheckLinksButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"UpdateLinksButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Command=\"{Binding CheckLinkCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Command=\"{Binding UpdateLinkCommand}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceBoundaries_ExposeCancelableAsyncMutations()
    {
        string profilesContract = ReadApplicationSource(
            "ViewModel/IProfileManagementCatalog.cs");
        string linksContract = ReadApplicationSource(
            "ViewModel/ISubscriptionLinkCatalog.cs");
        string profilesAdapter = ReadApplicationSource(
            "Presentation/Adapters/ProfileManagementCatalogAdapter.cs");
        string linksAdapter = ReadApplicationSource(
            "Presentation/Adapters/SubscriptionLinkCatalogAdapter.cs");

        Assert.Contains("Task<bool> TrySetActiveProfileAsync(", profilesContract, StringComparison.Ordinal);
        Assert.Contains(
            "Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(",
            linksContract,
            StringComparison.Ordinal);
        Assert.Contains("TryApplyActiveProfileAsync(", profilesAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetActiveProfile(", profilesAdapter, StringComparison.Ordinal);
        Assert.Contains("AddSubscriptionLinkAsync(name, uri, cancellationToken)", linksAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", linksAdapter, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileCatalogMutationAdmission_ReusesProcessWideBarrierAndFairGate()
    {
        string host = ReadApplicationSource("AppHost/ClashSharpAppHostFactory.cs");
        string coordinator = ReadApplicationSource("Service/ProfileCatalogMutationCoordinator.cs");

        Assert.Contains(
            "LateBoundProfileCatalogMutationCoordinator.Instance.Configure(",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "provider.GetRequiredService<MutationAdmissionBarrier>()",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "provider.GetRequiredService<FairAsyncMutationGate>()",
            host,
            StringComparison.Ordinal);
        Assert.Contains("AcquireOrdinaryAsync(cancellationToken)", coordinator, StringComparison.Ordinal);
        Assert.Contains("_mutationGate", coordinator, StringComparison.Ordinal);
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
