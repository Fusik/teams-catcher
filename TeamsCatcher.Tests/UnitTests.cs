using System.IO;
using NAudio.Wave;
using TeamsCatcher.Core;

namespace TeamsCatcher.Tests;

public class CallDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0);
    readonly CallDetector _d = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    [Fact]
    public void Short_mic_activity_does_not_start_call()
    {
        Assert.False(_d.Update(true, T0));
        Assert.False(_d.Update(true, T0.AddSeconds(1)));
        Assert.False(_d.Update(false, T0.AddSeconds(1.5)));
        Assert.False(_d.Update(true, T0.AddSeconds(3))); // licznik od nowa
        Assert.Equal(CallState.Idle, _d.State);
    }

    [Fact]
    public void Mic_active_for_start_delay_starts_call_once()
    {
        _d.Update(true, T0);
        Assert.True(_d.Update(true, T0.AddSeconds(2)));
        Assert.False(_d.Update(true, T0.AddSeconds(3)));
        Assert.Equal(CallState.InCall, _d.State);
    }

    [Fact]
    public void Short_mic_release_does_not_end_call()
    {
        StartCall();
        Assert.False(_d.Update(false, T0.AddSeconds(10)));
        Assert.False(_d.Update(false, T0.AddSeconds(15)));
        Assert.False(_d.Update(true, T0.AddSeconds(16)));
        Assert.False(_d.Update(false, T0.AddSeconds(20))); // licznik od nowa
        Assert.Equal(CallState.InCall, _d.State);
    }

    [Fact]
    public void Mic_released_for_end_delay_ends_call_once()
    {
        StartCall();
        _d.Update(false, T0.AddSeconds(10));
        Assert.True(_d.Update(false, T0.AddSeconds(20)));
        Assert.False(_d.Update(false, T0.AddSeconds(21)));
        Assert.Equal(CallState.Idle, _d.State);
    }



    void StartCall()
    {
        _d.Update(true, T0);
        _d.Update(true, T0.AddSeconds(2));
    }
}

public class TeamsWindowFinderTests
{
    static WindowInfo W(nint h, string cls = TeamsWindowFinder.TeamsWindowClass, int w = 800, int hgt = 600) => new(h, 1, cls, "", new Bounds(0, 0, w, hgt));

    [Fact]
    public void Largest_teams_window_is_main()
    {
        var small = W(1, w: 400, hgt: 300);
        var main = W(2, w: 1900, hgt: 1000);
        Assert.Equal(main, TeamsWindowFinder.PickLargest([small, main]));
    }

    [Fact]
    public void Ignores_other_classes_and_empty_windows()
    {
        Assert.Null(TeamsWindowFinder.PickLargest([W(1, cls: "PseudoConsoleWindow", w: 3000), W(2, w: 0, hgt: 0)]));
        Assert.Null(TeamsWindowFinder.PickLargest([]));
    }

    // Dane z sondy na żywo: monitor 1920x1080, pasek zadań 40 px.
    static readonly Bounds Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Share_border_equal_to_monitor_means_screen_share()
    {
        var target = TeamsWindowFinder.MatchShare(new Bounds(0, 0, 1920, 1080), 77, Monitor, [(5, new Bounds(0, 0, 1920, 1040), "Explorer", 50)]);
        Assert.Equal(new CaptureTarget(0, 77), target);
    }

    [Fact]
    public void Share_border_equal_to_window_frame_means_window_share()
    {
        // zmaksymalizowane okno: GetWindowRect -8,-8,1928,1048, ale granice DWM = ramka Teams 0,0,1920,1040
        var target = TeamsWindowFinder.MatchShare(new Bounds(0, 0, 1920, 1040), 77, Monitor,
            [(4, new Bounds(300, 200, 900, 700), "Other", 40), (5, new Bounds(0, 0, 1920, 1040), "Notatnik", 50)]);
        Assert.Equal(new CaptureTarget(5, 0), target);
        Assert.Equal("shared window: Notatnik", target!.Name);
        Assert.Equal(50, target.Pid);
    }

    [Fact]
    public void Unknown_share_area_falls_back_to_monitor() =>
        Assert.Equal(new CaptureTarget(0, 77), TeamsWindowFinder.MatchShare(new Bounds(100, 100, 600, 600), 77, Monitor, []));

    [Fact]
    public void Border_still_appearing_is_ignored() =>
        Assert.Null(TeamsWindowFinder.MatchShare(new Bounds(0, 0, 1, 1), 77, Monitor, []));
}

public class TeamsProcessesTests
{
    [Fact]
    public void Family_includes_all_descendants_of_teams()
    {
        // z testu na żywo: ms-teams 20252 -> webview 3520 -> webview 47660 (dźwięk); ms-teams 20252 -> 2352 (media)
        (int, int)[] all = [(20252, 37816), (3520, 20252), (47660, 3520), (2352, 20252), (21864, 14756), (3888, 21864), (0, 0)];
        Assert.Equal([2352, 3520, 20252, 47660], TeamsProcesses.Descendants(all, [20252]).Order());
    }

    // msedge 100 (okno) -> 101 (proces audio); Teams 20252 -> 3520 -> 47660; Spotify 300; ta aplikacja 400; sesja systemowa pid 0
    static readonly (int, int)[] Tree = [(100, 1), (101, 100), (20252, 2), (3520, 20252), (47660, 3520), (300, 1), (400, 1)];
    static readonly int[] Sessions = [0, 101, 47660, 300, 400];

    [Fact]
    public void Window_share_records_only_that_app() =>
        Assert.Equal([101], TeamsProcesses.PickSharedAudio(Sessions, Tree, [20252], sharedWindowPid: 100, ownPid: 400));

    [Fact]
    public void Screen_share_records_everything_except_teams_and_self() =>
        Assert.Equal([101, 300], TeamsProcesses.PickSharedAudio(Sessions, Tree, [20252], sharedWindowPid: 0, ownPid: 400));

    [Fact]
    public void No_roots_no_family() => Assert.Empty(TeamsProcesses.Descendants([(1, 0), (2, 1)], []));
}

public class RecordingSessionTests
{
    static readonly DateTime Start = new(2026, 10, 19, 14, 3, 7);

