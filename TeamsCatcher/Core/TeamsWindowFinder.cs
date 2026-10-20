using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamsCatcher.Core;

public readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Near(Bounds o, int tolerance) =>
        Math.Abs(Left - o.Left) <= tolerance && Math.Abs(Top - o.Top) <= tolerance &&
        Math.Abs(Right - o.Right) <= tolerance && Math.Abs(Bottom - o.Bottom) <= tolerance;
}

public sealed record WindowInfo(nint Handle, int ProcessId, string ClassName, string Title, Bounds Rect)
{
    public int Width => Rect.Width;
    public int Height => Rect.Height;
}

// Co nagrywa ffmpeg: okno (Hwnd) albo cały monitor (Monitor). Name tylko do logu, poza porównaniem.
public sealed record CaptureTarget(nint Hwnd, nint Monitor)
{
    public string Name { get; init; } = "";
    // Proces udostępnianego okna (do nagrania jego dźwięku); 0 = cały ekran.
    public int Pid { get; init; }
    public bool Equals(CaptureTarget? other) => other != null && Hwnd == other.Hwnd && Monitor == other.Monitor;
    public override int GetHashCode() => HashCode.Combine(Hwnd, Monitor);
}

public static class TeamsWindowFinder
{
    // Okna nowego Teams (główne i spotkania) mają tę klasę; sprawdzone w spike'u.
    public const string TeamsWindowClass = "TeamsWebView";
    // Ramka, którą Teams rysuje wokół udostępnianego ekranu / okna (sprawdzone na żywo); jej prostokąt = udostępniany obszar.
    public const string ShareBorderClass = "ScreenBorderWindow";

    // Widoczne okna top-level procesu, w kolejności Z (najwyżej pierwsze).
    public static List<WindowInfo> List(string processName)
    {
        var pids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
        return pids.Count == 0 ? [] : Enumerate(pid => pids.Contains(pid));
    }

    // Główne okno Teams = największe (ręczny start nagrania bez rozmowy).
    public static WindowInfo? PickLargest(IEnumerable<WindowInfo> windows) =>
        TeamsWindows(windows).MaxBy(w => w.Width * w.Height);

    static IEnumerable<WindowInfo> TeamsWindows(IEnumerable<WindowInfo> windows) =>
        windows.Where(w => w.ClassName == TeamsWindowClass && w.Width > 0 && w.Height > 0);

    // To, co użytkownik udostępnia w Teams (monitor albo okno), albo null.
    public static CaptureTarget? FindShared(IReadOnlyList<WindowInfo> teamsWindows)
    {
        var border = teamsWindows.FirstOrDefault(w => w.ClassName == ShareBorderClass);
        if (border == null) return null;
        var teamsPids = teamsWindows.Select(w => (uint)w.ProcessId).ToHashSet();
        var others = Enumerate(pid => !teamsPids.Contains(pid)).Where(w => !IsCloaked(w.Handle))
            .Select(w => (w.Handle, Frame(w.Handle), w.Title, w.ProcessId)).ToList();
        var monitor = MonitorFromRect(border.Rect, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return MatchShare(border.Rect, monitor, info.rcMonitor, others);
    }

    // Ramka = cały monitor -> monitor; ramka = granice okna (najwyżej w Z) -> to okno; nic nie pasuje -> monitor.
    public static CaptureTarget? MatchShare(Bounds border, nint monitor, Bounds monitorBounds, IEnumerable<(nint Handle, Bounds Frame, string Title, int Pid)> windowsInZOrder)
    {
        if (border.Width < 100 || border.Height < 100) return null; // ramka w trakcie pojawiania się (1x1)
        if (border.Near(monitorBounds, 4)) return new CaptureTarget(0, monitor) { Name = "shared screen" };
        foreach (var (handle, frame, title, pid) in windowsInZOrder)
            if (frame.Near(border, 10)) return new CaptureTarget(handle, 0) { Name = $"shared window: {title}", Pid = pid };
        return new CaptureTarget(0, monitor) { Name = "shared screen" };
    }

    static List<WindowInfo> Enumerate(Func<uint, bool> include)
    {
        var result = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!include(pid) || !IsWindowVisible(h)) return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            GetWindowRect(h, out var r);
            result.Add(new WindowInfo(h, (int)pid, cls.ToString(), title.ToString(), r));
            return true;
        }, 0);
        return result;
    }

    // Widoczne granice okna (bez niewidzialnej ramki zmiany rozmiaru) - tak samo liczy je ramka Teams.
    static Bounds Frame(nint h) =>
        DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Bounds b, Marshal.SizeOf<Bounds>()) == 0 ? b : default;

    // Okna "ukryte" przez system (np. aplikacje UWP w tle) są widoczne dla EnumWindows.
    static bool IsCloaked(nint h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    delegate bool EnumProc(nint h, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int cbSize; public Bounds rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out Bounds r);
    [DllImport("user32.dll")] static extern nint MonitorFromRect(in Bounds r, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out Bounds value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);
}
