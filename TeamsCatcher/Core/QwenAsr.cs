using System.IO;
using System.Text.Json;

namespace TeamsCatcher.Core;

// Wersja C rozpoznania mowy: Qwen3-ASR-1.7B (Alibaba, 30 języków z polskim) zamiast Parakeeta - Parakeet słabszy w polskim.
// Przez asr.py w środowisku Pythona pyannote (ten sam torch CUDA). Aligner czasów Qwen3 nie zna polskiego, więc wejściem są zdania A:
// każde zdanie osobno, tekst C ma czas swojego zdania.
public static class QwenAsr
{
    public static string Script => Path.Combine(AppContext.BaseDirectory, "asr.py");

    public static bool Available(Settings s) => File.Exists(s.PyannotePython) && File.Exists(Script) && s.QwenAsrModel.Length > 0;

    // wav: kanał 16 kHz mono; zdania tego kanału z Whispera. Zwraca po jednym "słowie" C na zdanie (czas zdania).
    public static async Task<List<HypothesisWord>> TranscribeAsync(string wav, IReadOnlyList<TranscriptSegment> segments, string speaker, string language,
        string context, Settings s, string folder, CancellationToken ct)
    {
        if (segments.Count == 0) return [];
        var input = Path.Combine(folder, "qwen_asr_segments.json");
        var output = Path.Combine(folder, "qwen_asr.json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(segments.Select(x => new[] { x.FromMs / 1000.0, x.ToMs / 1000.0 })), ct);
            await Transcriber.RunAsync(s.PyannotePython, $"\"{Script}\" \"{s.QwenAsrModel}\" \"{wav}\" \"{input}\" \"{output}\" {language} \"{context.Replace("\"", "")}\"", folder, ct);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, ct));
            Transcriber.Devices["Qwen3-ASR"] = json.RootElement.GetProperty("device").GetString() == "cuda" ? "GPU (CUDA0)" : "CPU";
            var texts = json.RootElement.GetProperty("texts").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            return Words(segments, texts, speaker);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    public static List<HypothesisWord> Words(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<string> texts, string speaker) =>
        segments.Zip(texts).Where(x => x.Second.Trim().Length > 0).Select(x => new HypothesisWord("C", speaker, x.First.FromMs, x.First.ToMs, x.Second.Trim())).ToList();
}
