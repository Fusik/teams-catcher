# Notatki ze spotkania

## Sekcje

| Sekcja (PL / EN) | Pełna notatka | Notatka częściowa (5 min) |
|---|---|---|
| Podsumowanie / Summary (główny sens) | tak | tak |
| Założenia / Assumptions | tak | tak |
| Wnioski / Conclusions | tak | tak |
| Decyzje i ustalenia / Decisions and agreements | tak | tak |
| Zadania / Action items | tak | nie |
| Otwarte kwestie i uwagi / Open questions and remarks | tak | tak |
| Tematy z czasem `[h:mm:ss]` (co ~3 min) | tak | tak |

Zasada użytkownika: "jak nie ma, to nie ma, nie szukamy na siłę". Pusta sekcja dostaje "brak" / "none".

## Jak powstają (małe zadania zamiast jednej długiej notatki)

1. **Źródło:** sprawdzona transkrypcja w języku głównym rozmowy (po dyskusji modeli).
2. **Listy (Llama):** dla każdej sekcji i każdej części transkrypcji (do 12 000 znaków) osobne zapytanie: "wypisz krótkie punkty, tylko to, co wprost jest w transkrypcji".
3. **Kontrola (Qwen):** numerowane paczki, jedno zapytanie na sekcję i część. Pytanie zależy od sekcji, np. dla Decyzji: "czy uczestnicy wyraźnie podjęli tę decyzję, a nie tylko wspomnieli fakt?". Odpowiedź to TAK/NIE.
4. **Poprawki (Llama):** odrzucone punkty paczką, z nakazem "tylko to, co wprost jest w transkrypcji, albo: brak".
5. Najwyżej `DebateRounds` (3) rundy. Punkt po ostatnim NIE znika.
6. **Tematy:** okna ~3 min. Llama pisze jedno zdanie tematu, a czas bierze kod z transkrypcji.
7. **Markdown składa kod**, nie model.
8. **Notatka w drugim języku** to tłumaczenie punktów, a nie osobna notatka ([tlumaczenie.md](tlumaczenie.md)).

## Twarde filtry w kodzie

| Filtr | Odrzuca | Skąd |
|---|---|---|
| `IsQuote` | "Rozmówca 1: ..." (cytat wypowiedzi) | TVN: Llama wkleiła wypowiedź do podsumowania |
| `IsCopied` | punkt przepisany prawie dosłownie (od 8 słów, ≥ 80% słów jednej wypowiedzi) | Heroes |
| `IsFragment` | półzdania: < 4 słowa, "Tak, ...", "Nie, ...", "Chyba, ...", "Dobra, ...", doklejona odpowiedź " - Tak, ...", szablon "Rozmówca N" | symulacja kawałków |
| `UnsupportedNames` | nazwy własne spoza transkrypcji i bazy wiedzy (z tolerancją odmiany) | TVN: zmyślony "Stanisław Bareja" |
| pytanie zamiast tematu | temat kończący się "?" | model powtarzał pytanie |

## Kontekst

- **Baza wiedzy:** objaśnienia pojęć z danej części trafiają do promptów z dopiskiem "tylko po to, żeby rozumieć słowa, nie wpisuj ich do notatki". Wcześniej objaśnienie "SP = siła czarów" trafiło do notatki jako punkt.
- **Notatka z całości** po kawałkach dostaje notatki częściowe jako kontekst.

## Notatka częściowa

- Powstaje w trybie `Chunk` dla każdego kawałka ~5 min, w folderze `live\NN\notes.*.md`.
- Pokazuje się jako powiadomienie przy prawej krawędzi ekranu. Pokazywane są tylko sekcje z treścią, a pusty fragment nie daje powiadomienia.

## Notatki pracy

`analysis\09-notes.md`: każdy punkt z oceną OK/NIE, numerem poprawki i wynikiem.
