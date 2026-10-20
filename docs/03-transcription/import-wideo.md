# Transkrypcja dowolnego pliku (zakładka VIDEO FILE)

## Jak użyć

1. Zakładka **VIDEO FILE**.
2. "Choose video or audio file..." albo przeciągnij pliki na zakładkę. Można kilka naraz.
3. Wynik pojawia się na liście w sekcji RECORDING, a postęp widać w wierszu ANALYZING.

Formaty: wszystko, co czyta ffmpeg (mp4, mkv, mov, avi, webm, wmv, m4v, mp3, wav, m4a, aac, flac, ogg, opus, wma).

## Co się dzieje

1. Powstaje nowy folder w `OutputFolder`: `yyyy-MM-dd_HH-mm-ss_<nazwa pliku>`, z `session.json` (`Title` = ścieżka źródła).
2. ffmpeg wyciąga dźwięk do `transcript.wav` (16 kHz mono PCM 16-bit). **Oryginał zostaje na miejscu**, nie jest kopiowany.
3. Plik trafia do tej samej kolejki co rozmowy (`Transcriber` w trybie `Full`). Wszyscy mówcy są Rozmówcami, rozdzielanymi przez diaryzację, bo nie ma osobnego kanału mikrofonu.
4. Tłumaczenie i notatki powstają według przełączników z zakładki TEAMS CALLS.

## Test

55 s wywiadu TVN: 3 rozmówców (pyannote), poprawna transkrypcja, razem 7:06. Notatki trwały 3:54, czyli podejrzanie długo jak na 55 s. Przyczyna nie jest jeszcze zbadana.
