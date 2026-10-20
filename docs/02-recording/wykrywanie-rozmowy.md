# Wykrywanie rozmowy

## Jak

- **Sygnał:** pasek rozmowy w oknie Teams. UI Automation szuka przycisku o id `microphone-button`, które nie zależy od języka Teams. Odczyt co 1 s (`CatcherService`, `TeamsCallWatcher`, `CallDetector`).
- **Start:** pasek widoczny ≥ 2 s.
- **Koniec:** pasek zniknął na ≥ 5 s. Błąd UI Automation nie kończy rozmowy.
- **Okno rozmowy:** największe niezminimalizowane okno procesu Teams, które ma pasek.
- **Fantom po wyjściu:** przez 20 s po końcu to samo okno nie może rozpocząć nowej rozmowy, bo Teams przez chwilę pokazuje pasek po wyjściu.
- **Wyciszenie:** odczyt nazwy przycisku co 200 ms ("Wycisz mikrofon" / "Wyłącz wyciszenie mikrofonu", PL+EN). Gdy jesteś wyciszony, ścieżka mikrofonu zapisuje ciszę.
- **Proces Teams:** nazwa `ms-teams` jest w `settings.json` (`TeamsProcess`), bo Microsoft może ją zmienić.

## Dlaczego nie rejestr mikrofonu

Pierwsza wersja patrzyła w `ConsentStore\microphone` (kto używa mikrofonu). Ekran przed dołączeniem i "Dołącz ponownie" też trzymały mikrofon, więc nagrywał się 15-20-sekundowy ogon po rozmowie. Pasek rozmowy jest dokładniejszy.

## Ręcznie

Przycisk START / STOP RECORDING w sekcji STATUS i pozycja w menu traya (nadpisanie automatu).

## Sprawdzone na żywo

Testy z prawdziwym Teams w trybie solo ("Spotkaj się teraz"):
- wybór właściwego okna;
- wyciszenie nie przerywa nagrania;
- YouTube w tle nie trafia do nagrania;
- koniec wykrywany kilka sekund po wyjściu.

Niesprawdzone: rozmowa z drugą osobą (głos i prezentacja rozmówcy) i połączenie 1:1.
