using System.IO;
using System.Text;
using NAudio.Wave;

namespace TeamsCatcher.Core;

// Transkrypcja na żywo w trakcie rozmowy: co ~30 s nowy fragment dźwięku (do ciszy przy końcu zrzutu) przez Whisper large-v3-turbo,
// osobno kanał "Ja" i "Rozmówcy"; dopisywana do live\live-transcript.md (widok "Live" w panelu RESULT). Po rozmowie i tak liczy się
// pełna transkrypcja large-v3 - ta jest tylko podglądem.
public sealed class LiveTranscript(Settings settings, string ffmpeg, Action<string> log)
{
    public const string FileName = "live-transcript.md";
    const int EverySeconds = 30;
    // fragment kończy się w ciszy w ostatnich sekundach zrzutu, nie w środku słowa
    const double SearchSeconds = 8, MinSeconds = 5;

    double _from;
    Task? _run;
    bool _failed;

    public Task Completion => _run ?? Task.CompletedTask;

    public static string PathIn(string folder) => Path.Combine(folder, LiveChunks.Dir, FileName);

    public void Schedule(RecordingSession s, CancellationToken ct)
    {
        if (_failed || !File.Exists(settings.WhisperAltModel) || _run is { IsCompleted: false } || s.Elapsed.TotalSeconds < _from + EverySeconds + 3) return;
        _run = RunAsync(s, ct);
    }

    async Task RunAsync(RecordingSession s, CancellationToken ct)
    {
        var dir = Path.Combine(s.Folder, LiveChunks.Dir);
        string snapshot = Path.Combine(dir, "live-text-snapshot.wav"), piece = Path.Combine(dir, "live-text-piece.wav"), output = Path.Combine(dir, "live-text");
        try
        {
            Directory.CreateDirectory(dir);
            if (await s.SnapshotTranscriptAsync(snapshot) == null) return;
            double length;
            int channels;
            using (var reader = new WaveFileReader(snapshot)) (length, channels) = (reader.TotalTime.TotalSeconds, reader.WaveFormat.Channels);
            var to = LiveChunks.QuietPoint(snapshot, Math.Max(_from, length - SearchSeconds), length, length - 2);
            if (to - _from < MinSeconds) return;
            (string Speaker, string? Pan)[] sides = channels == 2 ? [(Transcriber.Me, "c0"), (Transcriber.Others, "c1")] : [(Transcriber.Others, null)];
            var language = settings.TranscriptLanguage == "auto" ? "auto" : settings.TranscriptLanguage;
            var lines = new List<(long Ms, string Text)>();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var (speaker, pan) in sides)
            {
                await Transcriber.RunAsync(ffmpeg, string.Format(culture, "-hide_banner -loglevel error -y -ss {0:0.###} -to {1:0.###} -i \"{2}\" {3}-ac 1 -ar 16000 \"{4}\"",
                    _from, to, snapshot, pan != null ? $"-af \"pan=mono|c0={pan}\" " : "", piece), dir, ct);
                var (segments, _, _) = await Transcriber.WhisperAsync(piece, output, speaker, settings, File.Exists(settings.WhisperVadModel), language, "", dir, ct,
                    settings.WhisperAltModel);
                lines.AddRange(segments.Select(x => (x.FromMs + (long)(_from * 1000), $"{speaker}: {x.Text.Trim()}")));
            }
            var text = string.Concat(lines.OrderBy(l => l.Ms).Select(l => $"[{TimeSpan.FromMilliseconds(l.Ms):h\\:mm\\:ss}] {l.Text}\n\n"));
            await File.AppendAllTextAsync(PathIn(s.Folder), text, Encoding.UTF8, ct);
            _from = to;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            // jeden błąd = podgląd wyłączony do końca rozmowy (np. brak pamięci karty przy liczeniu poprzedniego nagrania)
            _failed = true;
            log($"Live transcript stopped: {e.Message}");
        }
        finally
        {
            foreach (var file in new[] { snapshot, piece, output + ".json" }) File.Delete(file);
        }
    }
}
