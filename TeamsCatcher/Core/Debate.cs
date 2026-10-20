using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Słowo z hipotezy B/C z czasem - do przycięcia hipotezy dokładnie do zdania z A.
public sealed record HypothesisWord(string Source, string Speaker, long FromMs, long ToMs, string Text);

// Dyskusja modeli o spornych zdaniach, aż się zgodzą (najwyżej DebateRounds rund):
// głosy: 3 rozpoznawania mowy (A = Whisper large-v3, B = Whisper turbo, C = Parakeet) + LanguageTool (pisownia, gramatyka)
// + 2 modele tekstu: CorrectionModel (Llama) proponuje i odpowiada, CheckModel (Qwen) recenzuje.
// Jednostka = całe zdanie (segmenty Whispera tną zdania w połowie - B i C rozjeżdżały się z fragmentami).
// Sporne = rozpoznawania się różnią albo LanguageTool zgłasza błąd. Jedno zdanie = jedno zapytanie (w paczkach 4B model
// przesuwał numerację), a modele ładowane po kolei: najpierw wszystkie propozycje, potem wszystkie recenzje.
// Brak zgody = słowa, w których się różnią, oznaczone ??tak??. Każda wersja przechodzi przez SafeCorrection (tylko podmiana słów).
public static class Debate
{
    // przerwa dłuższa = nowe zdanie, nawet bez kropki
    const long SentenceGapMs = 1500;
    // słowo hipotezy B/C należy do zdania, gdy jego środek leży w czasie zdania (+ margines na różnice w czasach modeli)
    const long WordMarginMs = 300;
    // więcej pytań na jedno nagranie to już nie pomoc, tylko praca dla użytkownika
    const int MaxQuestions = 30;
    // baza wiedzy bieżącej transkrypcji (jedna transkrypcja naraz - kolejka w CatcherService)
    static KnowledgeBase _kb = new();

    // Unclear: słowa do objaśnienia przez użytkownika (niepewne ??tak?? i nieznane słownikowi, których nie ma w bazie wiedzy).
    public sealed record Result(List<TranscriptSegment> Segments, int Disputed, int Agreed, int Unresolved, int Marked, string Topic, List<UnclearTerm> Unclear);

    sealed class Unit(List<int> segments, string speaker, string language, long fromMs, long toMs, string text)
    {
        public List<int> Segments = segments;
        public string Speaker = speaker, Language = language, Original = text, Proposal = text;
        public long FromMs = fromMs, ToMs = toMs;
        public string?[] Alternatives = [];
        public List<LanguageIssue> Issues = [];
        public string? Review, Final;
        // niezależne tłumaczenie mowy na angielski (Whisper -tr) dla tego zdania - podpowiedź sensu
        public string? Meaning;
        public string AllAlternatives => string.Join(' ', Alternatives.Where(a => a != null));
    }

