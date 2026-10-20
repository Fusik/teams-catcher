# Ustalenia z użytkownikiem

Decyzje podjęte w trakcie budowy (2026-10-19 / 2026-10-20), w kolejności.

## Zasady ogólne

1. **Jakość i dokładność to priorytet.** Przy dalszym przyspieszaniu kosztem jakości padło: "Nie, tyle wystarczy, jakość ważniejsza".
2. **Lokalnie:** treść spotkań nie wychodzi z komputera.
3. **Portable:** jeden folder z exe, ustawienia obok.
4. **6 GB VRAM** na docelowym komputerze. Na mocniejszej karcie modele mogą zostać w pamięci, nie wyrzucamy ich na siłę.
5. Niejasne rzeczy oznaczać `??xxx??` i innym kolorem, a nie zgadywać.

## Nagrywanie

6. .NET 10 zamiast .NET Framework 4.8 (brak zależności od Tekli, single-file).
7. Mikrofon jako osobna ścieżka.
8. Użytkownik wybiera, co powstaje: nagranie (wideo), transkrypcja, notatka.

## Transkrypcja

9. 3 modele mowy i 2 modele tekstu, które dyskutują do zgody. Najmniejszy Whisper zastąpiony Parakeetem.
10. LanguageTool + Java do pisowni i gramatyki.
11. Notatki `.md` dla każdego modelu (instrukcja i status), w folderze `analysis\`.
12. Mówca stały w obrębie zdania.
13. Echo mikrofonu (przebicie z głośników) usuwane.
14. Dyskusja o poprawności językowej, logice i spójności, zawsze w języku zdania.
15. Diaryzacja na GPU: **pyannote community-1**.

## Modele tekstu

16. "Wywal Bielika, używaj Llamy". Llama 3.1 8B zostaje.
17. Krzyżowe sprawdzanie: drugi model też tłumaczy i sprawdza.
18. Sprawdzać transkrypcję po angielsku (Whisper `-tr`), robić własne tłumaczenie na polski, sprawdzać polską transkrypcję Whispera i z niej robić notatki.
19. Gemma 4 E2B dawała szum. Po benchmarku zastąpiona przez Qwen 3.5 4B ("pełna dowolność" w wyborze).

## Kawałki i notatki

20. Rozmowy > 5 min: kawałki w trakcie, z transkrypcją i notatką częściową jako bazą dokładności i sensu.
21. Po rozmowie: składać z kawałków i dodatkowo zrobić notatkę z całości. Liczy się kontekst, "żeby nie było głupot".
22. Notatka częściowa: główny sens, założenia, wnioski, uwagi, ustalenia. "Jak nie ma, to nie ma, nie szukamy na siłę".
23. Kawałek 5 min ± 1 min, cięty w 2-sekundowej ciszy.
24. Notatki 5-minutowe pokazywane jako powiadomienia przy prawej krawędzi ekranu.

## Baza wiedzy i UI

25. Niejasne słowa i skróty użytkownik objaśnia sam w nawiasach, np. `[diamentowy golem - jednostka z gry]`, `[konsa - consa - budynek z gry, gdzie zdobywa się anioły]`. Model nie zgaduje skrótów.
26. Objaśnienia zostają w bazie wiedzy na przyszłość.
27. Objaśnianie w panelu aplikacji (nie w pliku).
28. Widać, które nagranie jest analizowane. Lista nagrań do wyboru, a pytania dotyczą wybranego nagrania.
29. Panel boczny z notatką i przyciskiem "Redo after clarification".
30. "Save and recalculate" zawsze widoczne.
31. Transkrypcja z dowolnego pliku wideo w osobnej zakładce.
32. Dokumentacja w `docs\`.

## Bezpieczeństwo

- Tokenów i haseł nie zapisujemy. Token HF użyty jednorazowo przez zmienną środowiskową, do unieważnienia przez użytkownika.
- Aplikacja nie ukrywa nagrywania.
