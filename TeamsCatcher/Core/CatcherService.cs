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
    string _lastAudible = "";
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
        await Locked(async () => ResumeTranscriptions(await RecordingSession.RecoverAsync(settings.OutputFolder, ffmpeg, log)));
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
        // co 10 s, tylko gdy lista grających sesji się zmieniła (bez powtarzania tej samej linii)
        if (Session != null && _ticks % 10 == 0 && TeamsProcesses.AudibleSessions(settings.TeamsProcess, Session.Info.TeamsAudio.Select(a => a.Pid).ToList()) is { Length: > 0 } audible
            && audible != _lastAudible)
            log($"Audible sessions: {_lastAudible = audible}");
        if (Session != null && _ticks % 30 == 0) log($"Levels (30 s): {Session.TakeLevels()}; Teams mic {(_watcher.Muted ? "MUTED" : "on")}.");
        if (Session != null && settings.LiveTranscript) _liveText?.Schedule(Session, _stopping);
        if (Session != null && settings.LiveTranscript && settings.AutoTranscribe) _liveNotes?.Schedule(Session, _stopping);
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
        _liveText = new LiveTranscript(settings, ffmpeg, log);
        _liveNotes = new LiveNotesService(settings, _liveText, log);
        _liveNotes.Ready += (title, notes, file) => PartialNotesReady?.Invoke(title, notes, file);
        Session = RecordingSession.Start(settings, ffmpeg, TeamsProcesses.AudioPids(settings.TeamsProcess), window, () => _watcher.Muted, log);
    }

    async Task StopCoreAsync()
    {
        if (Session is not { } s) return;
        Session = null;
        Finalizing = true;
        // trwający zrzut kawałka korzysta z plików sesji - najpierw on
        if (_liveText != null) await _liveText.Completion;
        if (_liveNotes != null)
        {
            await _liveNotes.Completion;
            await _liveNotes.DisposeAsync();
        }
        try { await s.StopAsync(); }
        finally { Finalizing = false; }
        if (!settings.AutoTranscribe || !File.Exists(Path.Combine(s.Folder, RecordingSession.TranscriptWav))) return;
        if (LiveChunks.List(s.Folder).Count > 0) QueueFinish(s.Folder);
        else QueueTranscription(s.Folder);
    }

    // Notatka częściowa gotowa (kawałek ~5 min w trakcie rozmowy): tytuł, treść Markdown, plik - dymek w trayu.
    public event Action<string, string, string>? PartialNotesReady;

    // Transkrypcja w tle, po jednej naraz (GPU), nie blokuje wykrywania kolejnej rozmowy.
    // Kroki po nagraniu / imporcie: transkrypcja + zaznaczone w opcjach.
    Transcriber.Steps AutoSteps => Transcriber.Steps.Transcribe | (settings.CorrectTranscript ? Transcriber.Steps.Correct : 0)
        | (settings.WriteNotes ? Transcriber.Steps.Notes : 0);

    // steps: null = kroki z opcji (po nagraniu); "Save and recalculate" podaje Correct. false = to zadanie już czeka albo trwa.
    public bool QueueTranscription(string folder, Transcriber.Steps? steps = null, Transcriber.Mode mode = Transcriber.Mode.Full) =>
        QueueJob(folder, mode == Transcriber.Mode.Full ? (steps ?? AutoSteps).ToString() : mode.ToString(), async () =>
        {
            var notes = await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping, steps ?? AutoSteps, mode);
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
        if (QueueImport(file, folder)) log($"Video file queued for transcription: {Path.GetFileName(file)}");
        else Directory.Delete(folder, true);
    }

    bool QueueImport(string file, string folder) => QueueJob(folder, AutoSteps.ToString(), async () =>
        {
            var wav = Path.Combine(folder, RecordingSession.TranscriptWav);
            if (!File.Exists(wav)) // wznowienie po restarcie: dźwięk już wyciągnięty
            {
                // do pliku tymczasowego: przerwane wyciąganie nie zostawia uciętego transcript.wav, który wyglądałby na gotowy
                var part = Path.Combine(folder, "transcript.part.wav");
                try { await Transcriber.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{file}\" -vn -ac 1 -ar 16000 -c:a pcm_s16le \"{part}\"", folder, _stopping); }
                catch (InvalidOperationException e)
                {
                    log($"No audio in {Path.GetFileName(file)} (file damaged, video only or unsupported format) - nothing to transcribe. {e.Message}");
                    Directory.Delete(folder, true);
                    return;
                }
                File.Move(part, wav);
            }
            await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping, AutoSteps);
        });

    // Plik w folderze nagrania = zlecona analiza, która jeszcze się nie skończyła; zostaje po zamknięciu/crashu aplikacji (treść: krok).
    public const string PendingFile = "transcribe.pending";

    // Start aplikacji: analizy przerwane zamknięciem/crashem i nagrania odzyskane po crashu wracają do kolejki.
    void ResumeTranscriptions(List<string> recovered)
    {
        if (!Directory.Exists(settings.OutputFolder)) return;
        foreach (var folder in Directory.GetDirectories(settings.OutputFolder))
        {
            var marker = Path.Combine(folder, PendingFile);
            var step = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (step == null && !(settings.AutoTranscribe && recovered.Contains(folder))) continue;
            // import przerwany przed wyciągnięciem dźwięku: od nowa z pliku źródłowego (ścieżka w session.json), jeśli wciąż jest
            var source = File.Exists(Path.Combine(folder, RecordingSession.TranscriptWav)) ? null : ImportSource(folder);
            if (source == null && !File.Exists(Path.Combine(folder, RecordingSession.TranscriptWav)))
            {
                File.Delete(marker);
                continue;
            }
            log($"Resuming interrupted analysis: {Path.GetFileName(folder)}");
            if (source != null) QueueImport(source, folder);
            else if (step == Transcriber.Mode.Assemble.ToString() || step == null && LiveChunks.List(folder).Count > 0) QueueFinish(folder);
            else QueueTranscription(folder, Enum.TryParse<Transcriber.Steps>(step, out var steps) ? steps : null);
        }
    }

    static string? ImportSource(string folder)
    {
        try
        {
            var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(folder, RecordingSession.InfoJson)));
            return info is { Imported: true } && File.Exists(info.Title) ? info.Title : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    // Folder analizowany teraz (nagranie albo kawałek live\NN); null = kolejka pusta.
    public string? CurrentJob { get; private set; }

    // Zlecone i trwające zadania (folder + krok): ten sam krok kliknięty drugi raz, gdy już czeka albo trwa, nie liczy się dwa razy
    // (np. "Save and recalculate" kliknięte dwa razy).
    readonly HashSet<string> _queued = [];

    bool QueueJob(string folder, string step, Func<Task> job)
    {
        if (Transcriber.MissingFiles(settings) is { } missing)
        {
            log($"Transcription skipped, file not found: {missing} - download the components (tray menu: Components...), then Transcribe again.");
            return false;
        }
        var key = $"{folder}|{step}";
        lock (_queued)
            if (!_queued.Add(key))
            {
                log($"{step} is already queued or running: {Path.GetFileName(folder)}");
                return false;
            }
        var marker = Path.Combine(folder, PendingFile);
        File.WriteAllText(marker, step);
        Interlocked.Increment(ref _transcribing);
        _ = Task.Run(async () =>
        {
            var interrupted = false;
            try
            {
                await _transcribeGate.WaitAsync(_stopping);
                try
                {
                    log($"{step}: {Path.GetRelativePath(settings.OutputFolder, folder)}");
                    CurrentJob = folder;
                    var clock = Stopwatch.StartNew();
                    await job();
                    log($"{step} finished in {clock.Elapsed:mm\\:ss}: {folder}");
                    // indeks wyszukiwania od razu po analizie (szybkie: mały model, raz na nagranie)
                    if (Directory.Exists(folder))
                        try { await MeetingSearch.IndexAsync(folder, settings, _stopping); }
                        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or InvalidOperationException) { log($"Search index not built (later, at the first search): {e.Message}"); }
                }
                finally
                {
                    CurrentJob = null;
                    _transcribeGate.Release();
                }
            }
            catch (OperationCanceledException) { interrupted = _stopping.IsCancellationRequested; }
            catch (Exception e) { log($"Transcription failed: {e.Message}"); }
            finally
            {
                lock (_queued)
                {
                    _queued.Remove(key);
                    // zamknięcie aplikacji = znacznik zostaje (wznowienie przy starcie); błąd = bez ponawiania w kółko
                    // drugi krok tego folderu w kolejce trzyma znacznik dalej
                    if (!interrupted && !_queued.Any(k => k.StartsWith(folder + "|")) && Directory.Exists(folder)) File.Delete(marker);
                }
                Interlocked.Decrement(ref _transcribing);
            }
        });
        return true;
    }

    static string NotesFile(string folder) => Directory.GetFiles(folder, "notes.*.md").FirstOrDefault() ?? Path.Combine(folder, MeetingNotes.FileName("pl"));

    static string PartialTitle(string chunkFolder) =>
        File.Exists(Path.Combine(chunkFolder, LiveChunks.InfoFile)) && LiveChunks.List(Path.GetDirectoryName(Path.GetDirectoryName(chunkFolder)!)!)
            .FirstOrDefault(c => c.Folder == chunkFolder) is { Info: { } info }
            ? $"Notatka {TimeSpan.FromSeconds(info.From):h\\:mm\\:ss}-{TimeSpan.FromSeconds(info.To):h\\:mm\\:ss}"
            : "Notatka częściowa";

    // Podgląd w trakcie rozmowy: transkrypcja na żywo (Whisper turbo) i z niej notatka narastająca + pytania "do wyjaśnienia".
    LiveTranscript? _liveText;
    LiveNotesService? _liveNotes;

    // Po rozmowie z kawałkami: ostatni kawałek (do końca nagrania) i złożenie całości; bez kompletu kawałków - całość jak zwykle.
    void QueueFinish(string folder) => QueueJob(folder, Transcriber.Mode.Assemble.ToString(), async () =>
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
        try { await Transcriber.RunAsync(folder, settings, ffmpeg, log, _stopping, AutoSteps, Transcriber.Mode.Assemble); }
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
