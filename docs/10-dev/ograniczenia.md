# Znane ograniczenia i do zrobienia

## Niesprawdzone

- Rozmowa z drugą osobą: głos i prezentacja rozmówcy, połączenie 1:1 (testy były solo).
- Pełna paczka portable na czystym komputerze (bez NVIDIA, bez .NET).
- Autostart (zmienia rejestr), sprawdzony tylko w kodzie.
- Nowe UI (lista nagrań, NOTES, powiadomienia) sprawdzone na zrzutach, jeszcze nie w działającej aplikacji.

## Pobieranie składników

- Okno "Components" sprawdzone na zrzucie; pobieranie, instalacja i działanie składników sprawdzone skryptem (Whisper/Parakeet, Java, LanguageTool, pyannote na GPU, Ollama portable z pobraniem modelu). Pełny przebieg w działającej aplikacji na czystym komputerze - jeszcze nie.
- Nie sprawdzone, czy Ollama portable liczy na GPU (odpowiedź modelu OK, VRAM nie mierzony).
- Transkrypcja zakolejkowana przed końcem pobierania jest pomijana (wpis w logu) - trzeba kliknąć "Transcribe again".
- Folder aplikacji musi mieć prawo zapisu.

## Jakość

- Notatki częściowe potrafią mieć zmyślony temat (np. "serial Dzień Dobry" w komentarzu gry), bo słowa tematu padły w rozmowie i filtr nazw go przepuszcza.
- Sprawdzający (Qwen 4B) czasem przepuszcza zwykły fakt jako decyzję.
- W angielskich nagraniach do TO CLARIFY trafiają zwykłe słowa ("blank", "question").
- Krótkie wtrącenia (< 1,5 s) nie są rozróżniane po głosie.
- Diaryzacja może zawyżać liczbę rozmówców. Wtedy pomaga ustawienie OTHERS.

## Wydajność

- Notatki z krótkiego importu (55 s) trwały 3:54, przyczyna nie jest zbadana.
- Kawałek na żywo liczy się mniej więcej w czasie rzeczywistym, więc przy zajętym GPU kolejka rośnie.
- Przełączanie Llama ↔ Qwen na 8 GB kosztuje ~7-10 s każde.

## Odłożone

- Rozpoznawanie, kto mówi, z okna Teams (kafelek, UIA/OCR), wymaga rozmowy testowej z kilkoma osobami.
- Fine-tuning Whispera na własnych nagraniach.
- WAV > 4 GB (RF64) dla nagrań > ~6 h.
- Wykrywanie czarnego obrazu przy ochronie treści (Teams Premium).
