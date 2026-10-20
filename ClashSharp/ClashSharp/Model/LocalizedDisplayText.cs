using System;
using System.Globalization;

namespace ClashSharp.Model;

/// <summary>Immutable provenance for application-authored text, independent of the operation that produced it.</summary>
internal sealed class LocalizedDisplayText : IEquatable<LocalizedDisplayText>
{
    private readonly object[] _arguments;
    public LocalizedDisplayText(string resourceKey, params object[] arguments)
    {
        ResourceKey = resourceKey ?? throw new ArgumentNullException(nameof(resourceKey));
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (object? argument in arguments)
        {
            if (argument is null or string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Enum or LocalizedDisplayText
                || argument.GetType().IsPrimitive) { continue; }
            throw new ArgumentException("Display arguments must be immutable scalar or display snapshots.", nameof(arguments));
        }
        _arguments = (object[])arguments.Clone();
    }

    public string ResourceKey { get; }
    public string Resolve(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        string template = getString(ResourceKey);
        if (_arguments.Length == 0) { return template; }
        object[] arguments = Array.ConvertAll(_arguments,
            argument => argument is LocalizedDisplayText text ? text.Resolve(getString) : argument);
        return string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    public bool Equals(LocalizedDisplayText? other)
    {
        if (other is null || !StringComparer.Ordinal.Equals(ResourceKey, other.ResourceKey) || _arguments.Length != other._arguments.Length) { return false; }
        for (int index = 0; index < _arguments.Length; index++)
        { if (!Equals(_arguments[index], other._arguments[index])) { return false; } }
        return true;
    }

    public override bool Equals(object? obj) => obj is LocalizedDisplayText text && Equals(text);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ResourceKey, StringComparer.Ordinal);
        foreach (object? argument in _arguments) { hash.Add(argument); }
        return hash.ToHashCode();
    }
}
