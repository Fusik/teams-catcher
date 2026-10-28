using System.IO;
using System.Windows;
using System.Windows.Threading;
using TeamsCatcher.Core;
using TeamsCatcher.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TeamsCatcher;

public partial class App : Application
{
    const string MutexName = @"Local\WF.TeamsCatcher";
    const string ShowEventName = @"Local\WF.TeamsCatcher.Show";

    readonly CancellationTokenSource _cts = new();
    readonly List<string> _pendingLog = [];
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _toggleItem;
    Drawing.Icon? _idleIcon, _recordingIcon;
    CatcherService? _service;
    Task? _run;
    MainWindow? _window;
    MainViewModel? _vm;
    bool _wasRecording;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Jedna instancja: druga prosi pierwszą o pokazanie okna i kończy się.
        _mutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // nieobsłużony wyjątek = ślad w logs\ zamiast cichego zamknięcia
        DispatcherUnhandledException += (_, ev) => FileLog.Write($"CRASH (UI): {ev.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => FileLog.Write($"CRASH: {ev.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ev) => FileLog.Write($"Unobserved task error: {ev.Exception}");
        FileLog.Write($"Teams Catcher started (pid {Environment.ProcessId}).");

        var firstRun = !File.Exists(Settings.DefaultPath);
        var settings = Settings.Load(Settings.DefaultPath, Log);
        if (firstRun) TrySave(settings);
        if (settings.StartWithWindows) Autostart.Apply(true, Environment.ProcessPath!); // exe mógł zostać przeniesiony

        _service = new CatcherService(settings, CatcherService.FindFfmpeg(), Log);
        Ollama.Log = Log;
        _vm = new MainViewModel(settings, Settings.DefaultPath, _service);
        foreach (var line in _pendingLog) _vm.AddLog(line);
        _vm.AddLog(settings.AutoRecord ? "Waiting for a Teams call..." : "Automatic recording is off.");
        _window = new MainWindow { DataContext = _vm };

        CreateTray();
        _service.PartialNotesReady += (title, notes, file) => Dispatcher.BeginInvoke(() => ShowPartialNotes(title, notes, file));
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);
        _run = Task.Run(() => _service.RunAsync(_cts.Token));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateTray();
        timer.Start();

        if (firstRun || !e.Args.Contains("--tray")) ShowWindow();
        _setup = new SetupViewModel(settings, Log);
        _setup.Finished += text => Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(5000, "Teams Catcher", text, Forms.ToolTipIcon.Info));
        _ = CheckComponentsAsync(settings);
    }

    SetupViewModel? _setup;
    SetupWindow? _setupWindow;

    // Pierwszy start (paczka bez modeli): Ollama portable, jeśli jest, a brakujące składniki -> okno pobierania.
    async Task CheckComponentsAsync(Settings settings)
    {
        if (await Ollama.ModelsAsync(settings, CancellationToken.None) == null && Components.StartOllama(settings) != null)
            Log("Started the portable Ollama (folder ollama).");
        if (await _setup!.RefreshAsync() > 0) ShowComponents();
    }

    async void ShowComponents()
    {
        if (_setup == null) return;
        await _setup.RefreshAsync();
        _setupWindow ??= new SetupWindow { DataContext = _setup };
        _setupWindow.Show();
        _setupWindow.Activate();
    }

    void Log(string message)
    {
        if (_vm == null) _pendingLog.Add(message);
        else _vm.AddLog(message);
    }

    void TrySave(Settings settings)
    {
        try { settings.Save(Settings.DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Settings not saved: {ex.Message}"); }
    }

    void CreateTray()
    {
        using (var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/WF/WF.TeamsCatcher.ico"))!.Stream)
            _idleIcon = new Drawing.Icon(stream, 32, 32);
        _recordingIcon = WithRecordingDot(_idleIcon);

        _toggleItem = new Forms.ToolStripMenuItem("Start recording", null, (_, _) => _vm!.ToggleRecordingCommand.Execute(null));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add(_toggleItem);
        menu.Items.Add("Components...", null, (_, _) => ShowComponents());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray = new Forms.NotifyIcon { Icon = _idleIcon, Text = "WF BIM - Teams Catcher", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    // Ikona traya w trakcie nagrania: ikona aplikacji z czerwoną kropką.
    static Drawing.Icon WithRecordingDot(Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF8, 0x71, 0x71)), 16, 0, 16, 16);
            g.DrawEllipse(new Drawing.Pen(Drawing.Color.FromArgb(0x07, 0x0E, 0x1D), 2), 17, 1, 14, 14);
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    void UpdateTray()
    {
        if (_tray == null || _vm == null) return;
        var recording = _vm.IsRecording;
        _tray.Icon = recording ? _recordingIcon : _idleIcon;
        var text = $"WF BIM - Teams Catcher ({_vm.State})";
        _tray.Text = text.Length > 63 ? text[..63] : text; // limit NotifyIcon
        _toggleItem!.Text = recording ? "Stop recording" : "Start recording";
        _toggleItem.Enabled = !_vm.IsFinalizing;
        if (recording && !_wasRecording)
            _tray.ShowBalloonTip(3000, "Teams Catcher", "Recording started: Teams window and sound.", Forms.ToolTipIcon.Info);
        _wasRecording = recording;
    }

    // Notatka częściowa jako powiadomienie przy prawej krawędzi ekranu (dymek traya ucinał treść do 250 znaków i znikał w centrum powiadomień).
    // Plik: <nagranie>\live\NN\notes.xx.md
    static void ShowPartialNotes(string title, string notes, string file) =>
        NoteToast.Show(title, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)))) ?? "", notes, file);

    void ShowWindow()
    {
        if (_window == null || IsExiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    async void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        _vm?.AddLog("Exiting, finishing the current recording...");
        _cts.Cancel();
        if (_run != null) await _run; // kończy i finalizuje bieżące nagranie
        Shutdown();
    }

    // Wylogowanie / wyłączenie Windows: zamknij nagranie, żeby pliki były kompletne.
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        _cts.Cancel();
        _run?.Wait(TimeSpan.FromSeconds(20));
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _service?.Dispose();
        Components.StopOllama();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
