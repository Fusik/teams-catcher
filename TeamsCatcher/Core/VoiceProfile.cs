using System.IO;
using System.Text.Json;
using SherpaOnnx;

namespace TeamsCatcher.Core;

// Głos użytkownika ("Ja"): średni odcisk głosu (CAM++ ze sherpa-onnx, ten sam model co diaryzacja) z jego mowy w kanale mikrofonu - profil uczy się
// sam po każdej rozmowie, w której mikrofon nie był wyciszony. Potem mówca po stronie Teams o takim głosie dostaje etykietę "Ja"
// (np. import wideo, gdzie wszyscy są w jednym kanale). Profil leży w voice-me.json obok exe i nie wychodzi z komputera.
// Odciski CAM++ nie rozróżniają osób idealnie (wywiad TVN: te same osoby 0,81-0,92, różne 0,74-0,88), dlatego etykieta "Ja" tylko przy
// wysokim podobieństwie i wyraźnej przewadze nad innymi mówcami, a wartości idą do logu.
public static class VoiceProfile
{
    const double MinSegmentSeconds = 2, MaxSegmentSeconds = 15, MaxEnrollSeconds = 90, MaxWeightSeconds = 900;
    public const double MatchSimilarity = 0.7, MatchMargin = 0.1;

    sealed record Stored(float[] Embedding, double Seconds);

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "voice-me.json");

    static Stored? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) : null; }
        catch (JsonException) { return null; }
    }

    // Po transkrypcji: najpierw profil uczy się z mowy "Ja" (bez nakładania się z rozmówcami - w mikrofonie bywa echo), potem mówca po stronie Teams
    // pasujący do profilu zostaje "Ja". Brak modelu odcisków albo błąd = zdania bez zmian.
    public static async Task<List<TranscriptSegment>> ApplyAsync(List<TranscriptSegment> segments, string? micWav, string? othersWav, Settings s, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(s.DiarizationEmbeddingModel))
        {
            log($"Voice profile skipped, file not found: {s.DiarizationEmbeddingModel}");
            return segments;
        }
        try
        {
            return await Task.Run(() =>
            {
                using var extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
                    { Model = s.DiarizationEmbeddingModel, NumThreads = Math.Max(1, Environment.ProcessorCount / 2) });
                if (micWav != null) Enroll(segments, micWav, extractor, log);
                return othersWav == null ? segments : Relabel(segments, othersWav, extractor, log);
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Voice profile skipped: {e.Message}");
            return segments;
        }
    }

    static float[] Embedding(SpeakerEmbeddingExtractor extractor, float[] samples, long fromMs, long toMs)
    {
        var a = Math.Clamp((int)(fromMs * 16), 0, samples.Length);
        var b = Math.Clamp((int)(toMs * 16), a, samples.Length);
        var stream = extractor.CreateStream();
        stream.AcceptWaveform(16000, samples[a..b]);
        stream.InputFinished();
        return extractor.Compute(stream);
    }

    static float[] Mean(IReadOnlyList<float[]> embeddings) =>
        Enumerable.Range(0, embeddings[0].Length).Select(i => embeddings.Average(e => e[i])).ToArray();

    // Najdłuższe wypowiedzi "Ja" bez nakładania się z rozmówcami (do MaxEnrollSeconds) -> średnia ważona z dotychczasowym profilem.
    static void Enroll(List<TranscriptSegment> segments, string micWav, SpeakerEmbeddingExtractor extractor, Action<string> log)
    {
        var others = segments.Where(x => x.Speaker != Transcriber.Me).ToList();
        var mine = segments.Where(x => x.Speaker == Transcriber.Me && x.ToMs - x.FromMs >= MinSegmentSeconds * 1000
                && !others.Any(o => o.FromMs < x.ToMs + 1000 && x.FromMs < o.ToMs + 1000))
            .OrderByDescending(x => x.ToMs - x.FromMs).ToList();
        var picked = new List<TranscriptSegment>();
        double seconds = 0;
        foreach (var x in mine)
        {
            if (seconds >= MaxEnrollSeconds) break;
            picked.Add(x);
            seconds += Math.Min(MaxSegmentSeconds, (x.ToMs - x.FromMs) / 1000.0);
        }
        if (picked.Count == 0) return;
        var samples = Diarizer.ReadSamples(micWav);
        var fresh = Mean(picked.Select(x => Embedding(extractor, samples, x.FromMs, Math.Min(x.ToMs, x.FromMs + (long)(MaxSegmentSeconds * 1000)))).ToList());
        var old = Load();
        var oldWeight = old == null ? 0 : Math.Min(old.Seconds, MaxWeightSeconds);
        var merged = old == null ? fresh : fresh.Select((v, i) => (float)((old.Embedding[i] * oldWeight + v * seconds) / (oldWeight + seconds))).ToArray();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(merged, (old?.Seconds ?? 0) + seconds)));
        log($"Voice profile updated from {picked.Count} of your sentences ({seconds:F0} s of speech).");
    }

    // Mówca po stronie Teams z głosem użytkownika -> "Ja"; pozostali rozmówcy numerowani od nowa (w kolejności pierwszego odezwania się).
    static List<TranscriptSegment> Relabel(List<TranscriptSegment> segments, string othersWav, SpeakerEmbeddingExtractor extractor, Action<string> log)
    {
        if (Load() is not { } profile) return segments;
        var labels = segments.Where(x => IsOther(x.Speaker)).Select(x => x.Speaker).Distinct().ToList();
        if (labels.Count == 0) return segments;
        var samples = Diarizer.ReadSamples(othersWav);
        var centroids = new Dictionary<string, float[]>();
        foreach (var label in labels)
        {
            var embeddings = new List<float[]>();
            double seconds = 0;
            foreach (var x in segments.Where(x => x.Speaker == label && x.ToMs - x.FromMs >= MinSegmentSeconds * 1000).OrderByDescending(x => x.ToMs - x.FromMs))
            {
                if (seconds >= MaxEnrollSeconds) break;
                embeddings.Add(Embedding(extractor, samples, x.FromMs, Math.Min(x.ToMs, x.FromMs + (long)(MaxSegmentSeconds * 1000))));
                seconds += Math.Min(MaxSegmentSeconds, (x.ToMs - x.FromMs) / 1000.0);
            }
            if (embeddings.Count > 0) centroids[label] = Mean(embeddings);
        }
        var me = ChooseMe(centroids, profile.Embedding);
        log($"Voice check of the Teams side: {string.Join(", ", centroids.Select(c => $"{c.Key} {Diarizer.Cosine(c.Value, profile.Embedding):F2}"))} (me = {MatchSimilarity:F2}+) -> {(me ?? "nobody is you")}.");
        if (me == null) return segments;
        var renamed = segments.Select(x => x.Speaker == me ? x with { Speaker = Transcriber.Me } : x).ToList();
        // pozostali: numeracja w kolejności pierwszego odezwania się; jedna osoba zostaje z etykietą "Rozmówcy", jak po diaryzacji
        var rest = renamed.Where(x => IsOther(x.Speaker)).OrderBy(x => x.FromMs).Select(x => x.Speaker).Distinct().ToList();
        return renamed.Select(x => IsOther(x.Speaker) ? x with { Speaker = rest.Count > 1 ? $"{Transcriber.OtherPrefix} {rest.IndexOf(x.Speaker) + 1}" : Transcriber.Others } : x).ToList();
    }

    static bool IsOther(string speaker) => speaker == Transcriber.Others || speaker.StartsWith(Transcriber.OtherPrefix);

    // Etykieta mówcy o głosie użytkownika: podobieństwo >= MatchSimilarity i przewaga >= MatchMargin nad następnym (gdy jest następny); inaczej null.
    public static string? ChooseMe(IReadOnlyDictionary<string, float[]> centroids, float[] profile)
    {
        var ranked = centroids.Select(c => (Label: c.Key, Similarity: Diarizer.Cosine(c.Value, profile))).OrderByDescending(x => x.Similarity).ToList();
        if (ranked.Count == 0 || ranked[0].Similarity < MatchSimilarity) return null;
        return ranked.Count == 1 || ranked[0].Similarity - ranked[1].Similarity >= MatchMargin ? ranked[0].Label : null;
    }
}
