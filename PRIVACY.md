# Datenschutz in OpenClean

OpenClean sammelt **keine Telemetrie**, benötigt **kein Konto** und läuft ohne
Hintergrunddienst. Die App nimmt im normalen Betrieb **keinerlei Netzwerkverbindung**
auf.

## Die einzige Ausnahme: Premium-Lizenz (optional)

Wer OpenClean Premium kauft und aktiviert, löst die einzigen Netzwerkzugriffe der App
aus – ausschließlich zu `daonware.de`:

| Wann | Was wird übertragen |
|---|---|
| Lizenz aktivieren (manuell im Dialog) | Lizenzschlüssel, anonymer Geräte-Hash*, App-Version |
| Lizenzprüfung beim Start und beim geplanten Reinigungslauf (**nur wenn bereits eine Lizenz vorhanden ist** – erneuert das Token und erkennt einen serverseitigen Widerruf; Free-Nutzer lösen dies nie aus) | Lizenzschlüssel, anonymer Geräte-Hash*, App-Version |
| Gerät deaktivieren (manuell) | Lizenzschlüssel, anonymer Geräte-Hash* |
| Premium-Modul-Download (nach Aktivierung) | kurzlebiger Download-Token, App-Version |

\* Der Geräte-Hash ist ein SHA-256-Hash der Windows-MachineGuid mit App-Präfix.
Er lässt sich nicht auf den Rechner oder die Person zurückrechnen; es wird nie eine
Roh-Kennung übertragen. Er dient allein der Begrenzung „Lizenz auf max. 3 Geräten“.

## Was der Lizenzserver speichert

- E-Mail-Adresse (aus dem Kauf – für Schlüsselversand und Support)
- SHA-256-Hash des Lizenzschlüssels (nie der Schlüssel selbst)
- Geräte-Hashes und Zeitstempel der Aktivierungen

Keine Konten, keine Passwörter, keine Nutzungs- oder Systemdaten.

## Offline-Nutzung

Die Lizenz wird lokal über eine signierte Datei (`license.json`) geprüft und
funktioniert **bis zu 30 Tage vollständig offline**. Besteht eine Internet-
verbindung, prüft die App die Lizenz beim Start kurz gegen den Server (Token-
Erneuerung; ein widerrufener Schlüssel wird dabei erkannt und lokal entfernt).
Ohne Internet läuft Premium weiter, bis der letzte erfolgreiche Server-Kontakt
30 Tage zurückliegt; danach ist einmalig eine Online-Prüfung erforderlich.

Die Zahlungsabwicklung erfolgt über Stripe (siehe deren Datenschutzerklärung);
OpenClean selbst erhält davon nur E-Mail-Adresse und Bestellreferenz.
