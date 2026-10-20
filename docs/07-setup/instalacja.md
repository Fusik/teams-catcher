# Instalacja, paczka i pobieranie składników

## Zasada

Paczka jest mała, a ciężkie modele i silniki pobierają się u użytkownika przy pierwszym starcie. Nagrywanie działa od razu, transkrypcja po pobraniu składników.

| W paczce | Dlaczego |
|---|---|
| `TeamsCatcher.exe` (self-contained, bez instalacji .NET) | aplikacja |
| `ffmpeg.exe` (9.x z `gfxcapture`) | nagrywanie musi działać od pierwszej minuty |
| `diarization\` (sherpa-onnx: segmentacja + CAM++, ~35 MB) | zapasowa diaryzacja na CPU |
| `pyannote-models\community-1` (~33 MB) | model bramkowany na Hugging Face (konto + akceptacja warunków + token), więc użytkownik nie pobierze go sam; licencja CC-BY-4.0 pozwala go dołączyć z podaniem autora |
| `diarize.py`, `asr.py`, `pyannote-requirements.txt` | skrypty diaryzacji i wersji C (Qwen3-ASR) oraz przypięte wersje pakietów Pythona |
| `notes-rules.md`, `notes-style.md` | zasady notatki lokalnej i styl notatki dla Copilota |
| `README.txt` | |

## Pobierane u użytkownika (okno "Components")

Wersje i sumy SHA-256 są przypięte w `Core/Components.cs`. Plik o innej sumie jest usuwany i zgłaszany jako błąd.

| Składnik | Źródło | Rozmiar |
|---|---|---|
| Whisper (whisper-cli, CUDA 12) | GitHub ggml-org/whisper.cpp, wydanie b5454 (v1.9.5) | 653 MB |
| Whisper large-v3 | Hugging Face ggerganov/whisper.cpp | 2,9 GB |
| Silero VAD v5.1.2 | Hugging Face ggml-org/whisper-vad | 1 MB |
| Whisper large-v3-turbo (wersja B, na żywo) | Hugging Face ggerganov/whisper.cpp | 1,5 GB |
| Java 21 (Temurin JRE 21.0.12.1) - tylko opcja Correct | GitHub adoptium | 47 MB |
| LanguageTool 6.6 - tylko opcja Correct | languagetool.org | 240 MB |
| Ollama 0.40.6 portable | GitHub ollama, **tylko gdy żadna Ollama nie działa** | 1,4 GB |
| Model notatki `hf.co/unsloth/Qwen3.5-4B-GGUF:Q3_K_M` | Ollama z Hugging Face | 3,0 GB |
| Korekta: `hf.co/LiquidAI/LFM2-2.6B-Transcript-GGUF:Q8_0`, `hf.co/cenkierpiotr/MEMORYDistillPL-1.5b:Q8_0` | Ollama z Hugging Face | 2,7 + 1,6 GB |
| Python 3.12.14 + PyTorch cu126 + pyannote.audio 4.0.7 + qwen-asr 0.0.6 | uv 0.12.18 (GitHub astral-sh), PyPI, download.pytorch.org | ~3 GB |
| Qwen3-ASR-1.7B (wersja C) do `models\Qwen3-ASR-1.7B` | Hugging Face Qwen/Qwen3-ASR-1.7B (`snapshot_download` środowiskiem Pythona) | 4,7 GB |

Razem ~20 GB (bez Ollamy portable, gdy jest zainstalowana). Składników Correct (Java, LanguageTool, 2 modele) można nie pobierać, gdy opcja Correct jest wyłączona.

## Jak działa okno "Components"

1. Przy starcie aplikacja sprawdza, czego brakuje. Składnik jest obecny, gdy istnieje jego plik pod ścieżką z `settings.json`, więc własna konfiguracja też się liczy.
2. Gdy czegoś brakuje, pokazuje się okno z listą: nazwa, do czego służy, rozmiar, pasek postępu. Pod listą jest suma do pobrania i wolne miejsce na dysku, z ostrzeżeniem, gdy go za mało.
3. Składniki można odznaczyć (np. turbo, Qwen3-ASR albo pyannote). Bez nich jakość jest niższa, ale potok działa: brak wersji B/C = notatka z mniejszej liczby wersji, brak pyannote = diaryzacja sherpa na CPU.
4. **Download** pobiera po kolei:
   - wznawianie po przerwaniu (plik `.part` + HTTP Range);
   - kontrola SHA-256;
   - rozpakowanie z ochroną przed ścieżkami spoza folderu (zip slip);
   - przy błędzie jednego składnika pozostałe pobierają się dalej.
5. **Hide** chowa okno, a pobieranie trwa dalej. Wraca z menu traya: **Components...**. Na koniec pokazuje się dymek w trayu.
6. **Cancel** przerywa pobieranie. Następne pobranie wznawia się od miejsca przerwania.

## Ollama

- Działa Ollama zainstalowana przez użytkownika: aplikacja z niej korzysta i pobiera tylko brakujące modele.
- Żadna nie działa: pobiera Ollamę portable do `ollama\` obok exe i uruchamia ją sama (`ollama serve`, modele w `ollama\models`). Przy każdym starcie aplikacji, gdy żadna Ollama nie odpowiada, portable startuje automatycznie, a przy wyjściu jest zamykana.

## pyannote (Python)

uv instaluje samodzielnego Pythona do `pyannote\` (ten sam układ co w paczce offline: `pyannote\python.exe`). Potem instaluje pakiety z `pyannote-requirements.txt`: dokładne wersje z działającej instalacji (pyannote i qwen-asr razem, sprawdzone, że diaryzacja dalej działa), z PyTorch z indeksu cu126. Cache uv jest usuwany po instalacji. Następny składnik, Qwen3-ASR, pobiera się tym samym Pythonem.

## Budowanie paczki

Najprościej: dwuklik na `build-package.bat` w katalogu repozytorium i wybór 1 albo 2. Ze skryptu albo CI: `build-package.bat small` / `build-package.bat full`, a kod wyjścia różny od 0 oznacza błąd.

| Wariant | Wynik | Rozmiar |
|---|---|---|
| `small` | `dist\TeamsCatcher\` + `dist\TeamsCatcher.zip` | ~0,5 GB folder, ~0,22 GB zip |
| `full` | `dist\TeamsCatcher-full\` + `.zip`, offline (bez Ollamy) | ~4,5 GB |

Pod spodem działa `publish.ps1` (`-Full` dla paczki offline, `-Out`, `-NoZip`).

`publish.ps1` wymaga pobranego modelu pyannote w `tools\pyannote-models\community-1`. Jednorazowo:
1. Na https://huggingface.co/pyannote/speaker-diarization-community-1 zaakceptuj warunki.
2. Utwórz token Read.
3. Pobierz model:
   ```
   tools\pyannote\Scripts\hf.exe download pyannote/speaker-diarization-community-1 --local-dir tools\pyannote-models\community-1
   ```
   (z tokenem w `HF_TOKEN`).
4. Token unieważnij, jeśli był gdzieś wklejony.

## Wymagania u użytkownika

- Windows 10 2004+ / 11 x64, nowy Teams.
- NVIDIA ze sterownikiem 551+ (CUDA 12). Bez niej Whisper liczy na CPU, bardzo wolno.
- Folder aplikacji z prawem zapisu (nie Program Files), bo tam lądują pobrane składniki.
- ~25 GB wolnego miejsca na dysku aplikacji.

## Prawo

Aplikacja nie ukrywa nagrywania (czerwona ikona, dymek). Poinformuj uczestników, że spotkanie jest nagrywane. Przed użyciem na komputerze służbowym sprawdź politykę firmy.
