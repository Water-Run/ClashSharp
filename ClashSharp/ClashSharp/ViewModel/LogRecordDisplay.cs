using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed record LogRecordDisplay(LogRecord Record, string LevelDisplay, string SourceDisplay)
{
    internal DisplayFormatter Formatter { get; init; } = new();

    public string CreatedAtDisplay => Formatter.Timestamp(Record.CreatedAt);

    public string Message => Record.Message;

    public string Detail => Record.Detail;
}
