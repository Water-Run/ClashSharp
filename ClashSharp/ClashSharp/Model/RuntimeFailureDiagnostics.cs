using System;
using System.Linq;
using ClashSharp.Diagnostics;

namespace ClashSharp.Model;

/// <summary>Stable release-facing runtime failure areas.</summary>
public enum RuntimeFailureArea
{
    /// <summary>Windows service availability, identity, or IPC failures.</summary>
    Service,

    /// <summary>TUN ownership, interface, or lifecycle failures.</summary>
    Tun,

    /// <summary>Runtime configuration validation or application failures.</summary>
    Configuration,

    /// <summary>Proxy-provider acquisition or update failures.</summary>
    Provider,

    /// <summary>Required GeoData asset validation or availability failures.</summary>
    GeoData,

    /// <summary>Mihomo controller ownership or connectivity failures.</summary>
    Controller,

    /// <summary>Mixed listener binding or ownership failures.</summary>
    MixedPort,

    /// <summary>Network route ownership or reconciliation failures.</summary>
    Route,

    /// <summary>DNS ownership or reconciliation failures.</summary>
    Dns,
}

/// <summary>Describes one bounded diagnostic code and its actionable localized presentation.</summary>
internal readonly record struct RuntimeFailureDescriptor(
    string Code,
    RuntimeFailureArea Area,
    string MessageResourceKey);

/// <summary>Preserves one validated runtime support code across mutation compensation layers.</summary>
internal sealed class StableRuntimeDiagnosticException : InvalidOperationException,
    IStableDiagnosticCodeProvider
{
    internal StableRuntimeDiagnosticException(
        string diagnosticCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!RuntimeDiagnosticCode.IsStable(diagnosticCode))
        {
            throw new ArgumentException("The runtime diagnostic code is invalid.", nameof(diagnosticCode));
        }

        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

/// <summary>Classifies stable runtime failure codes without exposing exception details to the UI.</summary>
internal static class RuntimeFailureDiagnostics
{
    internal const string ServiceUnavailable = "service.unavailable";
    internal const string TunConflict = "tun.conflict.active_interface";
    internal const string ConfigurationRejected = "configuration.rejected";
    internal const string ProviderUpdateFailed = "provider.update_failed";
    internal const string GeoAssetsMissing = "geo.assets_missing";
    internal const string ControllerUnavailable = "controller.owner_unavailable";
    internal const string MixedPortOccupied = "mixed.port_occupied";
    internal const string RouteConflict = "route.conflict.active_tun_interface";
    internal const string DnsConflict = "dns.conflict.active_tun_interface";

    /// <summary>Returns a typed descriptor for a stable bounded code.</summary>
    internal static RuntimeFailureDescriptor Describe(string code)
    {
        if (!IsStableCode(code))
        {
            throw new ArgumentException("Runtime failure codes must be bounded lowercase identifiers.", nameof(code));
        }

        RuntimeFailureArea area = ClassifyArea(code);
        return new RuntimeFailureDescriptor(
            code,
            area,
            code == "service.ipc.endpoint_occupied"
                ? "RuntimeFailure.EndpointOccupied"
                : area switch
                {
                    RuntimeFailureArea.Tun => "RuntimeFailure.Tun",
                    RuntimeFailureArea.Configuration => "RuntimeFailure.Configuration",
                    RuntimeFailureArea.Provider => "RuntimeFailure.Provider",
                    RuntimeFailureArea.GeoData => "RuntimeFailure.GeoData",
                    RuntimeFailureArea.Controller => "RuntimeFailure.Controller",
                    RuntimeFailureArea.MixedPort => "RuntimeFailure.MixedPort",
                    RuntimeFailureArea.Route => "RuntimeFailure.Route",
                    RuntimeFailureArea.Dns => "RuntimeFailure.Dns",
                    _ => "RuntimeFailure.Service",
                });
    }

    /// <summary>Builds a safe user-facing diagnostic with a stable support code.</summary>
    internal static string Format(
        string? code,
        Func<string, string> getString,
        string fallbackMessage)
    {
        ArgumentNullException.ThrowIfNull(getString);
        ArgumentNullException.ThrowIfNull(fallbackMessage);
        if (!IsStableCode(code))
        {
            return fallbackMessage;
        }

        RuntimeFailureDescriptor descriptor = Describe(code!);
        return $"{getString(descriptor.MessageResourceKey)} [{descriptor.Code}]";
    }

    /// <summary>Extracts only an exact stable code from a bounded exception graph.</summary>
    internal static string ExtractCode(Exception exception, string fallbackCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsStableCode(fallbackCode))
        {
            throw new ArgumentException("The fallback code is invalid.", nameof(fallbackCode));
        }

        return RuntimeDiagnosticCode.Extract(exception) ?? fallbackCode;
    }

    /// <summary>Attempts to extract one validated code without manufacturing a fallback area.</summary>
    internal static bool TryExtractCode(Exception exception, out string? code)
    {
        ArgumentNullException.ThrowIfNull(exception);
        code = RuntimeDiagnosticCode.Extract(exception);
        return code is not null;
    }

    internal static bool IsStableCode(string? code)
    {
        return RuntimeDiagnosticCode.IsStable(code);
    }

    private static RuntimeFailureArea ClassifyArea(string code)
    {
        if (HasSegment(code, "provider"))
        {
            return RuntimeFailureArea.Provider;
        }

        if (HasSegment(code, "geo") || HasSegment(code, "geodata"))
        {
            return RuntimeFailureArea.GeoData;
        }

        if (HasSegment(code, "configuration") || HasSegment(code, "config"))
        {
            return RuntimeFailureArea.Configuration;
        }

        if (HasSegment(code, "controller"))
        {
            return RuntimeFailureArea.Controller;
        }

        if (HasSegment(code, "mixed"))
        {
            return RuntimeFailureArea.MixedPort;
        }

        if (HasSegment(code, "route"))
        {
            return RuntimeFailureArea.Route;
        }

        if (HasSegment(code, "dns"))
        {
            return RuntimeFailureArea.Dns;
        }

        if (HasSegment(code, "tun"))
        {
            return RuntimeFailureArea.Tun;
        }

        return RuntimeFailureArea.Service;
    }

    private static bool HasSegment(string code, string segment)
    {
        return code.Split('.').Any(value =>
            StringComparer.Ordinal.Equals(value, segment)
            || value.StartsWith(segment + "_", StringComparison.Ordinal));
    }
}