    // heard: zdania z Whispera -tr (mowa od razu po angielsku) z kanałem (Ja / Rozmówcy).
    public static async Task<Result> RunAsync(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<HypothesisWord> words,
        IReadOnlyList<(string Speaker, TranscriptSegment Segment)> heard, LanguageTool? tool, string glossary, KnowledgeBase kb, Settings s, WorkNotes notes,
        Action<string> log, CancellationToken ct)
    {
        _kb = kb;
        var result = segments.ToList();
        var units = Units(result);
        var sources = words.Select(w => w.Source).Distinct().Order().ToList();
        foreach (var u in units) u.Alternatives = sources.Select(src => Cut(words, src, u)).ToArray();

        // 1. LanguageTool: pisownia i gramatyka każdego zdania
        if (tool != null)
            foreach (var u in units)
                u.Issues = (await tool.CheckAsync(u.Original, u.Language, ct)).Where(x => x.Relevant).ToList();
        log($"  step: LanguageTool check ({units.Count} sentences)");
        notes.Write("06-languagetool.md", "LanguageTool (pisownia i gramatyka)", "Wskazuje nieistniejące słowa i błędy gramatyczne w zdaniach hipotezy A.",
            "Sprawdź każde zdanie słownikiem i regułami języka (pl-PL / en-US). Zgłoś nieistniejące słowa (TYPOS) i błędy gramatyczne (GRAMMAR, SYNTAX).",
            tool == null ? "pominięty (brak Javy albo LanguageTool)" : $"gotowe: {units.Sum(u => u.Issues.Count)} uwag w {units.Count(u => u.Issues.Count > 0)} z {units.Count} zdań",
            string.Join("\n", units.Where(u => u.Issues.Count > 0).Select(u => $"- {Transcriber.Time(result[u.Segments[0]])} {u.Original}\n  " +
                string.Join("; ", u.Issues.Select(x => $"\"{x.Word}\": {x.Message}")))));

        // 2. kontrola sensu i sporne zdania
        AttachEnglish(units, heard, notes);
        // C (Parakeet, najsłabszy) różni się prawie zawsze - sama ta różnica nie wywołuje sporu, zostaje dowodem (TVN: 16/16 zdań spornych)
        var disputed = units.Where(u => u.Issues.Count > 0 || (u.Alternatives.FirstOrDefault() is { } b && !SameWords(b, u.Original))).ToList();
        var topic = await TranscriptLlm.TopicAsync(result, s, ct);
        log($"Topic: {topic}");
        log($"Debate: {disputed.Count} disputed sentences of {units.Count}.");

        // 3. propozycje (model korekty)
        foreach (var u in disputed)
        {
            u.Proposal = Filter(u, await Ask(ProposePrompt(u, units, topic, glossary), s.CorrectionModel, s, ct)) ?? u.Original;
            // bez zmiany i bez sygnału błędu (LanguageTool) nie ma czego recenzować - A zostaje
            if (u.Proposal == u.Original && u.Issues.Count == 0) u.Final = u.Original;
        }
        WriteModelNotes(notes, s, disputed, $"runda 1/{s.DebateRounds}: propozycje gotowe");
        log($"  step: debate proposals ({disputed.Count} sentences, Llama)");

        // 4. recenzja (model kontrolny) <-> odpowiedź (model korekty), aż się zgodzą
        for (var round = 1; round <= s.DebateRounds; round++)
        {
            foreach (var u in disputed.Where(u => u.Final == null))
            {
                var answer = await Ask(ReviewPrompt(u, units, topic), s.CheckModel, s, ct);
                if (IsOk(answer)) u.Final = u.Proposal;
                else
                {
                    u.Review = Filter(u, answer) ?? u.Proposal;
                    if (u.Review == u.Proposal) u.Final = u.Proposal;
                }
            }
            var open = disputed.Where(u => u.Final == null).ToList();
            WriteModelNotes(notes, s, disputed, $"runda {round}/{s.DebateRounds}: recenzja gotowa, otwartych {open.Count}");
            log($"  step: debate round {round} review ({s.CheckModel}), still open {open.Count}");
            if (open.Count == 0 || round == s.DebateRounds) break;
            foreach (var u in open)
            {
                var answer = await Ask(RespondPrompt(u, units, topic), s.CorrectionModel, s, ct);
                if (IsOk(answer)) u.Final = u.Review;
                else
                {
                    u.Proposal = Filter(u, answer) ?? u.Proposal;
                    if (u.Proposal == u.Review) u.Final = u.Proposal;
                }
            }
            WriteModelNotes(notes, s, disputed, $"runda {round}/{s.DebateRounds}: odpowiedź gotowa, otwartych {disputed.Count(u => u.Final == null)}");
            log($"  step: debate round {round} answer ({open.Count} sentences, Llama)");
        }

        // 5. brak zgody: słowa, w których się różnią, oznaczone ??tak??
        var unresolved = disputed.Where(u => u.Final == null).ToList();
        foreach (var u in unresolved) u.Final = MarkDisagreement(u.Proposal, u.Review ?? u.Proposal);

        // 6. końcowa pisownia: nieistniejące słowo, którego nie potwierdziło inne rozpoznawanie = ??tak??;
        //    nieznane słownikowi i bazie wiedzy (też potwierdzone, np. slang "armatrap") = pytanie do użytkownika
        var unclear = new List<UnclearTerm>();
        foreach (var u in units)
        {
            var text = u.Final ?? u.Original;
            if (tool != null)
            {
                // słowa ze słowniczka (nazwiska) i bazy wiedzy nie są w słowniku LanguageTool, ale są pewne - TVN: "??Kawulski??"
                var trusted = (glossary.Replace(',', ' ')).Split(' ').Select(TranscriptLlm.Bare).ToHashSet();
                var confirmed = u.AllAlternatives.Split(' ').Select(TranscriptLlm.Bare).ToHashSet();
                var spelling = (await tool.CheckAsync(text, u.Language, ct))
                    .Where(x => x.Spelling && x.Word.Length > 0 && !trusted.Contains(TranscriptLlm.Bare(x.Word)) && !kb.Knows(x.Word)).ToList();
                foreach (var x in spelling.Where(x => confirmed.Contains(TranscriptLlm.Bare(x.Word))))
                    unclear.Add(new UnclearTerm(x.Word, text.Replace("??", ""), Transcriber.Time(result[u.Segments[0]]), u.Language == "pl" ? "nieznane słowo" : "unknown word"));
                var typos = spelling.Where(x => !confirmed.Contains(TranscriptLlm.Bare(x.Word))).Select(x => TranscriptLlm.Bare(x.Word)).ToHashSet();
                // nieistniejące słowo: zgadnięte przez modele ("moralówką" -> "moralow") wraca do oryginału; zostaje oznaczone ??tak??
                var final = text.Split(' ');
                var original = u.Original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var aligned = TranscriptLlm.AlignWords(original, final).ToDictionary(p => p.B, p => p.A);
                for (var w = 0; w < final.Length; w++)
                    if (!final[w].Contains("??") && typos.Contains(TranscriptLlm.Bare(final[w])))
                        final[w] = TranscriptLlm.Mark(aligned.TryGetValue(w, out var o) ? original[o] : final[w]);
                text = string.Join(' ', final);
            }
            if (text != u.Original) Distribute(result, u.Segments, text);
            foreach (Match m in Regex.Matches(text, @"\?\?([^?\s]+)\?\?"))
                if (!kb.Knows(m.Groups[1].Value))
                    unclear.Add(new UnclearTerm(m.Groups[1].Value, text.Replace("??", ""), Transcriber.Time(result[u.Segments[0]]), u.Language == "pl" ? "niepewne rozpoznanie" : "unsure recognition"));
        }
        // odmiany tego samego słowa ("armatrap" / "armatrapa", "kąsach" / "kąsy") = jedno pytanie
        var distinct = new List<UnclearTerm>();
        foreach (var x in unclear)
            if (!distinct.Any(d => KnowledgeBase.SameWord(TranscriptLlm.Bare(d.Term), TranscriptLlm.Bare(x.Term)))) distinct.Add(x);
        unclear = distinct.Take(MaxQuestions).ToList();
        log("  step: final spelling check (LanguageTool)");
        var marked = result.Sum(x => Regex.Matches(x.Text, @"\?\?[^?\s]+\?\?").Count);
        WriteModelNotes(notes, s, disputed, $"gotowe: zgoda w {disputed.Count - unresolved.Count} z {disputed.Count} spornych zdań, bez zgody {unresolved.Count}, oznaczonych słów {marked}");
        return new Result(result, disputed.Count, disputed.Count - unresolved.Count, unresolved.Count, marked, topic, unclear);
    }

