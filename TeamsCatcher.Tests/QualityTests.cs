using System.IO;
using System.Text.RegularExpressions;
using TeamsCatcher.Core;
using Xunit.Abstractions;

namespace TeamsCatcher.Tests;

// WER (word error rate) = (podmienione + wstawione + zgubione słowa) / liczba słów w treści wzorcowej; 0,05 = 5% błędów.
// Bez wielkości liter, ogonków i interpunkcji - liczą się słowa, nie zapis.
public static class Wer
{
    public static List<string> Words(string text) => Regex.Matches(MeetingSearch.Normalize(text), @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();

    public static double Rate(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        if (r.Count == 0) return h.Count == 0 ? 0 : 1;
        // odległość edycyjna na słowach, jeden wiersz naraz
        var row = Enumerable.Range(0, h.Count + 1).ToArray();
        for (var i = 1; i <= r.Count; i++)
        {
            var prev = row[0];
            row[0] = i;
            for (var j = 1; j <= h.Count; j++)
            {
                var diagonal = prev;
                prev = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (r[i - 1] == h[j - 1] ? 0 : 1));
            }
        }
        return (double)row[h.Count] / r.Count;
    }
}

public class WerTests
{
    [Theory]
    [InlineData("Strop wylewamy w piątek.", "strop wylewamy w piatek", 0)]
    [InlineData("a b c d", "a x c d", 0.25)] // podmiana
    [InlineData("a b c d", "a c d", 0.25)] // zgubione
    [InlineData("a b c d", "a b b c d", 0.25)] // wstawione
    [InlineData("a b", "", 1)]
    public void Counts_word_errors(string reference, string hypothesis, double expected) => Assert.Equal(expected, Wer.Rate(reference, hypothesis), 3);
}

// Złote próbki (golden\): nagranie + ręcznie spisana treść. Pilnują, że zmiana modelu, whisper.cpp, ffmpeg albo progów nie pogorszyła rozpoznawania:
// - nazwa.wav (16 kHz) + nazwa.txt (dokładnie to, co powiedziano); nazwa zaczyna się od języka: pl-..., en-...;
// - nazwa.must.txt: nazwiska, firmy, terminy - każda linia musi paść w transkrypcji;
// - nazwa.max-wer: najwyższy dopuszczalny WER (pomiar wzorcowy + zapas).
// Uruchomienie (prawdziwy Whisper na GPU, ~1 min): dotnet test --filter Category=Quality
// Składniki z settings.json aplikacji (bin\x64\Debug) albo TC_SETTINGS=ścieżka do settings.json.
// Próbki pl-meeting / en-meeting to syntezator mowy Windows (czysty dźwięk) - łapią regresje, ale nie mierzą jakości na prawdziwym spotkaniu;
// dodaj własne nagrania z ręcznie spisaną treścią.
[Trait("Category", "Quality")]
public class GoldenSampleTests(ITestOutputHelper output)
{
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static string GoldenDir => Path.Combine(Root, "TeamsCatcher.Tests", "golden");

    static string? SettingsPath() => new[]
    {
        Environment.GetEnvironmentVariable("TC_SETTINGS") ?? "",
        Path.Combine(Root, "TeamsCatcher", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "settings.json"),
        Path.Combine(Root, "TeamsCatcher", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "settings.json"),
    }.FirstOrDefault(File.Exists);

    // Treść transkrypcji bez "[0:00:05] Speaker 1: " i znacznika innego języka "(EN) ".
    public static string Spoken(string transcript) => string.Join(" ", transcript.Split('\n')
        .Select(l => Regex.Replace(l.Trim(), @"^\[\d+:\d\d:\d\d\]\s*[^:]+:\s*(\([A-Z]{2}\)\s*)?", "")));

    [Fact]
    public async Task Golden_samples_keep_word_error_rate_and_names()
    {
        if (SettingsPath() is not { } path || Settings.Load(path, _ => { }) is var settings && Transcriber.MissingFiles(settings) is { } missing)
        {
            output.WriteLine($"Skipped: Whisper not found ({SettingsPath() ?? "no settings.json"}). Set TC_SETTINGS to the app's settings.json.");
            return;
        }
        var failures = new List<string>();
        foreach (var wav in Directory.GetFiles(GoldenDir, "*.wav"))
        {
            var name = Path.GetFileNameWithoutExtension(wav);
            var language = name[..2];
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tc-golden-{name}-{Guid.NewGuid():N}")).FullName;
            try
            {
                File.Copy(wav, Path.Combine(folder, RecordingSession.TranscriptWav));
                File.WriteAllText(Path.Combine(folder, RecordingSession.InfoJson),
                    System.Text.Json.JsonSerializer.Serialize(new SessionInfo { Finalized = true, Title = wav }));
                await Transcriber.RunAsync(folder, settings, CatcherService.FindFfmpeg(), _ => { }, CancellationToken.None, Transcriber.Steps.Transcribe);

                var transcriptFile = Path.Combine(folder, Transcriber.TranscriptFile(language));
                var heard = File.Exists(transcriptFile) ? Spoken(File.ReadAllText(transcriptFile)) : "";
                var wer = Wer.Rate(File.ReadAllText(Path.ChangeExtension(wav, ".txt")), heard);
                var maxFile = Path.ChangeExtension(wav, ".max-wer");
                var max = File.Exists(maxFile) ? double.Parse(File.ReadAllText(maxFile).Trim(), System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
                var mustFile = Path.Combine(GoldenDir, name + ".must.txt");
                var lost = File.Exists(mustFile)
                    ? File.ReadAllLines(mustFile).Where(l => l.Trim().Length > 0 && !MeetingSearch.Normalize(heard).Contains(MeetingSearch.Normalize(l.Trim()))).ToList() : [];

                output.WriteLine($"{name}: WER {wer:P1} (max {(max is { } m ? m.ToString("P1") : "not set")}), names lost: {(lost.Count == 0 ? "none" : string.Join(", ", lost))}");
                output.WriteLine($"  heard: {heard}");
                if (max == null) failures.Add($"{name}: no {Path.GetFileName(maxFile)} - measured WER {wer:F3}, write it (plus a margin) to the file");
                else if (wer > max) failures.Add($"{name}: WER {wer:P1} > {max:P1}");
                if (lost.Count > 0) failures.Add($"{name}: names not recognized: {string.Join(", ", lost)}");
            }
            finally { Directory.Delete(folder, true); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
