# Potok transkrypcji

`Transcriber.RunAsync(folder, ..., mode)` działa w trzech trybach:

| Tryb | Kiedy | Różnice |
|---|---|---|
| `Full` | nagranie bez kawałków, import wideo, "Transcribe again" | wszystkie kroki |
| `Chunk` | kawałek ~5 min w trakcie rozmowy | bez diaryzacji i tłumaczenia, notatka częściowa (bez Zadań), zapis `corrected.json` |
| `Assemble` | po rozmowie z kawałkami | zdania z kawałków przesunięte na oś całego nagrania, jedna diaryzacja, bez ponownej dyskusji, tłumaczenie i notatka z całości |

Dodatkowo `fromState = true` to przeliczenie po objaśnieniu słów. Startuje od dyskusji z zapisanego `analysis\state.json`, bez Whispera i diaryzacji.

## Kroki (tryb Full)

1. **Kanały:** `transcript.wav` stereo dzielony przez ffmpeg na Ja (L) i Rozmówców (R). Mono oznacza jeden kanał, Rozmówcy.
2. **Język kanału:** przy `TranscriptLanguage = auto` próbka 120 s idzie przez Whisper po polsku i po angielsku. Wygrywa wyższa średnia pewność tokenów. Wbudowane wykrywanie Whispera myliło angielski z polskim akcentem.
3. **Przebieg 1:** Whisper large-v3 na całym kanale w wybranym języku (hipoteza A, podstawa: zdania i czasy).
4. **Słowniczek nazw:** Llama wyciąga nazwy z przebiegu 1 i łączy je ze słowniczkiem użytkownika (GLOSSARY).
5. **Przebieg 2 ze słowniczkiem** (`--prompt`) tylko wtedy, gdy słowniczek użytkownika ma terminy nieobecne w przebiegu 1. Wynik zostaje tylko, jeśli pewność nie spada. Test TVN: prompt z już poprawnymi nazwiskami zgubił słowo "Pytanie".
6. **Hipotezy:**
   - B: Whisper large-v3-turbo;
   - C: NVIDIA Parakeet TDT 0.6B v3;
   - dla polskiego kanału Whisper `-tr` (mowa od razu po angielsku).

   Ollama jest wtedy zwalniana z VRAM.
7. **Rozmówcy:** diaryzacja kanału Rozmówcy: pyannote na GPU, a przy błędzie sherpa-onnx na CPU. Potem weryfikacja zdań odciskiem głosu i mówca stały w zdaniu ([rozmowcy.md](rozmowcy.md)).
8. **Echo mikrofonu:** zdanie "Ja" jest usuwane, gdy mikrofon jest ≥ 3 dB cichszy od Teams i ≥ 30% słów pochodzi od rozmówców w oknie ±2 s.
9. **Stan zapisany** do `analysis\state.json`.
10. **Poprawki z bazy wiedzy:** wpisy `= poprawne` podmieniane w tekście deterministycznie.
11. **Dyskusja modeli** o spornych zdaniach ([dyskusja-modeli.md](dyskusja-modeli.md)). Niejasne słowa trafiają do `questions.json`.
12. **Słowniczek użytkownika** nakładany ponownie (B i C słyszą "Sztur", a słowniczek mówi "Stuhr").
13. **Pliki transkrypcji** `transcript.*`; wypowiedzi tego samego mówcy z przerwą ≤ 2 s są łączone.
14. **Tłumaczenie** na drugi język ([04-notes/tlumaczenie.md](../04-notes/tlumaczenie.md)).
15. **Notatki** ([04-notes/notatki.md](../04-notes/notatki.md)).

Każdy krok wypisuje w logu linię `  step: ...`. Okno pokazuje ostatnią z nich w wierszu ANALYZING.

## Whisper - parametry

`whisper-cli` z VAD Silero (`-sns -ml 80 -sow -oj`), każdy kanał osobno.
- `--diarize` z VAD skleja wszystko w jeden segment.
- Bez VAD Whisper wymyśla "Dziękuję za oglądanie!" w ciszy.
- Offsety tokenów przy VAD są przesunięte, więc czasy słów liczy się proporcjonalnie w obrębie segmentu.
- Pewność zdania `MinP` to najniższa pewność słowa.
