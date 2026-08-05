namespace ClashSharp.Diagnostics;

/// <summary>Implemented by failures that carry one validated, user-facing runtime support code.</summary>
public interface IStableDiagnosticCodeProvider
{
    /// <summary>Gets the validated stable support code carried by this failure.</summary>
    string DiagnosticCode { get; }
}

/// <summary>Shared grammar and bounded exception-graph extraction for runtime support codes.</summary>
public static class RuntimeDiagnosticCode
{
    private const int MaximumCodeLength = 128;
    private const int MaximumExceptionCount = 16;

    private static readonly string[] SupportedPrefixes =
    [
        "service.provisioning.",
        "service.child.",
        "service.controller.",
        "service.ipc.",
        "controller.",
        "provider.",
        "geo.",
        "configuration.",
        "tun.",
        "route.",
        "mixed.",
        "dns.",
        "installer.transaction.",
    ];

    /// <summary>Returns whether a value is a bounded code in the supported runtime taxonomy.</summary>
    public static bool IsStable(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumCodeLength)
        {
            return false;
        }

        bool hasSeparator = false;
        bool previousWasSeparator = false;
        foreach (char character in code)
        {
            if (character == '.')
            {
                if (previousWasSeparator)
                {
                    return false;
                }

                hasSeparator = true;
                previousWasSeparator = true;
                continue;
            }

            if (character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is '_' or '-')
            {
                previousWasSeparator = false;
                continue;
            }

            return false;
        }

        return hasSeparator
            && code[0] != '.'
            && code[^1] != '.'
            && (StringComparer.Ordinal.Equals(code, "service.unavailable")
                || SupportedPrefixes.Any(prefix =>
                    code.StartsWith(prefix, StringComparison.Ordinal)));
    }

    /// <summary>Returns the first validated code from a bounded exception graph.</summary>
    public static string? Extract(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        Stack<Exception> pending = new();
        pending.Push(exception);
        while (pending.Count > 0 && visited.Count < MaximumExceptionCount)
        {
            Exception current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is IStableDiagnosticCodeProvider typed
                && IsStable(typed.DiagnosticCode))
            {
                return typed.DiagnosticCode;
            }

            string message = current.Message.Trim();
            if (IsStable(message))
            {
                return message;
            }

            if (current is AggregateException aggregate)
            {
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return null;
    }
}