    [Theory]
    [InlineData("Spotkanie z użytkownikiem W F | Microsoft Teams", "2026-10-19_14-03-07_Spotkanie z użytkownikiem W F")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "2026-10-19_14-03-07_a_b_c_d_e_f_g_h_i_j")]
    [InlineData("", "2026-10-19_14-03-07_Teams")]
    [InlineData(" | Microsoft Teams", "2026-10-19_14-03-07_Teams")]
    [InlineData("ends with dot.", "2026-10-19_14-03-07_ends with dot")]
    public void Folder_name_is_safe(string title, string expected) =>
        Assert.Equal(expected, RecordingSession.FolderName(Start, title));

    [Fact]
    public void Folder_name_title_is_limited_to_60_chars() =>
        Assert.Equal("2026-10-19_14-03-07_".Length + 60, RecordingSession.FolderName(Start, new string('x', 200)).Length);

    [Fact]
    public void Offsets_are_relative_to_earliest_stream()
    {
        Assert.Equal([0.5, 0, 0.25], RecordingSession.ComputeOffsets([1.5, 1.0, 1.25]));
        Assert.Equal([0, 0, 0.5], RecordingSession.ComputeOffsets([null, 1.0, 1.5]));
        Assert.Equal([0.0], RecordingSession.ComputeOffsets([null]));
    }

    [Theory]
    [InlineData("#software: Lavf63.1.102\r\n#tb 0: 1/1000\r\n#media_type 0: video\r\n0, 1792421110067, 1792421110067,       66,      659, 0x50ca0cc7\r\n", 1792421110.067)]
    [InlineData("#tb 0: 1/90000\n0, 161317899900000, 161317899900000, 6000, 10, 0x1\n", 1792421110.0)]
    [InlineData("#tb 0: 1/1000\n0,          0,          0,       66,     5956, 0x9727c581\n", null)] // bez czasu systemowego
    [InlineData("#tb 0: 1/1000\n", null)] // brak klatek
    [InlineData(null, null)]
    public void Parses_first_frame_time_from_framecrc(string? framecrc, double? expected) =>
        Assert.Equal(expected, RecordingSession.ParseFirstFrame(framecrc));

    [Fact]
    public void Video_args_capture_window_by_handle()
    {
        var args = RecordingSession.BuildVideoArgs(new CaptureTarget(723878, 0), 15, 1080, "video_01.mkv");
        Assert.Contains("gfxcapture=hwnd=723878:max_framerate=15:", args);
        Assert.Contains("-c:v libx264 -preset veryfast -tune zerolatency -crf 18", args);
        Assert.Contains("setpts=RTCTIME/(TB*1000000)", args); // czas systemowy w klatkach
        Assert.EndsWith("\"video_01.mkv\"", args);
    }

    [Theory]
    [InlineData(1080, "width=1920:height=1080")]
    [InlineData(1440, "width=2560:height=1440")]
    [InlineData(2160, "width=3840:height=2160")]
    public void Video_args_fixed_resolution(int height, string size) =>
        Assert.Contains(size, RecordingSession.BuildVideoArgs(new CaptureTarget(1, 0), 15, height, "v.mkv"));

    [Fact]
    public void Video_args_capture_monitor() =>
        Assert.Contains("gfxcapture=hmonitor=65537:", RecordingSession.BuildVideoArgs(new CaptureTarget(0, 65537), 15, 1080, "v.mkv"));

    [Fact]
    public void Concat_list_keeps_gaps_between_segments() =>
        Assert.Equal("ffconcat version 1.0\nfile 'video_01.mkv'\nduration 12.5\nfile 'video_02.mkv'\nduration 3.25\nfile 'video_03.mkv'\n",
            RecordingSession.BuildConcatList([("video_01.mkv", 1000), ("video_02.mkv", 1012.5), ("video_03.mkv", 1015.75)]));

    [Fact]
    public void Mux_args_video_and_one_mixed_audio_track()
    {
        var args = RecordingSession.BuildMuxArgs("video.ffconcat", 0.75, [("teams.wav", 0), ("mic.wav", 0.125)], "meeting.mp4");
        Assert.Equal("-hide_banner -loglevel error -y -itsoffset 0.75 -f concat -safe 0 -i \"video.ffconcat\" -i \"teams.wav\" -i \"mic.wav\" -map 0:v -c:v copy" +
                     " -filter_complex \"[1:a]adelay=0:all=1[a0];[2:a]adelay=125:all=1[a1];[a0][a1]amix=inputs=2:duration=longest:normalize=0[audio]\"" +
                     " -map \"[audio]\" -c:a aac -b:a 160k \"meeting.mp4\"", args);
    }

    [Fact]
    public void Mux_args_audio_only() =>
        Assert.Equal("-hide_banner -loglevel error -y -i \"teams.wav\" -filter_complex \"[0:a]adelay=0:all=1[a0];[a0]amix=inputs=1:duration=longest:normalize=0[audio]\"" +
                     " -map \"[audio]\" -c:a aac -b:a 160k \"meeting.mp4\"",
            RecordingSession.BuildMuxArgs(null, 0, [("teams.wav", 0)], "meeting.mp4"));

    [Fact]
    public void Transcript_is_stereo_me_left_others_right()
    {
        var args = RecordingSession.BuildTranscriptArgs(("mic.wav", 0.25), ("teams.wav", 0), "transcript.wav");
        Assert.Contains("[0:a]adelay=250:all=1,aresample=16000,aformat=channel_layouts=mono,pan=stereo|c0=c0|c1=0*c0[a0];", args);
        Assert.Contains("[1:a]adelay=0:all=1,aresample=16000,aformat=channel_layouts=mono,pan=stereo|c0=0*c0|c1=c0[a1];", args);
        Assert.Contains("[a0][a1]amix=inputs=2:duration=longest:normalize=0[out]", args);
        Assert.EndsWith("-c:a pcm_s16le \"transcript.wav\"", args);
    }

    [Fact]
    public void Transcript_without_microphone_has_only_others_channel() =>
        Assert.Contains("-i \"teams.wav\" -filter_complex \"[0:a]adelay=0:all=1,aresample=16000,aformat=channel_layouts=mono,pan=stereo|c0=0*c0|c1=c0[a0];[a0]amix=inputs=1",
            RecordingSession.BuildTranscriptArgs(null, ("teams.wav", 0), "transcript.wav"));

    [Fact]
    public void Teams_tracks_premix_keeps_48k_stereo()
    {
        var args = RecordingSession.BuildMixArgs([("teams_3520.wav", 0), ("teams_2352.wav", 1.5)], "teams.wav");
        Assert.Contains("[1:a]adelay=1500:all=1[a1];[a0][a1]amix=inputs=2:duration=longest:normalize=0[out]", args);
        Assert.DoesNotContain("aresample", args);
        Assert.DoesNotContain("-ac 1", args);
    }

    [Fact]
    public void Offsets_use_invariant_culture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
        try { Assert.Contains("-itsoffset 0.5 ", RecordingSession.BuildMuxArgs("v.ffconcat", 0.5, [], "o.mp4")); }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}

public class SettingsTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"tc-settings-{Guid.NewGuid()}.json");
    readonly List<string> _log = [];

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = Settings.Load(_path, _log.Add);
        Assert.True(s.AutoRecord);
        Assert.True(s.RecordMicrophone);
        Assert.Equal(15, s.Fps);
        Assert.Equal("ms-teams", s.TeamsProcess);
        Assert.Empty(_log);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_log()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(15, Settings.Load(_path, _log.Add).Fps);
        Assert.Single(_log);
    }

    [Fact]
    public void Round_trip()
    {
        new Settings { OutputFolder = @"D:\Rec", Fps = 5, RecordMicrophone = false }.Save(_path);
        var s = Settings.Load(_path, _log.Add);
        Assert.Equal(@"D:\Rec", s.OutputFolder);
        Assert.Equal(5, s.Fps);
        Assert.False(s.RecordMicrophone);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 60)]
    public void Fps_is_clamped(int fps, int expected)
    {
        File.WriteAllText(_path, $"{{\"Fps\":{fps}}}");
        Assert.Equal(expected, Settings.Load(_path, _log.Add).Fps);
    }

    [Theory]
    [InlineData("{\"TranscriptLanguage\":\"pl; calc.exe\",\"OllamaUrl\":\"file:///C:/secret\"}")]
    [InlineData("{\"WhisperModel\":\"x.bin\\\" -f C:\\\\secret.wav\"}")]
    public void Values_passed_to_command_line_are_validated(string json)
    {
        File.WriteAllText(_path, json);
        var s = Settings.Load(_path, _log.Add);
        Assert.Equal("auto", s.TranscriptLanguage);
        Assert.DoesNotContain('"', s.WhisperModel);
        Assert.Equal("http://127.0.0.1:11434", s.OllamaUrl);
    }

    [Theory]
    [InlineData("{\"DiarizationThreshold\":9,\"DiarizationSpeakers\":-3}", 1.5f, 0)]
    [InlineData("{\"DiarizationThreshold\":0,\"DiarizationSpeakers\":99}", 0.1f, 20)]
    public void Diarization_settings_are_clamped(string json, float threshold, int speakers)
    {
        File.WriteAllText(_path, json);
        var s = Settings.Load(_path, _log.Add);
        Assert.Equal(threshold, s.DiarizationThreshold);
        Assert.Equal(speakers, s.DiarizationSpeakers);
    }

    [Fact]
    public void Empty_output_folder_gives_default()
    {
        File.WriteAllText(_path, "{\"OutputFolder\":\" \"}");
        Assert.Equal(new Settings().OutputFolder, Settings.Load(_path, _log.Add).OutputFolder);
    }
}

public class WavRepairTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"tc-wav-{Guid.NewGuid()}.wav");

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData(48000, 16, 2)]  // teams.wav (PCM)
    [InlineData(48000, 32, 2)]  // mic.wav bywa IEEE float (dodatkowy chunk "fact")
    public void Repairs_header_left_empty_by_crash(int rate, int bits, int channels)
    {
        var format = bits == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, bits, channels);
        using (var w = new WaveFileWriter(_path, format)) w.Write(new byte[format.AverageBytesPerSecond], 0, format.AverageBytesPerSecond);
        ZeroSizes();

        Assert.Equal(format.AverageBytesPerSecond, RecordingSession.RepairWav(_path));
        using var r = new WaveFileReader(_path);
        Assert.Equal(1.0, r.TotalTime.TotalSeconds, 2);
    }

    [Fact]
    public void Missing_or_invalid_file_has_no_data()
    {
        Assert.Equal(0, RecordingSession.RepairWav(_path));
        File.WriteAllText(_path, "not a wav");
        Assert.Equal(0, RecordingSession.RepairWav(_path));
    }

    // Stan pliku po zabiciu procesu: nagłówek z rozmiarami 0.
    void ZeroSizes()
    {
        var bytes = File.ReadAllBytes(_path);
        Array.Clear(bytes, 4, 4);
        var data = bytes.AsSpan().IndexOf("data"u8);
        Array.Clear(bytes, data + 4, 4);
        File.WriteAllBytes(_path, bytes);
    }
}

public class TeamsCallWatcherTests
{
    [Theory]
    [InlineData("Wycisz mikrofon", false)]                 // z testu na żywo: mikrofon włączony
    [InlineData("Wyłącz wyciszenie mikrofonu", true)]      // z testu na żywo: wyciszony
    [InlineData("Mute mic (Ctrl+Shift+M)", false)]
    [InlineData("Unmute mic (Ctrl+Shift+M)", true)]
    [InlineData(null, false)]
    public void Button_name_tells_mute_state(string? name, bool muted) =>
        Assert.Equal(muted, TeamsCallWatcher.IsMutedName(name));

    static WindowInfo W(nint h, int l, int t, int r, int b) => new(h, 1, TeamsWindowFinder.TeamsWindowClass, "", new Bounds(l, t, r, b));

    [Fact]
    public void Meeting_window_is_the_call_window()
    {
        var meeting = W(861678, 294, 20, 1625, 852);
        Assert.Equal(meeting, TeamsCallWatcher.PickCallWindow([W(1, 565, 8, 1355, 63), meeting]));
    }

