# Modele i silniki

## Mowa → tekst

| Model | Plik / folder | Rola | Silnik | VRAM |
|---|---|---|---|---|
| Whisper large-v3 | `whisper\ggml-large-v3.bin` (3,1 GB) | wersja A (podstawa transkrypcji), próbki języka, `-tr` → EN (tylko Correct) | whisper.cpp v1.9.5, CUDA 12 | ~4,1 GB |
| Whisper large-v3-turbo | `whisper\ggml-large-v3-turbo.bin` (1,6 GB) | wersja B; transkrypcja na żywo w trakcie rozmowy (co ~30 s) | whisper.cpp | ~2 GB |
| Qwen3-ASR-1.7B (Alibaba, Apache-2.0) | `models\Qwen3-ASR-1.7B` (4,7 GB) | wersja C: każde zdanie A osobno (aligner Qwen3 nie zna polskiego); 22 min = ~2 min | `asr.py`, pakiet `qwen-asr` w środowisku Pythona pyannote | ~4 GB |
| Silero VAD v5.1.2 | `whisper\ggml-silero-v5.1.2.bin` | wykrywanie mowy (bez halucynacji w ciszy) | whisper.cpp | mały |

Brak B albo C = notatka z mniejszej liczby wersji. Brak A uniemożliwia transkrypcję. Parakeet (dawna wersja C) usunięty: słaby w polskim,
a wyjście parakeet-cli czytane w złym kodowaniu dawało krzaczki.

## Tekst (Ollama)

| Model | Ustawienie | Rola | Rozmiar |
|---|---|---|---|
| Qwen 3.5 4B Q3_K_M (`hf.co/unsloth/Qwen3.5-4B-GGUF:Q3_K_M`) | `NotesModel` | **jedyny model notatki**: porównuje wersje A/B/C każdej wypowiedzi, wybiera spójną z kontekstem, pisze przebieg (części po 10 min) i podsumowanie po polsku i po angielsku | 3,0 GB |
| LFM2-2.6B-Transcript Q8_0 (Liquid AI, tylko EN) | `CorrectionModel` | opcja Correct: propozycje poprawek; tłumacz PL → EN | 2,7 GB |
| MEMORYDistillPL-1.5B Q8_0 | `CheckModel` | opcja Correct: recenzje; tłumacz EN → PL | 1,6 GB |

Kontekst każdego modelu: największy mieszczący się w VRAM (max 16k), a jeśli Ollama mimo to wpuści część do RAM - mniejszy o 25% i przeładowanie.

Opcje zapytań:
- `think: false` (tryb myślenia tylko wydłuża odpowiedź), `repeat_penalty 1.1`, limit `num_predict`;
- temperatura 0,1 (korekta, tłumaczenie), 0,3 (notatka).

## Narzędzia

| Narzędzie | Folder | Rola |
|---|---|---|
| LanguageTool 6.x | `languagetool\` + Java 21 `jre\` | pisownia (TYPOS), gramatyka, składnia; lokalny serwer HTTP |
| pyannote community-1 | `pyannote\` (Python 3.12, PyTorch 2.14 cu126, pyannote.audio 4.0.7, 4,3 GB) + `pyannote-models\community-1` (~33 MB) | diaryzacja na GPU |
| sherpa-onnx 1.13.8 | NuGet + `diarization\` (pyannote-segmentation-3.0 onnx, 3D-Speaker CAM++) | diaryzacja na CPU (fallback), odcisk głosu |
| ffmpeg | `ffmpeg.exe` | nagrywanie obrazu, mux, miks, cięcie, import wideo |

## Historia wyboru modeli tekstu

| Model | Los | Dlaczego |
|---|---|---|
| qwen3.5:9b | zastąpiony | ~6 GB, nie mieścił się z Whisperem; jeden model bez kontroli krzyżowej |
| Bielik 4.5B v3 (polski) | usunięty | przy notatkach powtarzał punkty, polecenie "popraw całość" zamienił w zadanie z matematyki, przy sprawdzaniu tłumaczenia oddawał oryginał |
| Gemma 4 E2B (QAT) | zastąpiona | benchmark: 10/14 i 2 zmyślenia przepuszczone; ocena sensu była szumem (82% zdań "inny sens") |
| Qwen 3.5 4B (Q4) | zastąpiony | 11/14, 1 zmyślenie przepuszczone; jako sprawdzający pisał własną notatkę zamiast TAK/NIE, gdy dostał zasady |
| Llama 3.1 8B | zastąpiona | 12/14; notatka 1,5 h ~20 min; z kontekstem 16k szła w 13% na CPU |
| LFM2-2.6B-Transcript | korekta + tłumacz PL → EN | notatka szczegółowa w 76 s, ale tylko po angielsku i z przekłamaniami |
| MEMORYDistillPL-1.5B | sprawdzający w korekcie | jako autor notatki: odmowy, 150 słów na 1,5 h |
| Qwen 3.5 4B Q3_K_M | **model notatki** | notatka PL + EN z 3 wersji rozpoznania, 22 min nagrania = ~1,5 min |

Benchmark: 14 punktów notatek z prawdziwych nagrań, model ocenia, czy transkrypcja je potwierdza ([benchmarki.md](benchmarki.md)).

## Odrzucone silniki mowy

- **Whisper tiny/small** jako trzecia wersja: w polskim myli się co kilka słów.
- **Parakeet TDT 0.6B v3:** zastąpiony przez Qwen3-ASR (słabszy w polskim).
- **Canary-Qwen-2.5B (NVIDIA):** tylko angielski, wymaga NeMo z gałęzi głównej, klipy do 40 s, bez czasów.
- **Whisper turbo do wyboru języka:** na polskim dubbingu był pewniejszy w EN niż w PL, zysk ~7 s.
- **WhisperX:** nie używa modeli ggml i nie był portable. pyannote działa teraz bez niego.
