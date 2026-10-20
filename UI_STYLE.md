# Standard UI aplikacji WF BIM

Ten plik obowiązuje **każdą aplikację z tego repozytorium**. Wszystkie aplikacje mają wyglądać i zachowywać się podobnie.
Wzorcowa implementacja: `Apps/PhaseGenerator/PhaseGenerator/` (okno `MainWindow.xaml`, `Themes/WfPhaseTheme.xaml`, `Controls/WfIcon.cs`).
Znak, ikony i style źródłowe: `WF_assets/` (opis w `WF_assets/README.md`).

## Zasady ogólne
- Każda aplikacja jest **samodzielnym projektem** (bez wspólnej biblioteki): potrzebny motyw, ikony i klasy kopiujemy do projektu aplikacji.
- Motyw jest **ciemny** (granat WF BIM), kanciasty: bez zaokrągleń, cienkie obrysy, akcenty niebieski i cyjan.
- **Język interfejsu: angielski** (etykiety, przyciski, tooltipy, komunikaty, log, okna potwierdzeń, nagłówki kolumn). Dokumentacja (`README`, `UI_STYLE.md`) i komentarze w kodzie mogą być po polsku. Nazwy w kodzie i plikach: bez polskich znaków.
- Jeden ekran = jedno okno: nagłówek, sekcje (GroupBox), log, pasek postępu, przycisk głównej akcji.
- **Okno jest kompaktowe**: małe odstępy, nic nie rozciąga się na całą wysokość bez potrzeby (reguła „Kompaktowe okno” niżej).

## Zasoby w projekcie aplikacji
Skopiuj z `WF_assets/` do `Assets/WF/` w projekcie aplikacji:
- `WF.Colors.xaml`, `WF.Icons.xaml`, `WF.Controls.xaml` (kompilowane jako `Page`),
- `WF.<Moduł>.ico` i `WF.<Moduł>-48.png` swojej aplikacji (kompilowane jako `Resource`).

W `App.xaml` scal słowniki w tej kolejności (ostatni wygrywa):
`Assets/WF/WF.Colors.xaml` → `WF.Icons.xaml` → `WF.Controls.xaml` → `Themes/WfPhaseTheme.xaml`
(`WfPhaseTheme.xaml` z PhaseGenerator: style GroupBox, TextBox, CheckBox, ProgressBar, ListBox, przycisk drugorzędny, przyciski paska tytułu; skopiuj i zmień nazwę na `Wf<Aplikacja>Theme.xaml` albo zostaw `WfTheme.xaml`).

W `.csproj`: `ApplicationIcon` = ikona modułu, `<Resource Include="Assets\WF\*.ico;Assets\WF\*.png" />`.
Ikona okna jest ustawiana przez pasek tytułu (obraz `WF.<Moduł>-48.png`), ikona `.exe` przez `ApplicationIcon`.

## Kolory (z `WF.Colors.xaml`, używaj tylko kluczy `WF.Brush.*`)
| Zastosowanie | Klucz |
|---|---|
| tło okna | `Navy800` |
| pasek tytułu, nagłówek, log | `Navy900` |
| panele, GroupBox | `Navy700` |
| pola tekstowe, panel informacyjny | `Field` |
| nagłówki sekcji, najechanie | `Head` |
| linie, obrysy | `Line`, `Rim` |
| akcent główny, linia pod nagłówkiem | `Blue500` |
| nazwy własne (np. nazwa modelu), ikony | `Blue300` |
| tekst główny | `Ink` |
| tekst pomocniczy | `Mute`, `Slate` |
| wyróżnienie nazw technicznych | `Cyan` |
| stany | `Ok`, `Warn`, `Err` |

## Czcionki
- `WF.Font.Display` (Chakra Petch) — tytuły, nagłówki sekcji, przyciski główne.
- `WF.Font.Body` (IBM Plex Sans) — tekst, 13 px.
- `WF.Font.Mono` (IBM Plex Mono) — pola ścieżek, log, dane techniczne, podpisy małe (10–12 px).
Czcionki muszą być zainstalowane w systemie; brak = czcionka domyślna WPF.

