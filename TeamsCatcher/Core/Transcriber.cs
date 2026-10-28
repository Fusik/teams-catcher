using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace TeamsCatcher.Core;

// MinP = najniższa pewność słowa w zdaniu (z tokenów Whispera) - niska = zdanie do korekty.
public sealed record TranscriptSegment(long FromMs, long ToMs, string Speaker, string Text, string Language = "", double MinP = 1);

// Transkrypcja whisper.cpp (whisper-cli, GPU jeśli jest CUDA) z podziałem na mówców po kanałach transcript.wav:
// lewy = mikrofon (Ja), prawy = Teams + dźwięk prezentacji (Rozmówcy). Każdy kanał osobno z VAD - sprawdzone na żywo:
// "--diarize" z VAD skleja wszystko w jeden segment, a bez VAD Whisper "halucynuje" w ciszy ("Dziękuję za oglądanie!").
// Jakość ponad szybkość: kilka przebiegów (język, słowniczek), weryfikacja mówców odciskiem głosu, korekta niepewnych zdań.
public static class Transcriber
{
    public const string TranscriptTxt = "transcript.txt";
    public const string Me = "Ja", Others = "Rozmówcy", OtherPrefix = "Rozmówca";
    public static readonly string[] Languages = ["pl", "en"];
    // Segmenty tego samego mówcy z przerwą do 2 s łączone w jedną wypowiedź.
    const long JoinGapMs = 2000;
    // próbka do wyboru języka kanału (PL/EN): pierwsze 2 min; mowa po ciszy VAD i tak trafia do próbki
    // język wybierany na 4 minutach: 4 okna po minucie z różnych miejsc nagrania (najwięcej mowy)
    const int SampleWindows = 4, SampleWindowSeconds = 60, LanguageSampleSeconds = SampleWindows * SampleWindowSeconds;

    public static string TranscriptFile(string language) => $"transcript.{language}.txt";

    public static string? MissingFiles(Settings s) =>
        !File.Exists(s.WhisperCli) ? s.WhisperCli : !File.Exists(s.WhisperModel) ? s.WhisperModel : null;

    sealed record Channel(string Speaker, string Wav, List<TranscriptSegment> Segments, string Language, double Score);

    // Stan po rozpoznaniu mowy (Whisper, hipotezy B/C, -tr, diaryzacja, echo) - do przeliczenia bez ponownego Whispera.
    public sealed record RecognitionState(List<TranscriptSegment> Segments, List<HypothesisWord> Hypotheses,
        List<(string Speaker, TranscriptSegment Segment)> Heard, string Glossary);

    const string StateFile = "state.json";
    static readonly JsonSerializerOptions StateJson = new() { IncludeFields = true };

    static void SaveState(string folder, RecognitionState state, string file = StateFile)
    {
        Directory.CreateDirectory(Path.Combine(folder, WorkNotes.Dir));
        File.WriteAllText(Path.Combine(folder, WorkNotes.Dir, file), JsonSerializer.Serialize(state, StateJson));
    }

