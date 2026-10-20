# Baza wiedzy i niejasne słowa

## Po co

Modele nie zgadują skrótów ani żargonu. Słowo, którego nie rozumieją, oznaczają i pytają o nie użytkownika. Odpowiedź trafia do bazy wiedzy, więc przy kolejnych spotkaniach model już wie, o czym mowa (zagadnienia się powtarzają).

## Pliki

| Plik | Gdzie | Co |
|---|---|---|
| `knowledge.json` | obok exe | wpisy: `Term`, `Meaning`, `Aliases` (inne zapisy) |
| `questions.json` | folder nagrania (i kawałka) | niejasne słowa: termin, zdanie, czas, powód |

## Zapis objaśnień

W panelu TO CLARIFY (pole przy słowie) albo zbiorczo w polu pod listą:

| Wpis | Znaczenie |
|---|---|
| `[diamentowy golem - jednostka z gry]` | termin - znaczenie |
| `[konsa - consa - budynek z gry, gdzie zdobywa się anioły]` | termin - inny zapis - znaczenie |
| `= diamentowy` (w polu przy słowie "Jamentowy") | poprawka pisowni: źle usłyszane słowo → poprawne |

## Jak baza jest używana

1. **Poprawki pisowni (`=`)** podmienia kod w tekście na stałe, przed dyskusją modeli, bez udziału modeli i z zachowaniem wielkiej litery.
2. **Objaśnienia** trafiają do dyskusji, notatek i kontroli notatek, ale tylko te, które dotyczą danego fragmentu (`Relevant`, z dopasowaniem odmian). Służą wyłącznie do rozumienia słów.
3. **Zaufane słowa:** terminy z bazy i słowniczka użytkownika nie są oznaczane `??` i nie trafiają do pytań.
4. **Nazwy z objaśnień** liczą się w notatkach jako znane ("Heroes III" może pojawić się w notatce, choć nie padło w rozmowie).

## Przeliczenie po objaśnieniu

Przycisk **Save and recalculate** (sekcja RECORDING) albo **Redo after clarification** (panel NOTES) działa tak samo:
1. zapisuje objaśnienia do `knowledge.json`;
2. usuwa objaśnione słowa z `questions.json` wybranego nagrania;
3. liczy wybrane nagranie od dyskusji modeli z `analysis\state.json`, bez Whispera i diaryzacji. Dalej idzie dyskusja z nowym kontekstem, tłumaczenie i notatki.

Heroes: przeliczenie trwało 3:07-3:45 zamiast 10:39 pełnego przetwarzania.

## Skąd biorą się pytania

- słowo `??niepewne??` po dyskusji (modele się nie zgodziły);
- słowo nieznane słownikowi LanguageTool, a potwierdzone przez hipotezy (np. nazwa z gry);
- skróty (np. "DDK", "PDD").

Odmiany tego samego słowa są scalane w jedno pytanie (`SameWord`: wspólny początek ≥ 4 litery, różnica tylko w końcówce). Na nagranie przypada najwyżej 30 pytań.

## Znany problem

W angielskich nagraniach do pytań trafiają zwykłe słowa ("blank", "question", "enthusiasm"). Filtr nieznanych słów EN jest do poprawy.
