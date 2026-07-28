using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts persistent logging to the presentation log boundary.</summary>
internal sealed class PageLogAdapter : IPageLog
{
    private readonly LogStorageService _logStorage;

    public PageLogAdapter(LogStorageService logStorage)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    public void Append(string level, string category, string message, string? detail)
    {
        _logStorage.AppendLog(level, category, message, detail);
    }
}
