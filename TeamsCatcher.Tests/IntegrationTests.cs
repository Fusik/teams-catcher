using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using TeamsCatcher.Core;

namespace TeamsCatcher.Tests;

// Prawdziwy Windows + ffmpeg/ffplay w PATH; ffplay udaje Teams (okno + dźwięk).
// Uruchamianie: dotnet test --filter Category=Integration (tony grają cicho w głośnikach).
[Trait("Category", "Integration")]
[Collection("Integration")] // sekwencyjnie: testy otwierają okna i dźwięk
public sealed class IntegrationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("tc-it-").FullName;
    readonly List<Process> _processes = [];
    readonly List<string> _log = [];

    public void Dispose()
    {
        foreach (var p in _processes) if (!p.HasExited) p.Kill();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Process_loopback_records_only_target_process()
    {
        var a = Play("ToneA", "sine=frequency=440:sample_rate=48000", window: false);
        Play("ToneB", "sine=frequency=1000:sample_rate=48000", window: false);
        await Task.Delay(1500);

        var wav = Path.Combine(_dir, "a.wav");
        using (var cts = new CancellationTokenSource(3000))
        using (var writer = new WaveFileWriter(wav, new WaveFormat(48000, 16, 2)))
            await ProcessLoopback.CaptureAsync(a.Id, (b, n) => writer.Write(b, 0, n), cts.Token);

        Assert.True(Goertzel(wav, 440) - Goertzel(wav, 1000) > 40, "440 Hz should dominate 1000 Hz by > 40 dB");
    }

    [Fact]
    public async Task Audio_pids_finds_process_with_audio_session()
    {
        var player = Play("Pids", "sine=frequency=440:sample_rate=48000", window: false);
        await Task.Delay(1500);
        Assert.Contains(player.Id, TeamsProcesses.AudioPids("ffplay"));
        Assert.DoesNotContain(player.Id, TeamsProcesses.AudioPids("no-such-process"));
    }

    [Fact]
    public async Task Silent_process_still_produces_realtime_audio()
    {
        var p = Play("Silent", "color=c=black:s=320x240:r=30", window: true);
        await Task.Delay(1500);
        long bytes = 0;
        using (var cts = new CancellationTokenSource(3000))
            await ProcessLoopback.CaptureAsync(p.Id, (_, n) => bytes += n, cts.Token);
        var seconds = bytes / 192000.0; // 48 kHz * 2 kanały * 2 B
        Assert.InRange(seconds, 2.7, 3.1); // brak dziur = brak rozjazdu z wideo
    }

    [Fact]
    public async Task Video_captures_covered_window_with_odd_size()
    {
        var target = Play("Target", "color=c=0xFF00FF:s=641x361:r=30", window: true);
        var window = await WaitForWindow("Target");
        Play("Cover", "color=c=green:s=1920x1080:r=30", window: true, extra: "-left 0 -top 0");
        await WaitForWindow("Cover");

        var output = Path.Combine(_dir, "v.mkv");
        var ff = Process.Start(new ProcessStartInfo("ffmpeg", RecordingSession.BuildVideoArgs(new CaptureTarget(window.Handle, 0), 15, 1080, output))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        _processes.Add(ff);
        await Task.Delay(3000);
        ff.StandardInput.Write('q');
        await ff.WaitForExitAsync(new CancellationTokenSource(20000).Token);

        Assert.Equal(0, ff.ExitCode);
        Assert.Contains("width=1920", Probe(output, "stream=width")); // ramka 16:9, okno przeskalowane
        Assert.Equal((0xFF, 0x00, 0xFF), CenterPixel(output), new ColorComparer(75));
        GC.KeepAlive(target);
    }

    [Fact]
    public async Task Full_session_produces_mp4_and_transcript()
    {
        var player = Play("Meeting", "testsrc2=s=640x360:r=30", window: true, audio: "sine=frequency=440:sample_rate=48000");
        var window = await WaitForWindow("Meeting");
        var settings = new Settings { OutputFolder = _dir, Fps = 15, RecordMicrophone = true };

        var session = RecordingSession.Start(settings, "ffmpeg", [player.Id], window, () => false, _log.Add);
        await Task.Delay(5000);
        await session.StopAsync();

        var mp4 = Path.Combine(session.Folder, RecordingSession.MeetingMp4);
        var streams = Probe(mp4, "stream=codec_type");
        Assert.Contains("codec_type=video", streams);
        Assert.True(streams.Split("codec_type=audio").Length - 1 >= 1, string.Join("\n", _log));
        var duration = double.Parse(Probe(mp4, "format=duration").Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(duration is > 4.0 and < 6.5, $"duration {duration}\n{Probe(mp4, "stream=index,codec_type,start_time,duration")}\n" +
            File.ReadAllText(Path.Combine(session.Folder, RecordingSession.InfoJson)) + "\n" + string.Join("\n", _log));

        // synchronizacja: wideo zatrzymywane tuż przed audio, więc oba końce prawie razem
        var ends = Probe(mp4, "stream=start_time,duration").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.Parse(l.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture)).Chunk(2).Select(c => c[0] + c[1]).ToArray();
        Assert.True(Math.Abs(ends[1] - ends[0]) < 0.5, $"video end {ends[0]:F2}, Teams audio end {ends[1]:F2}");

        using var transcript =new WaveFileReader(Path.Combine(session.Folder, RecordingSession.TranscriptWav));
        Assert.Equal(16000, transcript.WaveFormat.SampleRate);
        Assert.Equal(2, transcript.WaveFormat.Channels); // lewy = Ja, prawy = Rozmówcy
        Assert.True(Goertzel(Path.Combine(session.Folder, RecordingSession.TranscriptWav), 440, channel: 1) > -20, "Others channel should contain the Teams 440 Hz tone");
        Assert.True(session.Info.Finalized);
        // sprzątanie: źródła są w MP4
        Assert.Equal([RecordingSession.MeetingMp4, RecordingSession.InfoJson, RecordingSession.TranscriptWav],
            Directory.GetFiles(session.Folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Session_survives_window_closing_mid_recording()
    {
        var player = Play("Closing", "testsrc2=s=640x360:r=30", window: true, audio: "sine=frequency=440:sample_rate=48000");
        var window = await WaitForWindow("Closing");
        var settings = new Settings { OutputFolder = _dir, RecordMicrophone = false };

        var session = RecordingSession.Start(settings, "ffmpeg", [player.Id], window, () => false, _log.Add);
        await Task.Delay(3000);
        player.Kill(); // Teams zamyka okno spotkania
        await Task.Delay(1500);
        Assert.False(session.VideoRunning);
        await session.StopAsync();

        Assert.Contains("codec_type=video", Probe(Path.Combine(session.Folder, RecordingSession.MeetingMp4), "stream=codec_type"));
    }

    // WGC nie wysyła klatek, gdy okno się nie zmienia - start wideo i tak musi być poprawny.
    [Fact]
    public async Task Static_window_video_starts_at_correct_time()
    {
        var player = Play("Static", "color=c=0xFF00FF:s=320x240:r=1", window: true, audio: "sine=frequency=440:sample_rate=48000");
        var window = await WaitForWindow("Static");
        await Task.Delay(1500); // okno wyrysowane, dalej bez zmian
        var session = RecordingSession.Start(new Settings { OutputFolder = _dir, RecordMicrophone = false }, "ffmpeg", [player.Id], window, () => false, _log.Add);
        await Task.Delay(4000);
        await session.StopAsync();

        var video = session.Info.Video[0];
        Assert.NotNull(video.Start);
        Assert.InRange(video.Start!.Value - session.Info.TeamsAudio[0].Start!.Value, -0.5, 2.5);
    }

    [Fact]
    public async Task Interrupted_session_is_recovered_on_start()
    {
        // Stan po crashu: MKV (czas systemowy w klatkach), WAV z pustym nagłówkiem, session.json bez Finalized.
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "2026-10-19_10-00-00_Crashed")).FullName;
        var start = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var video = Process.Start(new ProcessStartInfo("ffmpeg",
            "-hide_banner -loglevel error -re -f lavfi -t 2 -i testsrc2=s=320x240:r=15 -vf setpts=RTCTIME/(TB*1000000) -c:v h264_mf video_01.mkv")
            { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true })!;
        await video.WaitForExitAsync();
        using (var w = new WaveFileWriter(Path.Combine(folder, "teams_1.wav"), new WaveFormat(48000, 16, 2)))
            w.Write(new byte[192000 * 2], 0, 192000 * 2);
        var wav = File.ReadAllBytes(Path.Combine(folder, "teams_1.wav"));
        Array.Clear(wav, wav.AsSpan().IndexOf("data"u8) + 4, 4); // rozmiar danych = 0, jak po zabiciu procesu
        File.WriteAllBytes(Path.Combine(folder, "teams_1.wav"), wav);
        File.WriteAllText(Path.Combine(folder, RecordingSession.InfoJson), System.Text.Json.JsonSerializer.Serialize(new SessionInfo
        {
            Started = DateTime.Now, TeamsAudio = [new AudioTrack { File = "teams_1.wav", Pid = 1, Start = start }], Video = [new VideoSegment { File = "video_01.mkv" }],
        }));
        Directory.CreateDirectory(Path.Combine(_dir, "not-a-session"));

        await RecordingSession.RecoverAsync(_dir, "ffmpeg", _log.Add);

        var mp4 = Path.Combine(folder, RecordingSession.MeetingMp4);
        Assert.True(File.Exists(mp4), string.Join("\n", _log));
        Assert.Contains("codec_type=video", Probe(mp4, "stream=codec_type"));
        Assert.True(File.Exists(Path.Combine(folder, RecordingSession.TranscriptWav)));
        Assert.Contains("\"Finalized\":true", File.ReadAllText(Path.Combine(folder, RecordingSession.InfoJson)).Replace(" ", ""));

        _log.Clear();
        await RecordingSession.RecoverAsync(_dir, "ffmpeg", _log.Add); // drugi start: nic do zrobienia
        Assert.Empty(_log);
    }

    // Przełączenie celu w trakcie (np. start udostępniania): oba segmenty w jednym MP4, przerwa zachowana.
    [Fact]
    public async Task Switching_target_produces_one_video_with_both_segments()
    {
        var first = Play("SegA", "color=c=red:s=640x360:r=15", window: true, audio: "sine=frequency=440:sample_rate=48000");
        var a = await WaitForWindow("SegA");
        Play("SegB", "color=c=blue:s=640x360:r=15", window: true);
        var b = await WaitForWindow("SegB");
        var session = RecordingSession.Start(new Settings { OutputFolder = _dir, RecordMicrophone = false }, "ffmpeg", [first.Id], a, () => false, _log.Add);
        await Task.Delay(3000);
        await session.SwitchVideoAsync(new CaptureTarget(b.Handle, 0) { Name = "SegB" });
        await Task.Delay(3000);
        await session.StopAsync();

        var mp4 = Path.Combine(session.Folder, RecordingSession.MeetingMp4);
        var duration = double.Parse(Probe(mp4, "stream=duration").Split('\n')[0].Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(duration > 4.5, $"video {duration:F2} s should cover both segments\n" + string.Join("\n", _log));
        Assert.Empty(Directory.GetFiles(session.Folder, "video_*.mkv")); // sprzątanie
    }

    // Prezentacja z dźwiękiem: dźwięk udostępnianej aplikacji trafia do nagrania tylko w czasie udostępniania.
    [Fact]
    public async Task Shared_audio_is_recorded_only_while_sharing()
    {
        var teams = Play("TeamsLike", "sine=frequency=440:sample_rate=48000", window: false);
        var shared = Play("SharedApp", "sine=frequency=1000:sample_rate=48000", window: false);
        await Task.Delay(1500);
        var session = RecordingSession.Start(new Settings { OutputFolder = _dir, RecordMicrophone = false }, "ffmpeg", [teams.Id], null, () => false, _log.Add);
        await Task.Delay(1000);
        session.SetSharedAudio([shared.Id]);
        await Task.Delay(2000);
        session.SetSharedAudio([]);
        await Task.Delay(2000);
        await session.StopAsync();

        var wav = Path.Combine(session.Folder, RecordingSession.TranscriptWav);
        Assert.True(Goertzel(wav, 1000, channel: 1) > -30, "shared 1000 Hz tone should be in the recording\n" + string.Join("\n", _log));
        var track = Assert.Single(session.Info.SharedAudio);
        Assert.Equal(shared.Id, track.Pid);
        Assert.Empty(Directory.GetFiles(session.Folder, "shared_*.wav")); // sprzątanie
    }

    [Fact]
    public async Task Monitor_capture_produces_frames()
    {
        var target = TeamsWindowFinder.MatchShare(new Bounds(0, 0, 1, 1), 0, default, []); // tylko kontrola: zbyt mała ramka = brak celu
        Assert.Null(target);
        var output = Path.Combine(_dir, "m.mkv");
        var ff = Process.Start(new ProcessStartInfo("ffmpeg", RecordingSession.BuildVideoArgs(new CaptureTarget(0, MonitorFromPoint(0, 0, 1)), 15, 1080, output))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        _processes.Add(ff);
        Play("Moving", "testsrc2=s=320x240:r=30", window: true); // ruch na ekranie, żeby WGC wysyłał klatki
        await Task.Delay(3000);
        ff.StandardInput.Write('q');
        await ff.WaitForExitAsync(new CancellationTokenSource(20000).Token);
        Assert.NotNull(RecordingSession.ParseFirstFrame(FirstFrame(output)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint MonitorFromPoint(long point, uint flags);
    static nint MonitorFromPoint(int x, int y, uint flags) => MonitorFromPoint(((long)y << 32) | (uint)x, flags);

    static string FirstFrame(string file)
    {
        var p = Process.Start(new ProcessStartInfo("ffmpeg", RecordingSession.BuildFirstFrameArgs(file))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    // Prawdziwy whisper (GPU): TC_WHISPER_CLI, TC_WHISPER_MODEL, TC_WHISPER_VAD, TC_SPEECH_STEREO (lewy kanał mówi pierwszy, prawy później).
    // Bez zmiennych środowiskowych test nic nie robi (whisper i model to ok. 4 GB poza repozytorium).
    [Fact]
    public async Task Transcribes_each_side_with_speaker_labels()
    {
        var speech = Environment.GetEnvironmentVariable("TC_SPEECH_STEREO");
        if (speech == null) return;
        var settings = new Settings
        {
            WhisperCli = Environment.GetEnvironmentVariable("TC_WHISPER_CLI")!,
            WhisperModel = Environment.GetEnvironmentVariable("TC_WHISPER_MODEL")!,
            WhisperVadModel = Environment.GetEnvironmentVariable("TC_WHISPER_VAD")!,
        };
        File.Copy(speech, Path.Combine(_dir, RecordingSession.TranscriptWav));

        await Transcriber.RunAsync(_dir, settings, "ffmpeg", _log.Add, CancellationToken.None);

        // nagranie po polsku -> język główny polski, tłumaczenie na angielski (Ollama)
        var lines = File.ReadAllLines(Path.Combine(_dir, Transcriber.TranscriptTxt));
        Assert.Equal("Main language: Polish · translation: English", lines[0]);
        Assert.Contains(lines, l => l.Contains("] Ja (PL): "));
        Assert.Contains(lines, l => l.Contains("] Ja (EN): "));
        Assert.Contains(lines, l => l.Contains("] Rozmówcy (PL): "));
        foreach (var file in new[] { "transcript.pl.txt", "transcript.en.txt", "notes.pl.md", "notes.en.md" })
            Assert.True(File.Exists(Path.Combine(_dir, file)), $"{file} missing\n{string.Join("\n", _log)}");
        Assert.Contains("## Podsumowanie", File.ReadAllText(Path.Combine(_dir, "notes.pl.md")));
        Assert.Empty(Directory.GetFiles(_dir, "whisper_*")); // pliki tymczasowe usunięte
    }

    // Prawdziwa diaryzacja (sherpa-onnx, CPU) na nagraniu testowym k2-fsa z 4 mówcami; TC_DIARIZATION_DIR = folder z modelami i test/.
    [Fact]
    public void Diarization_finds_four_speakers_in_sample()
    {
        var dir = Environment.GetEnvironmentVariable("TC_DIARIZATION_DIR");
        if (dir == null) return;
        var settings = new Settings
        {
            DiarizationSegmentationModel = Path.Combine(dir, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"),
            DiarizationEmbeddingModel = Path.Combine(dir, "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx"),
        };
        var wav = Path.Combine(_dir, "four.wav");
        Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -i \"{Path.Combine(dir, "test", "0-four-speakers-zh.wav")}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{wav}\"")
            { UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();

        // zdania co 1 s przez całe nagranie (57 s)
        var sentences = Enumerable.Range(0, 56).Select(i => new TranscriptSegment(i * 1000, i * 1000 + 1000, Transcriber.Others, $"s{i}")).ToList();
        var (labeled, _) = Diarizer.Run(wav, sentences, settings, Transcriber.OtherPrefix);
        Assert.InRange(labeled.Select(t => t.Speaker).Where(l => l.StartsWith("Rozmówca ")).Distinct().Count(), 3, 5); // cisza = "Rozmówcy"
        settings.DiarizationSpeakers = 2; // znana liczba rozmówców = dokładnie tyle
        Assert.Equal(2, Diarizer.Run(wav, sentences, settings, Transcriber.OtherPrefix).Segments.Select(t => t.Speaker).Where(l => l.StartsWith("Rozmówca ")).Distinct().Count());
    }

    // Angielska mowa z polskim akcentem (nagranie z testu na żywo): wbudowane wykrywanie dawało "pl", ocena obu wersji - "en".
    [Fact]
    public async Task English_speech_with_polish_accent_stays_english()
    {
        var speech = Environment.GetEnvironmentVariable("TC_SPEECH_EN_STEREO");
        if (speech == null) return;
        var settings = new Settings
        {
            WhisperCli = Environment.GetEnvironmentVariable("TC_WHISPER_CLI")!,
            WhisperModel = Environment.GetEnvironmentVariable("TC_WHISPER_MODEL")!,
            WhisperVadModel = Environment.GetEnvironmentVariable("TC_WHISPER_VAD")!,
            TranslateTranscript = false,
        };
        File.Copy(speech, Path.Combine(_dir, RecordingSession.TranscriptWav));

        await Transcriber.RunAsync(_dir, settings, "ffmpeg", _log.Add, CancellationToken.None);

        var text = File.ReadAllText(Path.Combine(_dir, Transcriber.TranscriptTxt));
        Assert.Contains("Main language: English", text);
        Assert.Contains("] Ja (EN): Hey guys", text);
    }

    Process Play(string title, string video, bool window, string? audio = null, string extra = "")
    {
        // ffplay ma jedno wejście, więc obraz+dźwięk = jeden graf lavfi z dwoma wyjściami
        var input = audio == null ? $"-f lavfi -i \"{video}\"" : $"-f lavfi -i \"{video}[out0];{audio}[out1]\"";
        var args = $"-hide_banner -loglevel error -volume 5 -window_title {title} {extra} {(window ? "" : "-nodisp")} {input}";
        var p = Process.Start(new ProcessStartInfo("ffplay", args) { UseShellExecute = false, CreateNoWindow = true })!;
        _processes.Add(p);
        return p;
    }

    static async Task<WindowInfo> WaitForWindow(string title)
    {
        for (var i = 0; i < 50; i++)
        {
            var w = TeamsWindowFinder.List("ffplay").FirstOrDefault(x => x.Title == title);
            if (w != null) return w;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Window {title} did not appear.");
    }

    static string Probe(string file, string entries)
    {
        var p = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -show_entries {entries} -of default=nw=1 \"{file}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    static (int, int, int) CenterPixel(string video)
    {
        var p = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{video}\" -frames:v 1 -vf crop=8:8:iw/2:ih/2,scale=1:1 -f rawvideo -pix_fmt rgb24 -")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var buffer = new byte[3];
        p.StandardOutput.BaseStream.ReadExactly(buffer);
        p.WaitForExit();
        return (buffer[0], buffer[1], buffer[2]);
    }

    // Moc częstotliwości w dB (algorytm Goertzla, pierwszy kanał).
    static double Goertzel(string wav, double hz, int channel = 0)
    {
        using var reader = new AudioFileReader(wav);
        int channels = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
        double coeff = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
        long n = 0;
        var buffer = new float[rate * channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i += channels) { var s = buffer[i + channel] + coeff * s1 - s2; s2 = s1; s1 = s; n++; }
        return 10 * Math.Log10((s1 * s1 + s2 * s2 - coeff * s1 * s2) / Math.Max(n, 1) + 1e-12);
    }

    // Jeden krok nagrania na kopii folderu: TC_STEP_FOLDER, TC_STEP (np. Correct), TC_NOTES_SETTINGS, opcjonalnie TC_WRITER / TC_CHECKER
    // (inne modele niż w ustawieniach); log w folderze (step-test.log).
    [Fact]
    public async Task One_step_on_a_recording_copy()
    {
        var folder = Environment.GetEnvironmentVariable("TC_STEP_FOLDER");
        if (folder == null) return;
        var logFile = Path.Combine(folder, "step-test.log");
        void Log(string line) => File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {line}\n");
        var settings = Settings.Load(Environment.GetEnvironmentVariable("TC_NOTES_SETTINGS")!, Log);
        if (Environment.GetEnvironmentVariable("TC_WRITER") is { Length: > 0 } writer) settings.CorrectionModel = writer;
        if (Environment.GetEnvironmentVariable("TC_CHECKER") is { Length: > 0 } checker) settings.CheckModel = checker;
        Ollama.Log = Log;
        await Transcriber.RunAsync(folder, settings, "ffmpeg", Log, CancellationToken.None, Enum.Parse<Transcriber.Steps>(Environment.GetEnvironmentVariable("TC_STEP")!));
    }

    sealed class ColorComparer(int tolerance) : IEqualityComparer<(int, int, int)>
    {
        public bool Equals((int, int, int) a, (int, int, int) b) =>
            Math.Abs(a.Item1 - b.Item1) <= tolerance && Math.Abs(a.Item2 - b.Item2) <= tolerance && Math.Abs(a.Item3 - b.Item3) <= tolerance;
        public int GetHashCode((int, int, int) c) => 0;
    }
}
