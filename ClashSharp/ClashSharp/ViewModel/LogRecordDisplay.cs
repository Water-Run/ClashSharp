using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed class LogRecordDisplay(LogRecord entry, string levelDisplay, string sourceDisplay) : ObservableObject
{
    private string _levelDisplay = levelDisplay;
    private string _sourceDisplay = sourceDisplay;
    public LogRecord Record { get; } = entry;
    public string LevelDisplay => _levelDisplay;
    public string SourceDisplay => _sourceDisplay;
    internal DisplayFormatter Formatter { get; init; } = new();

    public string CreatedAtDisplay => Formatter.Timestamp(Record.CreatedAt);

    public string Message => Record.Message;

    public string Detail => Record.Detail;

    /// <summary>Refreshes headings and timestamp formatting while retaining original log text and row identity.</summary>
    public void RefreshLocalization(string level, string source)
    {
        SetProperty(ref _levelDisplay, level, nameof(LevelDisplay));
        SetProperty(ref _sourceDisplay, source, nameof(SourceDisplay));
        OnPropertyChanged(nameof(CreatedAtDisplay));
    }
}
