using NAudio.CoreAudioApi;

namespace TeamsCatcher.Core;

// Strażnik "obcego" dźwięku: co 100 ms sprawdza miernikami sesji Windows, czy na urządzeniu gra coś spoza Teams (YouTube, muzyka, dźwięki systemu).
// Wtedy ścieżka z dźwiękiem urządzenia jest zerowana, żeby do nagrania nie wpadało nic poza spotkaniem. Rodzina Teams = ms-teams.exe i potomkowie,
// ta aplikacja nie liczy się jako obca (jej własna sesja loopback pokazuje poziom urządzenia).
// shortcut: gdy obcy dźwięk gra równolegle z rozmową, ginie też głos rozmówców (urządzenie nie rozróżnia źródeł); przy tym zostaje wyciszenie obcego dźwięku.
public sealed class ForeignAudioGate : IDisposable
{
    const float AudiblePeak = 0.003f;
    const int PollMs = 100, HoldMs = 700, FamilyRefreshMs = 2000;
    readonly CancellationTokenSource _cts = new();
    long _until;
    bool _blocking;
    string _offender = "";

    // Zmiana stanu blokady (tekst do logu): kogo blokujemy i kiedy obcy dźwięk ucichł.
    public event Action<string>? Changed;

    public bool ForeignAudible => Environment.TickCount64 < Interlocked.Read(ref _until);

    // Domyślne urządzenie odtwarzania do rozmów (tam gra Teams): z niego nagrywamy dźwięk i na nim sprawdzamy sesje.
    public static MMDevice DefaultRender(MMDeviceEnumerator devices) => devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

    public ForeignAudioGate(string teamsProcess) => Task.Run(() => Loop(teamsProcess));

    void Loop(string teamsProcess)
    {
        using var devices = new MMDeviceEnumerator();
        HashSet<int> family = [];
        var refreshed = -1L;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (refreshed < 0 || Environment.TickCount64 - refreshed > FamilyRefreshMs)
                {
                    family = TeamsProcesses.FamilyPids(teamsProcess).ToHashSet();
                    refreshed = Environment.TickCount64;
                }
                using var device = DefaultRender(devices);
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    if (pid == Environment.ProcessId || family.Contains(pid)) continue;
                    if (session.AudioMeterInformation.MasterPeakValue >= AudiblePeak)
                    {
                        Interlocked.Exchange(ref _until, Environment.TickCount64 + HoldMs);
                        _offender = $"{Name(pid)} {pid}";
                        break;
                    }
                }
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            if (ForeignAudible != _blocking)
            {
                _blocking = ForeignAudible;
                Changed?.Invoke(_blocking ? $"Other audio ({_offender}) is playing - not recorded (also hides the meeting's voices while it lasts)." : "Other audio stopped - recording meeting audio again.");
            }
            _cts.Token.WaitHandle.WaitOne(PollMs);
        }
    }

    static string Name(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch (ArgumentException) { return pid == 0 ? "system sounds" : "?"; }
    }

    public void Dispose() => _cts.Cancel();
}
