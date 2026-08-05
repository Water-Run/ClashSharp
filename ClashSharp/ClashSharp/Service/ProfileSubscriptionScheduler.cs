using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Catalog operations required by automatic subscription updates.</summary>
internal interface IProfileSubscriptionSchedulerCatalog
{
    IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now);

    Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken);

    Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>Periodically imports enabled subscription links whose persisted interval has elapsed.</summary>
internal sealed class ProfileSubscriptionScheduler : IRuntimeParticipant
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly IProfileSubscriptionSchedulerCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly SupervisedLoop _supervisor;

    internal ProfileSubscriptionScheduler(
        IProfileSubscriptionSchedulerCatalog catalog,
        TimeProvider timeProvider,
        Action<string, string, string, string?> appendLog,
        ISupervisorClock? clock = null,
        SupervisorBackoffPolicy? backoff = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _supervisor = new SupervisedLoop(
            "profile-subscription-updates",
            UpdateDueSubscriptionsAsync,
            () => PollInterval,
            clock ?? SystemSupervisorClock.Instance,
            backoff ?? SupervisorBackoffPolicy.CreateProduction("profile-subscription-updates"),
            initialDelay: static () => TimeSpan.Zero);
    }

    public string Name => _supervisor.Name;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.RetryPendingProfileCleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            _appendLog(
                "Warning",
                "Profiles",
                "Pending profile cleanup could not be retried during scheduler startup.",
                exception.GetType().Name);
        }

        await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        return _supervisor.QuiesceAsync(cancellationToken);
    }

    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        return _supervisor.ResumeAsync(priorState, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _supervisor.StopAsync(cancellationToken);
    }

    /// <summary>Runs one deterministic scheduler pass, continuing after an individual link failure.</summary>
    internal async Task UpdateDueSubscriptionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileSubscriptionLink> dueLinks = _catalog.GetDueSubscriptionLinks(
            _timeProvider.GetUtcNow());
        foreach (ProfileSubscriptionLink link in dueLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProfileImportResult? result = await _catalog
                    .ImportDueSubscriptionLinkAsync(
                        link,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    continue;
                }

                _appendLog(
                    "Info",
                    "Profiles",
                    "Automatic subscription update completed.",
                    result.Value.ProfileId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or HttpRequestException
                    or IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or InvalidOperationException
                    or AggregateException
                    or OperationCanceledException
                && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                _appendLog(
                    "Warning",
                    "Profiles",
                    "Automatic subscription update failed.",
                    $"{link.Id}:{exception.GetType().Name}");
            }
        }
    }
}

/// <summary>Adapts the profile catalog to the automatic update scheduler.</summary>
internal sealed class ProfileSubscriptionSchedulerCatalogAdapter(ProfileCatalogService catalog) :
    IProfileSubscriptionSchedulerCatalog
{
    public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now)
    {
        return catalog.GetDueSubscriptionLinks(now);
    }

    public Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken)
    {
        return catalog.RetryPendingProfileCleanupAsync(cancellationToken);
    }

    public Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return catalog.ImportDueSubscriptionLinkAsync(link, now, cancellationToken);
    }
}