    // Sens: Whisper -tr tłumaczy mowę na angielski niezależnie od tekstu PL - podpowiedź w dyskusji (model czyta angielski bezpośrednio).
    // Wcześniej: tłumaczenie z powrotem na PL + ocena "ten sam sens?" przez Gemmę jako wyzwalacz sporu. Pomiar na 9,6 min: "inny sens"
    // w 91 ze 111 zdań (szum małego modelu), 2,5 min pracy i prawie wszystko w dyskusji - dlatego tylko podpowiedź, bez wyroku.
    static void AttachEnglish(List<Unit> units, IReadOnlyList<(string Speaker, TranscriptSegment Segment)> heard, WorkNotes notes)
    {
        var report = new StringBuilder();
        foreach (var u in units.Where(u => u.Language == "pl"))
        {
            var side = u.Speaker == Transcriber.Me ? Transcriber.Me : Transcriber.Others;
            var english = string.Join(' ', heard.Where(h => h.Speaker == side && h.Segment.FromMs < u.ToMs && u.FromMs < h.Segment.ToMs).Select(h => h.Segment.Text));
            // segment Whispera dużo dłuższy niż zdanie = nie wiadomo, która część dotyczy zdania
            if (english.Length == 0 || english.Length > 2 * u.Original.Length + 40) continue;
            u.Meaning = english;
            report.Append($"- {u.Original}\n  EN (Whisper): {english}\n");
        }
        notes.Write("05-meaning-check.md", "Sens z mowy po angielsku (Whisper -tr)",
            "Niezależne tłumaczenie mowy na angielski przy każdym zdaniu PL - podpowiedź sensu dla modeli w dyskusji.",
            "Dopasuj tłumaczenie mowy (Whisper -tr) do zdań PL po czasie; pokaż je modelom w dyskusji obok hipotez A/B/C.",
            $"gotowe: podpowiedź przy {units.Count(u => u.Meaning != null)} zdaniach", report.ToString());
    }
    // Kolejne segmenty tego samego mówcy i języka aż do końca zdania (.?!) albo przerwy.
    static List<Unit> Units(List<TranscriptSegment> segments)
    {
        var units = new List<Unit>();
        var order = Enumerable.Range(0, segments.Count).Where(i => Transcriber.Languages.Contains(segments[i].Language)).OrderBy(i => segments[i].FromMs).ToList();
        var current = new List<int>();
        for (var k = 0; k < order.Count; k++)
        {
            var seg = segments[order[k]];
            current.Add(order[k]);
            var next = k + 1 < order.Count ? segments[order[k + 1]] : null;
            if (next != null && next.Speaker == seg.Speaker && next.Language == seg.Language && next.FromMs - seg.ToMs <= SentenceGapMs
                && seg.Text.TrimEnd() is not [.., '.' or '?' or '!' or '…']) continue;
            var first = segments[current[0]];
            units.Add(new Unit(current, first.Speaker, first.Language, first.FromMs, seg.ToMs, string.Join(' ', current.Select(i => segments[i].Text))));
            current = [];
        }
        return units;
    }

