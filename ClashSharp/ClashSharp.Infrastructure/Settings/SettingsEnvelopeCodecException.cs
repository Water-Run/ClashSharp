namespace ClashSharp.Infrastructure.Settings;

internal sealed class SettingsEnvelopeCodecException : Exception
{
    public SettingsEnvelopeCodecException(
        string code,
        string path,
        Exception? innerException = null)
        : base(code, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Code = code;
        Path = path;
    }

    public string Code { get; }

    public string Path { get; }
}
