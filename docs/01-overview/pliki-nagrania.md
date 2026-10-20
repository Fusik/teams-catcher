# Pliki w folderze nagrania

Folder: `<OutputFolder>\yyyy-MM-dd_HH-mm-ss_<tytuł okna albo nazwa pliku>` (domyślnie `Wideo\TeamsCatcher`).

## Nagranie

| Plik | Co to |
|---|---|
| `meeting.mp4` | obraz okna (libx264 CRF 18, 16:9, 1080p/1440p/2160p) + jedna zmiksowana ścieżka audio (Teams + mikrofon + dźwięk prezentacji); bez wideo = sam dźwięk |
| `transcript.wav` | 16 kHz stereo: lewy kanał = mikrofon (Ja), prawy = Teams + dźwięk prezentacji (Rozmówcy). Import pliku wideo = mono (wszyscy jako Rozmówcy) |
| `session.json` | start, koniec, tytuł, starty strumieni (czas uniksowy), segmenty wideo, `Finalized`; przy imporcie `Title` = ścieżka pliku źródłowego |
| `video_02.mkv`... | dodatkowe segmenty wideo poza MP4 (rzadkie) |
| w trakcie: `teams_PID.wav`, `mic.wav`, `video_01.mkv` | źródła, usuwane po udanej finalizacji |

## Transkrypcja i notatki

| Plik | Co to |
|---|---|
| `transcript.txt` | zbiorczy: każda wypowiedź w języku oryginału i tłumaczenie |
| `transcript.pl.txt` / `transcript.en.txt` | cała rozmowa w jednym języku (`[0:00:05] Rozmówca 1: ...`, w EN `Me` / `Speaker N`) |
| `transcript.pl.md` / `transcript.en.md` | to samo do czytania; niepewne słowa `??tak??` na czerwono |
| `notes.pl.md` / `notes.en.md` | notatka: Podsumowanie, Założenia, Wnioski, Decyzje i ustalenia, Zadania, Otwarte kwestie i uwagi, Tematy z czasem |
| `questions.json` | niejasne słowa (termin, zdanie, czas, powód) do panelu TO CLARIFY; znika, gdy wszystkie objaśnione |

## analysis\ - notatki pracy modeli

Każdy model i narzędzie ma swój plik z sekcjami: rola, instrukcja, status, wynik. Plik `00-status.md` zbiera przegląd wszystkich kroków.

| Plik | Kto |
|---|---|
| `01-whisper-large-v3.md` | hipoteza A (podstawa) |
| `02-whisper-turbo.md` | hipoteza B |
| `03-parakeet.md` | hipoteza C |
| `04-whisper-english.md` | mowa PL → tekst EN (Whisper `-tr`) |
| `05-meaning-check.md` | podpowiedź sensu z angielskiego |
| `06-languagetool.md` | pisownia i gramatyka |
| `07-correction-model.md` | model piszący (Llama): propozycje |
| `08-check-model.md` | model sprawdzający (Qwen): recenzje |
| `09-notes.md` | notatki: każdy punkt z oceną OK/NIE i poprawkami |
| `10-notes-translation.md` | tłumaczenie notatek |
| `state.json` | stan po rozpoznaniu mowy (zdania, hipotezy, EN, słowniczek): pozwala przeliczyć bez Whispera |

## live\ - kawałki na żywo

`live\01`, `live\02`... Każdy kawałek ma własne `transcript.wav`, `chunk.json` (indeks, od, do, czas zero), wyniki, `notes.*.md` (notatka częściowa), `analysis\corrected.json` (zdania po dyskusji, do składania) i `questions.json`.
