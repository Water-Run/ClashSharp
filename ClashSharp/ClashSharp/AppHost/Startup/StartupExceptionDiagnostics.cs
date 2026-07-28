using System;

namespace ClashSharp.Hosting.Startup;

/// <summary>Formats application-boundary diagnostics without invoking exception-controlled text.</summary>
internal static class StartupExceptionDiagnostics
{
    /// <summary>Creates a stable debug message using only the runtime exception type.</summary>
    /// <param name="exception">Application-boundary exception to identify. Must not be null.</param>
    /// <returns>A stable message that does not read <see cref="Exception.Message"/> or call <see cref="object.ToString"/>.</returns>
    internal static string FormatDebugMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Type exceptionType = exception.GetType();
        string typeName = exceptionType.FullName ?? exceptionType.Name;
        return $"ClashSharp operation failed ({typeName}); recording diagnostic.";
    }
}
