using ClashSharp.Infrastructure.Security;
using ClashSharp.Tests.Unit.Services;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises the production Windows slot adapter with an isolated property-set boundary.</summary>
public sealed class WindowsControllerCredentialStoreTests
{
    [Fact]
    public void ConstructionIsLazyAndSlotOperationsPreserveEveryOtherValue()
    {
        Dictionary<string, object> values = new()
        {
            ["DisplayLanguage"] = 3,
            ["UnknownPrivateValue"] = new object(),
            [WindowsControllerCredentialStore.CredentialKey] = ControllerCredentialTestStore.ExistingSecret,
        };
        int opens = 0;
        WindowsControllerCredentialStore store = new(() => { ++opens; return values; });
        Assert.Equal(0, opens);
        Assert.True(store.TryRead(out string? original));
        Assert.Equal(ControllerCredentialTestStore.ExistingSecret, original);
        string replacement = new('a', 64);
        store.Write(replacement);
        Assert.True(store.TryRead(out string? verified));
        Assert.Equal(replacement, verified);
        store.Delete();
        Assert.False(store.TryRead(out _));
        Assert.Equal(2, values.Count);
        Assert.Equal(3, values["DisplayLanguage"]);
        Assert.Equal(5, opens);
    }

    [Fact]
    public void PresentInvalidType_IsDistinctFromAbsenceAndDoesNotCallToString()
    {
        Dictionary<string, object> values = new() { [WindowsControllerCredentialStore.CredentialKey] = new PrivateValue() };
        WindowsControllerCredentialStore store = new(() => values);
        Assert.True(store.TryRead(out string? value));
        Assert.Null(value);
        store.Delete();
        Assert.False(store.TryRead(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void InvalidWrite_IsRejectedBeforeOpeningWindowsStorage(string value)
    {
        WindowsControllerCredentialStore store = new(() => throw new InvalidOperationException("Storage must not open."));
        Assert.Throws<ArgumentException>(() => store.Write(value));
    }

    private sealed class PrivateValue
    {
        public override string ToString() => throw new InvalidOperationException("Private values must not be rendered.");
    }
}
