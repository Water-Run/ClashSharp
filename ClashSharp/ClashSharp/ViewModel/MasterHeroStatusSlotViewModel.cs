using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed class MasterHeroStatusSlotViewModel : ObservableObject
{
    private MasterHeroStatusItemKind _selectedKind;

    public MasterHeroStatusSlotViewModel(
        int index,
        string title,
        MasterHeroStatusItemKind selectedKind,
        IReadOnlyList<MasterHeroStatusOptionViewModel> options)
    {
        Index = index;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _selectedKind = selectedKind;
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public int Index { get; }

    public string Title { get; }

    public IReadOnlyList<MasterHeroStatusOptionViewModel> Options { get; }

    public MasterHeroStatusItemKind SelectedKind
    {
        get => _selectedKind;
        set => SetProperty(ref _selectedKind, value);
    }
}
