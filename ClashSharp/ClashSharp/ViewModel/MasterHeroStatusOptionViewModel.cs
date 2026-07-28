using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed class MasterHeroStatusOptionViewModel(MasterHeroStatusItemKind kind, string title)
{
    public MasterHeroStatusItemKind Kind { get; } = kind;

    public string Title { get; } = title ?? throw new ArgumentNullException(nameof(title));
}