    // Słowa hipotezy z tego samego kanału (Ja / Rozmówcy), których środek leży w czasie zdania.
    static string? Cut(IReadOnlyList<HypothesisWord> words, string source, Unit u)
    {
        var side = u.Speaker == Transcriber.Me ? Transcriber.Me : Transcriber.Others;
        var text = string.Join(' ', words.Where(w => w.Source == source && w.Speaker == side
            && (w.FromMs + w.ToMs) / 2 >= u.FromMs - WordMarginMs && (w.FromMs + w.ToMs) / 2 <= u.ToMs + WordMarginMs).Select(w => w.Text));
        return text.Length > 0 ? text : null;
    }

    // Poprawione zdanie z powrotem do segmentów (ta sama liczba słów - SafeCorrection): każdy segment dostaje swoją liczbę słów.
    static void Distribute(List<TranscriptSegment> result, List<int> segments, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        foreach (var i in segments)
        {
            var count = result[i].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (i == segments[^1]) count = words.Length - at;
            result[i] = result[i] with { Text = string.Join(' ', words.Skip(at).Take(count)) };
            at += count;
        }
    }

    static async Task<string> Ask(string prompt, string model, Settings s, CancellationToken ct) =>
        CleanAnswer(await Ollama.GenerateAsync(prompt, model, s, ct, 4096, 300));