## Okno
- Okno bez systemowego paska tytułu: `WindowStyle="None"` + `WindowChrome` (`CaptionHeight=34`, `ResizeBorderThickness=6`, `GlassFrameThickness=0`, `UseAeroCaptionButtons=False`).
- Cienka obwódka `WF.Brush.Rim` wokół całego okna. Przy maksymalizacji margines = `SystemParameters.WindowResizeBorderThickness`.
- Szerokość startowa 900, minimum 820; wysokość dobrana do treści (minimum 520 lub mniej, gdy treści jest mało). Start na środku ekranu.

### Układ (od góry)
1. **Pasek tytułu (34 px, `Navy900`)**: z lewej ikona 18 px + „WF BIM – <Nazwa aplikacji>”; z prawej `User: <nazwa z Windowsa>` (ikona `polacz`, mono 11) i przyciski okna po 46 px: **kotwica “Always on top”** (`ToggleButton` `App.Toggle.CaptionPin`, `IsChecked` ↔ `Window.Topmost`, włączona: tło `Blue700`, ikona `Cyan`), minimalizuj, maksymalizuj/przywróć, zamknij.
2. **Nagłówek (`Navy900`, dolna linia 2 px `Blue500`)**, jedna linia, kompaktowy (`Padding` 10,5): logo 32 px + tytuł WIELKIMI LITERAMI (Display Bold 16) z podtytułem „WF | BIM  ·  Tekla Structures 2025” (mono 11, `Slate`); obok ramka z połączonym modelem (`Padding` 8,2, ikona `model` 20 px, podpis MODEL, nazwa `Blue300` Display Bold 14, ścieżka mono 10 z obcięciem); po prawej przyciski akcji narzędziowych (np. “Refresh”, `Padding` 10,4). Opis “How it works” jest tooltipem tytułu (patrz niżej).
3. **Treść** (margines 8 px): sekcje `GroupBox`, log (stała wysokość).
4. **Pasek postępu** (widoczny tylko w trakcie pracy) z podpisem mono 11.
5. **Przycisk głównej akcji**: mały (patrz „Przyciski”), w sekcji, której dotyczy, albo wyrównany do prawej; nie na całą szerokość okna.

### Kompaktowe okno (reguła obowiązkowa)
- Nagłówek, margines treści (8 px) i odstępy jak wyżej; bez zbędnych przerw.
- Log ma **stałą wysokość ok. 10 linii** (176 px przy mono 12), nie wypełnia reszty okna; w oknie z przewijaną treścią log jest pod nią.
- “How it works” **nie zajmuje miejsca w oknie**: to `ToolTip` (MaxWidth 420) na bloku tytułu w nagłówku, pojawia się po najechaniu.
- Przycisk główny jest mały i leży przy swojej sekcji, nie na dole całej szerokości.
- Wysokość okna dobieraj do treści, bez pustych rozciągniętych obszarów.
- **Listy (np. filtry wyboru) wypełniają całą wolną wysokość okna**: ich sekcja leży w wierszu `*`, a listy mają tylko `MinHeight` (bez stałej `Height`). Gdy okno jest za niskie, treść przewija się (`ScrollViewer`, wysokość treści = `ViewportHeight`, `MinHeight` treści).

