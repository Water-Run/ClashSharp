using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Dialogs;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace ClashSharp.DialogProbe;

/// <summary>Runs native dialog lifecycle checks on a private desktop without injecting user input.</summary>
public sealed partial class ProbeApplication : Application
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    private readonly List<object> _results = [];
    private Window? _owner;
    private Window? _keepAlive;
    private Grid? _root;
    private TextBox? _focus;

    /// <summary>Loads the same WinUI control resources used by the application dialogs.</summary>
    public ProbeApplication()
    {
        UnhandledException += (_, args) => WriteStartupFailure(args.Exception);
        try
        {
            Require(ReadUiThreadDesktop() == Environment.GetCommandLineArgs().Skip(2).FirstOrDefault(), "unexpected-ui-thread-desktop");
            InitializeComponent();
        }
        catch (Exception exception) { WriteStartupFailure(exception); throw; }
    }

    private static void WriteStartupFailure(Exception exception)
    {
        string output = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? @"C:\ClashSharpDialogResults\result.json";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { status = "failed", phase = "application-initialization", error = exception.ToString(), inputInjected = false }, ReportOptions));
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string output = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? @"C:\ClashSharpDialogResults\result.json";
        object report;
        try
        {
            string desktop = ReadUiThreadDesktop();
            Require(desktop.StartsWith("ClashSharpValidation-", StringComparison.Ordinal)
                && desktop == Environment.GetCommandLineArgs().Skip(2).FirstOrDefault(), "unexpected-ui-thread-desktop");
            _keepAlive = new Window { Content = new Grid() };
            _keepAlive.Activate();
            _keepAlive.AppWindow.Hide();
            CreateOwner();
            await WaitAsync(() => _root!.XamlRoot is not null);
            await CheckAsync("small-owner-generic", startup: false, cancel: false);
            await CheckAsync("small-owner-startup", startup: true, cancel: false);
            await CheckAsync("reopen-startup", startup: true, cancel: false);
            await CheckAsync("cancel-generic", startup: false, cancel: true);
            await CheckAsync("theme-language-flow", startup: true, cancel: false, relocalize: true);
            await CheckOwnerCloseAsync();
            report = new { status = "passed", results = _results, uiThreadDesktop = desktop, inputInjected = false };
        }
        catch (Exception exception)
        {
            report = new { status = "failed", results = _results, error = exception.ToString(), inputInjected = false };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, ReportOptions));
        _owner?.Close();
        _keepAlive?.Close();
        Exit();
    }

    private void CreateOwner()
    {
        _root = new Grid { RequestedTheme = ElementTheme.Light, Language = "en-US" };
        _focus = new TextBox { Text = "Owner focus", Width = 180, HorizontalAlignment = HorizontalAlignment.Center };
        _root.Children.Add(_focus);
        _owner = new Window { Content = _root, Title = "Clash# dialog probe owner" };
        _owner.AppWindow.ResizeClient(new SizeInt32(320, 240));
        _owner.Activate();
    }

    private static ContentDialog CreateDialog(bool startup)
    {
        if (startup)
        {
            StartupCheckItem[] rows =
            [
                new(false, "Subscription", "No subscription configured; this is optional.") { Kind = StartupCheckKind.Subscription, IsOptional = true },
                new(true, "Transparent proxy service", "The service has been deployed.") { Kind = StartupCheckKind.TransparentProxy },
                new(true, "Startup recovery", "The recovery task is registered.") { Kind = StartupCheckKind.StartupRecovery },
                new(true, "Proxy ownership", "No stale proxy setting was found.") { Kind = StartupCheckKind.SystemProxy },
            ];
            return new StartupGuideDialog(rows, static key => key switch
            {
                "StartupPrompt.Title" => "Startup checks",
                "StartupPrompt.Description" => "Check configuration, service and system proxy status.",
                "Command.Close" => "Close",
                "StartupPrompt.Recheck" => "Recheck",
                "StartupPrompt.Summary.Ready" => "Checks completed",
                "StartupPrompt.Summary.Optional.Format" => "{0} optional configuration item",
                _ => key,
            });
        }
        ContentDialog dialog = new()
        {
            Title = "Independent dialog",
            Content = new Border { Width = 600, Height = 420, Child = new TextBox { Text = "Draft retained across modal lifetime", Width = 480 } },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 688d;
        return dialog;
    }

    private async Task CheckAsync(string name, bool startup, bool cancel, bool relocalize = false)
    {
        _owner!.Activate();
        _owner.AppWindow.ResizeClient(new SizeInt32(320, 240));
        _focus!.Focus(FocusState.Programmatic);
        XamlRoot ownerRoot = _root!.XamlRoot;
        ContentDialog dialog = CreateDialog(startup);
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        using CancellationTokenSource cancellation = new();
        Task<ContentDialogResult> presentation = WindowDialogCoordinator.ShowAsync(dialog, ownerRoot, cancellation.Token);
        await WaitOpenedAsync(opened.Task, presentation);
        AppWindow native = ReadHost(ownerRoot);
        await WaitAsync(() => native.IsVisible && native.Size.Width > 0 && native.Size.Height > 0);
        await Task.Delay(250);
        Require(!ReferenceEquals(dialog.XamlRoot, ownerRoot), "dialog-root-still-owned-by-main-window");
        Require(native.Size.Width > _owner.AppWindow.Size.Width, "dialog-width-limited-by-owner");
        Require(native.Size.Height > _owner.AppWindow.Size.Height, "dialog-height-limited-by-owner");
        Require(native.Presenter is OverlappedPresenter { IsModal: true }, "native-modality-missing");
        Require(!IsWindowEnabled(Win32Interop.GetWindowFromWindowId(_owner.AppWindow.Id)), "owner-remains-interactive");
        RectInt32 work = DisplayArea.GetFromWindowId(native.Id, DisplayAreaFallback.Nearest).WorkArea;
        PointInt32 position = native.Position;
        SizeInt32 size = native.Size;
        Require(position.X >= work.X && position.Y >= work.Y
            && (long)position.X + size.Width <= (long)work.X + work.Width
            && (long)position.Y + size.Height <= (long)work.Y + work.Height, "dialog-outside-work-area");
        Require(WindowDialogCoordinator.TryRefreshDialogPresentation(dialog.XamlRoot, static () => { }), "host-root-lost-modal-admission");
        _owner.AppWindow.ResizeClient(new SizeInt32(480, 320));
        await Task.Delay(100);
        Require(native.Size.Width == size.Width && native.Size.Height == size.Height, "owner-resize-changed-dialog-size");
        if (relocalize)
        {
            _root.RequestedTheme = ElementTheme.Dark;
            _root.Language = "fa-IR";
            _root.FlowDirection = FlowDirection.RightToLeft;
            await WaitAsync(() => dialog.RequestedTheme == ElementTheme.Dark && dialog.Language == "fa-IR"
                && dialog.FlowDirection == FlowDirection.RightToLeft);
        }
        nint handle = Win32Interop.GetWindowFromWindowId(native.Id);
        if (cancel) { cancellation.Cancel(); }
        else { dialog.Hide(); }
        try
        {
            _ = await presentation.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!cancel, "cancellation-was-not-propagated");
        }
        catch (OperationCanceledException) when (cancel) { }
        Require(!IsWindow(handle), "owned-window-leaked");
        Require(IsWindowEnabled(Win32Interop.GetWindowFromWindowId(_owner.AppWindow.Id)), "owner-not-restored");
        if (!startup)
        {
            Require(((TextBox)((Border)dialog.Content).Child).Text == "Draft retained across modal lifetime", "input-value-lost-after-close");
        }
        await WaitAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(ownerRoot), _focus));
        _results.Add(new
        {
            name,
            status = "passed",
            ownerWidth = _owner.AppWindow.Size.Width,
            ownerHeight = _owner.AppWindow.Size.Height,
            dialogWidth = size.Width,
            dialogHeight = size.Height,
            workWidth = work.Width,
            workHeight = work.Height,
            rasterizationScale = ownerRoot.RasterizationScale
        });
    }

    private async Task CheckOwnerCloseAsync()
    {
        XamlRoot root = _root!.XamlRoot;
        ContentDialog dialog = CreateDialog(startup: false);
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        using CancellationTokenSource cancellation = new();
        Task<ContentDialogResult> presentation = WindowDialogCoordinator.ShowAsync(dialog, root, cancellation.Token);
        await WaitOpenedAsync(opened.Task, presentation);
        nint handle = Win32Interop.GetWindowFromWindowId(ReadHost(root).Id);
        WindowDialogCoordinator.MarkWindowClosing(root);
        cancellation.Cancel();
        _owner!.Close();
        _owner = null;
        try { _ = await presentation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        Require(!IsWindow(handle), "owned-window-survived-owner");
        _results.Add(new { name = "owner-close", status = "passed" });
    }

    private static AppWindow ReadHost(XamlRoot root)
    {
        var hosts = (ConditionalWeakTable<XamlRoot, OwnedDialogHost>)typeof(WindowDialogCoordinator)
            .GetField("Hosts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Require(hosts.TryGetValue(root, out OwnedDialogHost? host), "native-host-not-registered");
        return (AppWindow)typeof(OwnedDialogHost).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    }

    private static async Task WaitOpenedAsync(Task opened, Task presentation)
    {
        Task winner = await Task.WhenAny(opened, presentation, Task.Delay(TimeSpan.FromSeconds(10)));
        if (winner == presentation) { await presentation; throw new InvalidOperationException("dialog-ended-before-opening"); }
        Require(winner == opened, "dialog-open-timeout");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) { throw new TimeoutException("native-state-timeout"); }
            await Task.Delay(50);
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) { throw new InvalidOperationException(code); }
    }

    private static string ReadUiThreadDesktop()
    {
        char[] name = new char[256];
        Require(GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _), "ui-desktop-read-failed");
        return new string(name).TrimEnd('\0');
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] value, uint length, out uint needed);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint handle);
}
