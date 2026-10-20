# WF BIM — pakiet znaku i ikon

Wszystkie pliki znajdują się w folderze `WF_assets/`. Podgląd wszystkich elementów: `WF BIM UI Kit v4.dc.html`.

## Zawartość
- WF_assets/wf-bim-mark.svg — znak pełny (monolit 3 warstwy), 240×150; od 96 px wysokości.
- WF_assets/wf-bim-mini.svg — znak uproszczony; do ikon ≤ 48 px.
- WF_assets/wf-bim-lockup-dark.svg, wf-bim-lockup-light.svg — znak + napis WF | BIM (fonty: Michroma, Rajdhani, IBM Plex Mono).
- WF_assets/WF.<Moduł>.ico — ikony aplikacji 16/24/32/48/64/128/256 px: Fazy, Numeracja, IFC, Kontrola, Zestawienia, Rysunki.
- WF_assets/WF.<Moduł>-<rozmiar>.png — te same ikony jako PNG; WF_assets/WF.<Moduł>.svg — wektor.
- WF_assets/ — 12 ikon UI jako SVG (siatka 24 px): eksport, excel, folder, generuj, model, odswiez, ostrzezenie, plik, polacz, ustawienia, walidacja, zamknij.
- WF_assets/WF.Colors.xaml — paleta (Color + Brush), gradient główny, fonty.
- WF_assets/WF.Icons.xaml — ikony UI jako Geometry (WF.Icon.<nazwa> + .Detail).
- WF_assets/WF.Controls.xaml — style: Button.Primary, Button.Secondary, TextBox, DataGridHeader.
- WF_assets/WF.Theme.xaml — niejawne style kontrolek WPF: Button (plaski, obrys, hover), ComboBox (ciemne pole i lista z cieniem), TabControl/TabItem (separator, podkreslenie akcentem), ProgressBar; pedzle pochodne (gradienty, Hover, Secondary, Danger, Shadow). Scalaj po WF.Controls.xaml; lokalne style tych typow z BasedOn="{StaticResource {x:Type ...}}".

## Użycie w WPF
    <Application.Resources>
      <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
          <ResourceDictionary Source="WF_assets/WF.Colors.xaml"/>
          <ResourceDictionary Source="WF_assets/WF.Icons.xaml"/>
          <ResourceDictionary Source="WF_assets/WF.Controls.xaml"/>
          <ResourceDictionary Source="WF_assets/WF.Theme.xaml"/>
        </ResourceDictionary.MergedDictionaries>
      </ResourceDictionary>
    </Application.Resources>

Ikona okna: Icon="pack://application:,,,/WF_assets/WF.Fazy.ico" (Build Action: Resource). Ikona exe: Właściwości projektu → Application → Icon.

## Zasady znaku
- Pole ochronne: wysokość jednej warstwy znaku z każdej strony.
- Minimum: pełny 96 px wysokości, uproszczony 16 px.
- „WF" (lewa ścianka dolnej warstwy) i „BIM" (prawa) są częścią znaku — nie przerysowuj.

## Uwagi
- Napisy w ikonach (FZ, NUM…) w PNG/ICO mogą być wyrenderowane czcionką zastępczą, jeśli Chakra Petch nie była dostępna podczas eksportu.
- Pliki ICO zawierają obrazy PNG (Windows Vista+).
