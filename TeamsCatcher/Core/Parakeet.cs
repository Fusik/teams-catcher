using System.IO;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Trzecia hipoteza: NVIDIA Parakeet TDT v3 (parakeet-cli z whisper.cpp). Inna architektura niż Whisper = inne błędy
// (nagranie Heroes: Whisper "jamentowy", Parakeet "diamantowy"). Słabszy w polskim, więc tylko głos w dyskusji, nie podstawa.
public static class Parakeet
{
    // Kawałki po 5 min: parakeet-cli liczy uwagę na całym pliku (długie nagranie = brak pamięci); inne cięcia niż VAD Whispera.
    const int ChunkSeconds = 300;

    public static string Cli(Settings s) => Path.Combine(Path.GetDirectoryName(s.WhisperCli) ?? "", "parakeet-cli.exe");

    public static bool Available(Settings s) => File.Exists(Cli(s)) && File.Exists(s.ParakeetModel);

    // wav: 16 kHz mono. Zwraca słowa z czasami.
    public static async Task<List<(long FromMs, long ToMs, string Text)>> TranscribeAsync(string wav, string ffmpeg, Settings s, string folder, CancellationToken ct)
    {
        double seconds;
        using (var reader = new NAudio.Wave.WaveFileReader(wav)) seconds = reader.TotalTime.TotalSeconds;
        var words = new List<(long FromMs, long ToMs, string Text)>();
        for (var start = 0; start < seconds; start += ChunkSeconds)
        {
            var chunk = Path.Combine(folder, $"parakeet_{start}.wav");
            try
            {
                await Transcriber.RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -ss {start} -t {ChunkSeconds} -i \"{wav}\" \"{chunk}\"", folder, ct);
                var output = await Transcriber.RunAsync(Cli(s), $"-m \"{s.ParakeetModel}\" -f \"{chunk}\" -ps", folder, ct);
                words.AddRange(ParseWords(output).Select(w => (w.FromMs + start * 1000L, w.ToMs + start * 1000L, w.Text)));
            }
            finally
            {
                File.Delete(chunk);
            }
        }
        return words;
    }

    // Linie tokenów: ... t0= 656 t1= 680 word_start=true "▁Nie" (czasy w setnych sekundy, "▁" = początek słowa).
    public static List<(long FromMs, long ToMs, string Text)> ParseWords(string output)
    {
        var words = new List<(long FromMs, long ToMs, string Text)>();
        foreach (Match m in Regex.Matches(output, @"t0=\s*(\d+)\s+t1=\s*(\d+)\s+word_start=(true|false)\s+""(.*)""\s*$", RegexOptions.Multiline))
        {
            long from = long.Parse(m.Groups[1].Value) * 10, to = long.Parse(m.Groups[2].Value) * 10;
            var piece = m.Groups[4].Value;
            if (m.Groups[3].Value == "true" || words.Count == 0) words.Add((from, to, piece.TrimStart('▁')));
            else words[^1] = (words[^1].FromMs, to, words[^1].Text + piece);
        }
        return words.Where(w => w.Text.Length > 0).ToList();
    }

    public static List<TranscriptSegment> Sentences(IReadOnlyList<(long FromMs, long ToMs, string Text)> words, string speaker, string language)
    {
        var result = new List<TranscriptSegment>();
        var start = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (i < words.Count - 1 && words[i].Text is not [.., '.' or '?' or '!']) continue;
            result.Add(new TranscriptSegment(words[start].FromMs, words[i].ToMs, speaker, string.Join(' ', words.Skip(start).Take(i - start + 1).Select(w => w.Text)), language));
            start = i + 1;
        }
        return result;
    }
}
