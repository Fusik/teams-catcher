using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TeamsCatcher.Core;

// Stan sesji zapisywany do session.json. Finalized == false po restarcie = sesja przerwana (crash) -> RecoverAsync.
public sealed class SessionInfo
{
    public bool Finalized { get; set; }
    public DateTime Started { get; set; }
    public DateTime? Ended { get; set; }
    public string Title { get; set; } = "";
    // Starty strumieni: czas uniksowy w sekundach (null = strumień nie dostarczył danych).
    public List<AudioTrack> TeamsAudio { get; set; } = [];
    // Dźwięk tego, co użytkownik udostępnia (np. YouTube w udostępnianym oknie) - tylko w czasie prezentacji.
    public List<AudioTrack> SharedAudio { get; set; } = [];
    public double? MicStart { get; set; }
    public List<VideoSegment> Video { get; set; } = [];
    // plik zaimportowany (zakładka VIDEO FILE): Title = pełna ścieżka źródła; rozmowa Teams: tytuł okna
    [System.Text.Json.Serialization.JsonIgnore] public bool Imported => Path.IsPathRooted(Title);
}

// Dźwięk jednego procesu Teams (WebView2, proces mediów).
public sealed class AudioTrack
{
    public string File { get; set; } = "";
    public int Pid { get; set; }
    public double? Start { get; set; }
    // ścieżka z dźwiękiem całego urządzenia (z blokadą obcego dźwięku), zamiast ścieżek po procesach Teams
    public bool Device { get; set; }
    // najgłośniejsza próbka od ostatniego odczytu (0-1) - do logu poziomów; pole, więc nie trafia do session.json
    public float Peak;
}

public sealed class VideoSegment
{
    public string File { get; set; } = "";
    public string Window { get; set; } = "";
    // Czas uniksowy pierwszej klatki (odczytany z pliku przy finalizacji).
    public double? Start { get; set; }
}

// Jedna rozmowa = jeden folder: teams_PID.wav, mic.wav, video_NN.mkv -> meeting.mp4 + transcript.wav.
public sealed class RecordingSession
{
    public const string TeamsWav = "teams.wav", MicWav = "mic.wav", MeetingMp4 = "meeting.mp4", TranscriptWav = "transcript.wav", InfoJson = "session.json", VideoConcat = "video.ffconcat";

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string _ffmpeg;
    readonly int _fps;
    readonly int _videoHeight;
    readonly Action<string> _log;
    readonly List<(Task Task, WaveFileWriter Writer)> _audio = [];
    readonly Dictionary<int, CancellationTokenSource> _shared = [];
    WasapiCapture? _mic;
    WaveFileWriter? _micWriter;
    Process? _video;
    Func<bool> _micMuted = () => false;

    public string Folder { get; }
    public SessionInfo Info { get; }
    public CaptureTarget? VideoTarget { get; private set; }
    public bool VideoRunning => _video is { HasExited: false };
    public TimeSpan Elapsed => _clock.Elapsed;

    RecordingSession(string folder, string ffmpeg, int fps, int videoHeight, Action<string> log, SessionInfo? info = null)
    {
        Folder = folder;
        Info = info ?? new SessionInfo();
        _ffmpeg = ffmpeg;
        _fps = fps;
        _videoHeight = videoHeight;
        _log = log;
    }

    // Ten sam zegar co RTCTIME w ffmpeg (czas systemowy), więc audio i wideo są porównywalne.
    static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    public static RecordingSession Start(Settings settings, string ffmpeg, IEnumerable<int> teamsAudioPids, WindowInfo? window, Func<bool> micMuted, Action<string> log)
    {
        var started = DateTime.Now;
        var folder = Path.Combine(settings.OutputFolder, FolderName(started, window?.Title ?? ""));
        Directory.CreateDirectory(folder);
        var session = new RecordingSession(folder, ffmpeg, settings.Fps, settings.VideoHeight, log);
        session._micMuted = micMuted;
        session.Info.Started = started;
        session.Info.Title = window?.Title ?? "";
        session.StartDeviceAudio(settings.TeamsProcess);
        session.AddTeamsAudio(teamsAudioPids);
        if (settings.RecordMicrophone) session.StartMic();
        if (window != null && settings.RecordVideo) session.StartVideo(new CaptureTarget(window.Handle, 0) { Name = window.Title });
        session.SaveInfo();
        log($"Recording started: {folder}");
        return session;
    }