    [Fact]
    public void While_sharing_minimized_meeting_window_is_skipped()
    {
        // z sondy na żywo: okno spotkania zminimalizowane, widok kompaktowy i pasek sterowania
        var compact = W(5509152, 1684, 747, 1890, 1040);
        Assert.Equal(compact, TeamsCallWatcher.PickCallWindow([W(861678, -32000, -32000, -31840, -31972), W(4136452, 565, 8, 1355, 63), compact]));
    }

    [Fact]
    public void Only_minimized_window_is_still_a_call() =>
        Assert.Equal(861678, TeamsCallWatcher.PickCallWindow([W(861678, -32000, -32000, -31840, -31972)])!.Handle);
}

public class TranscriberTests
{
    // Format wyjścia whisper-cli -oj (skrócony) z testu na żywo.
    const string Json = """
        {"result":{"language":"en"},"transcription":[
          {"timestamps":{"from":"00:00:21,440","to":"00:00:27,570"},"offsets":{"from":21440,"to":27570},"text":" Hello guys, how are you today?"},
          {"timestamps":{"from":"00:00:27,570","to":"00:00:32,730"},"offsets":{"from":27570,"to":32730},"text":" How is the situation in Paris?"},
          {"timestamps":{"from":"00:00:40,000","to":"00:00:40,500"},"offsets":{"from":40000,"to":40500},"text":" "}
        ]}
        """;

    static TranscriptSegment S(double from, double to, string speaker, string text, string language = "pl") =>
        new((long)(from * 1000), (long)(to * 1000), speaker, text, language);

    [Fact]
    public void Parses_segments_with_detected_language_and_skips_empty_text()
    {
        var (segments, language) = Transcriber.ParseWhisperJson(Json, Transcriber.Me);
        Assert.Equal("en", language);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new TranscriptSegment(21440, 27570, "Ja", "Hello guys, how are you today?", "en"), segments[0]);
    }

    [Fact]
    public void Score_is_mean_log_probability_of_text_tokens()
    {
        const string full = """
            {"transcription":[{"offsets":{"from":0,"to":1000},"text":" Hey","tokens":[
              {"text":"[_BEG_]","p":0.01},{"text":" Hey","p":0.5},{"text":" guys","p":1.0},{"text":"[_TT_50]","p":0.02}]}]}
            """;
        Assert.Equal((Math.Log(0.5) + Math.Log(1.0)) / 2, Transcriber.ScoreWhisperJson(full), 6);
        Assert.Equal(double.NegativeInfinity, Transcriber.ScoreWhisperJson("""{"transcription":[]}"""));
    }

    [Fact]
    public void Merges_turns_of_the_same_speaker_and_language()
    {
        var turns = Transcriber.MergeTurns([
            S(21.4, 27.5, "Rozmówca 1", "Druga osoba,"),
            S(3.5, 8, "Ja", "Raz, dwa,"),
            S(9, 12, "Ja", "trzy."),                    // przerwa 1 s -> ta sama wypowiedź
            S(12.5, 14, "Ja", "Switching to English.", "en"), // inny język -> nowa wypowiedź
            S(27.9, 30, "Rozmówca 1", "dalej."),
            S(36, 37, "Ja", "Koniec."),
        ]);
        Assert.Equal(["Raz, dwa, trzy.", "Switching to English.", "Druga osoba, dalej.", "Koniec."], turns.Select(t => t.Text));
    }

    [Fact]
    public void Gap_over_two_seconds_starts_new_turn() =>
        Assert.Equal(2, Transcriber.MergeTurns([S(0, 1, "Ja", "a"), S(3.5, 4, "Ja", "b")]).Count);

    [Fact]
    public void Echo_of_other_speaker_in_my_microphone_is_removed()
    {
        // z testu na żywo: mikrofon złapał YouTube z głośników
        var others = S(5, 13, "Rozmówca 1", "Milion widzów obejrzało już lalkę i tak w pięciu facetów będziemy rozmawiać o jednej lalce.");
        var echo = S(4.5, 6, "Ja", "i tak w pięciu facetów będziemy rozmawiać.");
        var mine = S(6, 8, "Ja", "A ja myślę, że to przesada.");
        Assert.Equal([others, mine], Transcriber.RemoveEcho([others, echo, mine]));
    }

    [Fact]
    public void Distorted_echo_is_removed_when_microphone_is_quieter_than_teams()
    {
        // nagranie gry: przebicie z głośników przez przetwarzanie mikrofonu Realtek
        var others = S(110, 118, "Rozmówca 1", "Tylko ona powinna zostać gdzieś na trasie, nawet na drodze, skoro ma te many.");
        var echo = S(115.9, 117.5, "Ja", "Kto ma te manek? I zrobi.");
        var mine = S(116, 117.5, "Ja", "Kto ma te manek? I zrobi."); // te same słowa, ale głośno przy mikrofonie = mówię sam
        Assert.Equal([others], Transcriber.RemoveEcho([others, echo], (_, _) => (-29.6, -25.1)));
        Assert.Equal([others, mine], Transcriber.RemoveEcho([others, mine], (_, _) => (-22, -25)));
    }

    [Fact]
    public void Quiet_microphone_with_own_words_is_kept()
    {
        var others = S(100, 104, "Rozmówca 1", "Ona powinna być gdzieś na trasie.");
        var mine = S(102, 106, "Ja", "Zgadzam się z tym całkowicie.");
        Assert.Equal([others, mine], Transcriber.RemoveEcho([others, mine], (_, _) => (-40, -20)));
    }

    [Fact]
    public void Main_language_is_the_most_spoken_one() =>
        Assert.Equal("en", Transcriber.MainLanguage([S(0, 1, "Ja", "Cześć."), S(2, 9, "Rozmówca 1", "A much longer English answer here.", "en")]));

    [Fact]
    public void Render_puts_translation_above_original()
    {
        var text = Transcriber.Render([S(5, 9, "Ja", "Hello guys, how are you today?", "en"), S(10, 11, "Rozmówca 1", "Dobrze.")],
            ["Cześć wszystkim, jak się macie?", null], "en").Replace("\r\n", "\n");
        Assert.Equal(
            "Main language: English · translation: Polish\n\n" +
            "[0:00:05] Ja (PL): Cześć wszystkim, jak się macie?\n" +
            "[0:00:05] Ja (EN): Hello guys, how are you today?\n\n" +
            "[0:00:10] Rozmówca 1 (PL): Dobrze.\n",
            text);
    }

    [Fact]
    public void Whisper_args_use_vad_json_and_language()
    {
        var s = new Settings { WhisperModel = @"C:\m\large-v3.bin", WhisperVadModel = @"C:\m\vad.bin" };
        var args = Transcriber.BuildWhisperArgs(s, "whisper_0.wav", "whisper_0", vad: true, "auto");
        Assert.Equal("-m \"C:\\m\\large-v3.bin\" -l auto -f \"whisper_0.wav\" -sns -ml 80 -sow -ojf -of \"whisper_0\" --vad -vm \"C:\\m\\vad.bin\"", args);
        Assert.DoesNotContain("--vad", Transcriber.BuildWhisperArgs(s, "a.wav", "a", vad: false, "pl"));
    }
}

public class TranslatorTests
{
    [Fact]
    public void Prompt_numbers_lines_and_names_languages()
    {
        var prompt = Translator.BuildPrompt(["Cześć.", "Jak\nleci?"], "pl", "en");
        Assert.Contains("from Polish to English", prompt);
        Assert.EndsWith("1. Cześć.\n2. Jak leci?\n", prompt);
    }

    [Fact]
    public void Parses_numbered_answer_and_marks_missing_lines()
    {
        var result = Translator.ParseNumbered("Here you go:\n1. Hello.\n3) How is it going?\n3. duplicate\n7. out of range", 3);
        Assert.Equal(["Hello.", null, "How is it going?"], result);
    }

    [Fact]
    public void Verify_prompt_pairs_original_with_translation()
    {
        var prompt = Translator.BuildVerifyPrompt(["Milion widzów obejrzało Lalkę.", "Dzień dobry."], ["A million saw the doll.", null], "pl", "en");
        Assert.Contains("1. Milion widzów obejrzało Lalkę.\n   => A million saw the doll.\n2. Dzień dobry.\n   => (missing)\n", prompt);
    }

    [Theory]
    [InlineData("Hello.", "Good day.", "Good day.")]      // poprawka przyjęta
    [InlineData("Hello.", "=> Good day.", "Good day.")]   // strzałka z promptu usunięta
    [InlineData("Hello.", null, "Hello.")]                // brak linii w odpowiedzi
    [InlineData("Hello.", "Cześć.", "Hello.")]            // model przepisał oryginał
    [InlineData(null, "Good day.", null)]                 // brak pierwszego tłumaczenia zostaje brakiem
    public void Verified_translation_falls_back_to_first(string? translation, string? verified, string? expected) =>
        Assert.Equal(expected, Translator.PickVerified("Cześć.", translation, verified));

    const string TvnTranscript = "[0:00:05] Rozmówca 1: Milion widzów obejrzało już lalkę.\n[0:00:15] Rozmówca 1: Goście: Marcin Dorociński.\n" +
        "[0:00:22] Rozmówca 2: Milion widzów do reżysera, do stanarzysty.\n[0:00:28] Rozmówca 4: Cześć, tu Dorota.\n";

