# Pictures of the Help Center

The pictures in `src/Sidera.Desktop/Assets/Help` are screenshots of the real application. The numbered markers are not part of the pictures: they are fractions of the picture in the articles
(`Help/HelpArticles.*.cs`), drawn by `CalloutImage`. A picture can be taken again without marking it again, as long as the page keeps its layout.

1. Build Release: `dotnet build src/Sidera.Desktop -c Release`.
2. Take the page with `ui_final.ps1` (it starts the application with its own equipment and settings files in `%TEMP%`, steps through the page by UI Automation and draws the window by `PrintWindow`:
   it needs neither the focus nor the front). Use the Sidera Dark theme and the same window size for every picture.
   `./ui_final.ps1 -Setup two -Prefix hw -Steps "btn:Equipment|shot:equipment"` leaves `hw_equipment.png` in `%TEMP%\sidera-help-shots` (`SIDERA_SHOTS` overrides the folder).
3. Write it to the assets: `python quantize.py <screenshot> <picture name>` (1280 x 820, reduced palette, about 30 KB each).
4. Look at the article in Help: a marker must not cover the text it points at. Move it in the article, not in the picture.

`HelpTests` fails when a picture of an article is missing and when a picture belongs to no article.
