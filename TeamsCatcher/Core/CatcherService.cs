using System.Diagnostics;
using System.IO;

namespace TeamsCatcher.Core;

// Pętla w tle: co sekundę sprawdza Teams, startuje i kończy nagrania.
public sealed class CatcherService(Settings settings, string ffmpeg, Action<string> log) : IDisposable
{
    readonly CallDetector _detector = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    readonly TeamsCallWatcher _watcher = new(settings.TeamsProcess);
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly SemaphoreSlim _transcribeGate = new(1, 1);
    CancellationToken _stopping;
    int _transcribing;
    // Okno spotkania przez kilka sekund po wyjściu wciąż ma pasek rozmowy (sprawdzone na żywo) - bez tego powstaje "fantomowa" rozmowa.
    static readonly TimeSpan EndedWindowCooldown = TimeSpan.FromSeconds(20);
    (nint Window, DateTime At) _ended;
    nint _lastCallWindow;
    CaptureTarget? _pendingTarget;
    bool _loggedMuted;
    int _ticks;

    public RecordingSession? Session { get; private set; }
    public CallState CallState => _detector.State;
    public bool TeamsRunning { get; private set; }
    public bool Finalizing { get; private set; }
    public bool Transcribing => Volatile.Read(ref _transcribing) > 0;

