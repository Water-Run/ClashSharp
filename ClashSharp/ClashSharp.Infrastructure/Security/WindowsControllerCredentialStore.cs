using ClashSharp.ApplicationModel.Security;
using ClashSharp.Security;
using Windows.Storage;

namespace ClashSharp.Infrastructure.Security;

/// <summary>Owns only the existing packaged-app controller credential slot, independently of preferences migration.</summary>
public sealed class WindowsControllerCredentialStore : IControllerCredentialStore
{
    internal const string CredentialKey = "MihomoControllerSecret";
    private readonly Func<IDictionary<string, object>> _getValues;

    /// <summary>Creates the packaged Windows boundary without opening LocalSettings.</summary>
    public WindowsControllerCredentialStore() : this(static () => ApplicationData.Current.LocalSettings.Values) { }

    internal WindowsControllerCredentialStore(Func<IDictionary<string, object>> getValues) =>
        _getValues = getValues ?? throw new ArgumentNullException(nameof(getValues));

    /// <inheritdoc />
    public bool TryRead(out string? secret)
    {
        bool present = _getValues().TryGetValue(CredentialKey, out object? value);
        secret = value as string;
        return present;
    }

    /// <inheritdoc />
    public void Write(string secret)
    {
        if (!ControllerCredentialPolicy.IsValid(secret)) { throw new ArgumentException("Invalid controller credential shape.", nameof(secret)); }
        _getValues()[CredentialKey] = secret;
    }

    /// <inheritdoc />
    public void Delete() => _getValues().Remove(CredentialKey);
}
