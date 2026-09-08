namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns a retained reset until its durable commit or rollback decision is finalized.</summary>
public interface IRetainedSettingsResetReceipt : IRetainedSettingsTransactionReceipt
{
}
