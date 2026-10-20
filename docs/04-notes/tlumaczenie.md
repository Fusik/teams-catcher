# Tłumaczenie PL ↔ EN

## Język rozmowy

Każdy kanał ma język wybrany po pewności Whispera ([03-transcription/potok.md](../03-transcription/potok.md)). Język główny to ten, w którym powiedziano najwięcej. Transkrypcja i notatka powstają w języku głównym, a drugi język jest tłumaczeniem.

## Tłumaczenie krzyżowe (`Translator`)

| Kierunek | Tłumaczy | Sprawdza |
|---|---|---|
| PL → EN | Qwen 3.5 4B (`CheckModel`) z podpowiedzią z Whisper `-tr` ("Heard:") | Llama 3.1 8B |
| EN → PL | Llama 3.1 8B | Qwen 3.5 4B |

- **Fazami:** najpierw wszystkie paczki tłumaczy jeden model, potem drugi sprawdza całość. Mniej przeładowań modeli w VRAM.
- **Weryfikacja:** sprawdzający widzi oryginał obok tłumaczenia i poprawia różnice. `PickVerified` odrzuca odpowiedź bliższą oryginałowi niż tłumaczeniu, bo mały model potrafi "sprawdzić" przez przepisanie oryginału.
- **Brak tłumaczenia** zdania: zostaje oryginał.

## Tłumaczenie notatek

- Punkt po punkcie, tym samym mechanizmem krzyżowym. Nagłówki i czasy się nie zmieniają.
- **Ochrona nazw:** jeśli tłumaczenie zgubiło nazwę własną z oryginału (np. "Kawulski" → "Pawłowski"), następuje jedna próba z listą nazw do zachowania. Gdy dalej się nie zgadza, zostaje oryginał punktu.
- Dlaczego tłumaczenie, a nie druga notatka: dwie niezależne notatki z wywiadu TVN mówiły różne rzeczy (EN "single doll", PL film "Lalka").

## Pliki

`transcript.pl.*`, `transcript.en.*`, `notes.pl.md`, `notes.en.md`, `analysis\10-notes-translation.md`.
