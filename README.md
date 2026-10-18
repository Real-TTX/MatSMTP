<div align="center">

<img src="src/MatSmtp/wwwroot/img/icon-192.png" width="96" alt="MatSMTP" />

# MatSMTP

**An SMTP relay for your network – with rules, a queue and webhooks.**

Applications send to one smarthost; MatSMTP decides how each mail leaves: through which account, with which sender, or to a webhook.
One container, SQLite, no cloud, no third-party services.

</div>

![The overview with today's numbers, the last 24 hours, the receiver and the accounts](docs/images/dashboard.png)

---

## What this is about

Every printer, NAS, shop, monitoring tool and line-of-business app wants to send mail – and every one of them needs its own SMTP account,
its own password, its own idea of TLS. When the provider changes, you touch twenty devices. MatSMTP sits in between: everything in the network
sends to MatSMTP, MatSMTP sends on. It keeps every message in a queue, retries what failed, shows what happened to each mail, and lets you decide
by rules which account, which sender and which webhook a message gets.

## At a glance

**Receiving (smarthost)**
- **SMTP with STARTTLS (25), submission (587) and SMTP over TLS (465)**
- From **trusted networks** (CIDR list) without signing in, from everywhere else with a **relay user** (SMTP AUTH PLAIN/LOGIN)
- A relay user can be **limited to its own senders** (`shop.example.com`, `*@billing.example.com`)
- TLS certificate: **self-signed automatically**, or **uploaded** (PFX or PEM with key)
- Ten wrong passwords from one address lock that address out for 15 minutes

**Rules – policy based routing**
- Conditions on **sender, recipient, client network, relay user and subject** – domains, wildcards, regular expressions
- Actions: send through a **specific account**, **rewrite the sender** (`noreply@example.com`, `{local}@example.com`, display name,
  Reply-To to the old sender), **trigger a webhook**, add a **blind copy**, **webhook only**, **reject** (SMTP 550 with your text) or **drop**
- Evaluated top to bottom like a firewall: every matching rule applies its changes, the first one with a decision ends it
- A **rule tester** shows what would happen to a message before one is sent

**Sending (spooler)**
- Several **SMTP accounts** (Microsoft 365, a mail host, the company server); a **default account** takes what no rule routes elsewhere
- Per account: encryption, sign-in, **forced sender** (the original goes to Reply-To), **limits per minute and per day**, connection check, test mail
- Temporary errors (4xx, network, timeout) are **retried** after configurable waits, permanent ones (5xx) end it
- Every message has its **history**: received, rules, every attempt with the server's answer, webhooks; send again or delete, one or many

**Webhooks**
- HTTP POST with **JSON**: metadata only, with text/HTML/attachment names, or the **complete .eml** (Base64)
- Optionally **signed** (`X-MatSMTP-Signature: t=…,v1=` HMAC-SHA256), extra headers, retries – a mail becomes a ticket

**Users and operations**
- Local sign-in with **TOTP** (authenticator app, recovery codes, enforceable for administrators)
- Roles **Administrator**, **User** (overview, messages, send again, share links, rule tester) and **Anonymous** (a share link)
- **Share links** show someone without an account whether and when a mail was delivered – never its content
- **Scheduled backups** to a **NAS over SMB** or a folder, with **retention** (last N, daily, weekly, monthly), optionally encrypted, restore with a safety copy
- Sessions survive a container restart, **activity log**, light and dark, works on a phone, installable as an app (PWA)

## Screenshots

### Every message and what became of it

![The list of messages with toolbar, selection and status](docs/images/messages.png)

Search, filter (status, account, period, sign-in) and sort above the list; below it, for the selected rows, *Send again* and *Delete*.
A rewritten sender shows the original right underneath.

### One message: envelope, content, history

![A message with envelope, content tabs, history, delivery and origin](docs/images/message.png)

The envelope as it left, the original sender, text and headers, every step in the history, the answer of the receiving server –
and a share link that shows the delivery to someone outside.

### Rules and the rule tester

| Rules in their order | Editing a rule |
|---|---|
| ![The rules with conditions, actions and hits](docs/images/rules.png) | ![A rule with conditions and actions](docs/images/rule-edit.png) |

The order is the logic: arrows move a rule, the list shows what it matches, what it does and how often it hit.
Fields that do not apply to the chosen action stay hidden.

| Rule tester | Test mail through the whole relay |
|---|---|
| ![The rule tester with its result](docs/images/rule-test.png) | ![Submitting a test mail](docs/images/tools-send.png) |

### Accounts, relay users, webhooks

| SMTP account | Relay users | Webhook |
|---|---|---|
| ![An SMTP account with server, sign-in and limits](docs/images/account-edit.png) | ![The relay users](docs/images/relayusers.png) | ![A webhook with payload, signature and sample](docs/images/webhook-edit.png) |

### Receiving, delivery and backup

| Receiving (ports, networks, certificate) | Delivery (retries, retention) | Backup target on a NAS |
|---|---|---|
| ![Receiver settings](docs/images/admin-smtp.png) | ![Delivery settings](docs/images/admin-delivery.png) | ![A backup target over SMB](docs/images/admin-backup-targets.png) |

### Share link, dark theme and narrow screens

| Share link (anonymous) | Dark | Phone |
|---|---|---|
| ![The delivery behind a share link](docs/images/share.png) | ![Dark theme](docs/images/dark-dashboard.png) | ![Messages on a phone](docs/images/mobile-messages.png) |

## Quick start

Ready-made images are published to the GitHub Container Registry:

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matsmtp:latest` | `main` | releases |
| `ghcr.io/real-ttx/matsmtp:nightly` | `dev` | the newest features |

### 1. Just run it

Copy this into `docker-compose.yml` and start it – nothing else needed:

```yaml
services:
  matsmtp:
    image: ghcr.io/real-ttx/matsmtp:latest
    container_name: matsmtp
    restart: unless-stopped
    ports:
      - "2525:2525"   # web interface
      - "25:2025"     # SMTP with STARTTLS
      - "587:2587"    # submission with STARTTLS
      - "465:2465"    # SMTP over TLS
    environment:
      TZ: Europe/Berlin
    volumes:
      - matsmtp-data:/data

volumes:
  matsmtp-data:
```

```bash
docker compose up -d
```

Open **http://localhost:2525**: the first visit creates the administrator and then asks for the first SMTP account – the one MatSMTP sends
through. Make it the default account, and the relay works. Health check: `/health`. An update is just `docker compose pull && docker compose up -d`.

Without Compose:

```bash
docker run -d --name matsmtp -p 2525:2525 -p 25:2025 -p 587:2587 -p 465:2465 \
  -v matsmtp-data:/data ghcr.io/real-ttx/matsmtp:latest
```

Inside the container SMTP listens on the unprivileged ports 2025, 2587 and 2465; the mapping above makes them 25, 587 and 465 on the host.
If port 25 is taken on the host (a local mail server), map another one: `- "2025:2025"`.

### 2. In production

A real host name, the web interface behind a reverse proxy with TLS, the public address for share links, and backups to a NAS:

```yaml
services:
  matsmtp:
    image: ghcr.io/real-ttx/matsmtp:latest
    container_name: matsmtp
    restart: unless-stopped
    ports:
      - "127.0.0.1:2525:2525"   # web interface only for the reverse proxy on this host
      - "25:2025"
      - "587:2587"
      - "465:2465"
    environment:
      TZ: Europe/Berlin
      MATSMTP__Server__PublicUrl: https://smtp.example.com      # for share links
      MATSMTP__Server__ForceSecureCookie: "true"                # the proxy speaks HTTPS
    volumes:
      - matsmtp-data:/data
      # Backups go to a NAS over SMB from the app (Einstellungen > Backup > Ziele); a mounted folder works as well:
      # - /mnt/nas/matsmtp-backups:/backups

volumes:
  matsmtp-data:
```

Then in the web interface:

1. *Einstellungen → Empfang*: set the **server name** (it appears in the greeting, the Received header and the self-signed certificate) and check the
   **trusted networks** – everything else has to sign in. Upload a certificate for the server name if your applications verify it.
2. *SMTP-Konten*: one **default account**, more if some senders have to leave through another provider.
3. *Zugänge* (relay users): one per application that sends from outside the trusted networks, limited to its senders.
4. *Regeln*: route, rewrite, hand to webhooks, reject. Try them under *Regeln testen*.
5. *Einstellungen → Backup*: a target (NAS share) and a plan with retention. *Einstellungen → Sicherheit*: enforce TOTP for administrators.

> Keep the trusted networks narrow. `0.0.0.0/0` there makes MatSMTP an open relay.

### 3. Pointing applications at MatSMTP

| What | Setting |
|---|---|
| Any application / device | SMTP server: the Docker host, port **587** with STARTTLS (or 25, or 465 with TLS); a relay user if it is not in a trusted network |
| Postfix | `relayhost = [smtp.example.com]:587` (plus `smtp_sasl_*` with a relay user from outside the trusted networks) |
| Synology / TrueNAS notifications | SMTP server and port as above, "Secure connection: STARTTLS" |
| Printer / scanner (scan to mail) | Often no TLS: port 25 from a trusted network |
| A quick test | `curl --url smtp://localhost:25 --mail-from app@example.com --mail-rcpt you@example.org -T mail.txt` |

### 4. From source

```bash
docker compose -f docker-compose.dev.yml up -d --build    # development stack
./scripts/redeploy.ps1                                    # the same on Windows (rebuild + redeploy)
```

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `MATSMTP_PORT` | `2525` | Port of the web interface inside the container |
| `MATSMTP_DATA` | `/data` | Data directory (SQLite, queue, configuration, keys, certificates) |
| `MATSMTP__Server__PublicUrl` | – | The public address, for share links |
| `MATSMTP__Server__TimeZone` | `Europe/Berlin` | Time zone for dates and backup schedules |
| `MATSMTP__Server__ForceSecureCookie` | `false` | Cookie only over HTTPS (behind a TLS proxy) |
| `MATSMTP__Smtp__Port` | `2025` | SMTP port inside the container (`0` = off); set here, the page *Empfang* shows it as fixed |
| `MATSMTP__Smtp__SubmissionPort` | `2587` | Submission port inside the container |
| `MATSMTP__Smtp__TlsPort` | `2465` | SMTPS port inside the container |
| `TZ` | `Europe/Berlin` | Time zone of the container |

Everything else – trusted networks, sign-in rules, size limit, retries, retention, pause – lives in the web interface and in `/data/config/app.json`.

### The `/data` volume

```
/data
├─ db/matsmtp.db   SQLite: rules, accounts, relay users, webhooks, messages, users, sessions
├─ spool/          the messages as .eml (removed after the retention of the content)
├─ config/         app.json – the settings
├─ keys/           DataProtection keys (sessions and stored passwords survive restarts)
├─ certs/          TLS certificate of the receiver
├─ backups/        local backup targets and safety copies before a restore
└─ tmp/            scratch space of the backup
```

A backup holds the database, the settings, the keys and the certificates – and, if the plan says so, the content of the queued messages.

## Development

```powershell
./scripts/redeploy.ps1          # rebuild the image and redeploy the dev stack (the project's live-reload / test workflow)
./scripts/redeploy.ps1 -Fresh   # the same with an empty volume (setup again)
dotnet build MatSmtp.slnx
dotnet test MatSmtp.slnx
```

The development stack (`docker-compose.dev.yml`):

| Service | Address | What for |
|---|---|---|
| MatSMTP | http://localhost:2525, SMTP on 2025 / 2587 / 2465 | the app, built from this folder |
| sqlite-web | http://localhost:2526 | the database UI of the stack (what pgAdmin is for PostgreSQL) |
| Mailpit | http://localhost:2527, SMTP on 2528 | a mail server that keeps everything: as an SMTP account use server `mailpit`, port `1025`, no TLS, no sign-in |

The tests need no container: the whole app runs in memory (SQLite in a temporary folder, the receiver on free ports), a real SMTP client sends to it,
a fake upstream server takes what the spooler sends, a small web server receives the webhooks. The SMB backup tests need a Samba server
(`MATSMTP_TEST_SMB=host;share=backups;user=backup;password=backup-secret`; the CI starts one). The browser check (`tests/e2e/flow.js`, Playwright
with the installed Edge) sets up a fresh stack through its forms, sends real mails and takes the screenshots of this page.

## How it is built

- **ASP.NET Core 10** Razor Pages, **EF Core** with **SQLite** (WAL)
- Receiving: **SmtpServer**; reading and rewriting messages: **MimeKit**; sending: **MailKit**; backups to a NAS: **SMBLibrary**
- The rule engine, patterns, networks and sender rewriting are a library without web types (`MatSmtp.Core`), covered by unit tests
- Content first, then the row: a message is on disk before it is acknowledged; a crash puts messages that were being sent back into the queue
- Secrets (account passwords, webhook secrets, the NAS password) encrypted via **DataProtection**; relay user and user passwords hashed (PBKDF2-SHA512)
- The look is **MatUi** from MatDesign (copied into `wwwroot/ui`, never loaded from elsewhere) – plain CSS and JavaScript, no build step,
  a strict content security policy

The interface is German (menu names in this README are given as they appear there); code, comments and the notes for development are English.

## Branches & versioning

| Branch | Purpose | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<build>-<yyyyMMdd>` |
| local | – | `local-<yyyyMMdd>` |

`Major`/`Minor` live in [`Directory.Build.props`](Directory.Build.props), the build number comes from the GitHub action (`github.run_number`).
Images are published to the GitHub Container Registry.
