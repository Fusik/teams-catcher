# Kod i testy

## Struktura

```
TeamsCatcher/
  App.xaml(.cs)            jedna instancja, tray, powiadomienia
  MainWindow.xaml(.cs)     okno: zakładki, RECORDING, NOTES, log, przeciąganie plików
  NoteToast.xaml(.cs)      powiadomienie z notatką częściową
  SetupWindow.xaml(.cs) + ViewModels/SetupViewModel.cs   okno "Components" (pobieranie składników)
  pyannote-requirements.txt   przypięte pakiety Pythona dla pyannote
  ViewModels/MainViewModel.cs
  Infrastructure/          ObservableObject, RelayCommand, AsyncRelayCommand
  Controls/WfIcon.cs
  Assets/WF/, Themes/WfTheme.xaml   styl WF BIM (+ TabControl/TabItem)
  diarize.py               pyannote (kopiowany obok exe)
  Core/
    CatcherService.cs      pętla, sesje, kolejka transkrypcji, kawałki, import wideo
    TeamsCallWatcher.cs, CallDetector.cs, CallState.cs   pasek rozmowy (UIA), start/koniec z opóźnieniem
    TeamsWindowFinder.cs, TeamsProcesses.cs, ProcessLoopback.cs, Autostart.cs
    RecordingSession.cs    nagrywanie, finalizacja, zrzut na żywo, odzyskiwanie
    LiveChunks.cs          kawałki, cięcie w ciszy, oś czasu
    Transcriber.cs         potok (Full / Chunk / Assemble, fromState)
    Parakeet.cs, LanguageTool.cs, Diarizer.cs
    Debate.cs, TranscriptLlm.cs, Glossary.cs, KnowledgeBase.cs
    Translator.cs, MeetingNotes.cs, Ollama.cs, WorkNotes.cs
    Settings.cs
    Components.cs          składniki pobierane u użytkownika: przypięte URL + SHA-256, wznawianie, zip, Ollama portable, uv + pyannote
TeamsCatcher.Tests/         xUnit
publish.ps1                 paczka portable
PLAN.md                     historia zmian, pomiary
docs/                       ta dokumentacja
```

## Testy

```
dotnet test TeamsCatcher.Tests
```

- 181 testów (2026-10-20): jednostkowe i integracyjne (`--filter Category=Integration` dla tych na prawdziwym Windows).
- Pokrywają m.in.:
  - wykrywanie rozmowy, okna i procesy;
  - argumenty ffmpeg i synchronizację, naprawę WAV;
  - SafeCorrection, dyskusję (z fałszywymi odpowiedziami modeli);
  - filtry notatek (IsQuote, IsCopied, IsFragment, nazwy);
  - bazę wiedzy (parsowanie, poprawki `=`, odmiany);
  - kawałki (cięcie w ciszy 2 s, oś czasu, czas zero);
  - parsowanie wyników pyannote, LanguageTool i Parakeeta.
- UI sprawdzany zrzutem okna renderowanym poza ekranem (skrypt `render.cs`) i ręcznie.

## Konwencje

- Komentarze po polsku, tylko "dlaczego" (często z przypadkiem testowym: TVN, Heroes).
- Twarde reguły w kodzie tam, gdzie małe modele się mylą, bo modele proponują, a kod decyduje.
- Wartości z `settings.json` to granica zaufania: walidowane przy wczytaniu.
- Zewnętrzne procesy przez `Transcriber.RunAsync(exe, args, dir, ct)`: przy anulowaniu ubijane, przy błędzie wyjątek z ostatnimi liniami stderr.

## Build przy działającej aplikacji

Uruchomiona aplikacja blokuje `bin\x64\Debug\...\TeamsCatcher.exe`. Zamknij ją z traya (Exit), potem zbuduj (Ctrl+Shift+B w VS).
