# MatSMTP

Ein SMTP-Relay für das eigene Netz: Anwendungen tragen MatSMTP als **Smarthost** ein (mit oder ohne Anmeldung, mit STARTTLS oder TLS), MatSMTP nimmt
ihre Mails an, entscheidet nach **Regeln**, was mit ihnen geschieht, und arbeitet sie als **Warteschlange** über hinterlegte SMTP-Konten ab, oder gibt
sie per **Webhook** an andere Systeme weiter. ASP.NET Core 10 Razor Pages, SQLite, ein Container (Web-Oberfläche auf Port **2525**). Das Aussehen
folgt MatDesign (MatUi).

![Übersicht](docs/images/dashboard.png)

## Was MatSMTP kann

- **Empfang (Smarthost):** SMTP mit STARTTLS (25), Submission (587) und SMTP über TLS (465). Aus **vertrauenswürdigen Netzen** (CIDR-Liste) ohne
  Anmeldung, sonst mit einem **Zugang** (SMTP AUTH PLAIN/LOGIN). Ein Zugang kann auf bestimmte Absender beschränkt werden. Zertifikat: selbst signiert
  (automatisch) oder hochgeladen (PFX oder PEM). Zehn falsche Passwörter von einer Adresse sperren deren Anmeldung für 15 Minuten.
- **SMTP-Konten:** Die Konten, über die versendet wird (Microsoft 365, ein Mail-Hoster, der Server der Firma). Ein **Standardkonto** nimmt alles, wofür
  keine Regel ein anderes wählt. Pro Konto: Verschlüsselung, Anmeldung, **Absender erzwingen** (der alte landet in Reply-To), Grenzen pro Minute und Tag,
  Verbindung prüfen und Testnachricht.
- **Regeln (Policy Based Routing):** Bedingungen nach Absender, Empfänger, IP-Netz des Clients, Anmeldung bzw. Zugang und Betreff (Domains, Platzhalter,
  reguläre Ausdrücke). Aktionen: über ein bestimmtes Konto **versenden**, **Absender umschreiben** (`noreply@firma.de`, `{local}@firma.de`, Anzeigename,
  Reply-To auf den alten Absender), **Webhook auslösen**, **Blindkopie** (z. B. Archiv), **nur an Webhook**, **abweisen** (SMTP 550 mit eigenem Text) oder
  **verwerfen**. Geprüft wird von oben nach unten wie bei einer Firewall: jede passende Regel wendet ihre Änderungen an, die erste mit einer Entscheidung
  beendet die Prüfung. Ein **Regel-Tester** zeigt vorab, was mit einer Nachricht passieren würde.
- **Warteschlange (Spooler):** Jede Nachricht wird als .eml gespeichert und versendet; vorübergehende Fehler (4xx, Netz, Zeitüberschreitung) werden nach
  einstellbaren Wartezeiten wiederholt, dauerhafte (5xx) beenden die Versuche. Gleichzeitigkeit, Pause, Aufbewahrung von Nachrichten und Inhalt.
  Jede Nachricht hat einen **Verlauf** (Empfang, Regeln, jeder Versuch mit der Antwort des Servers, Webhooks); erneut senden und löschen auch für viele auf einmal.
- **Webhooks:** HTTP-POST mit JSON (nur Metadaten, mit Text/HTML/Anhangsnamen oder die vollständige .eml als Base64), optional signiert
  (`X-MatSMTP-Signature: t=…,v1=HMAC-SHA256`), eigene Kopfzeilen, Wiederholung bei Fehlern. So wird aus „Mail empfangen“ z. B. ein Ticket.
- **Freigabe-Links (Rolle „Anonym“):** ein zeitlich begrenzter Link, der jemandem ohne Konto die **Zustellung** einer Nachricht zeigt (Status, Verlauf,
  Antwort des Servers), nie den Inhalt.
- **Backup mit Zeitplänen** direkt auf ein **NAS (SMB)** oder in einen Ordner, mit **Aufbewahrung** (letzte N, täglich, wöchentlich, monatlich), optional
  verschlüsselt (AES-256-GCM). Gesichert werden Datenbank, Einstellungen, Schlüssel und Zertifikate, auf Wunsch auch die Nachrichteninhalte;
  Wiederherstellung mit Sicherheitskopie des aktuellen Stands.
- **Benutzer und Rollen:** lokale Anmeldung, **TOTP** (Authenticator-App, Wiederherstellungscodes, für Administratoren erzwingbar), Sitzungen in der
  Datenbank (überstehen einen Neustart des Containers). Rollen: **Administrator** (alles), **Benutzer** (Übersicht, Nachrichten, erneut senden,
  Freigaben, Regel-Tester), **Anonym** (Freigabe-Link).

