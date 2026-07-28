namespace ClashSharp.ViewModel;

/// <summary>Page-level action requested by a functional master-control tile.</summary>
internal enum MasterControlTileAction
{
    ShowStartupPrompt,
    CheckStartupConflicts,
    RunLatencyTest,
    ExportConfiguration,
    ImportConfiguration,
}
