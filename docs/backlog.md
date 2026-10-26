# Backlog

Stand: 12.10.2026. Priorität 1 sind die Punkte, die Julian vor dem ersten echten Imaging-Abend gefixt haben will. Danach kommen die Lücken, die beim Testen aufgefallen sind.

## Priorität 1

### Bilder und Anzeige

- [~] **Bilder werden nicht gespeichert.** (Erster Schnitt gebaut: FITS pro Belichtung einer Session, Namens- und Ordnermuster, Header, Hintergrund-Schreiben. Offen: JPG, Backup, Verschieben, Fehlersammlung bei vollem Laufwerk.) Eine Session schreibt keine Dateien (nur Save FITS / Save PNG von Hand auf der Imaging-Seite; `FrameExporter`, `ImagingViewModel`). Gebraucht: jede belichtete Aufnahme einer Session automatisch als FITS in einen Ordner schreiben, mit Metadaten (Ziel, Belichtung, Filter, Kamera, Gain, Zeit, Standort). Ordner und Dateiname einstellbar (Muster wie `Ziel/Filter/Datum_Nr`), Verhalten bei vollem oder fehlendem Laufwerk.
  Ausbau dazu (Julian):
  - [FITS: fertig] Formate **FITS und JPG** (PNG bleibt für Manuelles); einstellbar, welche davon geschrieben werden.
  - [fertig] **Dateinamen einstellbar:** welche Bestandteile im Namen stehen (Ziel, Filter, Belichtung, Datum und Zeit, Nummer, Kamera, Gain, Temperatur, Binning), mit Platzhaltern und Vorschau.
  - [fertig, ohne BAYERPAT] **Richtige FITS-Header** schreiben (Standard-Schlüsselwörter: OBJECT, EXPTIME, DATE-OBS, FILTER, INSTRUME, GAIN, OFFSET, CCD-TEMP, XBINNING, RA/DEC, SITELAT/SITELONG, IMAGETYP, FOCALLEN, XPIXSZ, ROTATANG, Software und Version), so dass PixInsight, Siril und Stacker sie lesen.
  - [fertig, ohne getrennte Kalibrierordner] **Pfad und smarte Ordnerstruktur** wählbar (zum Beispiel `Ziel/Datum/Filter` oder `Datum/Ziel/Frames-Typ`), Lights, Darks, Flats, Bias getrennt.
  - **Auto-Backup** der Bilder (zweites Ziel, zum Beispiel ein Netzlaufwerk oder eine zweite Platte).
  - Option **Verschieben nach der Session** (von einem schnellen lokalen Ordner ins Archiv), mit Prüfung, dass die Dateien vollständig angekommen sind, bevor das Original gelöscht wird.
- [x] **Download-Marker**, solange das Bild von der Kamera geladen wird (die Phase nach der Belichtung): im Status und am Bild sichtbar.
- [ ] **Imaging-Fenster mit dem aktuellen Bild aktualisieren**, nicht nur das Dashboard. Das Bild einer laufenden Session soll auch auf der Imaging-Seite erscheinen.
- [ ] **Latest Image im Dashboard ist anders gestreckt als in Imaging.** Gleiche Stretch-Einstellung (oder eine gemeinsame) verwenden.
- [x] **Manual Capture sperren**, solange eine Sequenz läuft (Button deaktivieren, mit Hinweis warum).

### Dashboard

- [ ] **Mount-Info im Dashboard:** Meridian in (Countdown bis zum Meridian des aktuellen Ziels), aktuelle Position (RA/Dec, Höhe/Azimut), Pier-Seite, Zeit (UTC, Sternzeit), Standort der Montierung (und ob er von Sidera abweicht), Zustand (verbunden, Slewing, geparkt, am Home), Tracking an/aus mit Tracking-Rate.
- [ ] **Auch für die anderen Geräte** Info im Dashboard (Kamera: Temperatur, Kühlung, Zustand; Fokussierer: Position und Temperatur; Filterrad: aktueller Filter; Rotator: Winkel; Guider: RMS und Zustand; Dome: Azimut und Shutter; Session: Ziel, Fortschritt, Rest).
- [ ] **Dashboard anpassbar:** in den Settings einstellen, welche Karten und Infos erscheinen, in welcher Reihenfolge und wie groß (Dashboard bearbeiten).

### Feedback und Navigation

