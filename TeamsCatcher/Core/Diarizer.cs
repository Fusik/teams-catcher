using System.IO;
using NAudio.Wave;
using SherpaOnnx;

namespace TeamsCatcher.Core;

public readonly record struct SpeakerTurn(double Start, double End, int Speaker);

// Kto mówi po stronie Teams (Rozmówca 1/2/3): sherpa-onnx = segmentacja pyannote + odcisk głosu + klastrowanie, CPU, bez Pythona.
// Sprawdzone na nagraniach testowych k2-fsa: próg 0,6 dał 4/2/3 mówców przy prawdziwych 4/2/2 (nadmiarowy mówca zamiast łączenia osób).
public static class Diarizer
{
    // Weryfikacja zdania odciskiem głosu (sprawdzone na wywiadzie TVN): od ok. 2 s podobieństwo do właściwego mówcy 0,6-0,9;
    // poniżej 1,5 s ok. 0,2-0,4 do wszystkich - krótkie zdania zostają przy wyniku diaryzacji.
    const double VerifyMinSeconds = 2.0, CentroidMinSeconds = 1.5;
    const double VerifyMinSimilarity = 0.6, VerifyMargin = 0.15;

    public static string PyannoteScript => Path.Combine(AppContext.BaseDirectory, "diarize.py");

    public static bool PyannoteAvailable(Settings s) =>
        File.Exists(s.PyannotePython) && File.Exists(PyannoteScript) && File.Exists(Path.Combine(s.PyannoteModel, "config.yaml"));

    // pyannote community-1 przez Pythona (diarize.py): tury mówców na GPU, potem te same reguły przypisania co dla sherpa-onnx
    // (największe pokrycie czasowe, mówca stały w zdaniu). Bez weryfikacji odciskiem głosu - community-1 sam liczy odciski.
    public static async Task<(List<TranscriptSegment> Segments, int Speakers)> RunPyannoteAsync(string wav, IReadOnlyList<TranscriptSegment> segments,
        Settings s, string labelPrefix, string workDir, CancellationToken ct)
    {
        var output = Path.Combine(workDir, "pyannote.json");
        try
        {
            await Transcriber.RunAsync(s.PyannotePython, $"\"{PyannoteScript}\" \"{s.PyannoteModel}\" \"{wav}\" \"{output}\" {s.DiarizationSpeakers}", workDir, ct);
            var turns = ParsePyannote(await File.ReadAllTextAsync(output, ct));
            return (AssignSpeakers(segments, turns, labelPrefix), turns.Select(t => t.Speaker).Distinct().Count());
        }
        finally
        {
            File.Delete(output);
        }
    }

    // {"turns": [{"start": 1.2, "end": 3.4, "speaker": "SPEAKER_00"}]} -> numery mówców w kolejności etykiet
    public static List<SpeakerTurn> ParsePyannote(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ids = new Dictionary<string, int>();
        return doc.RootElement.GetProperty("turns").EnumerateArray().Select(t =>
        {
            var label = t.GetProperty("speaker").GetString() ?? "";
            if (!ids.TryGetValue(label, out var id)) ids[label] = id = ids.Count;
            return new SpeakerTurn(t.GetProperty("start").GetDouble(), t.GetProperty("end").GetDouble(), id);
        }).ToList();
    }

    public static string? MissingFiles(Settings s) =>
        !File.Exists(s.DiarizationSegmentationModel) ? s.DiarizationSegmentationModel
        : !File.Exists(s.DiarizationEmbeddingModel) ? s.DiarizationEmbeddingModel : null;

