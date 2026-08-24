using System;
using System.Net.Http;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the about page.</summary>
internal static class AboutPageComposition
{
    private static readonly HttpClient ReleaseHttpClient = GitHubReleaseUpdateChecker.CreateHttpClient();

    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string applicationVersion = typeof(AboutPageComposition).Assembly.GetName().Version?.ToString()
            ?? "1.0.0.0";
        AboutViewModel viewModel = new(
            new DisplayPageLocalizationAdapter(context.Localization),
            new AboutCoreAdapter(context.MihomoCore),
            new GitHubReleaseUpdateChecker(ReleaseHttpClient, applicationVersion),
            new WindowsUriLauncher(),
            context.ErrorSink);

        return new Dependencies(
            viewModel,
            context.Localization.GetString,
            () =>
            {
                CoreConfigurationState configuration = context.CoreConfiguration.GetState();
                return new ProxyInformation(
                    context.Settings.MixedPort,
                    configuration.ConfigPath,
                    context.MihomoCore.IsBinaryAvailable,
                    context.MihomoCore.BinaryPath);
            });
    }

    /// <summary>Injected dependencies used by the about view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            AboutViewModel viewModel,
            Func<string, string> getString,
            Func<ProxyInformation> readProxyInformation)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ReadProxyInformation = readProxyInformation
                ?? throw new ArgumentNullException(nameof(readProxyInformation));
        }

        public AboutViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public Func<ProxyInformation> ReadProxyInformation { get; }
    }

    /// <summary>Immutable proxy and core information displayed by the about view.</summary>
    internal sealed record ProxyInformation(
        int MixedPort,
        string ConfigPath,
        bool IsCoreBinaryAvailable,
        string CoreBinaryPath);
}
