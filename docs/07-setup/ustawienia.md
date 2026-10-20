# Ustawienia (`settings.json` obok exe)

Plik jest edytowalny ręcznie, więc wartości są sprawdzane przy wczytaniu: zakresy są przycinane, a złe ścieżki i język wracają do domyślnych. Ścieżki z cudzysłowem są odrzucane, bo trafiają do linii poleceń.

## Nagrywanie

| Klucz | Domyślnie | Opis |
|---|---|---|
| `OutputFolder` | `Wideo\TeamsCatcher` | folder nagrań |
| `AutoRecord` | true | nagrywaj rozmowy automatycznie |
| `RecordMicrophone` | true | mikrofon jako osobna ścieżka |
| `RecordVideo` | true | obraz okna; false = sam dźwięk |
| `Fps` | 15 | 1-60 (UI: 5/15/30) |
| `VideoHeight` | 1080 | 1080 / 1440 / 2160 |
| `MinFreeBytes` | 2 GB | minimum wolnego miejsca |
| `TeamsProcess` | `ms-teams` | nazwa procesu Teams |
| `StartWithWindows` | false | `HKCU\...\Run` z `--tray` |

## Transkrypcja

| Klucz | Domyślnie | Opis |
|---|---|---|
| `AutoTranscribe` | true | transkrypcja po rozmowie |
| `LiveChunkMinutes` | 5 | kawałki w trakcie (0-60; 0 = wyłączone) |
| `TranscriptLanguage` | `auto` | `auto` albo kod języka (`pl`, `en`) |
| `TranscriptGlossary` | "" | nazwy i terminy po przecinku |
| `CorrectTranscript` | true | dyskusja modeli (korekta) |
| `DebateRounds` | 3 | rundy dyskusji i kontroli notatek (1-6) |
| `WhisperCli`, `WhisperModel`, `WhisperAltModel`, `WhisperVadModel`, `ParakeetModel` | `whisper\...` | pliki silników mowy |
| `JavaExe`, `LanguageToolDir` | `jre\bin\java.exe`, `languagetool` | LanguageTool |

## Rozmówcy

| Klucz | Domyślnie | Opis |
|---|---|---|
| `PyannotePython` | `pyannote\python.exe` | Python z pyannote (GPU) |
| `PyannoteModel` | `pyannote-models\community-1` | model pyannote |
| `DiarizationSegmentationModel`, `DiarizationEmbeddingModel` | `diarization\...` | modele sherpa-onnx (CPU) |
| `DiarizationThreshold` | 0,6 | próg klastrowania sherpa (0,1-1,5) |
| `DiarizationSpeakers` | 0 | liczba rozmówców po stronie Teams (0 = auto, max 20) |

## Modele tekstu

| Klucz | Domyślnie | Opis |
|---|---|---|
| `OllamaUrl` | `http://127.0.0.1:11434` | tylko http/https |
| `CorrectionModel` | `llama3.1:8b` | model piszący |
| `CheckModel` | `qwen3.5:4b` | model sprawdzający |
| `VramBudgetMb` | 0 | budżet VRAM; 0 = karta minus 400 MB |
| `TranslateTranscript` | true | tłumaczenie PL/EN |
| `WriteNotes` | true | notatki |
