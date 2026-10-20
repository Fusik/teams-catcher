using System.Collections.ObjectModel;
using System.Windows;
using TeamsCatcher.Core;
using TeamsCatcher.Infrastructure;

namespace TeamsCatcher.ViewModels;

// Jeden składnik w oknie pobierania.
public sealed class ComponentItem(Component component) : ObservableObject
{
    bool _selected = true, _indeterminate, _done;
    string _status = "";
    double _progress;
    public Component Component { get; } = component;
    public string Size => Component.Size > 0 ? Gb(Component.Size) : "?";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public bool Done { get => _done; set => Set(ref _done, value); }

    public static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1 << 20):F0} MB";
}

// Pobieranie składników przy pierwszym starcie: lista brakujących, wybór, postęp. Działa dalej po schowaniu okna.
public sealed class SetupViewModel : ObservableObject
{
    readonly Settings _settings;
    readonly Action<string> _log;
    CancellationTokenSource? _cts;
    bool _busy;
    string _summary = "";

    public SetupViewModel(Settings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !Busy && Items.Any(i => i.Selected && !i.Done));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Busy);
    }

    public ObservableCollection<ComponentItem> Items { get; } = [];
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    // Koniec pobierania: tekst do dymka w trayu.
    public event Action<string>? Finished;

    // Brakujące składniki od nowa (nie w trakcie pobierania).
    public async Task<int> RefreshAsync()
    {
        if (Busy) return Items.Count(i => !i.Done);
        var missing = await Components.MissingAsync(_settings, CancellationToken.None);
        Items.Clear();
        foreach (var c in missing)
        {
            var item = new ComponentItem(c);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ComponentItem.Selected)) UpdateSummary(); };
            Items.Add(item);
        }
        UpdateSummary();
        return Items.Count;
    }

    void UpdateSummary()
    {
        var total = Items.Where(i => i.Selected && !i.Done).Sum(i => i.Component.Size);
        long free;
        try { free = Components.FreeSpace(); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { free = -1; }
        Summary = $"To download: {ComponentItem.Gb(total)}" + (free >= 0 ? $"  ·  free on disk: {ComponentItem.Gb(free)}" : "")
            + (free >= 0 && free < total * 1.3 ? "  ·  NOT ENOUGH SPACE" : "");
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    async Task DownloadAsync()
    {
        Busy = true;
        _cts = new CancellationTokenSource();
        int ok = 0, failed = 0;
        try
        {
            foreach (var item in Items.Where(i => i.Selected && !i.Done).ToList())
            {
                item.Status = "Starting...";
                try
                {
                    await Task.Run(() => Components.InstallAsync(item.Component, _settings, (status, done, total) =>
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            item.Indeterminate = total <= 0;
                            item.Progress = total > 0 ? 100.0 * done / total : 0;
                            item.Status = total > 0 ? $"{status} {ComponentItem.Gb(done)} / {ComponentItem.Gb(total)}" : status;
                        }), _cts.Token));
                    item.Done = true;
                    item.Indeterminate = false;
                    item.Progress = 100;
                    item.Status = "Ready";
                    ok++;
                    _log($"Component ready: {item.Component.Name}");
                }
                catch (OperationCanceledException)
                {
                    item.Indeterminate = false;
                    item.Status = "Cancelled (download resumes next time)";
                    break;
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException
                    or System.IO.InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    item.Indeterminate = false;
                    item.Status = $"Failed: {e.Message}";
                    failed++;
                    _log($"Component {item.Component.Name} failed: {e.Message}");
                }
            }
        }
        finally
        {
            Busy = false;
            UpdateSummary();
        }
        Finished?.Invoke(failed == 0 ? $"{ok} component(s) ready - transcription, translation and notes can run." : $"{ok} ready, {failed} failed - open Components to retry.");
    }
}
