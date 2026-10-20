# WF BIM - Teams Catcher: dokumentacja

Portable aplikacja WPF (.NET 10, x64), działająca w tle (ikona w trayu). Nagrywa rozmowy Microsoft Teams, robi lokalnie transkrypcję PL/EN, tłumaczenie i notatki. Wszystko liczy się na tym komputerze, nic nie trafia do chmury.

Stan na 2026-10-20. Historia zmian i pomiary krok po kroku: [PLAN.md](../PLAN.md).

## Spis treści

| Folder | Co opisuje |
|---|---|
| [01-overview](01-overview/) | jak działa całość, architektura, pliki w folderze nagrania |
| [02-recording](02-recording/) | wykrywanie rozmowy, nagrywanie dźwięku i obrazu, finalizacja, odzyskiwanie po awarii |
| [03-transcription](03-transcription/) | potok transkrypcji, rozpoznawanie mowy, rozmówcy, dyskusja modeli, kawałki na żywo, import wideo |
| [04-notes](04-notes/) | notatki (pełne i częściowe), tłumaczenie, baza wiedzy i niejasne słowa |
| [05-models](05-models/) | wszystkie modele i silniki, VRAM, benchmarki |
| [06-ui](06-ui/) | okno aplikacji, tray, powiadomienia |
| [07-setup](07-setup/) | instalacja, paczka portable, ustawienia, diagnostyka |
| [08-decisions](08-decisions/) | ustalenia z użytkownikiem, rozwiązania odrzucone i dlaczego |
| [09-performance](09-performance/) | czasy przetwarzania etap po etapie |
| [10-dev](10-dev/) | struktura kodu, testy, znane ograniczenia |

## W jednym akapicie

Gdy w oknie Teams pojawia się pasek rozmowy, aplikacja nagrywa samo okno spotkania (ffmpeg), sam dźwięk procesów Teams (WASAPI process loopback) i osobno Twój mikrofon. Co 5 minut wycina kawałek, rozpoznaje go i pokazuje notatkę częściową w powiadomieniu przy prawej krawędzi ekranu. Po rozmowie składa całość: trzy modele mowy (Whisper large-v3, Whisper turbo, Parakeet), LanguageTool i dwa modele tekstu (Llama 3.1 8B, Qwen 3.5 4B) uzgadniają każde sporne zdanie. pyannote na GPU rozdziela rozmówców. Na końcu powstaje tłumaczenie i notatka PL/EN. Słowa, których modele nie rozumieją, są oznaczone `??tak??` i trafiają do panelu TO CLARIFY. Twoje objaśnienia zapisuje baza wiedzy, która przydaje się przy kolejnych spotkaniach.