    [Fact]
    public void Names_missing_from_transcript_are_found_with_inflection_allowed()
    {
        var notes = "## Podsumowanie\n- Serial \"Lalka\" obejrzał milion widzów.\n- Reżyserem jest Stanisław Bareja.\n" +
                    "- [0:00:15] Powitanie Marcina Dorocińskiego. Twórcy są zadowoleni.\n- Powitanie przez Rozmówczynię 4 (Dorotę).";
        Assert.Equal(["Stanisław", "Bareja"], MeetingNotes.UnsupportedNames(notes, TvnTranscript));
    }

    [Theory]
    [InlineData("pl", "en")]
    [InlineData("en", "pl")]
    public void Other_language(string from, string to) => Assert.Equal(to, Translator.Other(from));
}

public class DiarizerTests
{
    static TranscriptSegment S(double from, double to, string text) => new((long)(from * 1000), (long)(to * 1000), Transcriber.Others, text);

    [Fact]
    public void Segments_get_speaker_with_largest_overlap_numbered_by_first_appearance()
    {
        // sherpa numeruje mówców dowolnie (0, 6, 11...) - w transkrypcji: kolejność pierwszego odezwania się
        SpeakerTurn[] turns = [new(0.3, 4.0, 6), new(4.4, 10.7, 11), new(11.0, 14.0, 6)];
        var result = Diarizer.AssignSpeakers([S(0.5, 3.9, "a."), S(4.0, 9.0, "b."), S(9.5, 13.5, "c.")], turns, "Rozmówca");
        Assert.Equal(["Rozmówca 1", "Rozmówca 2", "Rozmówca 1"], result.Select(r => r.Speaker));
    }

    [Fact]
    public void Segment_without_overlap_takes_nearest_speaker_within_two_seconds()
    {
        SpeakerTurn[] turns = [new(0, 2, 0), new(10, 12, 1)];
        var result = Diarizer.AssignSpeakers([S(3, 3.5, "near"), S(6, 6.5, "far")], turns, "Rozmówca");
        Assert.Equal("Rozmówca 1", result[0].Speaker);
        Assert.Equal(Transcriber.Others, result[1].Speaker); // >2 s od każdego mówcy
    }

    [Fact]
    public void Speaker_does_not_change_inside_a_sentence()
    {
        // komentator gry: "bo ja się" / "gubię w takich momentach." - jedno zdanie, diaryzacja dała dwóch mówców
        SpeakerTurn[] turns = [new(0, 2, 0), new(2, 12, 1), new(14, 16, 2)];
        var result = Diarizer.AssignSpeakers([S(0, 2, "Dobra, bo ja się"), S(2, 12, "gubię w takich momentach."), S(14, 16, "Tak.")], turns, "Rozmówca");
        Assert.Equal(["Rozmówca 1", "Rozmówca 1", "Rozmówca 2"], result.Select(r => r.Speaker)); // numeracja bez dziury po wchłoniętym mówcy
    }

    [Fact]
    public void Long_pause_ends_a_sentence_without_punctuation()
    {
        SpeakerTurn[] turns = [new(0, 2, 0), new(4, 12, 1)];
        var result = Diarizer.AssignSpeakers([S(0, 2, "no to"), S(4, 12, "zaczynamy")], turns, "Rozmówca");
        Assert.Equal(["Rozmówca 1", "Rozmówca 2"], result.Select(r => r.Speaker));
    }

    [Fact]
    public void Single_speaker_keeps_generic_label() =>        Assert.Equal(Transcriber.Others, Diarizer.AssignSpeakers([S(0, 1, "a")], [new(0, 5, 3)], "Rozmówca")[0].Speaker);
}

public class QualityTests
{
    static TranscriptSegment S(double from, double to, string speaker, string text, string language = "pl") =>
        new((long)(from * 1000), (long)(to * 1000), speaker, text, language);

    [Fact]
    public void Voice_check_moves_sentence_only_with_clear_margin()
    {
        var centroids = new Dictionary<int, float[]> { [0] = [1, 0, 0], [1] = [0, 1, 0] };
        Assert.Equal(1, Diarizer.PickSpeaker([0.1f, 1, 0], centroids, current: 0));     // wyraźnie mówca 1
        Assert.Equal(0, Diarizer.PickSpeaker([0.9f, 1, 0], centroids, current: 0));     // różnica < 0,15 - bez zmiany
        Assert.Equal(0, Diarizer.PickSpeaker([0.2f, 0.4f, 1], centroids, current: 0));  // podobieństwo < 0,6 - bez zmiany
    }

    [Fact]
    public void Verify_callback_can_reassign_sentence()
    {
        SpeakerTurn[] turns = [new(0, 10, 0), new(10, 20, 1)];
        var result = Diarizer.AssignSpeakers([S(1, 4, "Rozmówcy", "a."), S(5, 9, "Rozmówcy", "b."), S(11, 15, "Rozmówcy", "c.")], turns, "Rozmówca",
            (segment, current) => segment.Text == "b." ? 1 : current);
        Assert.Equal(["Rozmówca 1", "Rozmówca 2", "Rozmówca 2"], result.Select(r => r.Speaker));
    }

    [Theory]
    [InlineData("Marcin Dorociński, \"Tekla\" & IFC; rm -rf", "Marcin Dorociński, Tekla & IFC rm -rf")]
    [InlineData("  ,, Kowalski ,", "Kowalski")]
    public void Glossary_is_safe_for_command_line(string input, string expected) =>
        Assert.Equal(expected, TranscriptLlm.SanitizePrompt(input));

    [Fact]
    public void Glossary_is_limited_for_whisper_prompt()
    {
        var long_ = string.Join(", ", Enumerable.Range(0, 200).Select(i => $"Term{i}"));
        var prompt = TranscriptLlm.SanitizePrompt(long_);
        Assert.True(prompt.Length <= 600);
        Assert.False(prompt.EndsWith(','));
    }

    [Fact]
    public void Whisper_args_carry_glossary_prompt() =>
        Assert.EndsWith("--prompt \"Tekla, IFC\" --carry-initial-prompt",
            Transcriber.BuildWhisperArgs(new Settings(), "a.wav", "a", vad: false, "pl", "Tekla, IFC"));

    [Theory]
    [InlineData("Milion widzów do reżysera, do stanarzysty. Pytanie, robi wrażenie?", "Milion widzów do reżysera, do scenarzysty. Pytanie, robi wrażenie?", true)]
    [InlineData("Milion widzów do reżysera, do stanarzysty. Pytanie, robi wrażenie?", "Milion widzów do reżysera, do Maciej Sztur. robi wrażenie.", false)] // z testu na żywo
    [InlineData("raz dwa trzy cztery", "raz dwa trzy", false)]               // usunięte słowo
    [InlineData("raz dwa trzy cztery pięć", "jeden dwa trzy cztery sześć", false)] // 2 z 5 słów
    [InlineData("tak", "tak", false)]
    [InlineData("jakieś jedynki z jamentowym golem", "jakieś jedynki z diamentowym golem", true)]   // nagranie gry
    [InlineData("Jamentowy golem ignoruje 95% obrażeń, prawda?", "Kamienny golem ignoruje 95% obrażeń, prawda?", false)] // nie brzmi podobnie
    public void Only_small_word_substitutions_are_accepted(string original, string corrected, bool accepted) =>
        Assert.Equal(accepted, TranscriptLlm.SafeCorrection(original, corrected) == corrected);

    [Fact]
    public void Dissimilar_word_needs_both_other_recognizers_at_the_same_place()
    {
        Assert.Equal("Diamentowy golem ignoruje obrażenia.", TranscriptLlm.SafeCorrection("Kamienny golem ignoruje obrażenia.", "Diamentowy golem ignoruje obrażenia.", "Diamentowy golem ignoruje obrażenia", "Diamentowy Golem ignoruje"));
        Assert.Null(TranscriptLlm.SafeCorrection("Kamienny golem ignoruje obrażenia.", "Diamentowy golem ignoruje obrażenia.", "Diamentowy golem ignoruje obrażenia"));
        // nagranie Heroes: "że" jest w hipotezie, ale w innym miejscu, a "ucierpisz" potwierdza hipoteza B
        Assert.Null(TranscriptLlm.SafeCorrection("że nie ucierpisz, ale zajmiesz miasto.", "że nie że ale zajmiesz miasto.", "że nie ucierpisz, ale zajmiesz miasto.", "Nie ucierpisz. Tak, ale z miasto."));
    }

    [Fact]
    public void Guessed_similar_word_is_kept_and_cosmetic_changes_are_reverted()
    {
        // bez drugiej hipotezy: "jamentowym" -> "diamentowym" zgadnięte (podobne brzmienie); "," -> "." i "sp" -> "SP" cofnięte
        Assert.Equal("ile mieć sp, z diamentowym golem", TranscriptLlm.SafeCorrection("ile mieć sp, z jamentowym golem", "ile mieć SP. z diamentowym golem"));
        Assert.Null(TranscriptLlm.SafeCorrection("Tak w praktyce versus armatrap,", "Tak w praktyce versus Armatrap."));
        Assert.Equal("zabić jednego diamentowego golema", TranscriptLlm.SafeCorrection("zabić jednego jamentowego golema", "zabić jednego Diamentowego golema", "jednego diamentowego golema"));
    }

