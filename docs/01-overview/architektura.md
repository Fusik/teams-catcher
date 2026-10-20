# Architektura

```
TeamsCatcher.exe (WPF, tray, jedna instancja)
├─ App                  Mutex + EventWaitHandle (druga instancja pokazuje okno pierwszej), tray, powiadomienia NoteToast
├─ MainWindow / MainViewModel   UI: zakładki TEAMS CALLS / VIDEO FILE, RECORDING, NOTES, log
└─ CatcherService       pętla co 1 s: wykrywanie rozmowy, start/stop sesji, kolejka transkrypcji (jedno zadanie naraz - GPU)
   ├─ TeamsCallWatcher / CallDetector    pasek rozmowy w oknie Teams (UI Automation), wyciszenie mikrofonu
   ├─ TeamsWindowFinder / TeamsProcesses okno spotkania, procesy Teams z sesją audio
   ├─ RecordingSession  jeden folder = jedna rozmowa: audio (ProcessLoopback + NAudio), wideo (ffmpeg), session.json, finalizacja, odzyskiwanie
   ├─ LiveChunks        kawałki ~5 min w trakcie rozmowy, cięcie w ciszy, składanie po rozmowie
   └─ Transcriber       potok: Whisper / Parakeet / LanguageTool / diaryzacja / dyskusja / tłumaczenie / notatki
      ├─ Parakeet       hipoteza C (parakeet-cli)
      ├─ LanguageTool   serwer Java, pisownia i gramatyka
      ├─ Diarizer       pyannote community-1 (Python, GPU) albo sherpa-onnx (CPU), weryfikacja odciskiem głosu
      ├─ Debate         dyskusja modeli tekstu o spornych zdaniach
      ├─ TranscriptLlm  SafeCorrection, słowniczek nazw, dziedzina rozmowy
      ├─ Glossary       pisownia ze słowniczka użytkownika, odmiany
      ├─ KnowledgeBase  knowledge.json (objaśnienia, poprawki pisowni), questions.json (niejasne słowa)
      ├─ Translator     tłumaczenie krzyżowe (jeden model tłumaczy, drugi sprawdza)
      ├─ MeetingNotes   notatki z małych zadań, sekcje, tematy, tłumaczenie notatek
      ├─ Ollama         klient HTTP Ollamy, budżet VRAM
      └─ WorkNotes      analysis\*.md - notatki pracy każdego modelu
```

## Zasady

- **Portable:** wszystko obok exe (`settings.json`, `knowledge.json`, `whisper\`, `jre\`, `languagetool\`, `pyannote\`, `ffmpeg.exe`). Wyjątek: Ollama instalowana osobno.
- **Lokalnie:** żadnych usług w chmurze. Model pyannote pobiera się raz z Hugging Face. Nagrania nigdzie nie wychodzą.
- **Jedno zadanie GPU naraz:** kolejka w `CatcherService` (`_transcribeGate`). Nagrywanie kolejnej rozmowy nie czeka na transkrypcję.
- **Zewnętrzne procesy:** ffmpeg, whisper-cli, parakeet-cli, java (LanguageTool), python (pyannote). Ścieżki z `settings.json` są walidowane, bo trafiają do linii poleceń.
- **Jakość ponad szybkość:** kilka niezależnych hipotez, kontrola krzyżowa dwoma modelami z różnych rodzin, twarde reguły w kodzie tam, gdzie małe modele się mylą.

## Przepływ danych transkrypcji

```
transcript.wav (L = Ja, R = Rozmówcy)
  → kanały osobno: Whisper large-v3 (A) [+ turbo (B), Parakeet (C), Whisper -tr (EN)]
  → stan rozpoznania (analysis\state.json)
  → diaryzacja kanału Rozmówcy (pyannote / sherpa) → Rozmówca 1/2/3
  → echo mikrofonu, poprawki z bazy wiedzy, słowniczek
  → dyskusja modeli o spornych zdaniach (LanguageTool + Llama + Qwen) → ??niepewne??, questions.json
  → transcript.*.txt / *.md
  → tłumaczenie (Translator) → notatki (MeetingNotes) → notes.pl.md / notes.en.md
```
