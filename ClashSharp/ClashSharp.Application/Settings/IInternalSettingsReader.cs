namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Provides internal consumers with installed configuration without granting settings mutation or storage access.</summary>
public interface IInternalSettingsReader
{
    /// <summary>Captures one immutable, generation-bound configuration without I/O; a retired owner rejects new captures.</summary>
    /// <returns>The complete internal configuration installed at this observation.</returns>
    InternalSettingsSnapshot CaptureSnapshot();
}