    public static string FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(local) ? local : "ffmpeg"; // fallback: PATH
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _stopping = ct;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        await Locked(() => RecordingSession.RecoverAsync(settings.OutputFolder, ffmpeg, log));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await Locked(TickAsync);
        }
        catch (OperationCanceledException) { }
        finally { await Locked(StopCoreAsync); }
    }

    // Task.Run: kliknięcie w UI to wątek STA, a aktywacja WASAPI process loopback z STA kończy się E_NOINTERFACE (sprawdzone na żywo).
    public Task StartRecordingAsync() => Task.Run(() => Locked(() =>
    {
        StartCore(TeamsWindowFinder.List(settings.TeamsProcess));
        return Task.CompletedTask;
    }));

    public Task StopRecordingAsync() => Task.Run(() => Locked(StopCoreAsync));


    async Task TickAsync()
    {
        TeamsRunning = Process.GetProcessesByName(settings.TeamsProcess).Length > 0;
        var windows = TeamsWindowFinder.List(settings.TeamsProcess);
        var now = DateTime.UtcNow;
        var callWindow = _watcher.CallWindow;
        if (callWindow == _ended.Window && now - _ended.At < EndedWindowCooldown && Session == null) callWindow = null;
        if (callWindow is { } current) _lastCallWindow = current;

        if (_detector.Update(callWindow != null, now))
        {
            log(CallState == CallState.InCall ? "Teams call detected." : "Teams call ended.");
            if (CallState == CallState.InCall && settings.AutoRecord) StartCore(windows);
            else if (CallState == CallState.Idle)
            {
                _ended = (_lastCallWindow, now);
                await StopCoreAsync();
            }
        }
        else if (Session is { } s && callWindow is { } h)
        {
            var shared = TeamsWindowFinder.FindShared(windows);
            s.SetSharedAudio(shared != null ? TeamsProcesses.SharedAudioPids(shared, settings.TeamsProcess) : []);
            // Udostępniany ekran / okno ma pierwszeństwo (Teams minimalizuje wtedy okno rozmowy); potem powrót do okna rozmowy.
            var target = shared ?? (windows.FirstOrDefault(w => w.Handle == h) is { } w ? new CaptureTarget(w.Handle, 0) { Name = w.Title } : null);
            var change = target != null && (!target.Equals(s.VideoTarget) || !s.VideoRunning && target.Monitor != 0);
            // Zmiana musi trwać 2 kolejne sekundy: przy starcie/końcu udostępniania okna Teams przez chwilę przeskakują.
            if (change && target!.Equals(_pendingTarget) && settings.RecordVideo) await s.SwitchVideoAsync(target);
            _pendingTarget = change ? target : null;
        }

        if (Session != null && _watcher.Muted != _loggedMuted)
        {
            _loggedMuted = _watcher.Muted;
            log(_loggedMuted ? "Muted in Teams - microphone track silenced." : "Unmuted in Teams.");
        }
        if (Session != null && ++_ticks % 5 == 0) Session.AddTeamsAudio(TeamsProcesses.AudioPids(settings.TeamsProcess));
        if (Session != null && _ticks % 10 == 0) Session.SaveInfo();
        if (Session != null) ScheduleLiveChunk(Session);
        if (Session != null && _ticks % 60 == 0 && !RecordingSession.HasFreeSpace(settings.OutputFolder, settings.MinFreeBytes))
        {
            log("Low disk space, recording stopped.");
            await StopCoreAsync();
        }
    }

    void StartCore(List<WindowInfo> windows)
    {
        if (Session != null) return;
        Directory.CreateDirectory(settings.OutputFolder);
        if (!RecordingSession.HasFreeSpace(settings.OutputFolder, settings.MinFreeBytes))
        {
            log($"Not enough free disk space in {settings.OutputFolder}, recording skipped.");
            return;
        }
        if (Process.GetProcessesByName(settings.TeamsProcess).Length == 0)
        {
            log("Teams is not running.");
            return;
        }
        // ręczny start bez rozmowy -> główne (największe) okno Teams
        var window = windows.FirstOrDefault(w => w.Handle == _watcher.CallWindow) ?? TeamsWindowFinder.PickLargest(windows);
        _loggedMuted = false;
        (_liveIndex, _liveFrom, _liveCut) = (0, 0, null);
        Session = RecordingSession.Start(settings, ffmpeg, TeamsProcesses.AudioPids(settings.TeamsProcess), window, () => _watcher.Muted, log);
    }

    async Task StopCoreAsync()
    {
        if (Session is not { } s) return;
        Session = null;
        Finalizing = true;
        // trwający zrzut kawałka korzysta z plików sesji - najpierw on
        if (_liveCut != null) await _liveCut;
        try { await s.StopAsync(); }
        finally { Finalizing = false; }
        if (!settings.AutoTranscribe || !File.Exists(Path.Combine(s.Folder, RecordingSession.TranscriptWav))) return;
        if (LiveChunks.List(s.Folder).Count > 0) QueueFinish(s.Folder);
        else QueueTranscription(s.Folder);
    }

    // Notatka częściowa gotowa (kawałek ~5 min w trakcie rozmowy): tytuł, treść Markdown, plik - dymek w trayu.
    public event Action<string, string, string>? PartialNotesReady;

    // Transkrypcja w tle, po jednej naraz (GPU), nie blokuje wykrywania kolejnej rozmowy.
    // fromState: przeliczenie po objaśnieniu niejasnych słów (bez ponownego Whispera i diaryzacji).
    public void QueueTranscription(string folder, bool fromState = false, Transcriber.Mode mode = Transcriber.Mode.Full) =>
        QueueJob(folder, async () =>
        {
            var notes = await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping, fromState, mode);
            if (notes != null) PartialNotesReady?.Invoke(PartialTitle(folder), notes, NotesFile(folder));
        });

    // Dowolny plik wideo/audio: nowy folder nagrania z dźwiękiem 16 kHz mono (wszyscy jako Rozmówcy, rozdzieleni diaryzacją),
    // potem ta sama transkrypcja i notatki co po rozmowie. Plik źródłowy zostaje na miejscu (ścieżka w session.json).
    public void ImportVideo(string file)
    {
        if (!File.Exists(file))
        {
            log($"File not found: {file}");
            return;
        }
        var folder = Path.Combine(settings.OutputFolder, RecordingSession.FolderName(DateTime.Now, Path.GetFileNameWithoutExtension(file)));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RecordingSession.InfoJson), System.Text.Json.JsonSerializer.Serialize(
            new SessionInfo { Finalized = true, Started = DateTime.Now, Ended = DateTime.Now, Title = file }));
        log($"Video file queued for transcription: {Path.GetFileName(file)}");
        QueueJob(folder, async () =>
        {
            var wav = Path.Combine(folder, RecordingSession.TranscriptWav);
            await Transcriber.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{file}\" -vn -ac 1 -ar 16000 -c:a pcm_s16le \"{wav}\"", folder, _stopping);
            await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping);
        });
    }

    // Folder analizowany teraz (nagranie albo kawałek live\NN); null = kolejka pusta.
    public string? CurrentJob { get; private set; }

    void QueueJob(string folder, Func<Task> job)
    {
        if (Transcriber.MissingFiles(settings) is { } missing)
        {
            log($"Transcription skipped, file not found: {missing} - download the components (tray menu: Components...), then Transcribe again.");
            return;
        }
        Interlocked.Increment(ref _transcribing);
        _ = Task.Run(async () =>
        {
            try
            {
                await _transcribeGate.WaitAsync(_stopping);
                try
                {
                    log($"Transcribing: {Path.GetRelativePath(settings.OutputFolder, folder)}");
                    CurrentJob = folder;
                    var clock = Stopwatch.StartNew();
                    await job();
                    log($"Transcript saved in {clock.Elapsed:mm\\:ss}: {Path.Combine(folder, Transcriber.TranscriptTxt)}");
                }
                finally
                {
                    CurrentJob = null;
                    _transcribeGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log($"Transcription failed: {e.Message}"); }
            finally { Interlocked.Decrement(ref _transcribing); }
        });
    }

    static string NotesFile(string folder) => Directory.GetFiles(folder, "notes.*.md").FirstOrDefault() ?? Path.Combine(folder, MeetingNotes.FileName("pl"));

    static string PartialTitle(string chunkFolder) =>
        File.Exists(Path.Combine(chunkFolder, LiveChunks.InfoFile)) && LiveChunks.List(Path.GetDirectoryName(Path.GetDirectoryName(chunkFolder)!)!)
            .FirstOrDefault(c => c.Folder == chunkFolder) is { Info: { } info }
            ? $"Notatka {TimeSpan.FromSeconds(info.From):h\\:mm\\:ss}-{TimeSpan.FromSeconds(info.To):h\\:mm\\:ss}"
            : "Notatka częściowa";

    // Kawałek co LiveChunkMinutes: zrzut dźwięku do tej chwili, cięcie w ciszy, przetwarzanie w kolejce (tryb Chunk).
    int _liveIndex;
    double _liveFrom;
    Task? _liveCut;

    double ChunkSeconds => settings.LiveChunkMinutes * 60.0;

    void ScheduleLiveChunk(RecordingSession s)
    {
        if (!settings.AutoTranscribe || settings.LiveChunkMinutes <= 0 || _liveCut is { IsCompleted: false }) return;
        // zapas na okno ciszy za planowanym końcem kawałka
        if (s.Elapsed.TotalSeconds < _liveFrom + ChunkSeconds + LiveChunks.QuietWindowSeconds + 5) return;
        _liveCut = CutLiveChunkAsync(s);
    }

    async Task CutLiveChunkAsync(RecordingSession s)
    {
        var snapshot = Path.Combine(s.Folder, LiveChunks.Dir, LiveChunks.SnapshotWav);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            if (await s.SnapshotTranscriptAsync(snapshot) is not { } zero) return;
            var index = _liveIndex + 1;
            var folder = LiveChunks.ChunkFolder(s.Folder, index);
            if (await LiveChunks.CutAsync(snapshot, zero, index, _liveFrom, _liveFrom + ChunkSeconds, false, folder, ffmpeg, _stopping) is not { } chunk) return;
            (_liveIndex, _liveFrom) = (index, chunk.To);
            log($"Live chunk {index}: {TimeSpan.FromSeconds(chunk.From):h\\:mm\\:ss}-{TimeSpan.FromSeconds(chunk.To):h\\:mm\\:ss}, processing in the background.");
            QueueTranscription(folder, mode: Transcriber.Mode.Chunk);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException)
        {
            log($"Live chunk failed: {e.Message}");
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    // Po rozmowie z kawałkami: ostatni kawałek (do końca nagrania) i złożenie całości; bez kompletu kawałków - całość jak zwykle.
    void QueueFinish(string folder) => QueueJob(folder, async () =>
    {
        var chunks = LiveChunks.List(folder);
        var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(await File.ReadAllTextAsync(Path.Combine(folder, RecordingSession.InfoJson), _stopping));
        if (info != null && LiveChunks.FinalZero(info) is { } zero)
        {
            var last = chunks[^1].Info;
            var from = last.Zero + last.To - zero;
            var rest = LiveChunks.ChunkFolder(folder, last.Index + 1);
            if (await LiveChunks.CutAsync(Path.Combine(folder, RecordingSession.TranscriptWav), zero, last.Index + 1, from, 0, true, rest, ffmpeg, _stopping) != null)
            {
                var notes = await Transcriber.RunAsync(rest, settings, ffmpeg, log, _stopping, mode: Transcriber.Mode.Chunk);
                if (notes != null) PartialNotesReady?.Invoke(PartialTitle(rest), notes, NotesFile(rest));
            }
        }
        try { await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping, mode: Transcriber.Mode.Assemble); }
        catch (InvalidDataException e)
        {
            log($"Live chunks incomplete ({e.Message}) - transcribing the whole recording.");
            await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping);
        }
    });

    async Task Locked(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        catch (Exception e) { log($"Error: {e.Message}"); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _watcher.Dispose();
}
