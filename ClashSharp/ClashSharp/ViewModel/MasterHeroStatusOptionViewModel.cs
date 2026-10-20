using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed class MasterHeroStatusOptionViewModel(MasterHeroStatusItemKind kind, string title) : ObservableObject
{
    private string _title = title ?? throw new ArgumentNullException(nameof(title));
    public MasterHeroStatusItemKind Kind { get; } = kind;

    public string Title { get => _title; internal set => SetProperty(ref _title, value); }
}
