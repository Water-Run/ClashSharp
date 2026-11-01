using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Downloads a subscription immediately, reusing its saved link when an import is retried.</summary>
internal static class SubscriptionImportWorkflow
{
    internal static async Task<(ProfileSubscriptionLink Link, ProfileImportResult Profile)> ImportAsync(
        ISubscriptionLinkCatalog catalog, string name, string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        string input = uri.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new ArgumentException("A subscription requires an absolute HTTP or HTTPS URL.", nameof(uri));
        }
        ProfileSubscriptionLink? existing = catalog.GetSubscriptionLinks().Where(link =>
            Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? saved) && saved.Equals(parsed))
            .Select(static link => (ProfileSubscriptionLink?)link).FirstOrDefault();
        ProfileSubscriptionLink link = existing ?? await catalog.AddSubscriptionLinkAsync(
            string.IsNullOrWhiteSpace(name) ? parsed.Host : name.Trim(), input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep a failed newly saved link available for editing/retry; never activate it implicitly.
        ProfileImportResult profile = await catalog.ImportSubscriptionLinkAsync(link, cancellationToken);
        return (link, profile);
    }
}
