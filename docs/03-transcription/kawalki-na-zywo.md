# Kawałki na żywo (podwójny zapis)

> **Zmiana:** kawałki ~5 min z pełnym potokiem w trakcie rozmowy (opis niżej) zastąpiła lżejsza ścieżka `LiveNotesService`, bo Whisper large-v3 i Qwen nie mieszczą się
> obok Whispera turbo w 6 GB VRAM. Teraz (`LiveNotesMinutes = 3`): transkrypcja na żywo Whisper turbo co ~30 s -> co 3 min notatka kawałka (Qwen, kontekst 8k, tylko gdy
> Whisper turbo stoi, potem model zwolniony), dopisywana do notatek poprzednich kawałków (`live\live-notes.md`, widok "Live notes"; dymek w trayu). Nieznane słowa
> (LanguageTool na CPU) od razu w `questions.json` - panel TO CLARIFY. Po rozmowie całość liczy się jak zwykle, bez składania z kawałków. Kod kawałków
> (`Mode.Chunk`/`Assemble`, `LiveChunks`) zostaje dla starych nagrań.


Włączone opcją **Live notes** (`LiveChunkMinutes = 5`, `0` = wyłączone). Dają dwie rzeczy:
- notatki częściowe w trakcie rozmowy;
- dużo krótsze czekanie po rozmowie, bo większość pracy jest już zrobiona.

## W trakcie rozmowy

1. **Moment cięcia:** gdy od końca poprzedniego kawałka minęło 5 min + 60 s + 5 s zapasu.
2. **Zrzut:** `RecordingSession.SnapshotTranscriptAsync` zapisuje bieżący dźwięk (`Flush` pod blokadą) i miksuje go tak samo jak przy finalizacji (`live\snapshot.wav`).
3. **Cięcie:** w środku ciszy ≥ 2 s najbliższej 5. minuty, w oknie ±60 s, czyli kawałek ma 5 min ± 1 min.
   - cisza = poziom blisko szumu tła okna (3× dziesiąty percentyl), nie głośniej niż -34 dBFS;
   - bez takiej ciszy cięcie idzie w najcichszym 0,5 s.
4. **Kolejka:** kawałek `live\NN` przechodzi tryb `Chunk`: rozpoznanie (A, B, C, `-tr`), dyskusja i notatka częściowa.
5. **Powiadomienie** z notatką częściową przy prawej krawędzi ekranu ([06-ui/tray-powiadomienia.md](../06-ui/tray-powiadomienia.md)). Fragment bez treści nie daje powiadomienia.

## Po rozmowie

1. Ostatni kawałek (od końca poprzedniego do końca nagrania).
2. **Składanie** (`Assemble`):
   - zdania i angielskie wersje z kawałków przesunięte na oś końcowego `transcript.wav`;
   - czas 0 = najwcześniejszy z: pierwsza klatka wideo, dźwięk Teams, mikrofon;
   - pytania z kawałków scalone po odmianach;
   - jedna diaryzacja na całym kanale Rozmówców (spójne numery);
   - tłumaczenie i notatka z całości, z notatkami częściowymi jako kontekstem ("żeby nie było głupot").
3. Gdy kawałki są niekompletne (np. któryś nie został przetworzony), liczy się całe nagranie jak zwykle.

## Czasy (nagranie 9,6 min, RTX 4060)

| Co | Czas |
|---|---|
| kawałek 1 (293 s dźwięku) | 4:42, szybciej niż czas rzeczywisty |
| po rozmowie: ostatni kawałek + składanie | 9:38 |
| szacunkowo dla 1 h rozmowy po jej końcu | ~10-11 min zamiast ~63-83 min |

## Ograniczenie

Kawałek liczy się mniej więcej w czasie rzeczywistym. Jeśli GPU jest zajęte czymś innym, kolejka może się wydłużać i powiadomienia przychodzą z opóźnieniem.