    // Każdy proces osobno; nowe procesy z dźwiękiem (np. po dołączeniu rozmówcy) dochodzą w trakcie rozmowy.
    public void AddTeamsAudio(IEnumerable<int> pids)
    {
        if (Info.TeamsAudio.Any(t => t.Device)) return; // dźwięk urządzenia już jest (procesowy loopback Teams bywa głuchy na głos rozmówców)
        foreach (var pid in pids)
        {
            if (Info.TeamsAudio.Any(t => t.Pid == pid)) continue;
            var track = new AudioTrack { File = $"teams_{pid}.wav", Pid = pid };
            Info.TeamsAudio.Add(track);
            StartAudio(track, _cts.Token);
        }
    }

    // Dźwięk rozmówców Teams z całego urządzenia odtwarzania (loopback): process loopback na procesach Teams nie słyszy głosu rozmówców (test z telefonem:
    // cisza przy włączonym i wyciszonym mikrofonie, a loopback urządzenia ma ten głos). Obcy dźwięk (YouTube) jest zerowany przez ForeignAudioGate.
    // Brak urządzenia albo błąd = zostają ścieżki po procesach (AddTeamsAudio).
    void StartDeviceAudio(string teamsProcess)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var capture = new WasapiLoopbackCapture(ForeignAudioGate.DefaultRender(devices));
            var gate = new ForeignAudioGate(teamsProcess);
            gate.Changed += _log;
            var track = new AudioTrack { File = "teams_device.wav", Pid = 0, Device = true };
            var writer = new WaveFileWriter(Path.Combine(Folder, track.File), capture.WaveFormat);
            var stopped = new TaskCompletionSource();
            capture.DataAvailable += (_, e) =>
            {
                track.Start ??= Now;
                if (gate.ForeignAudible) Array.Clear(e.Buffer, 0, e.BytesRecorded);
                track.Peak = Math.Max(track.Peak, Peak(e.Buffer, e.BytesRecorded, capture.WaveFormat));
                lock (writer) writer.Write(e.Buffer, 0, e.BytesRecorded);
            };
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();
            _cts.Token.Register(() => capture.StopRecording());
            capture.StartRecording();
            Info.TeamsAudio.Add(track);
            _audio.Add((stopped.Task.ContinueWith(_ => { capture.Dispose(); gate.Dispose(); }), writer));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
        {
            _log($"Device audio not recorded, using Teams processes: {e.Message}");
        }
    }

    // Dźwięk udostępnianej treści: nowe procesy startują, procesy spoza listy (koniec prezentacji) są zatrzymywane.
    public void SetSharedAudio(IReadOnlyCollection<int> pids)
    {
        foreach (var pid in _shared.Keys.Except(pids).ToList())
        {
            _shared[pid].Cancel();
            _shared.Remove(pid);
        }
        foreach (var pid in pids.Where(p => !_shared.ContainsKey(p)))
        {
            var track = new AudioTrack { File = $"shared_{Info.SharedAudio.Count + 1:00}_{pid}.wav", Pid = pid };
            Info.SharedAudio.Add(track);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _shared[pid] = cts;
            StartAudio(track, cts.Token);
            _log($"Recording shared audio: {ProcessName(pid)}");
        }
    }

    void StartAudio(AudioTrack track, CancellationToken ct)
    {
        var writer = new WaveFileWriter(Path.Combine(Folder, track.File),
            new WaveFormat(ProcessLoopback.SampleRate, ProcessLoopback.Bits, ProcessLoopback.Channels));
        var task = ProcessLoopback.CaptureAsync(track.Pid, (buffer, count) =>
        {
            track.Start ??= Now;
            track.Peak = Math.Max(track.Peak, Peak(buffer, count, null));
            // blokada: zrzut kawałka w trakcie rozmowy robi Flush z innego wątku
            lock (writer) writer.Write(buffer, 0, count);
        }, ct);
        _audio.Add((task, writer));
    }

    static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (ArgumentException) { return pid.ToString(); }
    }

    float _micPeak;

    // Najgłośniejsza próbka bufora (0-1); null = 16-bit PCM (process loopback), inaczej format mikrofonu (zwykle float).
    static float Peak(byte[] buffer, int count, WaveFormat? format)
    {
        float peak = 0;
        if (format?.Encoding == WaveFormatEncoding.IeeeFloat)
            for (var i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        else
            for (var i = 0; i + 2 <= count; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768f);
        return peak;
    }

    // Poziomy od ostatniego wywołania (do logu co 30 s): kto z Teams dał dźwięk, czy mikrofon żyje (przed wyciszeniem Teams) i czy w ogóle jest dźwięk rozmówców.
    public string TakeLevels()
    {
        static string Db(float peak) => peak < 0.0003f ? "silent" : $"{20 * Math.Log10(peak):F0} dB";
        var tracks = Info.TeamsAudio.Concat(Info.SharedAudio).ToList();
        var parts = tracks.Select(t => $"{(Info.SharedAudio.Contains(t) ? "shared" : "teams")} {t.Pid} {(t.Start == null ? "no data yet" : Db(t.Peak))}").ToList();
        var any = tracks.Any(t => t.Peak >= 0.0003f);
        foreach (var t in tracks) t.Peak = 0;
        var mic = _micPeak;
        _micPeak = 0;
        return $"{(parts.Count > 0 ? string.Join(", ", parts) : "no Teams audio process yet")}; mic {Db(mic)}{(any ? "" : "  <- NO MEETING AUDIO in this period")}";
    }

    void StartMic()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var mic = new WasapiCapture(devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
            var writer = new WaveFileWriter(Path.Combine(Folder, MicWav), mic.WaveFormat);
            mic.DataAvailable += (_, e) =>
            {
                Info.MicStart ??= Now;
                _micPeak = Math.Max(_micPeak, Peak(e.Buffer, e.BytesRecorded, mic.WaveFormat));
                // wyciszony w Teams = cisza zamiast głosu (oś czasu zostaje)
                if (_micMuted()) Array.Clear(e.Buffer, 0, e.BytesRecorded);
                lock (writer) writer.Write(e.Buffer, 0, e.BytesRecorded);
            };
            mic.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) _log($"Microphone stopped with error: {e.Exception.Message}");
                _micStopped.TrySetResult();
            };
            mic.StartRecording();
            (_mic, _micWriter) = (mic, writer);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log($"Microphone not recorded: {e.Message}");
        }
    }

    public const string LiveTeamsWav = "live_teams.wav";

    // Zrzut dźwięku w trakcie rozmowy (kawałki na żywo): transcript.wav całości do tej chwili, ten sam miks co na końcu.
    // Zero = bezwzględny czas najwcześniejszego dźwięku (sekundy od 1970) - do przeliczenia czasów na oś końcowego nagrania.
    // Kawałki na żywo i transkrypcja na żywo robią zrzut w tle niezależnie - wspólny plik live_teams.wav, więc po jednym naraz.
    readonly SemaphoreSlim _snapshotGate = new(1, 1);

    public async Task<double?> SnapshotTranscriptAsync(string output)
    {
        await _snapshotGate.WaitAsync();
        try { return await SnapshotCoreAsync(output); }
        finally { _snapshotGate.Release(); }
    }

    async Task<double?> SnapshotCoreAsync(string output)
    {
        // nagłówek WAV aktualny do bieżącej chwili; dalszy zapis trwa
        foreach (var (_, writer) in _audio.ToList()) lock (writer) writer.Flush();
        if (_micWriter is { } mic) lock (mic) mic.Flush();
        var teams = Info.TeamsAudio.Concat(Info.SharedAudio).Where(t => t.Start != null).ToList();
        double?[] starts = [teams.Count > 0 ? teams.Min(t => t.Start!.Value) : null, Info.MicStart];
        if (starts.All(s => s == null)) return null;
        var zero = starts.Where(s => s != null).Min()!.Value;
        if (teams.Count > 0 && await RunFfmpegAsync(BuildMixArgs(teams.Select(t => (t.File, t.Start!.Value - starts[0]!.Value)).ToList(), LiveTeamsWav)) == null) return null;
        var ok = await RunFfmpegAsync(BuildTranscriptArgs(
            Info.MicStart is { } m ? (MicWav, m - zero) : null,
            starts[0] is { } t0 ? (LiveTeamsWav, t0 - zero) : null,
            output)) != null;
        File.Delete(Path.Combine(Folder, LiveTeamsWav));
        return ok ? zero : null;
    }

    // Nowy segment wideo: start rozmowy, przejście na udostępniany ekran / okno i z powrotem.
    public void StartVideo(CaptureTarget target)
    {
        if (VideoRunning) throw new InvalidOperationException("Video segment is already running.");
        var segment = new VideoSegment { File = $"video_{Info.Video.Count + 1:00}.mkv", Window = target.Name };
        Info.Video.Add(segment);
        var p = Process.Start(new ProcessStartInfo(_ffmpeg, BuildVideoArgs(target, _fps, _videoHeight, segment.File))
        {
            WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true,
        })!;
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log($"ffmpeg: {e.Data}"); };
        p.BeginErrorReadLine();
        _video = p;
        VideoTarget = target;
        _log($"Capturing: {target.Name}");
    }

    public async Task SwitchVideoAsync(CaptureTarget target)
    {
        await StopVideoAsync();
        StartVideo(target);
    }

    public async Task StopAsync()
    {
        var videoStopped = StopVideoAsync(); // "q" wysłane od razu; wolny ffmpeg nie wydłuża audio
        _cts.Cancel();
        foreach (var (task, writer) in _audio)
        {
            try { await task; }
            catch (Exception e) { _log($"Teams audio failed: {e.Message}"); }
            writer.Dispose();
        }
        if (_mic != null)
        {
            _mic.StopRecording();
            try { await _micStopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _log("Microphone did not stop in 5 s."); }
            _mic.Dispose();
        }
        _micWriter?.Dispose();
        await videoStopped;
        Info.Ended = DateTime.Now;
        SaveInfo();
        _log($"Recording stopped after {_clock.Elapsed:hh\\:mm\\:ss}, finalizing...");
        await FinalizeAsync();
    }

    async Task StopVideoAsync()
    {
        if (_video is not { HasExited: false } p) return;
        try
        {
            p.StandardInput.Write('q'); // ffmpeg kończy plik poprawnie po "q"
            p.StandardInput.Flush();
        }
        catch (IOException) { }
        // Okno bez nowych klatek (zminimalizowane, statyczne) blokuje obsługę "q" w ffmpeg - nie czekamy długo.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            p.Kill();
            _log("Video segment stopped (ffmpeg killed, recorded frames are kept).");
        }
    }

    async Task FinalizeAsync()
    {
        var audio = new List<(string File, double Start)>();
        // Wszystkie procesy Teams + dźwięk prezentacji -> jedna ścieżka teams.wav
        var teams = Info.TeamsAudio.Concat(Info.SharedAudio).Where(t => t.Start != null).ToList();
        if (teams.Count > 0)
        {
            var first = teams.Min(t => t.Start!.Value);
            if (await RunFfmpegAsync(BuildMixArgs(teams.Select(t => (t.File, t.Start!.Value - first)).ToList(), TeamsWav)) != null)
                audio.Add((TeamsWav, first));
        }
        if (Info.MicStart is { } m) audio.Add((MicWav, m));
        // Segmenty bez klatek (okno zminimalizowane / zamknięte zaraz po starcie) są usuwane.
        foreach (var segment in Info.Video)
        {
            var path = Path.Combine(Folder, segment.File);
            if (File.Exists(path) && (segment.Start = ParseFirstFrame(await RunFfmpegAsync(BuildFirstFrameArgs(segment.File), quiet: true))) == null)
                File.Delete(path);
        }
        Info.Video.RemoveAll(v => !File.Exists(Path.Combine(Folder, v.File)));
        Info.Video.Sort((a, b) => a.Start!.Value.CompareTo(b.Start!.Value));
        // Wszystkie segmenty -> jedna ścieżka wideo; przerwy między segmentami zachowane (czas systemowy).
        var videoStart = Info.Video.FirstOrDefault()?.Start;
        if (videoStart != null)
            File.WriteAllText(Path.Combine(Folder, VideoConcat), BuildConcatList(Info.Video.Select(v => (v.File, v.Start!.Value)).ToList()));
        SaveInfo();
        if (videoStart == null && audio.Count == 0)
        {
            _log("Nothing was recorded, folder removed.");
            Directory.Delete(Folder, true);
            return;
        }
        var offsets = ComputeOffsets([videoStart, .. audio.Select(a => (double?)a.Start)]);
        var mux = BuildMuxArgs(videoStart != null ? VideoConcat : null, offsets[0], audio.Select((a, i) => (a.File, offsets[i + 1])).ToList(), MeetingMp4);
        var mp4Ok = await RunFfmpegAsync(mux) != null;
        var tracks = audio.Select((a, i) => (a.File, Offset: offsets[i + 1])).ToList();
        var transcriptOk = audio.Count == 0 || await RunFfmpegAsync(BuildTranscriptArgs(
            tracks.FirstOrDefault(t => t.File == MicWav) is { File: not null } me ? me : null,
            tracks.FirstOrDefault(t => t.File == TeamsWav) is { File: not null } others ? others : null,
            TranscriptWav)) != null;
        Info.Finalized = true;
        SaveInfo();
        if (!mp4Ok || !transcriptOk)
        {
            _log("Could not create the final files, source files are kept.");
            return;
        }
        // Źródła są już w meeting.mp4 (wszystkie segmenty wideo + audio).
        foreach (var file in Info.TeamsAudio.Concat(Info.SharedAudio).Select(t => t.File).Concat(Info.Video.Select(v => v.File)).Append(TeamsWav).Append(MicWav).Append(VideoConcat))
            File.Delete(Path.Combine(Folder, file));
        _log($"Saved: {Path.Combine(Folder, MeetingMp4)}");
    }

    // Kończy sesje przerwane przez crash / zabicie procesu / wyłączenie prądu.
    // Zwraca foldery odzyskanych nagrań (do transkrypcji).
    public static async Task<List<string>> RecoverAsync(string outputFolder, string ffmpeg, Action<string> log)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(outputFolder)) return recovered;
        foreach (var folder in Directory.GetDirectories(outputFolder))
        {
            var json = Path.Combine(folder, InfoJson);
            if (!File.Exists(json)) continue;
            SessionInfo? info;
            try { info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(json)); }
            catch (JsonException e)
            {
                log($"Skipping {Path.GetFileName(folder)}: {e.Message}");
                continue;
            }
            if (info == null || info.Finalized) continue;

            log($"Recovering interrupted recording: {Path.GetFileName(folder)}");
            // Crash przed pierwszym zapisem startu: start audio = start sesji (synchronizacja przybliżona).
            var fallback = (info.Started.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
            foreach (var track in info.TeamsAudio.Concat(info.SharedAudio))
                if (RepairWav(Path.Combine(folder, track.File)) > 0) track.Start ??= fallback;
            if (RepairWav(Path.Combine(folder, MicWav)) > 0) info.MicStart ??= fallback;
            info.Ended ??= Directory.GetLastWriteTime(folder);
            await new RecordingSession(folder, ffmpeg, 0, 0, log, info).FinalizeAsync();
            if (Directory.Exists(folder)) recovered.Add(folder);
        }
        return recovered;
    }

    // WaveFileWriter zapisuje rozmiary w nagłówku dopiero przy zamknięciu; po crashu są 0. Zwraca liczbę bajtów danych.
    public static long RepairWav(string path)
    {
        if (!File.Exists(path)) return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var header = new byte[Math.Min(fs.Length, 4096)];
        fs.ReadExactly(header);
        if (header.Length < 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF") return 0;
        for (var pos = 12; pos + 8 <= header.Length;)
        {
            var size = BitConverter.ToUInt32(header, pos + 4);
            if (Encoding.ASCII.GetString(header, pos, 4) == "data")
            {
                var data = fs.Length - pos - 8;
                // shortcut: rozmiar WAV to uint (max 4 GB = ok. 6 h audio Teams); dłuższe nagrania wymagają RF64
                fs.Position = 4;
                fs.Write(BitConverter.GetBytes((uint)Math.Min(fs.Length - 8, uint.MaxValue)));
                fs.Position = pos + 4;
                fs.Write(BitConverter.GetBytes((uint)Math.Min(data, uint.MaxValue)));
                return data;
            }
            if (size > header.Length) return 0; // uszkodzony nagłówek
            pos += 8 + (int)size + (int)(size % 2);
        }
        return 0;
    }

    // Zwraca stdout ffmpeg albo null przy błędzie (błąd trafia do logu, chyba że quiet - np. pusty segment wideo).
    async Task<string?> RunFfmpegAsync(string args, bool quiet = false)
    {
        var p = Process.Start(new ProcessStartInfo(_ffmpeg, args)
        {
            WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode == 0) return await output;
        if (!quiet) _log($"ffmpeg error: {errors.Trim()}");
        return null;
    }

    // Pierwszy pakiet wideo z oryginalnym znacznikiem czasu (-copyts), format framecrc.
    public static string BuildFirstFrameArgs(string video) =>
        $"-hide_banner -loglevel error -copyts -i \"{video}\" -map 0:v:0 -c copy -frames:v 1 -f framecrc -";

    // "#tb 0: 1/1000" + "0, 1792421110067, 1792421110067, 66, 659, 0x..." -> czas uniksowy pierwszej klatki
    public static double? ParseFirstFrame(string? framecrc)
    {
        var tb = Regex.Match(framecrc ?? "", @"^#tb 0: (\d+)/(\d+)", RegexOptions.Multiline);
        var frame = Regex.Match(framecrc ?? "", @"^0,\s*-?\d+,\s*(\d+),", RegexOptions.Multiline);
        if (!tb.Success || !frame.Success) return null;
        var seconds = long.Parse(frame.Groups[1].Value) * double.Parse(tb.Groups[1].Value) / double.Parse(tb.Groups[2].Value);
        return seconds > 1e9 ? seconds : null; // bez czasu systemowego (stary plik) nie da się zsynchronizować
    }

    // Wołane też okresowo w trakcie nagrania, żeby po crashu znać starty strumieni.
    public void SaveInfo() =>
        SafeFile.WriteAllText(Path.Combine(Folder, InfoJson), JsonSerializer.Serialize(Info, new JsonSerializerOptions { WriteIndented = true }));

    public static bool HasFreeSpace(string folder, long minFreeBytes) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace >= minFreeBytes;

    public static string FolderName(DateTime started, string title)
    {
        var name = title.Replace("| Microsoft Teams", "").Trim(' ', '|');
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 60) name = name[..60];
        name = name.TrimEnd(' ', '.'); // Windows nie pozwala na kropkę/spację na końcu nazwy
        return $"{started:yyyy-MM-dd_HH-mm-ss}_{(name.Length == 0 ? "Teams" : name)}";
    }

    // Przesunięcia strumieni względem najwcześniejszego (wszystkie >= 0). Null = brak strumienia -> 0.
    public static double[] ComputeOffsets(IReadOnlyList<double?> starts)
    {
        var first = starts.Where(s => s != null).DefaultIfEmpty(0).Min()!.Value;
        return starts.Select(s => s is { } v ? v - first : 0).ToArray();
    }

    // Ramka 16:9 o wysokości videoHeight (źródło skalowane z zachowaniem proporcji): wszystkie segmenty mają ten sam rozmiar,
    // więc da się je skleić bez ponownego kodowania. libx264 CRF: h264_mf ignorował bitrate (136 kb/s przy -b:v 8M).
    public static string BuildVideoArgs(CaptureTarget target, int fps, int videoHeight, string output)
    {
        var source = target.Monitor != 0 ? $"hmonitor={target.Monitor}" : $"hwnd={target.Hwnd}";
        return "-hide_banner -loglevel error -nostats -y -filter_complex " +
               $"\"gfxcapture={source}:max_framerate={fps}:resize_mode=scale_aspect:scale_mode=bicubic:width={videoHeight * 16 / 9 / 2 * 2}:height={videoHeight}," +
               // czas systemowy w klatkach: start wideo znany dokładnie mimo opóźnienia startu i statycznego okna (WGC nie wysyła klatek bez zmian)
               "setpts=RTCTIME/(TB*1000000),hwdownload,format=bgra,format=yuv420p\" " +
               // zerolatency + flush + klatka kluczowa co 2 s: dane trafiają na dysk po ok. 1 s, więc ubicie ffmpeg
               // (okno zminimalizowane przy udostępnianiu blokuje "q") traci najwyżej ok. 1 s zamiast całego bufora kodera.
               $"-c:v libx264 -preset veryfast -tune zerolatency -crf 18 -g 30 -flush_packets 1 -cluster_time_limit 1000 \"{output}\"";
    }

    // Lista dla demuxera concat; "duration" = odstęp do następnego segmentu, więc przerwy zostają na osi czasu.
    public static string BuildConcatList(IReadOnlyList<(string File, double Start)> segments)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < segments.Count; i++)
        {
            list.Append($"file '{segments[i].File}'\n");
            if (i + 1 < segments.Count) list.Append($"duration {F(segments[i + 1].Start - segments[i].Start)}\n");
        }
        return list.ToString();
    }

    // Wideo = lista concat (zaczyna się od 0 = pierwsza klatka). Jedna ścieżka audio = miks Teams + mikrofon
    // (odtwarzacze grają tylko pierwszą ścieżkę); WAV-y przesuwa adelay.
    public static string BuildMuxArgs(string? videoConcat, double videoOffset, IReadOnlyList<(string File, double Offset)> audio, string output)
    {
        var args = new StringBuilder("-hide_banner -loglevel error -y");
        if (videoConcat != null) args.Append($" -itsoffset {F(videoOffset)} -f concat -safe 0 -i \"{videoConcat}\"");
        var first = videoConcat != null ? 1 : 0;
        foreach (var a in audio) args.Append($" -i \"{a.File}\"");
        if (videoConcat != null) args.Append(" -map 0:v -c:v copy");
        if (audio.Count > 0)
        {
            var delays = string.Concat(audio.Select((a, i) => $"[{first + i}:a]adelay={F(a.Offset * 1000)}:all=1[a{i}];"));
            var labels = string.Concat(audio.Select((_, i) => $"[a{i}]"));
            args.Append($" -filter_complex \"{delays}{labels}amix=inputs={audio.Count}:duration=longest:normalize=0[audio]\" -map \"[audio]\" -c:a aac -b:a 160k");
        }
        return args.Append($" \"{output}\"").ToString();
    }

    // 16 kHz stereo dla Whisper: lewy kanał = mikrofon (Ja), prawy = Teams + dźwięk prezentacji (Rozmówcy).
    // whisper-cli --diarize przypisuje mówcę po energii kanałów - bez dodatkowych modeli.
    public static string BuildTranscriptArgs((string File, double Offset)? me, (string File, double Offset)? others, string output)
    {
        (string File, double Offset, string Pan)[] tracks =
        [
            .. me is { } m ? [(m.File, m.Offset, "c0=c0|c1=0*c0")] : Array.Empty<(string, double, string)>(),
            .. others is { } o ? [(o.File, o.Offset, "c0=0*c0|c1=c0")] : Array.Empty<(string, double, string)>(),
        ];
        var inputs = string.Concat(tracks.Select(t => $" -i \"{t.File}\""));
        var chains = string.Concat(tracks.Select((t, i) =>
            $"[{i}:a]adelay={F(t.Offset * 1000)}:all=1,aresample=16000,aformat=channel_layouts=mono,pan=stereo|{t.Pan}[a{i}];"));
        var labels = string.Concat(tracks.Select((_, i) => $"[a{i}]"));
        return $"-hide_banner -loglevel error -y{inputs} -filter_complex \"{chains}{labels}amix=inputs={tracks.Length}:duration=longest:normalize=0[out]\" " +
               $"-map \"[out]\" -c:a pcm_s16le \"{output}\"";
    }

    // Miks ścieżek w jedną (48 kHz, kanały jak w źródle) - wszystkie procesy Teams + dźwięk prezentacji -> teams.wav.
    public static string BuildMixArgs(IReadOnlyList<(string File, double Offset)> audio, string output)
    {
        var inputs = string.Concat(audio.Select(a => $" -i \"{a.File}\""));
        var delays = string.Concat(audio.Select((a, i) => $"[{i}:a]adelay={F(a.Offset * 1000)}:all=1[a{i}];"));
        var labels = string.Concat(audio.Select((_, i) => $"[a{i}]"));
        return $"-hide_banner -loglevel error -y{inputs} -filter_complex \"{delays}{labels}amix=inputs={audio.Count}:duration=longest:normalize=0[out]\" " +
               $"-map \"[out]\" -c:a pcm_s16le \"{output}\"";
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
