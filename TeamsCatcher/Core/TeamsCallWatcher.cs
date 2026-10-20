using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TeamsCatcher.Core;

// Rozmowa = okno Teams z paskiem rozmowy, czyli przyciskiem o stałym id "microphone-button" (UI Automation, sprawdzone na żywo).
// Mikrofon w rejestrze nie wystarcza: ekran przed dołączeniem i "Dołącz ponownie" po wyjściu też trzymają mikrofon.
// Nazwa przycisku opisuje akcję: "Wycisz mikrofon" / "Wyłącz wyciszenie mikrofonu" -> stan wyciszenia.
public sealed class TeamsCallWatcher : IDisposable
{
    const string ButtonId = "microphone-button";
    // shortcut: nazwy przycisku dla PL i EN; inne języki Teams = mikrofon traktowany jako niewyciszony
    static readonly string[] MutedMarkers = ["unmute", "wyłącz wyciszenie"];
    static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);

    readonly string _processName;
    readonly CancellationTokenSource _cts = new();
    long _callWindow;
    volatile bool _muted;

    public TeamsCallWatcher(string processName)
    {
        _processName = processName;
        new Thread(Run) { IsBackground = true, Name = "TeamsCallWatcher" }.Start();
    }

    // Okno trwającej rozmowy albo null.
    public nint? CallWindow => Interlocked.Read(ref _callWindow) is var h and not 0 ? (nint)h : null;
    public bool Muted => _muted;

    public static bool IsMutedName(string? buttonName) =>
        buttonName != null && MutedMarkers.Any(m => buttonName.Contains(m, StringComparison.OrdinalIgnoreCase));

    // W trakcie udostępniania pasek rozmowy jest w kilku oknach (zminimalizowane okno spotkania, widok kompaktowy, pasek sterowania).
    // Okno rozmowy = największe niezminimalizowane z nich (zwykle okno spotkania).
    public static WindowInfo? PickCallWindow(IEnumerable<WindowInfo> windowsWithButton) =>
        windowsWithButton.OrderByDescending(w => w.Rect.Left > -30000).ThenByDescending(w => w.Width * w.Height).FirstOrDefault();

    void Run()
    {
        AutomationElement? button = null;
        var nextSearch = DateTime.MinValue;
        while (!_cts.IsCancellationRequested)
        {
            // Szukanie w drzewie jest drogie (ok. 100 ms na okno), odczyt nazwy znanego przycisku - tani (co 200 ms).
            // Ponowne szukanie co sekundę wykrywa też zniknięcie paska (koniec rozmowy w tym samym oknie).
            if (DateTime.UtcNow >= nextSearch)
            {
                nextSearch = DateTime.UtcNow + SearchInterval;
                // Błąd UI Automation (okno przebudowywane w trakcie udostępniania) = stan bez zmian; koniec rozmowy tylko po czystym "nie ma".
                if (Search() is { } found)
                {
                    button = found.Button;
                    Interlocked.Exchange(ref _callWindow, found.Window);
                }
            }
            try
            {
                if (button != null) _muted = IsMutedName(button.Current.Name);
            }
            catch (Exception e) when (IsUiaError(e))
            {
                button = null; // przycisk przebudowany; następne szukanie go znajdzie
            }
            _cts.Token.WaitHandle.WaitOne(200);
        }
    }

    // null = wynik niepewny (błąd w którymś oknie); (null, 0) = na pewno brak rozmowy.
    (AutomationElement? Button, long Window)? Search()
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, ButtonId);
        var found = new List<(WindowInfo Window, AutomationElement Button)>();
        var failed = false;
        foreach (var w in TeamsWindowFinder.List(_processName).Where(w => w.ClassName == TeamsWindowFinder.TeamsWindowClass))
        {
            try
            {
                if (AutomationElement.FromHandle(w.Handle).FindFirst(TreeScope.Descendants, condition) is { } button)
                    found.Add((w, button));
            }
            catch (Exception e) when (IsUiaError(e)) { failed = true; }
        }
        if (PickCallWindow(found.Select(f => f.Window)) is { } call)
            return (found.First(f => f.Window == call).Button, call.Handle);
        return failed ? null : (null, 0);
    }

    static bool IsUiaError(Exception e) => e is ElementNotAvailableException or COMException or InvalidOperationException;

    public void Dispose() => _cts.Cancel();
}
