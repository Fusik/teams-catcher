# Okno aplikacji

Styl WF BIM (`Assets\WF`, `Themes\WfTheme.xaml`): ciemny motyw, własny pasek tytułu (`User: ...`, pinezka Always on top), okno 1320×900 (min. 1180×600). Zamknięcie okna chowa je do traya. Wyjście jest tylko z menu traya.

## Układ

```
┌ pasek tytułu ─────────────────────────────────────────────────────────────┐
│ TEAMS CATCHER   [TEAMS Running/Not running]          [Recordings folder]  │
├───────────────────────────────────────────────┬───────────────────────────┤
│ [TEAMS CALLS] [VIDEO FILE]                    │ NOTES                     │
│  STATUS / SETTINGS  albo  wybór pliku         │ nazwa nagrania            │
├───────────────────────────────────────────────┤ notatka (notes.pl.md)     │
│ RECORDING                                     │                           │
│  [nagranie ▾]  [folder] [Transcribe again]    │                           │
│  ANALYZING  nagranie · live part 02 · krok    │                           │
│  TO CLARIFY  słowo | czas + zdanie | [pole]   │                           │
│  [objaśnienia zbiorczo] [Save and recalculate]│ [Open] [Redo after clar.] │
├───────────────────────────────────────────────┤                           │
│ log                                           │                           │
└───────────────────────────────────────────────┴───────────────────────────┘
```

## Zakładka TEAMS CALLS

- **STATUS:**
  - stan (Idle / In call (not recording) / Recording / Finalizing / Transcribing), czas, nagrywane okno, wolne miejsce;
  - przycisk START / STOP RECORDING.
- **SETTINGS:**
  - folder nagrań (Browse...);
  - GLOSSARY: nazwiska, firmy i terminy po przecinku (podpowiedź dla Whispera i pisownia);
  - przełączniki: Record Teams calls automatically, Record my microphone, Video, Transcript, Live notes, Meeting notes, Translate PL/EN, Start with Windows;
  - listy rozwijane: OTHERS (auto/1-6 rozmówców), VIDEO fps (5/15/30) i rozdzielczość (1080p/1440p/2160p).

## Zakładka VIDEO FILE

Przycisk "Choose video or audio file..." albo przeciągnięcie plików ([03-transcription/import-wideo.md](../03-transcription/import-wideo.md)).

## Zakładka MODELS

Wszystko, co zmienia pracę modeli tekstu ([04-notes/zasady-notatek.md](../04-notes/zasady-notatek.md)):
- **NOTE RULES:** edytor `notes-rules.md` (zasady notatek), "Save rules", "Open file";
- **TEXT MODELS AND KNOWLEDGE:**
  - WRITER, CHECKER (modele Ollamy), ROUNDS (1-6), VRAM MB (0 = cała karta), LANGUAGE (auto/pl/en);
  - edytor bazy wiedzy (jedna linia na wpis) i "Save knowledge".

## Sekcja RECORDING

- **Lista nagrań:** rozwijana, 10 ostatnich (nazwa, rozmiar, "transcript" / "not finalized"). Wybór nagrania ustawia, czego dotyczą TO CLARIFY i NOTES. Wybór zostaje po odświeżeniu listy.
- **Przyciski:** otwórz folder; Transcribe again (pełny potok od nowa).
- **ANALYZING:** co teraz liczy kolejka (nagranie, kawałek `live part NN`, ostatni krok z logu); "- (queue empty)", gdy kolejka jest pusta.
- **TO CLARIFY:**
  - niejasne słowa tylko wybranego nagrania;
  - słowo (podpowiedź: powód), czas i zdanie, pole na znaczenie;
  - wpisane objaśnienia nie znikają, gdy w tle skończy się kolejne zadanie;
  - "Nothing unclear in this recording.", gdy pytań brak.
- **Pole zbiorcze i Save and recalculate:** zawsze widoczne ([04-notes/baza-wiedzy.md](../04-notes/baza-wiedzy.md)).

## Panel NOTES

- Notatka wybranego nagrania (`notes.pl.md`, inaczej `notes.en.md`), odświeżana po każdym zadaniu kolejki.
- **Open:** otwiera plik notatki.
- **Redo after clarification:** to samo co Save and recalculate. Zapisuje objaśnienia i liczy nagranie od dyskusji z nowym kontekstem.

## Log

Na dole, 110 px, ostatnie 500 linii. Kroki potoku mają prefiks `step:`.
