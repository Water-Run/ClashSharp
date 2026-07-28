using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed record LogRecordDisplay(LogRecord Record, string LevelDisplay, string SourceDisplay)
{
    public string CreatedAtDisplay => Record.CreatedAtDisplay;

    public string Message => Record.Message;

    public string Detail => Record.Detail;
}
