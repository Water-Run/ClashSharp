using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Private write-ahead evidence for the original owned service. BinaryPath contains an IPC
/// credential and must never enter public journals, parent IPC, logs, UI, or release assets.
/// The recovery coordinator must additionally prove package, payload, association and trust
/// preservation before this evidence can authorize restoration.
/// </summary>
internal sealed class WindowsServicePreparationBaseline
{
    internal const int CurrentSchema = 1;
    internal const int MaximumDocumentBytes = 64 * 1024;
    internal const string FileName = "service-preparation-baseline-v1.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 6,
    };

    [JsonConstructor]
    public WindowsServicePreparationBaseline(int schema, InstallerTransactionJournal intent, WindowsServiceSnapshot service)
    {
        Schema = schema;
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        ArgumentNullException.ThrowIfNull(service);
        Service = service with
        {
            Configuration = service.Configuration with
            {
                Dependencies = Array.AsReadOnly(service.Configuration.Dependencies.ToArray()),
            },
            ProcessId = 0,
        };
        Validate();
    }

    public int Schema { get; }
    public InstallerTransactionJournal Intent { get; }
    public WindowsServiceSnapshot Service { get; }

    internal static WindowsServicePreparationBaseline Capture(WindowsMachineDeploymentPlan plan,
        InstallerTransactionJournal intent, WindowsServiceSnapshot service)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        var baseline = new WindowsServicePreparationBaseline(CurrentSchema, intent, service);
        baseline.RequireBoundary(plan, intent);
        return baseline;
    }

    internal void RequireBoundary(WindowsMachineDeploymentPlan plan, InstallerTransactionJournal current)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(current);
        Validate();
        plan.Validate();
        current.Validate();
        if (current.Phase is not (InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved)
            || current.TransactionId != Intent.TransactionId
            || !current.Matches(plan.Request)
            || !Intent.Matches(plan.Request)
            || !WindowsServiceConfigurationVerifier.ConfigurationMatches(Service.Configuration, plan.Service)
            || Service.DaclSddl != WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(plan.Request.TargetSid))
        {
            throw new InstallerProtocolException("installer.recovery.service_baseline_mismatch");
        }
    }

    internal byte[] Serialize()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaximumDocumentBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InstallerProtocolException("installer.recovery.service_baseline_size_invalid");
        }
        return bytes;
    }

    internal static WindowsServicePreparationBaseline Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.recovery.service_baseline_size_invalid");
        }
        try
        {
            WindowsServicePreparationBaseline baseline = JsonSerializer.Deserialize<WindowsServicePreparationBaseline>(bytes, Options)
                ?? throw new InstallerProtocolException("installer.recovery.service_baseline_invalid");
            byte[] canonical = baseline.Serialize();
            try
            {
                if (!bytes.SequenceEqual(canonical))
                {
                    throw new InstallerProtocolException("installer.recovery.service_baseline_noncanonical");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return baseline;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InstallerProtocolException("installer.recovery.service_baseline_invalid", exception);
        }
    }

    private void Validate()
    {
        Intent.Validate();
        Service.Validate();
        Service.Configuration.ValidateExpected();
        if (Schema != CurrentSchema || Intent.Operation != InstallerOperation.Repair
            || Intent.Phase != InstallerTransactionPhase.Prepared || Service.ProcessId != 0
            || Service.RuntimeState is not (WindowsServiceRuntimeState.Running or WindowsServiceRuntimeState.Stopped)
            || Service.DaclSddl != WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Intent.TargetSid))
        {
            throw new InstallerProtocolException("installer.recovery.service_baseline_invalid");
        }
    }
}
