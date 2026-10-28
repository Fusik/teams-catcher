# Tray i powiadomienia

## Ikona w trayu

- Ikona aplikacji (TC). W trakcie nagrania ma czerwoną kropkę.
- Podpowiedź po najechaniu pokazuje stan, np. "WF BIM - Teams Catcher (Recording)".
- Menu: Show, Start / Stop recording, Exit. Podwójne kliknięcie pokazuje okno.
- Dymek "Recording started" przy starcie nagrania. Aplikacja nie ukrywa nagrywania.

## Powiadomienia z notatkami częściowymi (`NoteToast`)

Własne okno przy prawej krawędzi ekranu, nad paskiem zadań. Zastąpiło dymek traya, który ucinał treść do 250 znaków i znikał w centrum powiadomień Windows.

- **Kiedy:** co 3 min rozmowy (Live notes): notatka z całej dotychczasowej transkrypcji na żywo (po 2 kawałkach z 2, po 3 z 3...).
- **Treść:**
  - tytuł z przedziałem czasu ("Notatka 0:04:53-0:09:40") i nazwa nagrania;
  - sekcje z punktami (bez pustych i bez "brak");
  - przewijanie do 420 px wysokości.
- **Przyciski:** "Open notes" otwiera plik notatki częściowej, X zamyka.
- **Zachowanie:**
  - kolejne powiadomienia układają się nad poprzednimi;
  - każde znika po 60 s;
  - nie znika, gdy kursor jest nad nim;
  - kliknięcie zatrzymuje znikanie;
  - nie zabiera fokusu (ShowActivated = false), jest zawsze na wierzchu.
