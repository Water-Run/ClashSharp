using ClashSharp.ApplicationModel.Security;

namespace ClashSharp.Tests.Unit.Services;

internal sealed class ControllerCredentialTestStore : IControllerCredentialStore
{
    public const string ExistingSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public bool Present { get; set; }
    public object? Value { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int Deletes { get; private set; }
    public bool IgnoreWrites { get; set; }
    public bool IgnoreDeletes { get; set; }
    public Action? BeforeRead { get; set; }
    public Action? BeforeWrite { get; set; }
    public Action? AfterWrite { get; set; }
    public Action? BeforeDelete { get; set; }
    public Action? AfterDelete { get; set; }
    public bool TryRead(out string? secret)
    {
        ++Reads;
        BeforeRead?.Invoke();
        secret = Value as string;
        return Present;
    }

    public void Write(string secret)
    {
        ++Writes;
        BeforeWrite?.Invoke();
        if (!IgnoreWrites) { Present = true; Value = secret; }
        AfterWrite?.Invoke();
    }

    public void Delete()
    {
        ++Deletes;
        BeforeDelete?.Invoke();
        if (!IgnoreDeletes) { Present = false; Value = null; }
        AfterDelete?.Invoke();
    }
}