| Nachrichten | Eine Nachricht |
|---|---|
| ![Nachrichten](docs/images/messages.png) | ![Nachricht](docs/images/message.png) |

| Regeln | Regel bearbeiten |
|---|---|
| ![Regeln](docs/images/rules.png) | ![Regel](docs/images/rule-edit.png) |

| Regel-Tester | SMTP-Konto |
|---|---|
| ![Regel-Tester](docs/images/rule-test.png) | ![Konto](docs/images/account-edit.png) |

| Empfang | Webhook |
|---|---|
| ![Empfang](docs/images/admin-smtp.png) | ![Webhook](docs/images/webhook-edit.png) |

| Backup-Ziel (NAS) | Freigabe-Link |
|---|---|
| ![Backup-Ziel](docs/images/admin-backup-targets.png) | ![Freigabe](docs/images/share.png) |

| Dunkel | Mobil |
|---|---|
| ![Dunkel](docs/images/dark-dashboard.png) | ![Mobil](docs/images/mobile-messages.png) |

## Betrieb

```yaml
# docker-compose.yml (Release): ein Container, ein Volume
services:
  matsmtp:
    image: ghcr.io/real-ttx/matsmtp:latest
    ports:
      - "2525:2525"   # Weboberfläche
      - "25:2025"     # SMTP (STARTTLS)
      - "587:2587"    # Submission (STARTTLS)
      - "465:2465"    # SMTP über TLS
    volumes:
      - matsmtp-data:/data
```

`docker compose up -d`, dann `http://<host>:2525` öffnen: der erste Aufruf legt den Administrator an und führt zum ersten SMTP-Konto.
Im Container lauscht SMTP auf den unprivilegierten Ports 2025/2587/2465; die Compose-Datei gibt sie als 25/587/465 nach außen.

Das Volume `/data` enthält alles: `db/matsmtp.db` (SQLite: Regeln, Konten, Zugänge, Webhooks, Nachrichten, Benutzer, Sitzungen), `spool/` (die .eml der
Nachrichten), `config/app.json` (Einstellungen), `keys/` (Schlüssel für Cookies und gespeicherte Passwörter), `certs/` (TLS), `backups/`, `tmp/`.

Umgebungsvariablen (optional): `MATSMTP__Server__PublicUrl` (Adresse für Freigabe-Links), `MATSMTP__Server__TimeZone`, `MATSMTP__Server__ForceSecureCookie`,
`MATSMTP__Smtp__Port`, `MATSMTP__Smtp__SubmissionPort`, `MATSMTP__Smtp__TlsPort` (feste Ports, auf der Seite „Empfang“ dann nicht änderbar), `MATSMTP_PORT` (Web).

Die Weboberfläche spricht HTTP: für den Zugriff von außen einen Reverse-Proxy mit TLS davor setzen.

## Entwicklung

```powershell
./scripts/redeploy.ps1          # Image neu bauen und den Dev-Stack neu starten (der Live-Reload/Test-Weg des Projekts)
./scripts/redeploy.ps1 -Fresh   # dasselbe mit leerem Volume (Einrichtung erneut)
dotnet build MatSmtp.slnx
dotnet test MatSmtp.slnx        # Unit- und Integrationstests: die ganze App im Speicher, echter SMTP-Client gegen den Empfang, Fake-Upstream, Webhook-Empfänger
```

Der Dev-Stack (`docker-compose.dev.yml`): **MatSMTP** auf `http://localhost:2525` (SMTP auf 2025/2587/2465), **sqlite-web** als Datenbank-Oberfläche
auf `http://localhost:2526` (für SQLite das, was pgAdmin für PostgreSQL ist) und **Mailpit** auf `http://localhost:2527`: ein SMTP-Server, der alles
behält. Als SMTP-Konto eintragen: Server `mailpit`, Port `1025`, keine Verschlüsselung, keine Anmeldung. Eine Testmail von der Kommandozeile:

```bash
curl --url smtp://localhost:2025 --mail-from app@example.com --mail-rcpt kunde@example.org -T mail.txt
```

Die SMB-Tests brauchen einen Samba-Server (`MATSMTP_TEST_SMB=host;share=backups;user=backup;password=backup-secret`; die CI startet einen).

## Versionen und Branches

`main` = Release, `dev` = Entwicklung. Die GitHub Action baut bei jedem Push das Image (`ghcr.io/real-ttx/matsmtp`):
Release `<major>.<minor>.<build>-<datum>` (Tags `latest`, `release`), Entwicklung `nightly-<build>-<datum>` (Tag `nightly`), lokal `local-<datum>`.