- [~] **Mehr Feedback bei laufenden Schritten und Belichtungen (gebaut: Dauer des aktuellen Schritts in der Statusleiste nach 20 s, "Waiting for the camera: n s over" bei überlaufender Belichtung, Download-Anzeige):** was gerade läuft, wie lange noch, was als Nächstes kommt. Überlaufende Schritte und Belichtungen sichtbar machen, nicht still weiterlaufen lassen.
- [ ] **Geteilte Ansicht (Split) in der App:** eine zweite Seite neben der aktuellen öffnen, zum Beispiel die Session links und Equipment oder Mount rechts, ohne dass man hin- und herspringen muss. Seite für die zweite Hälfte wählbar, Trennlinie verschiebbar, Split wieder schließen; der gewählte Split bleibt beim nächsten Start.
- [x] **Session-Fortschritt auch in Belichtungszeit, nicht nur in Frames:** je Block, Ziel und Session die belichteten und die geplanten Sekunden (Stunden:Minuten), zum Beispiel „23 / 100 Frames · 23:00 / 100:00 min“, dazu die Restzeit.
- [ ] **Modal beim Meridian-Flip** (wie in NINA): wenn der Flip kommt, ein Dialog mit den Schritten und dem Fortschritt, mit Abbrechen. Heute nur die Textzeile in der Session Automation.
- [ ] **Smartere Navigation bei Warnungen.** Beispiel: „The meridian flip needs a setup with a mount“ führt per Klick direkt zu Equipment → Imaging Setups, wo man die Montierung zuweist. Allgemein: jede Warnung und jedes Problem bekommt einen Knopf zur Stelle, an der man es behebt (Toasts und Problemzeilen).
- [ ] **UI springt**, wenn Meldungen erscheinen oder verschwinden. Layout stabil halten (Platz für Meldungen reservieren, nichts verschieben).
- [ ] **Equipment-Seite übersichtlicher.** Es ist nicht klar, wie man zu „alle Geräte“ kommt. Klarer und intuitiver gliedern (Einstieg, Wege zwischen Übersicht, Geräteart und Imaging Setups).
- [ ] **Bessere Verbindung zwischen den Seiten** (Framing, Session, Plate Solve, Dashboard). Beispiel: Ein Ziel aus Framing kann ein **vorhandenes Ziel der Session aktualisieren**, statt immer ein neues hinzuzufügen; als Option „Neu hinzufügen“ oder „Bestehendes ersetzen“.

### Autofokus

- [ ] **Autofokus ist nicht richtig smart:**
  - Läuft weiter in die falsche Richtung, wenn die Sterne größer werden. Soll **umkehren**.
  - **Startet nicht beim aktuellen Fokuswert.** Soll von der aktuellen Position ausgehen.
  - **Merkt sich die letzten Fokuspositionen** (pro Kamera, Filter, Temperatur) und startet dort.
  - **Timeouts** nicht so schnell und nicht so stur (sinnvoll nachfassen, statt abzubrechen).
  - **Kurvenanpassung** mit Hyperbel (HFR²), nicht nur Parabel oder Minimum, plus Fit-Qualität und Ausreißer.
  - **Mehr Einstellungen** zum Algorithmus in Settings → Autofocus (Schrittweite, Anzahl der Punkte, Fit-Art, Timeouts, Richtung, Rückfahrweg für Backlash).

### Standort

- [ ] **Location Mismatch fertig machen.** Die Karte und die drei Knöpfe (Use Mount Location in Sidera, Send Sidera Location to Mount, Keep Both Unchanged) gibt es (`MountSiteViewModel`, `MountWorkspaceView`). Was daran noch fehlt, hat Julian nicht gesagt: klären (Anzeige beim Verbinden einer echten Montierung, Übernehmen, Rückmeldung, Verhalten bei OnStep).

## Priorität 2: Lücken aus den Tests

### Meridian-Flip

- [x] **Pier-Seite nach dem Flip erzwingen.** Bleibt die gemeldete Pier-Seite gleich (`MeridianFlipPierUnchanged`), gilt der Flip heute trotzdem als erledigt (`MeridianFlipGroup.cs`, `FlipVerifyStep`). Soll als Fehler zählen (Wiederholung, dann Halt).
- [ ] **Tracking am Meridian einstellbar pausieren/stoppen** (Julian: manche Montierungen stoßen sonst an und müssen früher aufhören zu tracken). Einstellungen **Hand in Hand mit den anderen Flip-Einstellungen** (Settings → Meridian Flip und Session Automation, gleiche Karte, gleiche Einheit: Minuten vom Meridian):
  - Tracking **vor** dem Meridian pausieren (Minuten vor dem Meridian) und **nach** dem Meridian (Minuten nach dem Meridian), jeweils einzeln einstellbar und abschaltbar.
  - Zusammenspiel mit dem Flip: Die Grenze nach dem Meridian muss mit „Flip after“ und „Latest allowed flip“ zusammenpassen (Warnung, wenn das Tracking vor dem fälligen Flip stoppen würde; Flip hat Vorrang). Ohne Flip: nach der Grenze Imaging beenden und Tracking stoppen, optional parken.
  - Gilt auch, wenn der Flip überfällig ist oder fehlschlägt: Tracking stoppen, optional parken (heute trackt die Montierung dann einfach weiter; nur das Limit der Montierung schützt).
  - **Bei fehlgeschlagenem Flip (Session hält an) Tracking standardmäßig stoppen**, außer die Montierung meldet nach dem Flip die richtige Pier-Seite (dann steht die Mechanik auf der sicheren Seite und Tracking ist unbedenklich). Beim Retry oder Fortsetzen das Tracking wieder einschalten.
  - Nach dem Flip Tracking wieder einschalten; Zustand in der Statuszeile und im Dashboard anzeigen.