    [Fact]
    public void Substitutions_count_even_when_the_model_drops_a_word() =>
        // Bielik: "żeby zabić 1 diamentowego Golema, Armagedon." (zgubione "w", cyfra zamiast słowa)
        Assert.Equal("Zastanawiam się, ile trzeba mieć sp, żeby zabić jednego diamentowego golema w Armagedonem.", TranscriptLlm.SafeCorrection(
            "Zastanawiam się, ile trzeba mieć sp, żeby zabić jednego jamentowego golema w Armagedonem.",
            "Zastanawiam się, ile trzeba mieć SP, żeby zabić 1 diamentowego Golema, Armagedonem.", "Zastanawiam się ile trzeba mieć SP, żeby zabić 1 diamantowego Golema, Ramagadon."));

    [Fact]
    public void Word_heard_by_both_recognizers_is_not_changed() =>
        // Bielik na nagraniu gry: poprawił golema, ale przy okazji "zastanawiasz" -> "zastanawiam"
        Assert.Equal("jedynki z diamentowych golemów i się zastanawiasz.", TranscriptLlm.SafeCorrection("jedynki z jamentowym golem i się zastanawiasz.",
            "jedynki z diamentowych golemów i się zastanawiam.", "jakieś jedynki z diamentowych golemów i się zastanawiasz takie rzeczy,"));

    [Theory]
    [InlineData("Marcin Dorociński", true)]
    [InlineData("IFC", false)]
    [InlineData("stanarzysty", false)]
    [InlineData("dzieńdobry.tvn.pl", false)]
    public void Glossary_from_model_keeps_proper_names_only(string term, bool kept) =>
        Assert.Equal(kept, TranscriptLlm.IsProperName(term));

    [Fact]
    public void Single_language_files_use_original_or_translation_and_english_labels()
    {
        TranscriptSegment[] turns = [S(5, 9, "Ja", "Hello guys.", "en"), S(10, 11, "Rozmówca 1", "Dobrze."), S(12, 13, "Rozmówcy", "Tak.")];
        string?[] translations = ["Cześć wszystkim.", "Fine.", null];
        Assert.Equal("[0:00:05] Ja: Cześć wszystkim.\n[0:00:10] Rozmówca 1: Dobrze.\n[0:00:12] Rozmówcy: Tak.\n",
            Transcriber.RenderSingle(turns, translations, "pl").Replace("\r\n", "\n"));
        Assert.Equal("[0:00:05] Me: Hello guys.\n[0:00:10] Speaker 1: Fine.\n[0:00:12] Others: (PL) Tak.\n",
            Transcriber.RenderSingle(turns, translations, "en").Replace("\r\n", "\n"));
    }

    [Fact]
    public void Notes_chunks_keep_whole_lines()
    {
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"[0:00:{i:00}] Ja: zdanie numer {i}"));
        var chunks = MeetingNotes.Chunk(text, 500);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 530));
        Assert.Equal(text + "\n", string.Concat(chunks));
    }
}

public class DebateTests
{
    static TranscriptSegment S(double from, double to, string speaker, string text) => new((long)(from * 1000), (long)(to * 1000), speaker, text, "pl");

    [Theory]
    [InlineData("golem,", "??golem??,")]
    [InlineData("prawda?", "??prawda???")]
    [InlineData("??golem??", "??golem??")]
    public void Mark_keeps_punctuation_outside(string word, string marked) => Assert.Equal(marked, TranscriptLlm.Mark(word));

    [Fact]
    public void Model_can_mark_any_word_as_unsure_without_changing_it() =>
        Assert.Equal("z ??golem?? i tyle", TranscriptLlm.SafeCorrection("z golem i tyle", "z ??golem?? i tyle"));

    [Fact]
    public void Words_the_models_disagree_on_are_marked() =>
        Assert.Equal("jakieś ??diamentowe?? golemy.", Debate.MarkDisagreement("jakieś diamentowe golemy.", "jakieś kamienne golemy."));

    [Fact]
    public void Hypothesis_words_become_sentences_and_qwen_text_keeps_the_sentence_time()
    {
        var sentences = Transcriber.HypothesisSentences([(0, 300, "Nie"), (300, 600, "wiem."), (700, 900, "Tak")], "Rozmówcy", "pl");
        Assert.Equal(["Nie wiem.", "Tak"], sentences.Select(x => x.Text));
        TranscriptSegment[] a = [new(1000, 4000, "Rozmówca 1", "Chili było pikantne."), new(5000, 6000, "Rozmówca 2", "Okej.")];
        Assert.Equal([new HypothesisWord("C", "Rozmówcy", 1000, 4000, "Chili było zbyt pikantne.")], QwenAsr.Words(a, [" Chili było zbyt pikantne. ", ""], "Rozmówcy"));
    }

    [Fact]
    public void LanguageTool_answer_gives_word_and_category()
    {
        const string text = "z jamentowym golem";
        const string json = """
            {"matches":[{"message":"Możliwa literówka","offset":2,"length":10,"rule":{"id":"MORFOLOGIK_RULE_PL_PL","category":{"id":"TYPOS"}}},
            {"message":"Styl","offset":0,"length":1,"rule":{"id":"X","category":{"id":"STYLE"}}}]}
            """;
        var issues = LanguageTool.Parse(json, text);
        Assert.Equal("jamentowym", issues[0].Word);
        Assert.True(issues[0].Spelling && issues[0].Relevant);
        Assert.False(issues[1].Relevant);
    }

    [Fact]
    public void Whisper_tokens_become_timed_words()
    {
        const string json = """
            {"transcription":[{"offsets":{"from":0,"to":900},"text":" Nie ucierpisz,","tokens":[
              {"text":"[_BEG_]","offsets":{"from":0,"to":0},"p":0.9},
              {"text":" Nie","offsets":{"from":0,"to":200},"p":0.5},
              {"text":" u","offsets":{"from":200,"to":267},"p":0.6},
              {"text":"cierpisz","offsets":{"from":267,"to":771},"p":0.9},
              {"text":",","offsets":{"from":808,"to":815},"p":0.7}]}]}
            """;
        // czasy z segmentu (offsety tokenów przy VAD są przesunięte), rozłożone wg pozycji słowa
        Assert.Equal([(0L, 192L, "Nie"), (257L, 900L, "ucierpisz,")], Transcriber.ParseWhisperWords(json));
    }

    [Theory]
    [InlineData("**Diamentowy golem ignoruje.**\n\nNumbered: ...", "Diamentowy golem ignoruje.")]
    [InlineData("P: \"Diamentowy golem.\"", "Diamentowy golem.")]
    [InlineData("\n OK.", "OK.")]
    public void Model_answer_is_reduced_to_the_first_clean_line(string answer, string clean) => Assert.Equal(clean, Debate.CleanAnswer(answer));
    [Fact]
    public void Markdown_transcript_colors_unsure_words_and_escapes_html()
    {
        var md = Transcriber.RenderMarkdown("[0:00:31] Rozmówca 3: jakieś ??diamentowe?? <golemy>\n", "Spotkanie");
        Assert.Equal("# Spotkanie\n\n[0:00:31] Rozmówca 3: jakieś <span style=\"color:#d32f2f\">??diamentowe??</span> &lt;golemy&gt;\n\n", md);
    }

