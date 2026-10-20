# Modele i silniki

## Mowa → tekst

| Model | Plik | Rola | Silnik | VRAM |
|---|---|---|---|---|
| Whisper large-v3 | `whisper\ggml-large-v3.bin` (3,1 GB) | hipoteza A (podstawa), próbki języka, `-tr` → EN | whisper.cpp v1.9.5, CUDA 12 | ~4,1 GB |
| Whisper large-v3-turbo | `whisper\ggml-large-v3-turbo.bin` | hipoteza B | whisper.cpp | mniej niż A |
| NVIDIA Parakeet TDT 0.6B v3 (f16) | `whisper\ggml-parakeet-tdt-0.6b-v3-f16.bin` | hipoteza C (inna architektura, kawałki po 300 s) | `parakeet-cli` (whisper.cpp) | ~2,2 GB |
| Silero VAD v5.1.2 | `whisper\ggml-silero-v5.1.2.bin` | wykrywanie mowy (bez halucynacji w ciszy) | whisper.cpp | mały |

Brak pliku B albo C oznacza dyskusję bez tej hipotezy. Brak A uniemożliwia transkrypcję.

## Tekst (Ollama)

| Model | Ustawienie | Rola | VRAM |
|---|---|---|---|
| Llama 3.1 8B (`llama3.1:8b`) | `CorrectionModel` | pisze: słowniczek nazw, dziedzina, propozycje w dyskusji, listy notatek, poprawki, tematy, tłumaczenie EN → PL, sprawdzanie PL → EN | ~5,7 GB (kontekst 8192) |
| Qwen 3.5 4B (`qwen3.5:4b`) | `CheckModel` | sprawdza: recenzje w dyskusji, kontrola punktów notatek, tłumaczenie PL → EN, sprawdzanie EN → PL | ~3,7 GB |

Dwa modele z różnych rodzin sprawdzają się krzyżowo, bo model nie powinien oceniać sam siebie.

Opcje zapytań:
- `temperature 0.1`, `think: false` (tryb myślenia tylko wydłuża odpowiedź);
- `repeat_penalty 1.1` i limit długości `num_predict`, bo Llama potrafiła się zapętlić na 15 min;
- limity: TAK/NIE kilka tokenów, lista 400, tłumaczenie do 2048.

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
| Qwen 3.5 4B | **obecny sprawdzający** | 11/14, 1 zmyślenie przepuszczone |
| Llama 3.1 8B | **obecny piszący** | 12/14; zostaje decyzją użytkownika |

Benchmark: 14 punktów notatek z prawdziwych nagrań, model ocenia, czy transkrypcja je potwierdza ([benchmarki.md](benchmarki.md)).

## Odrzucone silniki mowy

- **Whisper tiny/small** jako trzecia hipoteza: w polskim myli się co kilka słów. Zastąpiony Parakeetem za zgodą użytkownika.
- **WhisperX:** nie używa modeli ggml i nie był portable. pyannote działa teraz bez niego.
