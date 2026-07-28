using System;

namespace ClashSharp.ViewModel;

/// <summary>Represents an expected runtime snapshot failure at a storage or catalog boundary.</summary>
internal sealed class MasterControlRuntimeUnavailableException : Exception
{
    public MasterControlRuntimeUnavailableException(Exception innerException)
        : base(
            "Runtime snapshot data is unavailable.",
            innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
    }
}
