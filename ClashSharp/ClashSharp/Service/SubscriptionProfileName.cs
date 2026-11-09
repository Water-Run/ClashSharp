using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ClashSharp.Service;

/// <summary>Reads bounded provider names without interpreting URL paths or credentials as display text.</summary>
internal static class SubscriptionProfileName
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string? Read(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.TryGetValues("profile-title", out var values))
        {
            string? raw = values.FirstOrDefault();
            if (raw is { Length: <= 2048 })
            {
                if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                {
                    try { raw = StrictUtf8.GetString(Convert.FromBase64String(raw[7..])); }
                    catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { raw = null; }
                }
                if (Normalize(raw) is string title) { return title; }
            }
        }
        if (response.Content.Headers.TryGetValues("Content-Disposition", out var dispositions))
        {
            string? raw = dispositions.FirstOrDefault();
            if (raw is { Length: <= 4096 } && ContentDispositionHeaderValue.TryParse(raw, out ContentDispositionHeaderValue? disposition))
            {
                return Normalize(disposition.FileNameStar) ?? Normalize(disposition.FileName);
            }
        }
        return null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) { return null; }
        string text = value.Trim().Trim('"').Trim();
        int separator = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
        if (separator >= 0) { text = text[(separator + 1)..]; }
        foreach (string extension in new[] { ".yaml", ".yml", ".txt", ".conf" })
        {
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) { text = text[..^extension.Length]; break; }
        }
        text = text.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > 128 ? null : text;
    }
}