- [~] **Zentrieren nach dem Flip robuster machen** (gebaut: Settle-Zeit, Solve-Wiederholung, Tracking einschalten, Retry behält die Korrektur; offen: Sync nach Solve, Warnung bei großem Pointing-Fehler, Fortschritt je Versuch in der Session) (erster echter Flip, 12.10.2026, OnStep; Log: Pointing nach dem Flip etwa 1,2° daneben, Korrektur-Slew landet nicht dort, wo er hinsollte, der Solve danach findet keine Lösung):
  - **Settle-Zeit** nach jedem Korrektur-Slew, vor der nächsten Solve-Aufnahme (Montierung muss ruhig stehen und tracken).
  - **Retry des Flips fährt erneut auf das unkorrigierte Ziel** und verwirft die bisherigen Korrekturen. Soll die Korrektur behalten (oder einen bereits geflippten Zustand erkennen und den Slew auslassen).
  - Die Meldung „same side of pier after the flip“ ist bei einem Retry irreführend (die Montierung stand schon auf der richtigen Seite).
  - **Sync nach erfolgreichem Solve** als Option (nur mit Bestätigung), damit das Montierungsmodell nachzieht; sonst wiederholt jeder Slew denselben Versatz.
  - **Warnung bei großem Pointing-Fehler** (zum Beispiel > 0,5°): Hinweis, Standort, Uhrzeit und Ausrichtung der Montierung zu prüfen, statt still weiter zu versuchen.
  - [x] **Tracking vor jeder Solve-Aufnahme prüfen** (Julian, 12.10.: die OnStep stand nach dem Korrektur-Slew auf „Not tracking“, die Sterne verschmierten, der Solve scheiterte). Ist das Tracking aus, melden und (nach Rückfrage oder per Einstellung) wieder einschalten, statt zu solven.
  - Fortschritt der Zentrierversuche in der Session anzeigen (Fehler in ″ je Versuch).
- [x] **Flip-Fehler melden:** Toast oder Ton, wenn ein Flip fehlschlägt und die Session wartet. Optional: Montierung parken.
- [~] **Koordinaten auf das heutige Datum umrechnen (Präzession).** (Meridian-Zeit: fertig. Slew an eine Topocentric-Montierung: noch offen.) Sidera nimmt J2000-Koordinaten unverändert. Folgen: Meridian-Zeit etwa 80 s zu früh (bei M15), Slew an eine Topocentric-Montierung etwa 22′ daneben (Slew & Center gleicht es aus). Betrifft `MeridianFlipGroup.HourAngleMinutes` und den Slew.
- [ ] **OnStep:** Auto-Flip und Preferred Pier Side der Firmware dürfen den Flip von Sidera nicht stören; Sidera sollte das anzeigen oder prüfen.
- [ ] Flip-Schritte in der Tree-Ansicht prüfen (nicht angesehen).

### Hardware

- [ ] Nie an echter Hardware gelaufen: Slew, Abort und Tracking einer echten Montierung (OnStep), Guiding und Dither über PHD2, Dome und Rolldach, ASCOM-Filterrad.
- [ ] Dome folgt der Montierung nicht (kein Slaving).
- [ ] Echter Meridian-Flip mit der OnStep-Montierung, erster Test.

### Sonstiges

- [ ] Help: ein paar Marker liegen leicht auf Text; Red Night und Light nicht angesehen.
- [ ] Hardware-Tests: ProgIds aus der `equipment.json` lesen, statt `SIDERA_ASCOM_*` zu setzen.
- [ ] Ein OmniSim lauscht auf `0.0.0.0:32323`; Tests sollten ihn nur an `127.0.0.1` binden.
