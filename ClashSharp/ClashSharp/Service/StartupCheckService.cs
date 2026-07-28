using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Builds a localized startup-health snapshot from background system probes.</summary>
/// <remarks>
/// Localization is captured before background work starts so every row uses one consistent
/// language snapshot and probe code remains display-independent. Probe failures become localized
/// unavailable rows and are sent to the application error sink; exception text is never returned
/// as display content.
/// </remarks>
public sealed class StartupCheckService
{
    private readonly IStartupCheckProbe _probe;
    private readonly Func<string, string> _getString;
    private readonly IApplicationErrorSink _errorSink;

    internal StartupCheckService(
        IStartupCheckProbe probe,
        Func<string, string> getString,
        IApplicationErrorSink errorSink)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
    }

    /// <summary>
    /// Collects subscription, transparent-proxy, fallback, and stale-proxy checks once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the current presentation-owned collection.</param>
    /// <returns>An immutable, localized four-row startup-health snapshot.</returns>
    public async Task<IReadOnlyList<StartupCheckItem>> GetChecksAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupCheckText text = CaptureLocalizedText();

        CheckEvaluation[] evaluations = await Task.Run(
            () => EvaluateChecks(text, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        foreach (CheckEvaluation evaluation in evaluations)
        {
            if (evaluation.Error is not null)
            {
                await ReportProbeFailureAsync(
                    evaluation.OperationName,
                    evaluation.Error).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<StartupCheckItem> checks = new(evaluations.Length);
        foreach (CheckEvaluation evaluation in evaluations)
        {
            checks.Add(evaluation.Item);
        }

        return checks.AsReadOnly();
    }

    internal static StartupCheckItem BuildTransparentProxyCheck(
        bool transparentProxyEnabled,
        MihomoServiceStatus status,
        string title,
        string disabledDescription,
        string missingDescription,
        string unknownDescription)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(disabledDescription);
        ArgumentNullException.ThrowIfNull(missingDescription);
        ArgumentNullException.ThrowIfNull(unknownDescription);
        if (!transparentProxyEnabled)
        {
            return new StartupCheckItem(true, title, disabledDescription);
        }

        string statusMessage = string.IsNullOrWhiteSpace(status.Message)
            ? unknownDescription
            : status.Message;
        string description = !status.IsKnown
            ? statusMessage
            : status.IsInstalled
                ? statusMessage
                : missingDescription;
        return new StartupCheckItem(
            status.IsKnown && status.IsInstalled,
            title,
            description);
    }

    private CheckEvaluation[] EvaluateChecks(
        StartupCheckText text,
        CancellationToken cancellationToken)
    {
        return
        [
            EvaluateSubscriptionCheck(text, cancellationToken),
            EvaluateTransparentProxyCheck(text, cancellationToken),
            EvaluateFallbackCheck(text, cancellationToken),
            EvaluateStaleProxyCheck(text, cancellationToken),
        ];
    }

    private CheckEvaluation EvaluateSubscriptionCheck(
        StartupCheckText text,
        CancellationToken cancellationToken)
    {
        const string operationName = "startup-check-subscription";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasSubscription = _probe.HasSubscription(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CheckEvaluation.Succeeded(
                new StartupCheckItem(
                    hasSubscription,
                    text.SubscriptionTitle,
                    hasSubscription
                        ? text.SubscriptionReady
                        : text.SubscriptionMissing),
                operationName);
        }
        catch (Exception exception) when (CanContain(exception, cancellationToken))
        {
            return CheckEvaluation.Failed(
                new StartupCheckItem(false, text.SubscriptionTitle, text.Unavailable),
                operationName,
                exception);
        }
    }

    private CheckEvaluation EvaluateTransparentProxyCheck(
        StartupCheckText text,
        CancellationToken cancellationToken)
    {
        const string operationName = "startup-check-transparent-proxy";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isEnabled = _probe.IsTransparentProxyEnabled(cancellationToken);
            MihomoServiceStatus status = isEnabled
                ? _probe.GetMihomoStatus(cancellationToken)
                : default;
            cancellationToken.ThrowIfCancellationRequested();
            return CheckEvaluation.Succeeded(
                BuildTransparentProxyCheck(
                    isEnabled,
                    status,
                    text.TransparentProxyTitle,
                    text.TransparentProxyDisabled,
                    text.TransparentProxyMissing,
                    text.MihomoStatusUnknown),
                operationName);
        }
        catch (Exception exception) when (CanContain(exception, cancellationToken))
        {
            return CheckEvaluation.Failed(
                new StartupCheckItem(false, text.TransparentProxyTitle, text.Unavailable),
                operationName,
                exception);
        }
    }

    private CheckEvaluation EvaluateFallbackCheck(
        StartupCheckText text,
        CancellationToken cancellationToken)
    {
        const string operationName = "startup-check-fallback";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isRegistered = _probe.IsFallbackRegistered(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CheckEvaluation.Succeeded(
                new StartupCheckItem(
                    isRegistered,
                    text.FallbackTitle,
                    isRegistered
                        ? text.FallbackRegistered
                        : text.FallbackNotRegistered),
                operationName);
        }
        catch (Exception exception) when (CanContain(exception, cancellationToken))
        {
            return CheckEvaluation.Failed(
                new StartupCheckItem(false, text.FallbackTitle, text.Unavailable),
                operationName,
                exception);
        }
    }

    private CheckEvaluation EvaluateStaleProxyCheck(
        StartupCheckText text,
        CancellationToken cancellationToken)
    {
        const string operationName = "startup-check-stale-proxy";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsProxyState state = _probe.GetWindowsProxyState(cancellationToken);
            int mixedPort = _probe.GetMixedPort(cancellationToken);
            bool hasStaleProxy = _probe.IsStaleProxy(
                state,
                mixedPort,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CheckEvaluation.Succeeded(
                new StartupCheckItem(
                    !hasStaleProxy,
                    text.StaleProxyTitle,
                    hasStaleProxy
                        ? text.StaleProxyDetected
                        : text.StaleProxyClean),
                operationName);
        }
        catch (Exception exception) when (CanContain(exception, cancellationToken))
        {
            return CheckEvaluation.Failed(
                new StartupCheckItem(false, text.StaleProxyTitle, text.Unavailable),
                operationName,
                exception);
        }
    }

    private StartupCheckText CaptureLocalizedText()
    {
        return new StartupCheckText(
            _getString("StartupPrompt.Check.Subscription.Title"),
            _getString("StartupPrompt.Check.Subscription.Ready"),
            _getString("StartupPrompt.Check.Subscription.Missing"),
            _getString("StartupPrompt.Check.TransparentProxy.Title"),
            _getString("StartupPrompt.Check.TransparentProxy.Disabled"),
            _getString("StartupPrompt.Check.TransparentProxy.Missing"),
            _getString("MihomoService.Status.Unknown"),
            _getString("StartupPrompt.Check.Fallback.Title"),
            _getString("StartupPrompt.Check.Fallback.Registered"),
            _getString("StartupPrompt.Check.Fallback.NotRegistered"),
            _getString("StartupPrompt.Check.StaleProxy.Title"),
            _getString("StartupPrompt.Check.StaleProxy.Clean"),
            _getString("StartupPrompt.Check.StaleProxy.Detected"),
            _getString("StartupPrompt.Check.Unavailable"));
    }

    private async Task ReportProbeFailureAsync(string operationName, Exception exception)
    {
        try
        {
            await _errorSink.ReportAsync(
                new ApplicationError(operationName, exception),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // The primary diagnostic sink has no independent fallback channel.
        }
    }

    private static bool CanContain(
        Exception exception,
        CancellationToken cancellationToken)
    {
        return !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken);
    }

    private sealed record StartupCheckText(
        string SubscriptionTitle,
        string SubscriptionReady,
        string SubscriptionMissing,
        string TransparentProxyTitle,
        string TransparentProxyDisabled,
        string TransparentProxyMissing,
        string MihomoStatusUnknown,
        string FallbackTitle,
        string FallbackRegistered,
        string FallbackNotRegistered,
        string StaleProxyTitle,
        string StaleProxyClean,
        string StaleProxyDetected,
        string Unavailable);

    private readonly record struct CheckEvaluation(
        StartupCheckItem Item,
        string OperationName,
        Exception? Error)
    {
        public static CheckEvaluation Succeeded(
            StartupCheckItem item,
            string operationName)
        {
            return new CheckEvaluation(item, operationName, null);
        }

        public static CheckEvaluation Failed(
            StartupCheckItem item,
            string operationName,
            Exception error)
        {
            return new CheckEvaluation(item, operationName, error);
        }
    }
}
