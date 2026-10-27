using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using Microsoft.Win32;

namespace ClashSharp.Installer.Windows.Execution;

/// <summary>A bounded user-only intent; it never supplies machine paths, credentials or a target SID.</summary>
internal sealed record WindowsInstallerPreferencesDocument(int Schema, string Id, string PackageFamilyName,
    string PackageVersion, string PayloadSha256, InstallerPreferences Preferences,
    bool InitializeApplicationDefaults = true, bool CoreVerified = false);

internal interface IWindowsInstallerPreferencesStore
{
    WindowsInstallerPreferencesDocument? ReadPending();
    WindowsInstallerPreferencesDocument? ReadInstalled();
    void Stage(WindowsInstallerPreferencesDocument document, WindowsInstallerPreferencesDocument? expected);
    void Complete(WindowsInstallerPreferencesDocument document);
    void RemoveInstalled(WindowsInstallerPreferencesDocument? expected);
    void RemovePending(WindowsInstallerPreferencesDocument? expected);
}

/// <summary>Persists one atomic current-user intent across installer shutdown and package registration.</summary>
internal sealed class WindowsInstallerPreferencesStore : IWindowsInstallerPreferencesStore
{
    private const string PendingName = "PendingPreferences";
    private const string InstalledName = "InstalledPreferences";
    private const int MaximumDocumentBytes = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };
    private readonly string _packageFamilyName;
    private readonly string _expectedSid;
    private readonly string _subKey;

    internal WindowsInstallerPreferencesStore(InstallerReleaseManifest manifest, string expectedSid)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        InstallerProtocolValidation.ValidateTargetSid(expectedSid);
        _packageFamilyName = manifest.PackageIdentity.PackageFamilyName;
        _expectedSid = expectedSid;
        _subKey = @"Software\ClashSharp\Installer\" + _packageFamilyName;
    }

    public WindowsInstallerPreferencesDocument? ReadPending() => Read(PendingName);
    public WindowsInstallerPreferencesDocument? ReadInstalled() => Read(InstalledName);

    public void Stage(WindowsInstallerPreferencesDocument document, WindowsInstallerPreferencesDocument? expected)
    {
        RequireUser();
        string encoded = Encode(document, _packageFamilyName);
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey key = root.CreateSubKey(_subKey, writable: true)
            ?? throw Failure("store_unavailable");
        RequireExpected(key, PendingName, expected);
        key.SetValue(PendingName, encoded, RegistryValueKind.String);
        key.Flush();
        RequireExact(key, PendingName, encoded);
    }

    public void Complete(WindowsInstallerPreferencesDocument document)
    {
        RequireUser();
        string encoded = Encode(document, _packageFamilyName);
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey key = root.OpenSubKey(_subKey, writable: true)
            ?? throw Failure("pending_missing");
        RequireExpected(key, PendingName, document);
        // Writing installed intent before releasing pending intent makes an interrupted completion replayable.
        key.SetValue(InstalledName, encoded, RegistryValueKind.String);
        key.Flush();
        RequireExact(key, InstalledName, encoded);
        RequireExpected(key, PendingName, document);
        key.DeleteValue(PendingName, throwOnMissingValue: true);
        key.Flush();
        if (key.GetValue(PendingName) is not null) { throw Failure("completion_unconfirmed"); }
    }

    public void RemoveInstalled(WindowsInstallerPreferencesDocument? expected) => RemoveValue(InstalledName, expected);

    public void RemovePending(WindowsInstallerPreferencesDocument? expected) => RemoveValue(PendingName, expected);

    private void RemoveValue(string name, WindowsInstallerPreferencesDocument? expected)
    {
        RequireUser();
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(_subKey, writable: true);
        if (key is null)
        {
            if (expected is not null) { throw Failure("store_changed"); }
            return;
        }
        RequireExpected(key, name, expected);
        if (expected is not null) { key.DeleteValue(name, throwOnMissingValue: true); }
        key.Flush();
    }

    internal static string Encode(WindowsInstallerPreferencesDocument document, string packageFamilyName)
    {
        Validate(document, packageFamilyName);
        string value = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(value) > MaximumDocumentBytes) { throw Failure("document_budget"); }
        return value;
    }

    internal static WindowsInstallerPreferencesDocument Decode(string encoded, string packageFamilyName)
    {
        if (Encoding.UTF8.GetByteCount(encoded) > MaximumDocumentBytes) { throw Failure("document_budget"); }
        try
        {
            WindowsInstallerPreferencesDocument document = JsonSerializer.Deserialize<WindowsInstallerPreferencesDocument>(encoded, JsonOptions)
                ?? throw Failure("document_invalid");
            string canonical = Encode(document, packageFamilyName);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(encoded), Encoding.UTF8.GetBytes(canonical)))
            {
                throw Failure("document_noncanonical");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException("installer.preferences.document_invalid", exception);
        }
    }

    private WindowsInstallerPreferencesDocument? Read(string name)
    {
        RequireUser();
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(_subKey, writable: false);
        return key is null ? null : ReadValue(key, name);
    }

    private WindowsInstallerPreferencesDocument? ReadValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string encoded when key.GetValueKind(name) == RegistryValueKind.String => Decode(encoded, _packageFamilyName),
            _ => throw Failure("document_type_invalid"),
        };
    }

    private void RequireExpected(RegistryKey key, string name, WindowsInstallerPreferencesDocument? expected)
    {
        if (ReadValue(key, name) != expected) { throw Failure("store_changed"); }
    }

    private static void RequireExact(RegistryKey key, string name, string encoded)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string actual
            || !string.Equals(actual, encoded, StringComparison.Ordinal)) { throw Failure("write_unconfirmed"); }
    }

    private static void Validate(WindowsInstallerPreferencesDocument document, string packageFamilyName)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Schema != 1 || !Guid.TryParseExact(document.Id, "N", out Guid id) || id == Guid.Empty || id.ToString("N") != document.Id
            || !string.Equals(document.PackageFamilyName, packageFamilyName, StringComparison.Ordinal)
            || document.Preferences is null)
        {
            throw Failure("document_invalid");
        }
        InstallerProtocolValidation.ValidatePackageVersion(document.PackageVersion);
        InstallerProtocolValidation.ValidateLowerHex256(document.PayloadSha256, "installer.preferences.payload_invalid");
        document.Preferences.Validate();
    }

    private void RequireUser()
    {
        if (!string.Equals(WindowsInstallerCurrentUser.GetSid(), _expectedSid, StringComparison.Ordinal))
        {
            throw Failure("user_changed");
        }
    }

    private static InstallerProtocolException Failure(string code) => new("installer.preferences." + code);
}