    // Małe modele dodają Markdown, cudzysłowy i komentarze: liczy się pierwsza niepusta linia.
    public static string CleanAnswer(string answer)
    {
        var line = answer.Replace("*", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Regex.Replace(line, @"^(P|R|Answer|Odpowiedź)\s*:\s*", "", RegexOptions.IgnoreCase).Trim('"', '„', '”', ' ');
    }

    static string? Filter(Unit u, string answer) => TranscriptLlm.SafeCorrection(u.Original, answer, u.Alternatives);

    static bool IsOk(string answer) => Regex.IsMatch(answer, @"^\W*OK\W*$", RegexOptions.IgnoreCase);

    static bool SameWords(string a, string b) =>
        a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TranscriptLlm.Bare).Where(w => w.Length > 0)
            .SequenceEqual(b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TranscriptLlm.Bare).Where(w => w.Length > 0));

    // Słowa, w których propozycja i recenzja się różnią (ta sama liczba słów - obie po SafeCorrection) -> ??propozycja??.
    public static string MarkDisagreement(string proposal, string review)
    {
        var a = proposal.Split(' ');
        var b = review.Split(' ');
        if (a.Length != b.Length) return proposal;
        return string.Join(' ', a.Zip(b, (x, y) => TranscriptLlm.Bare(x) == TranscriptLlm.Bare(y) ? x : TranscriptLlm.Mark(x)));
    }

    // Dyskusja zawsze w języku zdania (polskie zdanie = polskie polecenia), z bazą wiedzy użytkownika zamiast zgadywania znaczeń.
    static string L(Unit u, string pl, string en) => u.Language == "pl" ? pl : en;

    // Nieistniejące słowo A i co w tym samym miejscu zdania usłyszały B i C - wprost, bo Llama zostawiła "jamentowym" jako "znany termin".
    static IEnumerable<string> UnknownWordHints(Unit u)
    {
        var a = u.Original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var issue in u.Issues.Where(x => x.Spelling && x.Word.Length > 0 && !_kb.Knows(x.Word)))
        {
            var index = Array.FindIndex(a, w => TranscriptLlm.Bare(w) == TranscriptLlm.Bare(issue.Word));
            if (index < 0) continue;
            var heard = new List<string>();
            for (var k = 0; k < u.Alternatives.Length; k++)
            {
                if (u.Alternatives[k] is not { } alt) continue;
                var words = alt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pair = TranscriptLlm.AlignWords(a, words).FirstOrDefault(p => p.A == index, (A: -1, B: -1));
                if (pair.B >= 0) heard.Add(L(u, $"{(char)('B' + k)} usłyszał \"{words[pair.B]}\"", $"{(char)('B' + k)} heard \"{words[pair.B]}\""));
            }
            yield return L(u, $"Słowo \"{issue.Word}\" w A nie istnieje", $"Word \"{issue.Word}\" in A does not exist")
                + (heard.Count > 0 ? L(u, "; w tym samym miejscu ", "; at the same place ") + string.Join(", ", heard) + "." : ".");
        }
    }

    static string Context(Unit u, List<Unit> units)
    {
        var i = units.IndexOf(u);
        return (i > 0 ? L(u, "Poprzednie zdanie: ", "Previous sentence: ") + units[i - 1].Original + "\n" : "")
            + (i + 1 < units.Count ? L(u, "Następne zdanie: ", "Next sentence: ") + units[i + 1].Original + "\n" : "");
    }

    static string Evidence(Unit u)
    {
        var text = new StringBuilder($"A: {u.Original}\n");
        for (var k = 0; k < u.Alternatives.Length; k++) text.Append($"{(char)('B' + k)}: {u.Alternatives[k] ?? L(u, "(nic nie usłyszał)", "(nothing heard)")}\n");
        // tylko porównywalny fragment: dłuższy segment Whispera -tr był szumem, a jego "Jament Golem" utwierdził model w błędzie
        if (u.Meaning != null && u.Meaning.Length <= 2 * u.Original.Length + 40)
            text.Append(L(u, "Niezależne tłumaczenie tej samej mowy na angielski: ", "Independent English translation of the same speech: ") + u.Meaning + "\n");
        if (u.Issues.Count > 0)
            text.Append(L(u, "Sprawdzenie językowe A: ", "Language check of A: ") + string.Join("; ", u.Issues.Select(x => $"\"{x.Word}\" - {Short(x.Message)}")) + "\n");
        foreach (var hint in UnknownWordHints(u)) text.Append(hint + "\n");
        return text.ToString();
    }

    static string Short(string message) => message.Length > 90 ? message[..90] + "..." : message;

    static string Header(Unit u, List<Unit> units, string topic, string glossary = "")
    {
        var i = units.IndexOf(u);
        var known = _kb.Relevant(string.Join(' ', units.Skip(Math.Max(0, i - 1)).Take(3).Select(x => x.Original)));
        return L(u,
                "Sprawdzasz automatyczną transkrypcję rozmowy po polsku. A, B i C to to samo zdanie z trzech niezależnych rozpoznawań mowy " +
                "(A jest główne, C najsłabsze). Sprawdzenie językowe wskazuje słowa w A, które nie istnieją, i błędy gramatyczne. " +
                "Oceniasz, czy zdanie jest poprawne językowo, logiczne i spójne z rozmową. Zasady: zachowaj słowa i szyk A; zamień pojedyncze słowo " +
                "tylko na słowo usłyszane przez B lub C albo na podobnie brzmiące prawdziwe słowo, gdy słowo z A nie istnieje lub nie ma sensu " +
                "w kontekście. Nie dodawaj i nie usuwaj słów. Nie zgaduj znaczeń skrótów ani nazw - słowo, którego nadal nie jesteś pewien, " +
                "zapisz jako ??słowo??.\n",
                "You check an automatic transcript of an English conversation. A, B and C are the same sentence from three independent " +
                "speech recognizers (A is the main one, C is the weakest). Language check lists words in A that do not exist and grammar errors. " +
                "You judge whether the sentence is linguistically correct, logical and consistent with the conversation. Rules: keep A's words " +
                "and word order; replace a single word only with a word heard by B or C, or with a similar-sounding real word when A's word does " +
                "not exist or makes no sense in context. Do not add or remove words. Do not guess abbreviations or names - write a word you are " +
                "still not sure about as ??word??.\n")
            + (topic.Length > 0 ? L(u, "Temat rozmowy: ", "Conversation topic: ") + topic + "\n" : "")
            + (glossary.Length > 0 ? L(u, "Znane nazwy: ", "Known names: ") + glossary + "\n" : "")
            + (known.Count > 0 ? L(u, "Objaśnienia użytkownika: ", "User's explanations: ") + KnowledgeBase.Describe(known) + "\n" : "");
    }

    static string ProposePrompt(Unit u, List<Unit> units, string topic, string glossary) =>
        Header(u, units, topic, glossary) + Context(u, units) + "\n" + Evidence(u) +
        L(u, "\nNapisz zdanie, które mówca najpewniej powiedział. Odpowiedz tylko tym jednym zdaniem.",
             "\nWrite the sentence the speaker most likely said. Answer with that one sentence only.");

    static string ReviewPrompt(Unit u, List<Unit> units, string topic) =>
        Header(u, units, topic) + Context(u, units) + "\n" + Evidence(u) + $"P: {u.Proposal}\n" +
        L(u, "\nP to proponowane zdanie końcowe. Sprawdź, czy P składa się z prawdziwych słów, jest poprawne gramatycznie, logiczne " +
             "i spójne z rozmową. Odpowiedz OK, jeśli P jest poprawne, w przeciwnym razie odpowiedz tylko poprawionym zdaniem.",
             "\nP is a proposed final sentence. Check that P uses real words, is grammatically correct, logical and consistent with the " +
             "conversation. Answer OK if P is correct, otherwise answer with your corrected sentence only.");

    static string RespondPrompt(Unit u, List<Unit> units, string topic) =>
        Header(u, units, topic) + Context(u, units) + "\n" + Evidence(u) + $"P: {u.Proposal}\nR: {u.Review}\n" +
        L(u, "\nP to Twoje wcześniejsze zdanie, R to poprawka recenzenta. Odpowiedz OK, jeśli R jest poprawne (co najmniej tak dobre jak P), " +
             "w przeciwnym razie odpowiedz tylko swoim zdaniem końcowym.",
             "\nP is your earlier sentence and R is a reviewer's correction. Answer OK if R is correct (at least as good as P), " +
             "otherwise answer with your final sentence only.");

    static void WriteModelNotes(WorkNotes notes, Settings s, List<Unit> units, string status)
    {
        var changes = string.Join("\n", units.Where(u => (u.Final ?? u.Proposal) != u.Original)
            .Select(u => $"- {u.Original}\n  -> {u.Final ?? u.Proposal}{(u.Final == null ? " (w dyskusji)" : "")}"));
        notes.Write("07-correction-model.md", $"{s.CorrectionModel} (model korekty)", "Proponuje poprawione zdania i odpowiada na recenzję.",
            "Dla każdego spornego zdania: z wersji A/B/C i uwag LanguageTool wybierz to, co mówca najpewniej powiedział. " +
            "Tylko podmiana pojedynczych słów, niepewne słowo jako ??słowo??. Po recenzji: \"OK\" albo własna wersja.", status, changes);
        notes.Write("08-check-model.md", $"{s.CheckModel} (model kontrolny)", "Recenzuje propozycje: prawdziwe słowa, gramatyka, sens w kontekście.",
            "Dla każdej propozycji P: \"OK\" albo poprawione zdanie (tylko podmiana słów, niepewne jako ??słowo??). " +
            "Zgoda obu modeli kończy dyskusję o zdaniu; bez zgody po ostatniej rundzie różniące się słowa są oznaczane ??tak??.",
            status, $"Zgoda: {units.Count(u => u.Final != null)} / {units.Count}");
    }
}
