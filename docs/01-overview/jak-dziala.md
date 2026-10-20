# Jak działa aplikacja

## Cykl jednej rozmowy

1. **Start aplikacji:** ikona w trayu. Okno jest widoczne przy pierwszym uruchomieniu albo bez argumentu `--tray`.
2. **Wykrycie rozmowy:** w oknie Teams pojawia się pasek rozmowy (przycisk `microphone-button`, UI Automation). Szczegóły w [02-recording/wykrywanie-rozmowy.md](../02-recording/wykrywanie-rozmowy.md).
3. **Nagrywanie:**
   - obraz samego okna spotkania (ffmpeg `gfxcapture`), opcjonalnie wyłączony (`RecordVideo`);
   - dźwięk tylko procesów Teams (każdy proces z sesją audio osobno);
   - mikrofon jako osobna ścieżka (wyciszenie w Teams = cisza na tej ścieżce);
   - w czasie prezentacji: udostępniany ekran albo okno i jego dźwięk.
4. **W trakcie rozmowy (Live notes):** co 5 min ± 1 min (cięcie w ciszy ≥ 2 s) kawałek idzie do kolejki: rozpoznanie mowy, dyskusja modeli i notatka częściowa w powiadomieniu.
5. **Koniec rozmowy:** pasek rozmowy znika. Następuje finalizacja: `meeting.mp4` (obraz + zmiksowany dźwięk) i `transcript.wav` (16 kHz stereo: lewy kanał mikrofon, prawy Teams).
6. **Po rozmowie:**
   - z kawałkami: ostatni kawałek, potem złożenie całości, jedna diaryzacja na całym nagraniu, tłumaczenie i notatka z całości;
   - bez kawałków (krótka rozmowa albo Live notes wyłączone): pełny potok na całym nagraniu.
7. **Wyniki** trafiają do folderu nagrania ([pliki-nagrania.md](pliki-nagrania.md)). Niejasne słowa trafiają do panelu TO CLARIFY. Po objaśnieniu przycisk "Save and recalculate" albo "Redo after clarification" liczy analizę od nowa, bez ponownego Whispera.

## Co wybiera użytkownik

| Opcja (UI) | Ustawienie | Efekt |
|---|---|---|
| Record Teams calls automatically | `AutoRecord` | wyłączone = tylko ręczny START RECORDING |
| Record my microphone | `RecordMicrophone` | bez mikrofonu transkrypcja ma tylko stronę Teams |
| Video | `RecordVideo` | wyłączone = `meeting.mp4` z samym dźwiękiem |
| Transcript | `AutoTranscribe` | transkrypcja po rozmowie |
| Live notes | `LiveChunkMinutes` (5 / 0) | kawałki i notatki częściowe w trakcie |
| Meeting notes | `WriteNotes` | notatka ze spotkania |
| Translate PL/EN | `TranslateTranscript` | tłumaczenie transkrypcji na drugi język |
| OTHERS | `DiarizationSpeakers` | liczba rozmówców po stronie Teams (auto = model zgaduje) |

## Dowolny plik wideo

Zakładka VIDEO FILE przyjmuje dowolne nagranie (mp4, mkv, mp3, wav...). Plik przechodzi ten sam potok, a wszyscy mówcy trafiają jako Rozmówcy rozdzielani po głosie. Szczegóły w [03-transcription/import-wideo.md](../03-transcription/import-wideo.md).
