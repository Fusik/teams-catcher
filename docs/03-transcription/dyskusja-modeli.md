# Dyskusja modeli (korekta transkrypcji)

Cel: poprawić tylko to, co jest naprawdę źle usłyszane, nic nie zmyślić, a rzeczy niepewne oznaczyć `??tak??`.

## Głosy w dyskusji

| Kto | Rola |
|---|---|
| Whisper large-v3 (A) | podstawa: zdania, czasy |
| Whisper large-v3-turbo (B) | druga hipoteza, ten sam typ modelu, inne błędy |
| Parakeet TDT 0.6B v3 (C) | trzecia hipoteza, inna architektura; tylko dowód, sama różnica z C nie wywołuje sporu |
| Whisper `-tr` | angielska wersja z dźwięku, jako podpowiedź sensu (bez wyroku) |
| LanguageTool 6.x | nieistniejące słowa, gramatyka, składnia |
| Llama 3.1 8B (`CorrectionModel`) | proponuje poprawione zdanie, odpowiada na recenzję |
| Qwen 3.5 4B (`CheckModel`) | recenzuje propozycję ("OK" albo uwaga) |
| baza wiedzy + słowniczek | znane terminy i objaśnienia użytkownika |

## Przebieg

1. **Jednostki:** całe zdania z hipotezy A. Hipotezy B i C przycinane do zdania A po czasach słów.
2. **Zdanie sporne:** A ≠ B albo LanguageTool zgłasza problem.
3. **Dziedzina rozmowy** (Llama, jedno zdanie, np. "Gry komputerowe") trafia do promptów jako kontekst.
4. **Propozycja (Llama)** dostaje:
   - zdanie;
   - sąsiednie zdania;
   - hipotezy B i C;
   - angielską podpowiedź;
   - uwagi LanguageTool (np. "jamentowy" nie istnieje, B i C słyszą tu "diamentowy");
   - objaśnienia z bazy wiedzy.
5. **Recenzja (Qwen)** jest pomijana, gdy propozycja = oryginał i nie ma uwag.
6. **Odpowiedź (Llama)** na uwagi, potem kolejna recenzja, najwyżej `DebateRounds` (3) rund. Brak zgody: słowo oznaczone `??`.
7. **Język:** polecenia w języku zdania. Polskie zdanie jest omawiane po polsku, angielskie po angielsku. Ocenia się poprawność językową, logikę i spójność z rozmową.
8. **Końcowa pisownia (LanguageTool):**
   - słowniczek i baza wiedzy są zaufane;
   - nieznane słowo potwierdzone przez hipotezy idzie do pytań;
   - zgadnięte nieistniejące słowo wraca do oryginału z oznaczeniem `??`.
9. **Pytania** (`questions.json`) są deduplikowane po odmianach (`SameWord`), najwyżej 30. Skróty i nazwy, których model nie zna, nie są zgadywane, tylko trafiają do pytań.

## SafeCorrection - twarde reguły w kodzie

Model może proponować, a kod przyjmuje tylko bezpieczne zmiany:
- dopasowanie słów jak w diff, więc zgubione słowo nie przekreśla podmian obok;
- nowe słowo musi brzmieć podobnie (odległość ≤ 0,4) albo słyszą je B i C w tym samym miejscu (≥ 2 hipotezy dla niepodobnych);
- słowo A potwierdzone przez inną hipotezę jest nietykalne;
- cyfra nie zastępuje słowa;
- zmienionych może być najwyżej 30% słów;
- zmiany samej wielkości liter i interpunkcji są cofane.

## Przykłady z testów

- Heroes: "jamentowy golem" → "diamentowy golem" (3/3), "z pulką" → "z półką", `??armatrap??` oznaczone.
- TVN: "stanarzysty" → "scenarzysty", bez regresji. Luźniejsza reguła wcześniej zamieniła "do stanarzysty. Pytanie," na "do Maciej Sztur.".

## Notatki pracy

`analysis\06-languagetool.md`, `07-correction-model.md`, `08-check-model.md`: każde sporne zdanie, propozycje, recenzje i wynik.