    // wav: 16 kHz mono PCM (kanał "Rozmówcy" przygotowany dla Whispera). Zwraca segmenty z etykietami i liczbę zdań poprawionych weryfikacją.
    public static (List<TranscriptSegment> Segments, int Corrected) Run(string wav, IReadOnlyList<TranscriptSegment> segments, Settings s, string labelPrefix)
    {
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = s.DiarizationSegmentationModel;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = s.DiarizationEmbeddingModel;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = s.DiarizationSpeakers > 0 ? s.DiarizationSpeakers : -1;
        config.Clustering.Threshold = s.DiarizationThreshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        var samples = ReadSamples(wav);
        List<SpeakerTurn> turns;
        using (var diarization = new OfflineSpeakerDiarization(config))
            turns = diarization.Process(samples).Select(x => new SpeakerTurn(x.Start, x.End, x.Speaker)).ToList();

        using var extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = s.DiarizationEmbeddingModel, NumThreads = threads });
        float[] Embedding(double from, double to)
        {
            var a = Math.Clamp((int)(from * 16000), 0, samples.Length);
            var b = Math.Clamp((int)(to * 16000), a, samples.Length);
            var stream = extractor.CreateStream();
            stream.AcceptWaveform(16000, samples[a..b]);
            stream.InputFinished();
            return extractor.Compute(stream);
        }
        // wzorzec głosu mówcy = średni odcisk z jego dłuższych wypowiedzi
        var centroids = turns.Where(t => t.End - t.Start >= CentroidMinSeconds).GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g =>
        {
            var embeddings = g.Select(t => Embedding(t.Start, t.End)).ToList();
            return Enumerable.Range(0, embeddings[0].Length).Select(i => embeddings.Average(e => e[i])).ToArray();
        });
        var corrected = 0;
        var result = AssignSpeakers(segments, turns, labelPrefix, (segment, current) =>
        {
            if (centroids.Count < 2 || (segment.ToMs - segment.FromMs) / 1000.0 < VerifyMinSeconds) return current;
            var picked = PickSpeaker(Embedding(segment.FromMs / 1000.0, segment.ToMs / 1000.0), centroids, current);
            if (picked != current) corrected++;
            return picked;
        });
        return (result, corrected);
    }

    // Przerwa dłuższa niż ta = nowa wypowiedź, nawet bez kropki na końcu poprzedniego segmentu.
    const int SentenceMaxGapMs = 1500;

    // Mówca nie zmienia się w środku zdania (jak "realign by punctuation" w whisper-diarization). Gra komentowana przez jedną osobę:
    // "bo ja się" / "gubię w takich momentach" dostały Rozmówcę 1 i 2, bo podobieństwo wzorców tej samej osoby (0,81-0,92)
    // nie różni się od różnych osób w wywiadzie TVN (0,74-0,88). Zdanie = segmenty do kropki/pytajnika; mówca = najdłużej mówiący w nim.
    public static List<T> RealignBySentence<T>(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<T> speakers)
    {
        var result = speakers.ToList();
        var start = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var ends = i == segments.Count - 1 || segments[i].Text.TrimEnd() is [.., '.' or '?' or '!' or '…']
                || segments[i + 1].FromMs - segments[i].ToMs > SentenceMaxGapMs;
            if (!ends) continue;
            var speaker = Enumerable.Range(start, i - start + 1).GroupBy(j => speakers[j]).MaxBy(g => g.Sum(j => segments[j].ToMs - segments[j].FromMs))!.Key;
            for (var j = start; j <= i; j++) result[j] = speaker;
            start = i + 1;
        }
        return result;
    }

    static float[] ReadSamples(string wav)
    {
        using var reader = new WaveFileReader(wav);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException($"Diarization needs 16 kHz mono, got {reader.WaveFormat}.");
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount];
        var read = 0;
        int n;
        while (read < samples.Length && (n = provider.Read(samples, read, samples.Length - read)) > 0) read += n;
        return samples;
    }

    // Zmiana mówcy tylko przy wyraźnej przewadze innego wzorca: podobieństwo >= 0,6 i o >= 0,15 lepsze niż do obecnego.
    public static int PickSpeaker(float[] embedding, IReadOnlyDictionary<int, float[]> centroids, int current)
    {
        var (best, bestSimilarity) = centroids.Select(c => (c.Key, Cosine(embedding, c.Value))).MaxBy(c => c.Item2);
        var currentSimilarity = centroids.TryGetValue(current, out var own) ? Cosine(embedding, own) : -1;
        return best != current && bestSimilarity >= VerifyMinSimilarity && bestSimilarity - currentSimilarity >= VerifyMargin ? best : current;
    }

    static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    // Każdy segment Whispera dostaje mówcę z największym pokryciem czasowym (opcjonalnie poprawionego przez verify);
    // numeracja w kolejności pierwszego odezwania się. Jeden wykryty mówca (albo brak) = etykieta bez zmian ("Rozmówcy").
    public static List<TranscriptSegment> AssignSpeakers(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<SpeakerTurn> turns, string labelPrefix,
        Func<TranscriptSegment, int, int>? verify = null)
    {
        if (turns.Select(t => t.Speaker).Distinct().Count() <= 1) return segments.ToList();
        var speakers = segments.Select(s =>
        {
            double from = s.FromMs / 1000.0, to = s.ToMs / 1000.0;
            var best = turns
                .Select(t => (t.Speaker, Overlap: Math.Min(to, t.End) - Math.Max(from, t.Start), Distance: Math.Max(t.Start - to, from - t.End)))
                .OrderByDescending(x => x.Overlap).ThenBy(x => x.Distance)
                .First();
            // bez pokrycia: najbliższy mówca do 2 s, dalej - zostawiamy ogólną etykietę
            if (best.Overlap <= 0 && best.Distance > 2) return (int?)null;
            return verify?.Invoke(s, best.Speaker) ?? best.Speaker;
        }).ToList();
        speakers = RealignBySentence(segments, speakers);
        var order = segments.Zip(speakers).Where(x => x.Second != null).OrderBy(x => x.First.FromMs).Select(x => x.Second!.Value).Distinct().ToList();
        return segments.Zip(speakers, (s, speaker) => speaker is { } id ? s with { Speaker = $"{labelPrefix} {order.IndexOf(id) + 1}" } : s).ToList();
    }
}