    public static RecognitionState? LoadState(string folder, string file = StateFile)
    {
        var path = Path.Combine(folder, WorkNotes.Dir, file);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RecognitionState>(File.ReadAllText(path), StateJson); }
        catch (JsonException) { return null; }
    }

    // Hipotezy B (Whisper turbo), C (Qwen3-ASR) i mowa po angielsku (-tr) - głosy w dyskusji modeli (krok Correct).
    // english: mowa po angielsku (-tr) - tylko dla korekty (podpowiedź sensu w dyskusji); notatka korzysta z samych B i C
    static async Task<(List<HypothesisWord> Hypotheses, List<(string Speaker, TranscriptSegment Segment)> Heard)> HypothesesAsync(List<Channel> channels, string folder,
        Settings settings, bool vad, string ffmpeg, WorkNotes work, Action<string> log, CancellationToken ct, bool withEnglish = true)
    {
        var hypotheses = new List<HypothesisWord>();
        var heard = new List<(string Speaker, TranscriptSegment Segment)>();
        await Ollama.UnloadAsync(settings, ct);
        for (var i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            if (c.Segments.Count == 0) continue;
            if (File.Exists(settings.WhisperAltModel))
            {
                var (_, _, words) = await WhisperAsync(c.Wav, Path.Combine(folder, $"whisper_{i}_alt"), c.Speaker, settings, vad, c.Language, "", folder, ct, settings.WhisperAltModel);
                hypotheses.AddRange(words.Select(w => new HypothesisWord("B", c.Speaker, w.FromMs, w.ToMs, w.Text)));
                log($"  step: {c.Speaker} hypothesis B (Whisper turbo)");
            }
            else log($"  WARNING: hypothesis B skipped, model not found: {settings.WhisperAltModel}");
            if (QwenAsr.Available(settings))
            {
                hypotheses.AddRange(await QwenAsr.TranscribeAsync(c.Wav, c.Segments, c.Speaker, c.Language, "", settings, folder, ct));
                log($"  step: {c.Speaker} hypothesis C (Qwen3-ASR)");
            }
            else log("  WARNING: hypothesis C skipped, Python environment, asr.py or Qwen3-ASR model not found.");
            // mowa od razu po angielsku (Whisper -tr): podpowiedź sensu w dyskusji i dla tłumaczenia na EN
            if (withEnglish && c.Language == "pl")
            {
                var (english, _, _) = await WhisperAsync(c.Wav, Path.Combine(folder, $"whisper_{i}_en"), c.Speaker, settings, vad, "pl", "", folder, ct, translate: true);
                heard.AddRange(english.Select(e => (c.Speaker, e)));
                log($"  step: {c.Speaker} speech to English (Whisper large-v3 -tr)");
            }
        }
        work.Write("04-whisper-english.md", $"Whisper {Path.GetFileNameWithoutExtension(settings.WhisperModel)} -tr (mowa PL -> tekst EN)",
            "Niezależne tłumaczenie mowy na angielski (z dźwięku, nie z tekstu PL) - kontrola sensu i podpowiedź dla tłumaczenia.",
            "Przetłumacz mowę z kanałów po polsku od razu na angielski; wynik porównywany z transkrypcją PL po przetłumaczeniu z powrotem.",
            heard.Count > 0 ? $"gotowe: {heard.Count} zdań" : "pominięty (brak kanałów po polsku)",
            string.Join("\n", heard.OrderBy(h => h.Segment.FromMs).Select(h => $"- {Time(h.Segment)} {h.Speaker}: {h.Segment.Text}")));
        foreach (var (source, file, title, role) in new[]
        {
            ("B", "02-whisper-turbo.md", $"Whisper {Path.GetFileNameWithoutExtension(settings.WhisperAltModel)} (mowa -> tekst, hipoteza B)", "Drugie rozpoznawanie: ten sam typ modelu, inne błędy."),
            ("C", "03-qwen3-asr.md", $"{settings.QwenAsrModel} (mowa -> tekst, hipoteza C)", "Trzecie rozpoznawanie: inna architektura (Alibaba), zdania A osobno."),
        })
        {
            var found = hypotheses.Where(h => h.Source == source).GroupBy(h => h.Speaker)
                .SelectMany(g => HypothesisSentences(g.Select(h => (h.FromMs, h.ToMs, h.Text)).ToList(), g.Key, "")).OrderBy(x => x.FromMs).ToList();
            work.Write(file, title, role, "Przepisz ten sam dźwięk niezależnie od hipotezy A; wynik jest głosem w dyskusji modeli tekstu, nie podstawą.",
                found.Count > 0 ? $"gotowe: {found.Count} zdań" : "pominięty (brak modelu)",
                string.Join("\n", found.Select(h => $"- {Time(h)} {h.Speaker}: {h.Text}")));
        }
        log($"Hypotheses: B {hypotheses.Count(h => h.Source == "B")}, C {hypotheses.Count(h => h.Source == "C")} words (Qwen3-ASR on {Devices.GetValueOrDefault("Qwen3-ASR", "?")}).");
        return (hypotheses, heard);
    }

    // Słowa hipotezy w zdania (do kropki/pytajnika) - plik roboczy B/C.
    public static List<TranscriptSegment> HypothesisSentences(IReadOnlyList<(long FromMs, long ToMs, string Text)> words, string speaker, string language)
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

    // Kanały do kroku Correct po samej transkrypcji: dźwięk kanału z transcript.wav, zdania i język (najczęstszy) z zapisu.
    static async Task<List<Channel>> ChannelsAsync(string folder, string wav, (string Speaker, string? Pan)[] sides, List<TranscriptSegment> segments, string ffmpeg,
        List<string> temp, CancellationToken ct)
    {
        var channels = new List<Channel>();
        for (var i = 0; i < sides.Length; i++)
        {
            var input = wav;
            if (sides[i].Pan is { } pan)
            {
                input = Path.Combine(folder, $"whisper_{i}.wav");
                temp.Add(input);
                await RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{wav}\" -af \"pan=mono|c0={pan}\" \"{input}\"", folder, ct);
            }
            var mine = segments.Where(x => sides.Length == 1 || (x.Speaker == Me) == (sides[i].Speaker == Me)).ToList();
            var language = mine.Where(x => x.Language.Length > 0).GroupBy(x => x.Language).MaxBy(g => g.Count())?.Key ?? Languages[0];
            channels.Add(new Channel(sides[i].Speaker, input, mine, language, 0));
        }
        return channels;
    }

    // Składanie: poprawione zdania z kawałków przesunięte na oś końcowego transcript.wav, rozmówcy po stronie Teams rozpoznani raz
    // na całym nagraniu (diaryzacja kawałków osobno dawałaby różne numery tej samej osobie). Stan zapisany - przeliczenie bez Whispera.
    static async Task<RecognitionState> AssembleStateAsync(string folder, string wav, string ffmpeg, Settings settings, Action<string> log,
        List<string> temp, CancellationToken ct)
    {
        var info = JsonSerializer.Deserialize<SessionInfo>(await File.ReadAllTextAsync(Path.Combine(folder, RecordingSession.InfoJson), ct))
            ?? throw new InvalidDataException("No session.json.");
        var zero = LiveChunks.FinalZero(info) ?? throw new InvalidDataException("No audio start in session.json.");
        var segments = new List<TranscriptSegment>();
        var heard = new List<(string Speaker, TranscriptSegment Segment)>();
        var glossary = "";
        foreach (var (chunk, chunkFolder) in LiveChunks.List(folder))
        {
            var state = LoadState(chunkFolder, LiveChunks.CorrectedFile) ?? throw new InvalidDataException($"Live chunk {chunk.Index} was not processed.");
            var offset = LiveChunks.OffsetMs(chunk, zero);
            segments.AddRange(state.Segments.Select(s => LiveChunks.Shift(s, offset)));
            heard.AddRange(state.Heard.Select(h => (h.Speaker, LiveChunks.Shift(h.Segment, offset))));
            if (state.Glossary.Length > glossary.Length) glossary = state.Glossary;
        }
        log($"Assembled {LiveChunks.List(folder).Count} live chunks: {segments.Count} sentences.");
        // pytania "do wyjaśnienia" z kawałków -> panel nagrania (odmiany tego samego słowa raz)
        var questions = new List<UnclearTerm>();
        foreach (var q in LiveChunks.List(folder).SelectMany(c => KnowledgeBase.LoadQuestions(c.Folder)))
            if (!questions.Any(x => KnowledgeBase.SameWord(TranscriptLlm.Bare(x.Term), TranscriptLlm.Bare(q.Term)))) questions.Add(q);
        KnowledgeBase.SaveQuestions(folder, questions);
        int channels;
        using (var reader = new WaveFileReader(wav)) channels = reader.WaveFormat.Channels;
        var others = segments.Where(s => s.Speaker != Me).OrderBy(s => s.FromMs).ToList();
        if (others.Count > 0)
        {
            var othersWav = wav;
            if (channels == 2)
            {
                othersWav = Path.Combine(folder, "whisper_others.wav");
                temp.Add(othersWav);
                await RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{wav}\" -af \"pan=mono|c0=c1\" \"{othersWav}\"", folder, ct);
            }
            others = await SplitOthersAsync(others, othersWav, settings, log, ct);
        }
        var all = segments.Where(s => s.Speaker == Me).Concat(others).OrderBy(s => s.FromMs).ToList();
        var state2 = new RecognitionState(all, [], heard, glossary);
        SaveState(folder, state2);
        return state2;
    }

    // Full = całe nagranie; Chunk = kawałek w trakcie rozmowy (bez diaryzacji i tłumaczenia, notatka częściowa);
    // Assemble = po rozmowie złożenie z kawałków (diaryzacja raz na całości, bez ponownej dyskusji, notatka z całości).
    public enum Mode { Full, Chunk, Assemble }

    // Kroki nagrania. Po nagraniu automatycznie Transcribe (Whisper + pyannote) i kroki zaznaczone w opcjach (Correct, Notes) -
    // CatcherService.AutoSteps; panel RECORDING pokazuje, które policzone i ile trwały.
    [Flags]
    public enum Steps { Transcribe = 1, Correct = 2, Translate = 4, Notes = 8, All = 15 }

    // Czasy kroków nagrania (analysis\steps.json): krok -> "20.10 21:31, 0:20:41".
    const string StepsFile = "steps.json";

    public static Dictionary<string, string> LoadSteps(string folder)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, WorkNotes.Dir, StepsFile))) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    static void SaveStep(string folder, Steps step, System.Diagnostics.Stopwatch watch, Action<string> log)
    {
        var steps = LoadSteps(folder);
        steps[step.ToString()] = $"{DateTime.Now:dd.MM HH:mm}, {watch.Elapsed:h\\:mm\\:ss}";
        Directory.CreateDirectory(Path.Combine(folder, WorkNotes.Dir));
        File.WriteAllText(Path.Combine(folder, WorkNotes.Dir, StepsFile), JsonSerializer.Serialize(steps));
        log($"Step {step} done in {watch.Elapsed:h\\:mm\\:ss}.");
        watch.Restart();
    }

    // steps bez Transcribe: rozpoznanie mowy z zapisu (state.json); Correct zaczyna od surowych zdań, Translate/Notes od poprawionych.
    // Kawałek na żywo (Chunk) zawsze wszystkie kroki - notatka częściowa to sens trybu na żywo.
    // Zwraca notatkę częściową (Markdown) w trybie Chunk - do dymka w trayu.
    public static async Task<string?> RunAsync(string folder, Settings settings, string ffmpeg, Action<string> log, CancellationToken ct, Steps steps = Steps.Transcribe,
        Mode mode = Mode.Full)
    {
        if (mode == Mode.Chunk) steps = Steps.All;
        var fromState = !steps.HasFlag(Steps.Transcribe);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // każda linia logu z czasem od startu zadania: widać, ile trwa który krok
        var write = log;
        log = message => write($"[{(int)watch.Elapsed.TotalMinutes}:{watch.Elapsed:ss}] {message}");
        string? partialNotes = null;
        var kb = KnowledgeBase.Load(KnowledgeBase.DefaultPath);
        var wav = Path.Combine(folder, RecordingSession.TranscriptWav);
        if (!File.Exists(wav)) throw new FileNotFoundException("No transcript.wav in the recording folder.", wav);
        int channelCount;
        using (var reader = new WaveFileReader(wav)) channelCount = reader.WaveFormat.Channels;
        // starsze nagrania: mono (miks) = jeden mówca
        (string Speaker, string? Pan)[] sides = channelCount == 2 ? [(Me, "c0"), (Others, "c1")] : [(Others, null)];
        var vad = File.Exists(settings.WhisperVadModel);
        if (!vad) log("VAD model not found - transcription may contain phantom phrases in silence.");
        // modele potrzebne w tym przebiegu: korekta/tłumaczenie - piszący i sprawdzający, notatka - model notatki
        string[] needed = [.. (steps & (Steps.Correct | Steps.Translate)) != 0 ? [settings.CorrectionModel, settings.CheckModel] : Array.Empty<string>(),
            .. steps.HasFlag(Steps.Notes) && mode != Mode.Chunk ? [settings.NotesModel] : Array.Empty<string>()];
        var llm = needed.Length > 0 && await Ollama.IsAvailableAsync(settings, ct, needed);
        if (!llm && needed.Length > 0) log($"Ollama with {string.Join(" and ", needed)} is not available - no correction, translation or notes.");

        var temp = new List<string>();
        try
        {
            var state = !fromState ? null : (steps.HasFlag(Steps.Correct) ? null : LoadState(folder, LiveChunks.CorrectedFile)) ?? LoadState(folder);
            if (fromState && state == null) throw new InvalidOperationException("Transcribe the recording first (no saved speech recognition).");
            if (mode == Mode.Assemble && state == null) state = await AssembleStateAsync(folder, wav, ffmpeg, settings, log, temp, ct);
            // Przelicz (po objaśnieniu niejasnych słów): bez Whispera i diaryzacji - stan zapisany po rozpoznaniu mowy
            var work = new WorkNotes(folder);
            var correct = llm && steps.HasFlag(Steps.Correct);
            string glossary;
            List<HypothesisWord> hypotheses;
            List<(string Speaker, TranscriptSegment Segment)> heard;
            List<TranscriptSegment> segmentsFinal;
            if (state == null)
            {
                // 1. przebieg: każdy kanał po polsku i po angielsku, wygrywa wersja z wyższą pewnością modelu
                var channels = new List<Channel>();
                for (var i = 0; i < sides.Length; i++)
                {
                    var input = wav;
                    if (sides[i].Pan is { } pan)
                    {
                        input = Path.Combine(folder, $"whisper_{i}.wav");
                        temp.Add(input);
                        await RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{wav}\" -af \"pan=mono|c0={pan}\" \"{input}\"", folder, ct);
                    }
                    string[] candidates = settings.TranscriptLanguage == "auto" ? Languages : [settings.TranscriptLanguage];
                    // długi kanał: język wybierany na próbce (pomiar 9,6 min: oba języki na całości = 2 min z 15); potem całość raz
                    double seconds;
                    using (var reader = new WaveFileReader(input)) seconds = reader.TotalTime.TotalSeconds;
                    if (candidates.Length > 1 && seconds > LanguageSampleSeconds + 30)
                    {
                        var sample = Path.Combine(folder, $"whisper_{i}_sample.wav");
                        temp.Add(sample);
                        // okna z największą ilością mowy, nie początek: rozmowa często zaczyna się ciszą albo muzyką (test: mowa od 28. minuty = losowy język)
                        var starts = SpeechWindows(input);
                        var trims = string.Concat(starts.Select((s, n) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[0:a]atrim=start={0}:duration={1},asetpts=PTS-STARTPTS[a{2}];", s, SampleWindowSeconds, n)));
                        await RunAsync(ffmpeg, $"-hide_banner -loglevel error -y -i \"{input}\" -filter_complex \"{trims}{string.Concat(starts.Select((_, n) => $"[a{n}]"))}concat=n={starts.Count}:v=0:a=1\" \"{sample}\"", folder, ct);
                        var scores = new List<(string Language, double Score)>();
                        // próbka modelem large-v3, nie turbo: na polskim dubbingu (Rick i Morty) turbo był pewniejszy w EN (-0,19 vs -0,25)
                        // i wybrałby angielski; zysk byłby ~7 s (większość czasu to wczytanie modelu)
                        foreach (var language in candidates)
                        {
                            scores.Add((language, (await WhisperAsync(sample, Path.Combine(folder, $"whisper_{i}_sample_{language}"), sides[i].Speaker, settings, vad, language, "", folder, ct)).Score));
                            log($"  step: {sides[i].Speaker} language sample {language.ToUpperInvariant()} (Whisper large-v3, {LanguageSampleSeconds} s)");
                        }
                        candidates = [scores.MaxBy(x => x.Score).Language];
                    }
                    Channel? best = null;
                    foreach (var language in candidates)
                    {
                        var (segments, score, _) = await WhisperAsync(input, Path.Combine(folder, $"whisper_{i}_{language}"), sides[i].Speaker, settings, vad, language, "", folder, ct);
                        if (best == null || score > best.Score) best = new Channel(sides[i].Speaker, input, segments, language, score);
                        log($"  step: {sides[i].Speaker} Whisper large-v3 {language.ToUpperInvariant()} ({seconds:F0} s audio)");
                    }
                    channels.Add(best!);
                }
                log($"Transcription pass 1 done ({string.Join(", ", channels.Select(c => $"{c.Speaker}: {c.Language.ToUpperInvariant()}"))}), Whisper on {Devices.GetValueOrDefault("Whisper", "?")}.");

                // nazwy własne z pierwszego przebiegu (model piszący) - zaufane słowa w dyskusji; pisownię objaśnia użytkownik w TO CLARIFY (baza wiedzy)
                // po nagraniu/imporcie (nie kawałek): bez słowniczka i hipotez B/C - najpierw notatka wstępna z samego A, potem drugi przebieg (niżej)
                glossary = "";
                if (correct && mode == Mode.Chunk)
                {
                    glossary = await TranscriptLlm.BuildGlossaryAsync(channels.SelectMany(c => c.Segments).ToList(), settings, ct);
                    log($"  step: glossary of names ({settings.CorrectionModel})");
                }

                work.Write("01-whisper-large-v3.md", $"Whisper {Path.GetFileNameWithoutExtension(settings.WhisperModel)} (mowa -> tekst, hipoteza A)",
                    "Główne rozpoznawanie mowy: podstawa transkrypcji, czasy i podział na zdania.",
                    "Każdy kanał (Ja / Rozmówcy) osobno, z VAD; język PL i EN, wygrywa wyższa pewność modelu.",
                    $"gotowe: {string.Join(", ", channels.Select(c => $"{c.Speaker} {c.Language.ToUpperInvariant()} ({c.Segments.Count} zdań)"))}",
                    string.Join("\n", channels.SelectMany(c => c.Segments).OrderBy(s => s.FromMs).Select(s => $"- {Time(s)} {s.Speaker}: {s.Text}")));

                // hipotezy B i C innymi modelami mowy (inne błędy) - głosy w dyskusji modeli

                hypotheses = [];
                heard = [];
                if (correct && mode == Mode.Chunk) (hypotheses, heard) = await HypothesesAsync(channels, folder, settings, vad, ffmpeg, work, log, ct);
                // 3. rozmówcy po stronie Teams (diaryzacja + weryfikacja zdań odciskiem głosu)
                var all = new List<TranscriptSegment>();
                foreach (var c in channels)
                    // kawałek: rozmówcy dopiero przy składaniu (jedna diaryzacja na całości = te same numery w całej rozmowie)
                    all.AddRange(c.Speaker == Others && c.Segments.Count > 0 && mode != Mode.Chunk ? await SplitOthersAsync(c.Segments, c.Wav, settings, log, ct) : c.Segments);

                // 4. echo, 5. pisownia ze słowniczka użytkownika, 6. korekta niepewnych zdań, 7. wypowiedzi, 8. tłumaczenie
                var withoutEcho = RemoveEcho(all, channelCount == 2 ? StereoLevels(wav) : null);
                if (all.Count - withoutEcho.Count is var echoes and > 0) log($"Removed microphone echo: {echoes} sentences.");
                segmentsFinal = withoutEcho;
                // profil głosu użytkownika: uczy się z mikrofonu, a mówca po stronie Teams o jego głosie (np. import wideo) dostaje etykietę "Ja"
                if (mode != Mode.Chunk)
                    segmentsFinal = await VoiceProfile.ApplyAsync(withoutEcho, channels.FirstOrDefault(c => c.Speaker == Me)?.Wav,
                        channels.FirstOrDefault(c => c.Speaker == Others)?.Wav, settings, log, ct);
                SaveState(folder, new RecognitionState(segmentsFinal, hypotheses, heard, glossary));
                // nowe rozpoznanie = stara korekta nieaktualna
                File.Delete(Path.Combine(folder, WorkNotes.Dir, LiveChunks.CorrectedFile));
            }
            else (segmentsFinal, hypotheses, heard, glossary) = (state.Segments, state.Hypotheses, state.Heard, state.Glossary);
            if (steps.HasFlag(Steps.Transcribe)) SaveStep(folder, Steps.Transcribe, watch, log);
            var writeNotes = llm && steps.HasFlag(Steps.Notes) && mode != Mode.Chunk;
            // notatka wstępna od razu z samego A (Whisper large-v3); drugi przebieg (B, C, dyskusja) potwierdza i nadpisuje ją dokładniejszą
            if (writeNotes && correct && hypotheses.Count == 0)
                await WriteNotesAsync(folder, segmentsFinal, hypotheses, settings, log, ct, "preliminary notes (Whisper large-v3 only)");
            // Correct albo Notes po samej transkrypcji: hipotezy B/C (i -tr dla korekty) dopiero teraz, słowniczek nazw od piszącego
            if ((correct || writeNotes) && hypotheses.Count == 0 && mode != Mode.Chunk)
            {
                var channels = await ChannelsAsync(folder, wav, sides, segmentsFinal, ffmpeg, temp, ct);
                if (correct)
                {
                    glossary = await TranscriptLlm.BuildGlossaryAsync(segmentsFinal, settings, ct);
                    log($"  step: glossary of names ({settings.CorrectionModel})");
                }
                await Ollama.UnloadAsync(settings, ct);
                (hypotheses, heard) = await HypothesesAsync(channels, folder, settings, vad, ffmpeg, work, log, ct, withEnglish: correct);
                // do surowego zapisu (segmentsFinal może być już poprawioną wersją z corrected.json)
                SaveState(folder, (LoadState(folder) ?? new RecognitionState(segmentsFinal, [], [], glossary)) with { Hypotheses = hypotheses, Heard = heard, Glossary = glossary });
            }
            // składanie: zdania już przedyskutowane w kawałkach (przeliczenie po objaśnieniach - dyskusja od nowa)
            if (correct && (mode != Mode.Assemble || fromState))
            {
                // LanguageTool tylko na czas dyskusji (Java ~1 GB RAM); bez niego dyskusja idzie na samych hipotezach
                LanguageTool? tool = null;
                if (LanguageTool.MissingFiles(settings) is { } missingLt) log($"LanguageTool skipped, not found: {missingLt}");
                else
                    try { tool = await LanguageTool.StartAsync(settings, ct); log("  step: LanguageTool start"); }
                    catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { log($"LanguageTool failed: {e.Message}"); }
                try
                {
                    // poprawki pisowni od użytkownika ("Jamentowy" = "diamentowy") przed dyskusją - modele widzą już poprawne słowo
                    segmentsFinal = kb.ApplyCorrections(segmentsFinal, out var kbFixed);
                    if (kbFixed > 0) log($"Knowledge base corrections applied: {kbFixed} times.");
                    var debate = await Debate.RunAsync(segmentsFinal, hypotheses, heard, tool, glossary, kb, settings, work, log, ct);
                    KnowledgeBase.SaveQuestions(folder, debate.Unclear);
                    if (debate.Unclear.Count > 0) log($"To clarify: {debate.Unclear.Count} unclear words ({string.Join(", ", debate.Unclear.Take(8).Select(x => x.Term))}) - see the panel.");
                    segmentsFinal = debate.Segments;
                    log($"Debate: agreed {debate.Agreed} of {debate.Disputed} disputed sentences, unresolved {debate.Unresolved}, unsure words marked: {debate.Marked}.");
                }
                finally
                {
                    if (tool != null) await tool.DisposeAsync();
                }
            }
            // poprawione zdania: podstawa kroków Translate i Notes (kawałek: do składania, bez hipotez)
            if (correct || mode == Mode.Chunk) SaveState(folder, new RecognitionState(segmentsFinal, mode == Mode.Chunk ? [] : hypotheses, heard, glossary), LiveChunks.CorrectedFile);
            if (correct && mode != Mode.Chunk) SaveStep(folder, Steps.Correct, watch, log);
            var turns = MergeTurns(segmentsFinal);
            var main = MainLanguage(turns);
            var translations = new string?[turns.Count];
            // kawałek: bez tłumaczenia (czas w trakcie rozmowy); tłumaczenie całości przy składaniu
            if (llm && steps.HasFlag(Steps.Translate) && turns.Count > 0 && mode != Mode.Chunk)
            {
                translations = await TranslateAsync(turns, heard, settings, ct);
                log($"  step: transcript translation ({turns.Count} turns: {settings.CheckModel} translates, {settings.CorrectionModel} checks)");
            }

            // sam krok Notes nie przepisuje transkrypcji (bez tłumaczenia w tym przebiegu zgubiłby je z transcript.txt)
            if (steps != Steps.Notes)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, TranscriptTxt), Render(turns, translations, main), Encoding.UTF8, ct);
                foreach (var language in Languages)
                    if (turns.Any(t => t.Language == language) || translations.Any(t => t != null))
                    {
                        var single = RenderSingle(turns, translations, language);
                        await File.WriteAllTextAsync(Path.Combine(folder, TranscriptFile(language)), single, Encoding.UTF8, ct);
                        await File.WriteAllTextAsync(Path.Combine(folder, $"transcript.{language}.md"), RenderMarkdown(single, Path.GetFileName(folder)), Encoding.UTF8, ct);
                    }
            }
            if (llm && steps.HasFlag(Steps.Translate) && mode != Mode.Chunk) SaveStep(folder, Steps.Translate, watch, log);

            // 9. notatka całości z 3 rozpoznań mowy (A Whisper large-v3, B turbo, C Qwen3-ASR), po polsku i po angielsku - notes.pl.md, notes.en.md
            if (writeNotes)
            {
                await WriteNotesAsync(folder, segmentsFinal, hypotheses, settings, log, ct, "meeting notes from 3 speech recognitions");
                SaveStep(folder, Steps.Notes, watch, log);
            }
            // notatka częściowa kawałka na żywo: dawny pipeline w głównym języku (Llama pisze, Qwen sprawdza)
            var mainFile = Path.Combine(folder, TranscriptFile(main));
            if (llm && mode == Mode.Chunk && turns.Count > 0 && File.Exists(mainFile))
            {
                var title = Path.GetFileName(folder);
                log($"Writing meeting notes ({main.ToUpperInvariant()})...");
                var notes = await MeetingNotes.WriteAsync(await File.ReadAllTextAsync(mainFile, ct), main, settings, work, ct, log, kb,
                    MeetingNotes.Partial);
                var rendered = MeetingNotes.Render(notes, main, title);
                await File.WriteAllTextAsync(Path.Combine(folder, MeetingNotes.FileName(main)), rendered, Encoding.UTF8, ct);
                partialNotes = rendered;
            }
            // następne nagranie z kolejki zaczyna od Whispera
            if (llm) await Ollama.UnloadAsync(settings, ct);
            return partialNotes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            throw new InvalidOperationException($"Ollama stopped responding ({e.Message}).", e);
        }
        finally
        {
            foreach (var file in temp) File.Delete(file);
        }
    }

    static async Task WriteNotesAsync(string folder, IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<HypothesisWord> hypotheses, Settings settings,
        Action<string> log, CancellationToken ct, string what)
    {
        log($"Writing {what} (PL + EN, {settings.NotesModel})...");
        foreach (var (language, text) in await SttNotes.WriteAsync(segments, hypotheses, Path.GetFileName(folder), settings, log, ct))
        {
            var file = Path.Combine(folder, MeetingNotes.FileName(language));
            SafeFile.Backup(file); // notatka mogła być poprawiona ręcznie - przeliczenie jej nie gubi
            await File.WriteAllTextAsync(file, text, Encoding.UTF8, ct);
        }
    }

    // Początki SampleWindows okien po SampleWindowSeconds (rosnąco, bez nakładania) z największą liczbą "głośnych" sekund (RMS powyżej ćwierci
    // typowej głośności mowy); wejście: wav 16-bit. Krótsze nagranie niż jedno okno = jedno okno od zera.
    // shortcut: głośność, nie detektor mowy - muzyka w tle też się liczy; przy rozmowie z długą muzyką rozważyć VAD whisper.cpp.
    public static List<double> SpeechWindows(string wav)
    {
        using var reader = new WaveFileReader(wav);
        var rms = new List<double>();
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        var samples = reader.ToSampleProvider();
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            rms.Add(Math.Sqrt(buffer.Take(read).Sum(x => (double)x * x) / read));
        if (rms.Count <= SampleWindowSeconds) return [0];
        var typical = rms.Order().ElementAt((int)(rms.Count * 0.9));
        var loud = rms.Select(x => x > Math.Max(0.003, typical * 0.25) ? 1 : 0).ToArray();
        var taken = new bool[loud.Length];
        var starts = new List<double>();
        for (var k = 0; k < SampleWindows; k++)
        {
            var best = (Start: -1, Count: -1);
            for (var start = 0; start + SampleWindowSeconds <= loud.Length; start++)
            {
                if (Enumerable.Range(start, SampleWindowSeconds).Any(i => taken[i])) continue;
                var count = loud.Skip(start).Take(SampleWindowSeconds).Sum();
                if (count > best.Count) best = (start, count);
            }
            if (best.Start < 0) break;
            for (var i = best.Start; i < best.Start + SampleWindowSeconds; i++) taken[i] = true;
            starts.Add(best.Start);
        }
        return starts.Order().ToList();
    }

    // Transkrypcja do czytania: niepewne słowa ??tak?? na czerwono (Markdown z HTML - VS Code, Obsidian, Typora).
    public static string RenderMarkdown(string transcript, string title) =>
        $"# {title}\n\n" + string.Concat(transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line.TrimEnd('\r').Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), @"\?\?([^?\s][^?]*?)\?\?", "<span style=\"color:#d32f2f\">??$1??</span>") + "\n\n"));
    // Na czym liczy whisper.cpp (z jego wyjścia): "using CUDA0 backend" = GPU; do logu (kolejka liczy jedno zadanie naraz).
    internal static readonly Dictionary<string, string> Devices = [];
    public static string Device(string output) =>
        System.Text.RegularExpressions.Regex.Match(output, @"using (CUDA\d+) backend|found GPU device \d+: (CUDA\d+)") is { Success: true } m
            ? $"GPU ({(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)})" : "CPU";

    internal static async Task<(List<TranscriptSegment> Segments, double Score, List<(long FromMs, long ToMs, string Text)> Words)> WhisperAsync(string input, string output, string speaker, Settings settings,
        bool vad, string language, string prompt, string folder, CancellationToken ct, string? model = null, bool translate = false)
    {
        try
        {
            Devices["Whisper"] = Device(await RunAsync(settings.WhisperCli, BuildWhisperArgs(settings, input, output, vad, language, prompt, model, translate), folder, ct));
            var json = await File.ReadAllTextAsync(output + ".json", ct);
            return (ParseWhisperJson(json, speaker).Segments, ScoreWhisperJson(json), ParseWhisperWords(json));
        }
        finally
        {
            File.Delete(output + ".json");
        }
    }

    // Średni logarytm prawdopodobieństwa tokenów tekstu (bez tokenów specjalnych "[_...]"); brak mowy = -inf.
    // Wbudowane wykrywanie języka Whispera myliło angielski z polskim akcentem (p = 0,54 dla "pl"); ta ocena: EN -0,24 vs PL -0,48.
    public static double ScoreWhisperJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var logs = doc.RootElement.GetProperty("transcription").EnumerateArray()
            .Where(s => s.TryGetProperty("tokens", out _))
            .SelectMany(s => s.GetProperty("tokens").EnumerateArray())
            .Where(t => !(t.GetProperty("text").GetString() ?? "").StartsWith("[_"))
            .Select(t => Math.Log(Math.Max(t.GetProperty("p").GetDouble(), 1e-6)))
            .ToList();
        return logs.Count == 0 ? double.NegativeInfinity : logs.Average();
    }

    // Rozmówca 1/2/3 po stronie Teams; brak modeli albo błąd = zostaje wspólna etykieta "Rozmówcy".
    static async Task<List<TranscriptSegment>> SplitOthersAsync(List<TranscriptSegment> side, string wav, Settings settings, Action<string> log, CancellationToken ct)
    {
        // pyannote community-1 na GPU (lepiej rozróżnia osoby); błąd = sherpa-onnx na CPU
        if (Diarizer.PyannoteAvailable(settings))
            try
            {
                var (result, speakers, device) = await Diarizer.RunPyannoteAsync(wav, side, settings, OtherPrefix, Path.GetDirectoryName(wav)!, ct);
                log($"Speakers on the Teams side: {result.Select(s => s.Speaker).Distinct().Count()} (pyannote community-1 found {speakers}, on {(device == "cuda" ? "GPU" : device.ToUpperInvariant())}).");
                return result;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                log($"pyannote diarization failed, using sherpa-onnx: {e.Message}");
            }
        if (Diarizer.MissingFiles(settings) is { } missing)
        {
            log($"Speaker diarization skipped, file not found: {missing}");
            return side;
        }
        try
        {
            var (result, corrected) = await Task.Run(() => Diarizer.Run(wav, side, settings, OtherPrefix), ct);
            log($"Speakers on the Teams side: {result.Select(s => s.Speaker).Distinct().Count()} (voice check reassigned {corrected} sentences).");
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Speaker diarization failed: {e.Message}");
            return side;
        }
    }

    // heard: Whisper -tr dla tej samej wypowiedzi (ten sam kanał, nachodzący czas) - podpowiedź sensu przy tłumaczeniu PL -> EN.
    static async Task<string?[]> TranslateAsync(List<TranscriptSegment> turns, List<(string Speaker, TranscriptSegment Segment)> heard, Settings settings, CancellationToken ct)
    {
        var result = new string?[turns.Count];
        foreach (var group in turns.Select((t, i) => (Turn: t, Index: i)).Where(x => Languages.Contains(x.Turn.Language)).GroupBy(x => x.Turn.Language))
        {
            var items = group.ToList();
            var hints = items.Select(x => x.Turn.Language != "pl" ? null : string.Join(' ', heard.Where(h => h.Speaker == (x.Turn.Speaker == Me ? Me : Others)
                && h.Segment.FromMs < x.Turn.ToMs && x.Turn.FromMs < h.Segment.ToMs).Select(h => h.Segment.Text)) is { Length: > 0 } hint ? hint : null).ToList();
            var translated = await Translator.TranslateAsync(items.Select(x => x.Turn.Text).ToList(), group.Key, Translator.Other(group.Key), settings, ct, hints);
            for (var i = 0; i < items.Count; i++) result[items[i].Index] = translated[i];
        }
        return result;
    }

    // --carry-initial-prompt: bez tego prompt działa tylko na pierwsze 30 s nagrania.
    // translate: Whisper tłumaczy mowę od razu na angielski (-tr) - niezależna od tekstu PL kontrola sensu.
    // bez -np: wycisza też "found GPU device 0: CUDA0" i log pisał "Whisper on CPU" przy liczeniu na GPU
    // Domyślny prompt z poprawną interpunkcją: bez niego Whisper na trudnym dźwięku (szybka mowa, początek w pół zdania) przechodzi w tryb "napisów" -
    // same małe litery, prawie bez interpunkcji, powtórzone fragmenty (test 35 min po angielsku: 0 wielkich liter, 130 powtórzeń; z promptem: wielkie litery,
    // interpunkcja, "PDMS", "Excel"). Prompt trafia do każdego okna (--carry-initial-prompt).
    static string DefaultPrompt(string language) => language switch
    {
        "pl" => "Dzień dobry, witam na spotkaniu. Zaczynamy.",
        "en" => "Hello, welcome to the meeting. Let's get started.",
        _ => "",
    };

    public static string BuildWhisperArgs(Settings s, string input, string outputBase, bool vad, string language, string prompt = "", string? model = null, bool translate = false) =>
        BuildWhisperArgsCore(s, input, outputBase, vad, language, prompt.Length > 0 || translate ? prompt : DefaultPrompt(language), model, translate);

    static string BuildWhisperArgsCore(Settings s, string input, string outputBase, bool vad, string language, string prompt, string? model, bool translate) =>
        $"-m \"{model ?? s.WhisperModel}\" -l {language} -f \"{input}\" -sns -ml 80 -sow -ojf -of \"{outputBase}\"" +
        (vad ? $" --vad -vm \"{s.WhisperVadModel}\"" : "") +
        (prompt.Length > 0 ? $" --prompt \"{prompt}\" --carry-initial-prompt" : "") + (translate ? " -tr" : "");

    public static (List<TranscriptSegment> Segments, string Language) ParseWhisperJson(string json, string speaker)
    {
        using var doc = JsonDocument.Parse(json);
        var language = doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
        var segments = doc.RootElement.GetProperty("transcription").EnumerateArray()
            .Select(t => new TranscriptSegment(
                t.GetProperty("offsets").GetProperty("from").GetInt64(),
                t.GetProperty("offsets").GetProperty("to").GetInt64(),
                speaker,
                t.GetProperty("text").GetString()!.Trim(),
                language,
                MinTokenP(t)))
            .Where(t => t.Text.Length > 0)
            .ToList();
        return (segments, language);
    }

    // Słowa z czasami do przycinania hipotezy B do zdań. Czasy z segmentów, rozłożone wg pozycji słowa w tekście: offsety tokenów
    // przy VAD nie są przeliczane na czas nagrania (nagranie Heroes: przesunięcie ~20 s), offsety segmentów tak.
    public static List<(long FromMs, long ToMs, string Text)> ParseWhisperWords(string json)
    {
        var words = new List<(long FromMs, long ToMs, string Text)>();
        foreach (var segment in ParseWhisperJson(json, "").Segments)
        {
            var text = segment.Text;
            var length = Math.Max(1, text.Length);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\S+"))
                words.Add((segment.FromMs + (segment.ToMs - segment.FromMs) * m.Index / length,
                           segment.FromMs + (segment.ToMs - segment.FromMs) * (m.Index + m.Length) / length, m.Value));
        }
        return words;
    }
    static double MinTokenP(JsonElement segment) =>
        segment.TryGetProperty("tokens", out var tokens)
            ? tokens.EnumerateArray().Where(t => !(t.GetProperty("text").GetString() ?? "").StartsWith("[_")).Select(t => t.GetProperty("p").GetDouble()).DefaultIfEmpty(1).Min()
            : 1;

    // Echo: mikrofon bez słuchawek łapie głośniki, więc "Ja" powtarza w tym samym czasie słowa rozmówcy (sprawdzone na żywo).
    // Wypowiedź "Ja", której >= 60% słów występuje w nakładającej się wypowiedzi po stronie Teams, jest usuwana.
    public static List<TranscriptSegment> RemoveEcho(IReadOnlyList<TranscriptSegment> segments, Func<long, long, (double MicDb, double TeamsDb)>? levels = null)
    {
        var others = segments.Where(s => s.Speaker != Me).ToList();
        return segments.Where(s => s.Speaker != Me || !(others.Any(o =>
            o.FromMs < s.ToMs + 1000 && s.FromMs < o.ToMs + 1000 && WordOverlap(s.Text, o.Text) >= 0.6) || IsMutedEcho(s, others, levels))).ToList();
    }

    // Przebicie z głośników zniekształcone przez przetwarzanie mikrofonu (Realtek): tekst tylko częściowo zgodny ("Kto ma te manek?"
    // przy "skoro ma te many"), a korelacja z sygnałem Teams znika. Echo = rozmówcy mówią w tym czasie, mikrofon cichszy od Teams
    // (własny głos przy mikrofonie jest głośniejszy niż echo) i część słów z okna +-2 s. Wejście w słowo przy cichym mówieniu zostaje,
    // jeśli nie powtarza słów rozmówców. Bez warunku pokrycia segmentami rozmówców: Whisper pomija część ich mowy (gra, 36-41 s).
    const double EchoMinOverlap = 0.3, EchoQuieterDb = 3;

    static bool IsMutedEcho(TranscriptSegment s, List<TranscriptSegment> others, Func<long, long, (double MicDb, double TeamsDb)>? levels)
    {
        if (levels == null || s.ToMs <= s.FromMs) return false;
        var (mic, teams) = levels(s.FromMs, s.ToMs);
        if (mic > teams - EchoQuieterDb) return false;
        var nearby = string.Join(" ", others.Where(o => o.FromMs < s.ToMs + 2000 && s.FromMs < o.ToMs + 2000).Select(o => o.Text));
        return WordOverlap(s.Text, nearby) >= EchoMinOverlap;
    }

    // Poziom RMS (dB) obu kanałów transcript.wav w zadanym czasie: lewy = mikrofon, prawy = Teams.
    public static Func<long, long, (double MicDb, double TeamsDb)> StereoLevels(string wav)
    {
        // czytany tylko fragment segmentu - godzina stereo w pamięci to ok. 230 MB
        return (from, to) =>
        {
            using var reader = new WaveFileReader(wav);
            var rate = reader.WaveFormat.SampleRate;
            reader.Position = Math.Min(reader.Length, from * rate / 1000 * reader.WaveFormat.BlockAlign);
            var samples = new float[(int)Math.Max(0, (to - from) * rate / 1000) * 2];
            var read = reader.ToSampleProvider().Read(samples, 0, samples.Length) / 2;
            double mic = 0, teams = 0;
            for (var i = 0; i < read; i++) { mic += samples[2 * i] * samples[2 * i]; teams += samples[2 * i + 1] * samples[2 * i + 1]; }
            static double Db(double energy, int count) => 10 * Math.Log10(Math.Max(1e-12, energy / Math.Max(1, count)));
            return (Db(mic, read), Db(teams, read));
        };
    }

    static double WordOverlap(string echo, string original)
    {
        var words = Words(echo);
        if (words.Count == 0) return 0;
        var source = Words(original).ToHashSet();
        return words.Count(source.Contains) / (double)words.Count;
    }

    static List<string> Words(string text) =>
        text.Split((char[])[' ', ',', '.', '?', '!', ';', ':', '-', '"'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();

    // Kolejne segmenty tego samego mówcy i języka (przerwa <= 2 s) = jedna wypowiedź; ciągły monolog tnie się na akapity (po zdaniu od TurnSoftChars,
    // najpóźniej przy TurnHardChars), inaczej jedna "wypowiedź" trwałaby minuty.
    const int TurnSoftChars = 400, TurnHardChars = 900;

    public static List<TranscriptSegment> MergeTurns(IEnumerable<TranscriptSegment> segments)
    {
        var turns = new List<TranscriptSegment>();
        foreach (var s in segments.OrderBy(s => s.FromMs))
        {
            if (turns.Count > 0 && turns[^1] is var last && last.Speaker == s.Speaker && last.Language == s.Language && s.FromMs - last.ToMs <= JoinGapMs
                && last.Text.Length < TurnHardChars && !(last.Text.Length >= TurnSoftChars && last.Text.TrimEnd() is [.., '.' or '?' or '!' or '…']))
                turns[^1] = last with { ToMs = s.ToMs, Text = last.Text + " " + s.Text, MinP = Math.Min(last.MinP, s.MinP) };
            else
                turns.Add(s);
        }
        return turns;
    }

    // Główny język rozmowy = ten, w którym powiedziano najwięcej.
    public static string MainLanguage(IEnumerable<TranscriptSegment> turns) =>
        turns.GroupBy(t => t.Language).OrderByDescending(g => g.Sum(t => t.Text.Length)).Select(g => g.Key).FirstOrDefault() ?? "";

    internal static string Time(TranscriptSegment t) => $"[{TimeSpan.FromMilliseconds(t.FromMs):h\\:mm\\:ss}]";

    // Zbiorczy plik: wypowiedź = tłumaczenie, pod nim oryginał (oba z oznaczeniem języka); bez tłumaczenia sam oryginał.
    public static string Render(IReadOnlyList<TranscriptSegment> turns, IReadOnlyList<string?> translations, string mainLanguage)
    {
        var text = new StringBuilder();
        if (Languages.Contains(mainLanguage))
            text.AppendLine($"Main language: {Translator.Name(mainLanguage)}{(translations.Any(t => t != null) ? $" · translation: {Translator.Name(Translator.Other(mainLanguage))}" : "")}").AppendLine();
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            if (translations[i] is { } translation && Languages.Contains(t.Language))
                text.AppendLine($"{Time(t)} {t.Speaker} ({Translator.Other(t.Language).ToUpperInvariant()}): {translation}");
            var tag = Languages.Contains(t.Language) ? $" ({t.Language.ToUpperInvariant()})" : "";
            text.AppendLine($"{Time(t)} {t.Speaker}{tag}: {t.Text}");
            if (translations[i] != null) text.AppendLine();
        }
        return text.ToString();
    }

    // Plik w jednym języku: oryginał, jeśli wypowiedź była w tym języku, inaczej tłumaczenie (brak tłumaczenia = oryginał z oznaczeniem).
    // W angielskim pliku etykiety po angielsku (Me / Speaker N / Others).
    public static string RenderSingle(IReadOnlyList<TranscriptSegment> turns, IReadOnlyList<string?> translations, string language)
    {
        var text = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var line = t.Language == language ? t.Text : translations[i] ?? $"({t.Language.ToUpperInvariant()}) {t.Text}";
            text.AppendLine($"{Time(t)} {Label(t.Speaker, language)}: {line}");
        }
        return text.ToString();
    }

    public static string Label(string speaker, string language) => language != "en" ? speaker : speaker switch
    {
        Me => "Me",
        Others => "Others",
        _ when speaker.StartsWith(OtherPrefix + " ") => "Speaker " + speaker[(OtherPrefix.Length + 1)..],
        _ => speaker,
    };

    // Zwraca wyjście procesu (stdout + stderr; whisper-cli pisze backend na stderr).
    // withErrors: stdout + stderr (whisper-cli pisze backend - "using CUDA0 backend" - na stderr; bez tego log mówił "Whisper on CPU")
    internal static async Task<string> RunAsync(string exe, string args, string workingDirectory, CancellationToken ct, bool withErrors = false)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // narzędzia piszą w UTF-8; domyślne kodowanie konsoli dawało "â–siÄ™" (parakeet-cli)
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        })!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var errors = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // zamknięcie aplikacji w trakcie transkrypcji
            throw;
        }
        var stdout = await output;
        if (p.ExitCode != 0)
        {
            var lastLines = string.Join(Environment.NewLine, (await errors).Trim().Split('\n').TakeLast(3));
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {lastLines}");
        }
        return stdout + "\n" + await errors;
    }
}
