# Zasady notatek (`notes-rules.md`)

Instrukcja dla modeli: jak powstają wnioski, decyzje, założenia i pozostałe sekcje, co mają zawierać i czego nie.

## Gdzie

- Plik `notes-rules.md` obok exe (w repozytorium: `TeamsCatcher/notes-rules.md`, kopiowany do builda i paczki).
- Edycja w zakładce **MODELS** (lewa część: NOTE RULES, przycisk "Save rules") albo w dowolnym edytorze ("Open file").

## Jak działa

1. Przy każdej notatce (pełnej i częściowej) aplikacja czyta plik od nowa.
2. Komentarze `<!-- -->` są pomijane, a tekst jest przycinany do 6000 znaków, bo trafia do każdego zapytania i dłuższy spowalnia notatki.
3. Zasady trafiają do trzech rodzajów zapytań:
   - **listy punktów** (model piszący, Llama): jak pisać;
   - **kontrola** (model sprawdzający, Qwen): punkt niezgodny z zasadami dostaje NIE;
   - **poprawki odrzuconych** (Llama).
4. Zmiana działa od następnej notatki. Dla starego nagrania: wybierz je w RECORDING i kliknij "Redo after clarification", co policzy notatkę od nowa bez ponownego Whispera.
5. Brak pliku: notatki powstają bez zasad użytkownika, tylko z wbudowanymi pytaniami kontrolnymi dla sekcji.

## Domyślna treść (skrót)

- Ogólne: tylko to, co padło; jeden punkt = jedna myśl; nazwy i liczby dokładnie; bez cytatów; "brak", gdy nic nie ma.
- Wnioski: do czego uczestnicy doszli (ocena, przyczyna, skutek), nie streszczenie jednej wypowiedzi ani rada modelu.
- Decyzje: tylko wyraźne postanowienia i zgody; propozycja bez zgody trafia do otwartych kwestii.
- Założenia: rzeczy przyjęte za pewne i będące podstawą ustaleń, a nie fakty wspomniane po drodze.
- Zadania: kto, co, do kiedy, bez zgadywania osoby i terminu.

## Inne rzeczy w zakładce MODELS, które zmieniają pracę modeli

| Pole | Ustawienie | Wpływ |
|---|---|---|
| WRITER | `CorrectionModel` | model piszący (korekta, notatki, tłumaczenie EN → PL, sprawdzanie PL → EN) |
| CHECKER | `CheckModel` | model sprawdzający (recenzje, kontrola punktów, tłumaczenie PL → EN) |
| ROUNDS | `DebateRounds` (1-6) | rundy dyskusji o zdaniu i rundy kontroli notatek; więcej = dokładniej i wolniej |
| VRAM MB | `VramBudgetMb` | budżet pamięci karty; gdy oba modele się mieszczą, zostają załadowane |
| LANGUAGE | `TranscriptLanguage` | auto (wybór PL/EN na kanał) albo wymuszony |
| KNOWLEDGE BASE | `knowledge.json` | tabela z wyszukiwaniem (termin, inne zapisy po przecinku, znaczenie); nowy wpis w pustym ostatnim wierszu, usuwanie klawiszem Delete; poprawka pisowni: znaczenie `=poprawne` |

Słowniczek nazw (GLOSSARY) jest na zakładce TEAMS CALLS: podpowiada Whisperowi pisownię i trafia do korekty.

## Duża baza wiedzy

Baza rośnie z każdym spotkaniem, ale do modeli trafiają tylko wpisy, które występują w analizowanym fragmencie (z odmianą), więc tysiące wpisów nie wydłużają zapytań. Wybór wpisów przy 5000 pozycjach i fragmencie 2000 słów mieści się w teście w 3 s.

## Uwaga o modelu sprawdzającym

Qwen 3.5 ma pamięć hybrydową (rekurencyjną), więc Ollama nie używa ponownie przetworzonego początku zapytania. Każda kontrola od nowa czyta fragment transkrypcji (do 12 000 znaków). Model liczy się na GPU (33/33 warstwy), a ten koszt to czas, nie CPU.
