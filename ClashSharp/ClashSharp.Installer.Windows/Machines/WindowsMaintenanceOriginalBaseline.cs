using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Complete private original-installation evidence bound to the first prepared repair.</summary>
internal sealed class WindowsMaintenanceOriginalBaseline
{
    internal const int CurrentSchema = 1;
    internal const int MaximumDocumentBytes = 128 * 1024;
    internal const string FileName = "original-installation-baseline-v1.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
    };

    [JsonConstructor]
    public WindowsMaintenanceOriginalBaseline(int schema, WindowsServicePreparationBaseline service, WindowsMaintenanceOriginalContents contents)
    {
        Schema = schema;
        Service = service ?? throw new ArgumentNullException(nameof(service));
        Contents = contents ?? throw new ArgumentNullException(nameof(contents));
        if (Schema != CurrentSchema || Service.Intent.Operation != InstallerOperation.Repair)
        {
            throw new InstallerProtocolException("installer.recovery.original_baseline_invalid");
        }
    }

    public int Schema { get; }
    public WindowsServicePreparationBaseline Service { get; }
    public WindowsMaintenanceOriginalContents Contents { get; }

    internal void RequireBoundary(WindowsMachineDeploymentPlan plan, InstallerTransactionJournal current)
    {
        Service.RequireBoundary(plan, current);
        Contents.RequirePlan(plan);
    }

    internal byte[] Serialize()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumDocumentBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InstallerProtocolException("installer.recovery.original_baseline_size_invalid");
        }
        return bytes;
    }

    internal static WindowsMaintenanceOriginalBaseline Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.recovery.original_baseline_size_invalid");
        }
        try
        {
            WindowsMaintenanceOriginalBaseline baseline = JsonSerializer.Deserialize<WindowsMaintenanceOriginalBaseline>(bytes, Options)
                ?? throw new InstallerProtocolException("installer.recovery.original_baseline_invalid");
            byte[] canonical = baseline.Serialize();
            try
            {
                if (!bytes.SequenceEqual(canonical)) { throw new InstallerProtocolException("installer.recovery.original_baseline_noncanonical"); }
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            return baseline;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InstallerProtocolException("installer.recovery.original_baseline_invalid", exception);
        }
    }
}
