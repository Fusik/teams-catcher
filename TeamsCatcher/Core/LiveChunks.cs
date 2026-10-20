using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace TeamsCatcher.Core;

// Kawałek rozmowy przetwarzany w trakcie nagrania: From/To w sekundach od Zero (bezwzględny czas, sekundy od 1970,
// najwcześniejszy dźwięk zrzutu) - po nagraniu przeliczany na oś końcowego transcript.wav.
public sealed record ChunkInfo(int Index, double From, double To, double Zero);

// Podwójny zapis dla rozmów dłuższych niż kawałek (~5 min): w trakcie rozmowy każdy kawałek przechodzi rozpoznanie mowy,
// dyskusję modeli i notatkę częściową (dymek w trayu); po rozmowie wynik jest składany z kawałków, rozmówcy rozpoznawani
// raz na całym nagraniu (spójne numery), a notatka z całości dostaje notatki częściowe jako kontekst.
public static class LiveChunks
{
    public const string Dir = "live", InfoFile = "chunk.json", CorrectedFile = "corrected.json", SnapshotWav = "snapshot.wav";
    // kawałek 5 min ± 1 min: cięcie w ciszy ≥ 2 s najbliższej planowanego końca (bez przecinania zdania)
    public const double QuietWindowSeconds = 60, SilenceSeconds = 2;

    public static string ChunkFolder(string folder, int index) => Path.Combine(folder, Dir, $"{index:00}");

    public static List<(ChunkInfo Info, string Folder)> List(string folder)
    {
        var dir = Path.Combine(folder, Dir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Select(d => (Info: Load(d), Folder: d)).Where(x => x.Info != null)
            .Select(x => (x.Info!, x.Folder)).OrderBy(x => x.Item1.Index).ToList();
    }

    static ChunkInfo? Load(string chunkFolder)
    {
        var path = Path.Combine(chunkFolder, InfoFile);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ChunkInfo>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }

    // Kawałek [from, cięcie] ze zrzutu (transcript.wav do tej chwili); cięcie w ciszy koło target (toEnd = do końca pliku).
    public static async Task<ChunkInfo?> CutAsync(string sourceWav, double zero, int index, double from, double target, bool toEnd,
        string chunkFolder, string ffmpeg, CancellationToken ct)
    {
        double length;
        using (var reader = new WaveFileReader(sourceWav)) length = reader.TotalTime.TotalSeconds;
        var to = toEnd ? length : QuietPoint(sourceWav, Math.Max(from + 1, target - QuietWindowSeconds), Math.Min(length, target + QuietWindowSeconds), target);
        if (to - from < 1) return null;
        Directory.CreateDirectory(chunkFolder);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        await Transcriber.RunAsync(ffmpeg, string.Format(culture, "-hide_banner -loglevel error -y -ss {0:0.###} -to {1:0.###} -i \"{2}\" -c:a pcm_s16le \"{3}\"",
            from, to, sourceWav, Path.Combine(chunkFolder, RecordingSession.TranscriptWav)), chunkFolder, ct);
        var info = new ChunkInfo(index, from, to, zero);
        await File.WriteAllTextAsync(Path.Combine(chunkFolder, InfoFile), JsonSerializer.Serialize(info), ct);
        return info;
    }

    // Środek ciszy ≥ SilenceSeconds najbliższej target w [from, to] sekund (oba kanały razem);
    // bez takiej ciszy - środek najcichszego okna 0,5 s.
    public static double QuietPoint(string wav, double from, double to, double? target = null)
    {
        const double frame = 0.1;
        using var reader = new WaveFileReader(wav);
        var format = reader.WaveFormat;
        reader.Position = Math.Min(reader.Length, (long)(from * format.SampleRate) * format.BlockAlign);
        var samples = reader.ToSampleProvider();
        var buffer = new float[(int)(format.SampleRate * frame) * format.Channels];
        var rms = new List<double>();
        for (var t = from; t + frame <= to + 1e-9; t += frame)
        {
            var read = samples.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            var energy = 0.0;
            for (var i = 0; i < read; i++) energy += buffer[i] * buffer[i];
            rms.Add(Math.Sqrt(energy / read));
        }
        if (rms.Count == 0) return to;
        // cisza = blisko szumu tła tego okna, ale nie głośniej niż -34 dBFS (całe okno mowy nie udaje ciszy)
        var floor = rms.Order().ElementAt(rms.Count / 10);
        var silent = Math.Min(0.02, Math.Max(0.002, floor * 3));
        var need = (int)Math.Round(SilenceSeconds / frame);
        double? best = null;
        var aim = target ?? (from + to) / 2;
        for (int i = 0, start = 0; i <= rms.Count; i++)
        {
            if (i < rms.Count && rms[i] < silent) continue;
            if (i - start >= need)
            {
                var middle = from + (start + i) / 2.0 * frame;
                if (best == null || Math.Abs(middle - aim) < Math.Abs(best.Value - aim)) best = middle;
            }
            start = i + 1;
        }
        if (best != null) return best.Value;
        var half = (int)Math.Round(0.5 / frame);
        double quietest = double.MaxValue, at = to;
        for (var i = 0; i + half <= rms.Count; i += half)
        {
            var sum = rms.Skip(i).Take(half).Sum(r => r * r);
            if (sum < quietest) (quietest, at) = (sum, from + (i + half / 2.0) * frame);
        }
        return at;
    }

    // Czas 0 końcowego transcript.wav (jak w finalizacji): najwcześniejszy z pierwszej klatki wideo, dźwięku Teams i mikrofonu.
    public static double? FinalZero(SessionInfo info)
    {
        double?[] starts =
        [
            info.Video.Where(v => v.Start != null).Select(v => v.Start).DefaultIfEmpty(null).Min(),
            info.TeamsAudio.Concat(info.SharedAudio).Where(t => t.Start != null).Select(t => t.Start).DefaultIfEmpty(null).Min(),
            info.MicStart,
        ];
        return starts.Where(s => s != null).DefaultIfEmpty(null).Min();
    }

    // Przesunięcie kawałka na oś końcowego nagrania (ms).
    public static long OffsetMs(ChunkInfo chunk, double finalZero) => (long)Math.Round((chunk.Zero + chunk.From - finalZero) * 1000);

    public static TranscriptSegment Shift(TranscriptSegment s, long offsetMs) => s with { FromMs = s.FromMs + offsetMs, ToMs = s.ToMs + offsetMs };
}