    [Fact]
    public void Work_notes_write_model_file_and_overview()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tc_notes_" + Guid.NewGuid().ToString("N"));
        try
        {
            var notes = new WorkNotes(dir);
            notes.Write("02-whisper-turbo.md", "Whisper turbo", "Drugie rozpoznawanie.", "Przepisz dźwięk.", "gotowe: 20 zdań", "- zdanie");
            var file = File.ReadAllText(Path.Combine(dir, WorkNotes.Dir, "02-whisper-turbo.md"));
            Assert.Contains("## Status\ngotowe: 20 zdań", file);
            Assert.Contains("| [02-whisper-turbo.md](02-whisper-turbo.md) | Whisper turbo | gotowe: 20 zdań |", File.ReadAllText(Path.Combine(dir, WorkNotes.Dir, "00-status.md")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
public class NotesReviewTests
{
    [Theory]
    [InlineData("- brak", 0)]
    [InlineData("**- none**", 0)]
    [InlineData("- **Dorota**: przedstawia się.\n- [ ] Rozmówca 1: zasubskrybować kanał\nKomentarz modelu.", 2)]
    [InlineData("1. Premiera filmu.\n2) Premiera filmu.", 1)]
    [InlineData("- **Decyzja:** Zasubskrybować kanał.", 1)]
    public void Model_answer_becomes_bullets(string answer, int count) => Assert.Equal(count, MeetingNotes.Bullets(answer).Count);

    [Fact]
    public void Labels_like_decision_are_stripped() => Assert.Equal(["Zasubskrybować kanał."], MeetingNotes.Bullets("- **Decyzja:** Zasubskrybować kanał."));

    [Fact]
    public void Conclusion_labels_are_stripped() =>
        Assert.Equal(["Merist powinna przebić breaki.", "Shiva się ulepszy."], MeetingNotes.Bullets("- Wniosek: Merist powinna przebić breaki.\n- Wnioskuję, że Shiva się ulepszy."));

    [Fact]
    public void Verifier_copying_the_source_text_is_ignored() =>
        // notatki TVN: Bielik "sprawdzając" tłumaczenie zwrócił polski oryginał z pogrubieniem
        Assert.Equal("Guests: Marcin Dorociński, Maciej Stuhr.", Translator.PickVerified("Goście: Marcin Dorociński, Maciej Stuhr.", "Guests: Marcin Dorociński, Maciej Stuhr.", "**Goście:** Marcin Dorociński, Maciej Stuhr."));

    [Theory]
    [InlineData("YES", true)]
    [InlineData("**Tak.**", true)]
    [InlineData("NO - the transcript says otherwise", false)]
    [InlineData("I think yes", false)]
    public void Checker_answer_is_yes_only_when_it_starts_with_yes(string answer, bool yes) => Assert.Equal(yes, MeetingNotes.IsYes(answer));

    [Theory]
    [InlineData("Rozmówca 1: Że nie spodziewaliśmy się czegokolwiek innego.", true)]
    [InlineData("Speaker 2: Good morning.", true)]
    [InlineData("Film obejrzało milion widzów.", false)]
    [InlineData("Dorota: przedstawia się prowadząca.", false)]
    public void Quoted_utterances_are_not_note_points(string bullet, bool quote) => Assert.Equal(quote, MeetingNotes.IsQuote(bullet));

    [Theory]
    [InlineData("To moment.", true)]
    [InlineData("Tak, SEPI zrobiła regen na pustynię.", true)]
    [InlineData("Chyba, że dobra, nie, nie ma sensu.", true)]
    [InlineData("Po powrocie do miasta SEPI została zregenerowana przez portal miejski. - Tak, to jest prawda.", true)]
    [InlineData("Rozmówca N i reżyser mieli poczucie, że powstaje coś super.", true)]
    [InlineData("Armatrap jest trudny do pokonania bez odpowiedniej kombinacji.", false)]
    [InlineData("Nie wiadomo, jaki był cel skoku SEPI.", false)]
    [InlineData("The team agreed to ship on Friday.", false)]
    public void Conversation_fragments_are_not_note_points(string bullet, bool fragment) => Assert.Equal(fragment, MeetingNotes.IsFragment(bullet));

    [Fact]
    public void Sentence_copied_from_the_transcript_is_not_a_note_point()
    {
        const string transcript = "[0:00:18] Rozmówca 3: Tak w praktyce to bardzo ciężko jest zrobić kontrę versus armatrap, jak jest mało zegara.";
        Assert.True(MeetingNotes.IsCopied("Tak w praktyce to bardzo ciężko jest zrobić kontrę versus armatrap, jak jest mało zegara.", transcript));
        Assert.False(MeetingNotes.IsCopied("Kontra na armatrap jest trudna przy małej ilości czasu na zegarze.", transcript));
    }

    [Fact]
    public void Lost_name_in_translation_is_detected()
    {
        // TVN: Gemma zamieniła "Maciej Kawulski" na "Maciej Pawłowski"
        var names = MeetingNotes.Names("Rozmowa z gośćmi: Marcin Dorociński, Maciej Stuhr i Maciej Kawulski.");
        Assert.Equal(["Dorociński", "Maciej", "Stuhr", "Kawulski"], names); // po dwukropku = początek zdania
        Assert.Equal(["Kawulski"], MeetingNotes.MissingNames(names, "Guests: Marcin Dorociński, Maciej Stuhr and Maciej Pawłowski."));
        Assert.Empty(MeetingNotes.MissingNames(["Kawulski"], "a talk with Kawulskiego"));
    }

    [Fact]
    public void Notes_are_rendered_by_code_with_fixed_headings()
    {
        // kolejność sekcji z kodu, nie z modelu; tylko sekcje notatki (częściowa nie ma zadań)
        var notes = new MeetingNotes.Notes(new()
        {
            [MeetingNotes.Section.Tasks] = [new("0:02:10", "Widzowie: subskrypcja kanału")], [MeetingNotes.Section.Summary] = [new("", "Milion widzów obejrzało film.")],
            [MeetingNotes.Section.Decisions] = [],
        }, [new("0:00:05", "Zapowiedź gości.", [new("0:00:07", "Gośćmi są aktorzy filmu.")])]);
        Assert.Equal("# Spotkanie\n\n## Podsumowanie\n- Milion widzów obejrzało film.\n\n## Decyzje i ustalenia\n- brak\n\n## Zadania\n- [ ] [0:02:10] Widzowie: subskrypcja kanału\n\n" +
                     "## Przebieg rozmowy\n\n### [0:00:05] Zapowiedź gości.\n- [0:00:07] Gośćmi są aktorzy filmu.\n\n", MeetingNotes.Render(notes, "pl", "Spotkanie"));
        Assert.StartsWith("## Summary\n", MeetingNotes.Render(notes, "en", ""));
        Assert.Equal([MeetingNotes.Section.Summary, MeetingNotes.Section.Assumptions, MeetingNotes.Section.Conclusions, MeetingNotes.Section.Open, MeetingNotes.Section.Decisions],
            MeetingNotes.Partial);
    }
}
public class CrossCheckTests
{
    [Fact]
    public void Whisper_can_translate_speech_to_english() =>
        Assert.EndsWith(" -tr", Transcriber.BuildWhisperArgs(new Settings(), "a.wav", "a", vad: false, "pl", translate: true));

    [Fact]
    public void Translation_is_cross_checked_by_the_other_model()
    {
        // piszący (LFM2, po angielsku) tłumaczy na EN, sprawdzający na PL
        var s = new Settings { CorrectionModel = "lfm2", CheckModel = "memory" };
        Assert.Equal(("lfm2", "memory"), Translator.Models("en", s));
        Assert.Equal(("memory", "lfm2"), Translator.Models("pl", s));
    }

    [Fact]
    public void Translation_copying_a_polish_line_is_suspect()
    {
        string[] originals = ["Okulary? To moje oczy, stary.", "Nie wiem się, Morty. Jakie masz zaimki?"];
        Assert.False(Translator.Suspect("Glasses? These are my eyes, man.", 0, originals));
        Assert.True(Translator.Suspect("Nie wiem się, Morty. Jakie masz zaimki?", 0, originals)); // tekst innej wypowiedzi
        Assert.True(Translator.Suspect(null, 0, originals));
        Assert.False(Translator.Suspect("Morty!", 0, ["Morty!"])); // krótka linia brzmi tak samo
    }

    [Fact]
    public void Translation_prompt_shows_what_whisper_heard_in_english()
    {
        var prompt = Translator.BuildPrompt(["Jamentowy golem ignoruje obrażenia."], "pl", "en", ["The diamond golem ignores damage."]);
        Assert.Contains("1. Jamentowy golem ignoruje obrażenia.\n   Heard: The diamond golem ignores damage.\n", prompt);
        Assert.DoesNotContain("Heard", Translator.BuildPrompt(["Tak."], "pl", "en", [null]));
    }
}
public class KnowledgeBaseTests
{
    [Fact]
    public void User_explanations_in_brackets_become_entries()
    {
        var entries = KnowledgeBase.Parse("[diamentowy golem - jednostka z gry], [konsa - consa - budynek z gry, gdzie zdobywa się anioły]");
        Assert.Equal(new KnowledgeEntry("diamentowy golem", "jednostka z gry"), entries[0] with { Aliases = null });
        Assert.Equal("konsa", entries[1].Term);
        Assert.Equal(["consa"], entries[1].Aliases!);
        Assert.Equal("budynek z gry, gdzie zdobywa się anioły", entries[1].Meaning);
    }

    [Fact]
    public void Relevant_entries_match_inflected_forms()
    {
        var kb = new KnowledgeBase();
        kb.Add(new KnowledgeEntry("diamentowy golem", "jednostka z gry"));
        kb.Add(new KnowledgeEntry("konsa", "budynek z gry", ["consa"]));
        kb.Add(new KnowledgeEntry("Tekla", "program BIM"));
        var relevant = kb.Relevant("Ile SP, żeby zabić jednego diamentowego golema? Dwie consy byłoby fajnie.");
        Assert.Equal(["diamentowy golem", "konsa"], relevant.Select(e => e.Term));
        Assert.True(kb.Knows("Consy"));
        Assert.False(kb.Knows("armatrap"));
    }

    [Fact]
    public void Knowledge_base_and_questions_survive_save_and_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tc_kb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var kb = new KnowledgeBase();
            kb.Add(new KnowledgeEntry("armatrap", "zagranie w grze"));
            kb.Add(new KnowledgeEntry("Armatrap", "pułapka z Armagedonem"));
            kb.Save(Path.Combine(dir, KnowledgeBase.FileName));
            var loaded = KnowledgeBase.Load(Path.Combine(dir, KnowledgeBase.FileName));
            Assert.Equal("pułapka z Armagedonem", Assert.Single(loaded.Entries).Meaning);
            KnowledgeBase.SaveQuestions(dir, [new UnclearTerm("moralówką", "który by nie minął z moralówką", "[0:01:10]", "nieznane słowo")]);
            Assert.Equal("moralówką", Assert.Single(KnowledgeBase.LoadQuestions(dir)).Term);
            KnowledgeBase.SaveQuestions(dir, []);
            Assert.Empty(KnowledgeBase.LoadQuestions(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
public class KnowledgeCorrectionTests
{
    [Fact]
    public void Misheard_word_is_replaced_by_the_users_correction()
    {
        var kb = new KnowledgeBase();
        kb.Add(new KnowledgeEntry("diamentowy", KnowledgeBase.CorrectionMark + "diamentowy", ["Jamentowy"]));
        kb.Add(new KnowledgeEntry("konsa", "budynek z gry", ["consa"])); // objaśnienie, nie poprawka - bez podmiany
        TranscriptSegment[] segments = [new(0, 1, "Rozmówca 4", "??Jamentowy?? golem ignoruje 95% obrażeń, prawda?"), new(1, 2, "Rozmówca 3", "jamentowy golem i consa.")];
        var result = kb.ApplyCorrections(segments, out var replaced);
        Assert.Equal(2, replaced);
        Assert.Equal("Diamentowy golem ignoruje 95% obrażeń, prawda?", result[0].Text);
        Assert.Equal("diamentowy golem i consa.", result[1].Text);
    }
}
public class LiveChunksTests
{
    [Fact]
    public void Chunk_is_cut_in_the_quietest_moment_of_the_window()
    {
        var path = Path.Combine(Path.GetTempPath(), "tc_quiet_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 10 s stereo 16 kHz: szum mowy wszędzie poza ciszą 6,0-6,5 s
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16000, 2)))
            {
                var random = new Random(1);
                for (var i = 0; i < 16000 * 10; i++)
                {
                    var t = i / 16000.0;
                    var v = t is >= 6.0 and < 6.5 ? 0f : (float)(random.NextDouble() - 0.5);
                    writer.WriteSample(v);
                    writer.WriteSample(v);
                }
            }
            Assert.Equal(6.25, LiveChunks.QuietPoint(path, 4, 9), 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Chunk_is_cut_in_the_middle_of_a_two_second_silence()
    {
        var path = Path.Combine(Path.GetTempPath(), "tc_quiet_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 10 s: cisza 2 s (3-5 s, szum tła) i krótsza, cichsza pauza 7,0-7,5 s bliżej celu - wygrywa cisza 2 s
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16000, 2)))
            {
                var random = new Random(1);
                for (var i = 0; i < 16000 * 10; i++)
                {
                    var t = i / 16000.0;
                    var noise = (float)(random.NextDouble() - 0.5);
                    var v = t is >= 3.0 and < 5.0 ? noise * 0.002f : t is >= 7.0 and < 7.5 ? 0f : noise;
                    writer.WriteSample(v);
                    writer.WriteSample(v);
                }
            }
            Assert.Equal(4.0, LiveChunks.QuietPoint(path, 0, 10, 7), 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Final_zero_is_the_earliest_of_video_teams_and_microphone()
    {
        var info = new SessionInfo { MicStart = 1005.2, TeamsAudio = [new AudioTrack { Start = 1004.0 }, new AudioTrack { Start = null }], Video = [new VideoSegment { Start = 1003.5 }] };
        Assert.Equal(1003.5, LiveChunks.FinalZero(info));
        Assert.Null(LiveChunks.FinalZero(new SessionInfo()));
    }

    [Fact]
    public void Chunk_times_move_onto_the_final_recording()
    {
        // kawałek 2: od 300 s zrzutu, którego zero było 0,5 s po zerze końcowego nagrania (wideo zaczęło się wcześniej)
        var chunk = new ChunkInfo(2, 300, 600, 1004.0);
        Assert.Equal(300_500, LiveChunks.OffsetMs(chunk, 1003.5));
        var s = LiveChunks.Shift(new TranscriptSegment(1_000, 2_000, "Ja", "Tak."), 300_500);
        Assert.Equal((301_500L, 302_500L), (s.FromMs, s.ToMs));
    }
}
public class ComponentsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tc_comp_" + Guid.NewGuid().ToString("N"));

    public ComponentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return path;
    }

    [Fact]
    public void Extract_strips_the_top_folder_filters_files_and_keeps_existing_ones()
    {
        var target = Path.Combine(_dir, "whisper");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "ggml-large-v3.bin"), "model"); // model pobrany wcześniej zostaje
        var zip = Zip(("Release/whisper-cli.exe", "cli"), ("Release/ggml-cuda.dll", "dll"), ("Release/test-parakeet.exe", "test"), ("README.md", "x"));
        Components.Extract(zip, target, "Release/", @"^(whisper-cli\.exe|ggml.*\.dll)$");
        Assert.Equal(["ggml-cuda.dll", "ggml-large-v3.bin", "whisper-cli.exe"], Directory.GetFiles(target).Select(Path.GetFileName).Order().ToArray());
        Assert.False(Directory.Exists(target + ".new"));
    }

    [Fact]
    public void Extract_keeps_subfolders_without_filter()
    {
        var target = Path.Combine(_dir, "jre");
        Components.Extract(Zip(("jdk-21-jre/bin/java.exe", "java"), ("jdk-21-jre/lib/modules", "m")), target, "jdk-21-jre/", null);
        Assert.True(File.Exists(Path.Combine(target, "bin", "java.exe")));
        Assert.True(File.Exists(Path.Combine(target, "lib", "modules")));
    }

    [Fact]
    public void Extract_rejects_paths_leaving_the_target_folder()
    {
        var target = Path.Combine(_dir, "safe");
        Assert.Throws<InvalidDataException>(() => Components.Extract(Zip(("../evil.txt", "x")), target, null, null));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public void Every_download_is_pinned_with_a_checksum()
    {
        foreach (var c in Components.All(new Settings()).Where(c => c.Kind is ComponentKind.File or ComponentKind.Zip))
        {
            Assert.StartsWith("https://", c.Url);
            Assert.Matches("^[0-9a-f]{64}$", c.Sha256);
        }
    }
}
public class SttNotesTests
{
    [Fact]
    public void Model_bullets_keep_times_and_drop_headings()
    {
        var answer = "## Fragment\n\nHere's a breakdown:\n\n* **[0:14:55]**: Pączek introduces Ledan.\n* **0:45:51:** Talk about Pupo.\n- Plain point.";
        Assert.Equal(["[0:14:55] Pączek introduces Ledan.", "[0:45:51] Talk about Pupo.", "Plain point."], SttNotes.Bullets(answer));
    }

    [Fact]
    public void Summary_keeps_only_our_sections()
    {
        var answer = "Jasne!\n## Notatka ze spotkania\n\n### ## Podsumowanie\n* A.\n\n### ## Decyzje\n- - brak\n## Inne\n* C.";
        Assert.Equal("## Podsumowanie\n* A.\n\n## Decyzje\n- brak", SttNotes.Sections(answer, "pl"));
    }

    [Fact]
    public void Each_sentence_gets_versions_b_and_c_from_its_channel_and_time()
    {
        TranscriptSegment[] segments = [new(1000, 4000, "Rozmówca 1", "Chili było zbyt <span style=\"c\">??pikantne??</span>."), new(5000, 7000, Transcriber.Me, "Okej.")];
        HypothesisWord[] words = [new("B", Transcriber.Others, 1000, 1500, "Czyli"), new("B", Transcriber.Others, 1500, 2500, "było"),
            new("C", Transcriber.Others, 1200, 1800, "Chili"), new("B", Transcriber.Me, 1000, 2000, "inny kanał"), new("C", Transcriber.Me, 5200, 5600, "Okej")];
        var lines = SttNotes.Lines(segments, words);
        Assert.Equal(new SttNotes.Line(1000, "Rozmówca 1", "Chili było zbyt pikantne.", "Czyli było", "Chili"), lines[0]);
        Assert.Equal(new SttNotes.Line(5000, Transcriber.Me, "Okej.", "", "Okej"), lines[1]);
        Assert.Contains("C: Chili", SttNotes.PartPrompt("pl", lines, ""));
        Assert.Equal(2, SttNotes.Parts([.. lines, new(700_000, "Ja", "x", "", "")]).Count); // części po 10 min
    }
}

public class CopilotNotesTests
{
    [Fact]
    public void Prompt_has_the_style_and_a_clean_transcript()
    {
        var style = "<!--\nKomentarz w <!-- --> pomijany.\n-->\n## Podsumowanie\n- 5-10 punktów.";
        var transcript = "[0:00:05] Rozmówca 1: gramy na <span style=\"color:#d32f2f\">??Nebuli??</span>.";
        var text = CopilotNotes.Prompt(style, transcript, "transcript.pl.md", attached: false);
        Assert.DoesNotContain("pomijany", text);
        Assert.Contains("## Podsumowanie", text);
        Assert.Contains("gramy na Nebuli (?).", text);
        var attached = CopilotNotes.Prompt(style, transcript, "transcript.pl.md", attached: true);
        Assert.Contains("transcript.pl.md", attached);
        Assert.DoesNotContain("Rozmówca 1", attached);
        Assert.Equal(Path.Combine("C:\\r", "notes-copilot.md"), CopilotNotes.NotesPath(Path.Combine("C:\\r", "transcript.pl.md")));
    }
}
public class NotesRulesTests
{
    [Fact]
    public void Rules_file_drops_comments_and_reaches_the_note_prompts()
    {
        var path = Path.Combine(Path.GetTempPath(), "tc_rules_" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            // "<!-- -->" w środku tekstu komentarza nie zamyka go (wcześniej reszta komentarza trafiała do modeli)
            File.WriteAllText(path, "<!--\ntylko dla ludzi.\nKomentarze w <!-- --> modele pomijają.\n-->\n## Wnioski\n- Wniosek musi wynikać z rozmowy.");
            var rules = MeetingNotes.LoadRules(path);
            Assert.DoesNotContain("tylko dla ludzi", rules);
            Assert.DoesNotContain("pomijają", rules);
            Assert.StartsWith("## Wnioski", rules);
            // model sprawdzający dostaje tylko transkrypcję i pytanie - z zasadami Qwen pisał notatkę zamiast TAK/NIE
            var check = MeetingNotes.CheckBatchPrompt(MeetingNotes.Section.Conclusions, "[0:00:01] Ja: tekst", ["punkt"], "pl");
            Assert.StartsWith("## TRANSKRYPCJA", check); // transkrypcja dalej na początku (wspólny początek zapytań)
            Assert.Contains("Wniosek musi wynikać z rozmowy.", MeetingNotes.FixBatchPrompt("pl", "x", ["punkt"], "", rules));
            Assert.Equal("", MeetingNotes.LoadRules(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Shipped_rules_file_describes_every_section()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "notes-rules.md"));
        foreach (var heading in new[] { "Podsumowanie", "Założenia", "Wnioski", "Decyzje", "Zadania", "Otwarte kwestie" }) Assert.Contains(heading, text);
    }

    // baza rośnie z każdym spotkaniem: do promptu trafiają tylko wpisy z fragmentu, a wybór musi być szybki także przy tysiącach wpisów
    [Fact]
    public void Large_knowledge_base_gives_only_relevant_entries_quickly()
    {
        var kb = new KnowledgeBase { Entries = Enumerable.Range(0, 5000).Select(i => new KnowledgeEntry($"termin{i:0000}x", "znaczenie")).ToList() };
        kb.Entries.Add(new KnowledgeEntry("diamentowy golem", "jednostka z gry"));
        var text = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"słowo{i}")) + " a potem diamentowego golema";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var relevant = kb.Relevant(text);
        Assert.Equal(["diamentowy golem"], relevant.Select(e => e.Term).ToArray());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Relevant took {clock.Elapsed}");
    }}
public class DeviceLogTests
{
    [Theory]
    [InlineData("whisper_backend_init_gpu: using CUDA0 backend", "GPU (CUDA0)")]
    [InlineData("found GPU device 0: CUDA0 (type: 1, cnt: 0)", "GPU (CUDA0)")]
    [InlineData("whisper_backend_init: using CPU backend", "CPU")]
    public void Engine_output_tells_gpu_or_cpu(string output, string device) => Assert.Equal(device, Transcriber.Device(output));

    [Theory]
    [InlineData(5_300_000_000, 5_300_000_000, "100% GPU")]
    [InlineData(5_000_000_000, 0, "100% CPU")]
    [InlineData(4_000_000_000, 3_000_000_000, "25% CPU / 75% GPU")]
    public void Ollama_placement_reads_like_ollama_ps(long size, long vram, string text) => Assert.Equal(text, Ollama.Placement(size, vram));
}
public class ContextFitTests
{
    // model_info z /api/show (Ollama 0.40) dla modeli używanych przez aplikację
    const string Llama = """{"general.architecture":"llama","llama.attention.head_count":32,"llama.attention.head_count_kv":8,"llama.block_count":32,"llama.context_length":131072,"llama.embedding_length":4096}""";
    const string Qwen = """{"general.architecture":"qwen35","qwen35.attention.head_count":16,"qwen35.attention.head_count_kv":4,"qwen35.attention.key_length":256,"qwen35.attention.value_length":256,"qwen35.block_count":32,"qwen35.context_length":262144,"qwen35.embedding_length":2560,"qwen35.full_attention_interval":4}""";
    const long Mb = 1024 * 1024;

    static (long, int) Kv(string json) => Ollama.KvPerToken(System.Text.Json.JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Kv_cache_per_token_matches_the_architecture()
    {
        Assert.Equal((131072L, 131072), Kv(Llama)); // 32 warstwy x 8 głowic KV x (128 + 128) x 2 B
        Assert.Equal((32768L, 262144), Kv(Qwen)); // uwaga tylko w 8 z 32 warstw (hybryda)
    }

    [Theory]
    [InlineData(8188 - 400, 12288, 98304)] // RTX 4060 8 GB: Llama 12288 (16384 = 13% na CPU), Qwen ~98k (limit = VRAM)
    [InlineData(6144 - 400, 4096, 32768)] // docelowa karta 6 GB: Llama tylko minimum
    public void Context_is_the_largest_that_fits_the_vram_budget(long budgetMb, int llama, int qwen)
    {
        Assert.Equal(llama, Ollama.FitContext(budgetMb * Mb, 4_920_753_328, Kv(Llama).Item1, Kv(Llama).Item2));
        Assert.Equal(qwen, Ollama.FitContext(budgetMb * Mb, 3_324_173_934, Kv(Qwen).Item1, Kv(Qwen).Item2));
    }

    [Fact]
    public void Note_chunks_grow_with_the_context_but_never_below_the_minimum()
    {
        Assert.Equal(4000, MeetingNotes.ChunkCharsFor(4096, 5000));
        Assert.Equal((int)((16384 - 2000) * 2.5) - 5000, MeetingNotes.ChunkCharsFor(16384, 5000));
    }

    [Fact]
    public void Notes_parts_are_five_minutes_and_a_short_tail_joins_the_last_part()
    {
        var lines = Enumerable.Range(0, 23).Select(i => $"[{TimeSpan.FromSeconds(900 + i * 30):h\\:mm\\:ss}] Rozmówca 1: zdanie {i}.");
        var parts = MeetingNotes.Parts(string.Join("\n\n", lines));
        // 15:00-26:00: okna 15:00, 20:00, 25:00-26:00 (ostatnie ponad minutę = osobna część)
        Assert.Equal(["0:15:00", "0:20:00", "0:25:00"], parts.Select(p => p.Time));
        Assert.Equal(2, MeetingNotes.Parts(string.Join("\n", lines.Take(21))).Count); // 25:00 samo = dołączone do poprzedniej
        Assert.Single(MeetingNotes.Parts("bez czasów"));
    }
}
public class LiveNotesTests
{
    [Fact]
    public void Notes_of_all_parts_so_far_are_merged_in_order()
    {
        MeetingNotes.Notes Piece(string time, string point) => new(
            new() { [MeetingNotes.Section.Conclusions] = [new MeetingNotes.Item(time, point)] }, [new MeetingNotes.Part(time, "temat", [new MeetingNotes.Item(time, point)])]);
        var merged = LiveNotesService.Merge([Piece("0:01:00", "a"), new MeetingNotes.Notes([], []), Piece("0:07:00", "b")]);
        Assert.Equal(["a", "b"], merged.Sections[MeetingNotes.Section.Conclusions].Select(x => x.Text));
        Assert.Equal(["0:01:00", "0:07:00"], merged.Parts.Select(p => p.Time));
    }
}

public class LanguageSampleTests
{
    [Fact]
    public void Language_sample_takes_four_one_minute_windows_where_the_speech_is()
    {
        var file = Path.Combine(Path.GetTempPath(), $"speech-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(file, new WaveFormat(16000, 16, 1)))
            {
                var rnd = new Random(1);
                // 15 min ciszy z trzema 30-sekundowymi fragmentami mowy (szum) w 100., 400. i 700. sekundzie
                for (var second = 0; second < 900; second++)
                    for (var i = 0; i < 16000; i++)
                        writer.WriteSample(second is >= 100 and < 130 or >= 400 and < 430 or >= 700 and < 730 ? (float)(rnd.NextDouble() - 0.5) * 0.4f : 0f);
            }
            var starts = Transcriber.SpeechWindows(file);
            Assert.Equal(4, starts.Count);
            Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= 60));
            foreach (var speech in new[] { 100, 400, 700 })
                Assert.Contains(starts, s => s <= speech && s + 60 >= speech + 30);
        }
        finally { File.Delete(file); }
    }
}

public class VoiceProfileTests
{
    static readonly float[] Me = [1, 0, 0];

    [Fact]
    public void Single_speaker_with_my_voice_is_me()
    {
        Assert.Equal("Rozmówcy", VoiceProfile.ChooseMe(new Dictionary<string, float[]> { ["Rozmówcy"] = [0.9f, 0.1f, 0] }, Me));
    }

    [Fact]
    public void Speaker_that_is_clearly_closer_than_the_others_is_me()
    {
        var speakers = new Dictionary<string, float[]> { ["Rozmówca 1"] = [0.3f, 1, 0], ["Rozmówca 2"] = [1, 0.2f, 0] };
        Assert.Equal("Rozmówca 2", VoiceProfile.ChooseMe(speakers, Me));
    }

    [Fact]
    public void Nobody_is_me_when_similarity_is_low_or_ambiguous()
    {
        Assert.Null(VoiceProfile.ChooseMe(new Dictionary<string, float[]> { ["Rozmówcy"] = [0.3f, 1, 0] }, Me));
        Assert.Null(VoiceProfile.ChooseMe(new Dictionary<string, float[]> { ["Rozmówca 1"] = [1, 0.1f, 0], ["Rozmówca 2"] = [1, 0.15f, 0] }, Me));
    }
}