## Elementy
- **GroupBox**: nagłówek WIELKIMI LITERAMI, Display SemiBold, tło `Head`, ikona 18 px z lewej; wewnątrz margines 10, odstęp między sekcjami 10.
- **Przyciski**: główny `WF.Button.Primary` (gradient niebieski, jeden na ekran, mały: FontSize 13, `Padding` 12,6, ikona 18 px, w sekcji); pozostałe `App.Button.Secondary` (obrys `Blue300`, tło półprzezroczyste). Każdy przycisk ma ikonę 18 px po lewej i tekst, odstęp 8 px.
- **Pola tekstowe**: styl `WF.TextBox` (mono, tło `Field`), ścieżki plików: pole + przycisk “Browse…” po prawej.
- **Opis “How it works”**: tooltip tytułu w nagłówku (nagłówek “How it works” Display Bold + krótki, ogólny opis przepływu pracy, bez list kolumn i szczegółów, które mogą się zmienić). Nie ma osobnego panelu w oknie.
- **Log**: pole tylko do odczytu, mono 12, tło `Navy900`, zawijanie wierszy, przewijanie pionowe, stała wysokość ok. 10 linii (176 px).
- **Pasek postępu**: wysokość 10 px; nieokreślony, dopóki nie znamy liczby kroków.
- **Lista wyboru** zamiast ComboBox: `ToggleButton` + `Popup` z ciemnym `ListBox` (domyślny ComboBox ma jasną listę).

## Ikony
- Zestaw 24 px w `WF.Icons.xaml`: `model`, `plik`, `folder`, `excel`, `polacz`, `odswiez`, `generuj`, `ustawienia`, `walidacja`, `ostrzezenie`, `zamknij`, `eksport`, `import`, `punkt`.
- Rysuj kontrolką `WfIcon` (`Controls/WfIcon.cs`): `<controls:WfIcon Kind="folder" />`; kontur `Blue300`, szczegół `Cyan`; na przycisku głównym kontur biały, szczegół `Blue100`.
- Własne ikony UI dodawaj do `WF.Icons.xaml` jako dwie geometrie w siatce 24 px: `WF.Icon.<nazwa>` i `WF.Icon.<nazwa>.Detail`, obrys 1,6 px, końce kwadratowe.
- Ikona aplikacji: **każda aplikacja ma własną** `WF.<NazwaAplikacji>.ico` (+ `-48.png` do okna) ze skrótem nazwy (DC, MB, RPM, RC, PG, IFC, WT, MC); źródła w `WF_assets/`. Rozmiary ≤ 48 px (pasek zadań) mają powiększony napis, większe — wersję pełną.

## Zachowanie
- Operacje trwające dłużej (Tekla, Excel, pliki) działają **asynchronicznie** (`Task.Run` + `await`); okno nigdy się nie zawiesza.
- W trakcie pracy: pasek postępu widoczny, wszystkie przyciski akcji nieaktywne (`IsBusy`).
- Postęp przez `IProgress<ProgressInfo>`; wynik i błędy trafiają do logu, nie do okien komunikatów (okno tylko do potwierdzenia operacji zmieniających pliki).
- Operacje zmieniające pliki użytkownika (np. `objects.inp`) pytają o potwierdzenie i robią kopię `.bak`.
- Połączenie z Teklą: `TeklaAssemblyResolver` ładuje biblioteki z katalogu `bin` uruchomionej Tekli; referencje w `.csproj` mają `Private=False`.

## Technika
- Target `net48`, x64, WPF, MVVM (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand` kopiowane do `Infrastructure/`).
- Wiązania właściwości tylko do odczytu (np. `ProgressBar.Value`) muszą mieć `Mode=OneWay`.
- Nazwy atrybutów Tekli (UDA) w API są WIELKIMI literami.

## Lista kontrolna nowej aplikacji
- [ ] Projekt samodzielny, `Assets/WF/` skopiowane, słowniki scalone w `App.xaml`.
- [ ] Okno bez paska systemowego, własny pasek tytułu z `User: …`, kotwicą “Always on top” i przyciskami okna.
- [ ] Nagłówek kompaktowy w jednej linii: logo 32 px + tytuł, model (jeśli dotyczy), akcje narzędziowe.
- [ ] Sekcje w `GroupBox` z ikonami, jeden mały przycisk główny przy swojej sekcji.
- [ ] Log (ok. 10 linii), pasek postępu, operacje asynchroniczne, blokada przycisków w trakcie pracy.
- [ ] Ikona modułu `.ico` ustawiona jako `ApplicationIcon`, napis w ikonie zgodny z nazwą aplikacji.
- [ ] “How it works” jako tooltip tytułu (krótki, ogólny opis).
