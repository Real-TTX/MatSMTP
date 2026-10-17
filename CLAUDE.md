# MatSMTP: notes for working in this repository

An SMTP relay (smarthost) with rules, a queue over stored SMTP accounts, sender rewriting and webhooks: ASP.NET Core 10 Razor Pages, EF Core 10 on **SQLite**,
one container (web **2525**; SMTP 2025/2587/2465 inside, 25/587/465 outside). The user-facing text is **German**; code, comments and docs are English.
The infrastructure (setup, sign-in, TOTP, sessions, backup with NAS, UI kit) came from `../MatBill` / `../KommasKorrektus` and keeps their style: when in doubt,
look there. The look follows `../MatDesign` (MatUi toolkit, rule book).

## Commands

```powershell
./scripts/redeploy.ps1            # always: rebuild the image and redeploy the dev stack (web 2525, sqlite-web 2526, Mailpit 2527 / SMTP 2528)
./scripts/redeploy.ps1 -Fresh     # the same with an empty volume (setup page again)
dotnet build MatSmtp.slnx         # build locally BEFORE a redeploy: a compile error otherwise shows up as "docker compose failed"
dotnet test MatSmtp.slnx          # everything in memory: SQLite in a temp folder, the SMTP receiver on free ports, a fake upstream (FakeUpstream), a webhook receiver
./scripts/check-matui.ps1         # is there news in MatDesign for the toolkit copy in wwwroot/ui?
./scripts/sync-matui.ps1 -Archive <matui.zip> -Commit <sha>   # take a new toolkit version
NODE_PATH=<folder with playwright-core>/node_modules node tools/icons.js   # PNG icons from logo.svg / img/icon-maskable.svg (installed Edge)
```

Test and look at changes by **rebuilding the container and redeploying the stack**, not with a watcher. Wait for `http://localhost:2525/health`. A real mail:
`curl --url smtp://localhost:2025 --mail-from a@example.com --mail-rcpt b@example.org -T mail.txt` (SMTP account "mailpit", port 1025, no TLS, no auth catches it).
A migration: `dotnet ef migrations add <Name> -p src/MatSmtp -s src/MatSmtp -o Data/Migrations` (`dotnet tool restore` first).

## Layout

- `src/MatSmtp.Core`: no web types. `Relay/` = `AddressPattern` (the patterns of rules), `IpNetwork(s)` (CIDR), `RuleEngine` + `SenderRewrite` (the decision),
  `RetrySchedule`, `WebhookSignature`; `Security/` (TOTP, password hashing). All covered by `tests/.../Core/RelayRuleTests.cs`.
- `src/MatSmtp/Services/Relay/`: `SmtpListener` (hosted, SmtpServer library: endpoints, trusted networks / AUTH, restarts on a change of the settings),
  `RelayService` (receive: parse, rules, rewrite, Received header, spool file, message row with history, webhook deliveries), `DeliveryService` + `DeliveryWorker`
  (the spooler: MailKit, limits, retries, permanent vs temporary), `WebhookService`, `SpoolStore` (`{data}/spool/yyyy/MM/dd/<guid>.eml`, keys checked before they
  become paths), `CertificateService` (uploaded PFX/PEM or a kept self-signed one), `SpoolSignal`.
- `Pages/`: `Index` (overview), `Messages/` (list + queue share `MessageListModel` and `_MessageList`, `Details`), `Rules/`, `RuleTest`, `Accounts/`, `RelayUsers/`,
  `Webhooks/`, `Tools/Send`, `Share/Index` (`/s/{token}`, anonymous), `Admin/` (Smtp, Delivery, Users, Shares, Backup, Security, Activity, System), `Account/`.
- `UiKit/` (table with optional selection, toolbar, pager, shell/menu, labels), `Controls/` (tag helpers `ms-*`), `wwwroot/` (app.css, app.js, `ui/` = MatUi copy).

## Conventions

- **Database**: SQLite in `{data}/db/matsmtp.db`, WAL, tables PascalCase, `Id` long (SQLite INTEGER = 64 bit), audit columns `CreateDate/CreateUserId/UpdateDate/UpdateUserId`
  (filled by `MsDbContext`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`ISoftDeletable`: User, SmtpAccount, RelayUser, Webhook, Rule), enums as text,
  a `Token` uuid for what acts as a secret (`UserSession`, `ShareLink`). JSON is for configuration (`{data}/config/app.json`: Server, Smtp, Delivery, Security, Backup);
  logic lives in the database. Search uses `EF.Functions.Like(x, Like.Contains(text), Like.Escape)` (no ILIKE in SQLite).
- **Rules** are evaluated like a firewall (`RuleEngine`): every matching rule applies its changes, the first with a decision (Relay, WebhookOnly, Reject, Drop) ends it,
  no decision = default account. Keep the engine pure and tested; pages and the receiver only map entities to `RuleSpec` (`RelayService.ToSpec`).
- **Never lose a message**: content first (spool file), then the row; a message being sent is `Sending` (conditional update), `RecoverAsync` puts it back after a crash.
  5xx = permanent, everything else is retried. Webhook deliveries are rows too (survive restarts).
- **UI**: lists = toolbar (search, filter, sort) above, table, pager directly below, actions below on the left (delete spaced apart with `mat-btn-row__spacer`);
  create / edit on a separate page; buttons in one row positive to negative (`ms-buttons`). Dependent fields with `show-if` / `data-show-if` (hidden fields are disabled
  and not posted). No inline scripts or styles (CSP); a value that is data is a `data-*` attribute that `app.js` turns into a custom property (`data-bar`). Icons from the
  sprite (`Ui.Icon`); `IconTests` checks every name. A toolkit switch hides its checkbox: browser tests click the `label.mat-switch`.
- **Colour**: sun yellow `#e8a200` with dark text on it (`--mat-app-on-accent`) and a darker gold for links (`--mat-app-accent-text: #805c00`), set in `app.css`.
  Colour is never the only signal (pills carry words and icons).
- **Formatting**: invariant request culture; numbers and dates for people with `Fmt` (de-DE).
- **Razor**: `@` in a German hint must be written `@@`; Razor encodes `ü` as an entity (tests use `AppFixture.TextAsync`).
- **Code**: little nesting, early returns, several related business functions per class are fine; Microsoft C# guidelines; comments say *why*.

## Gotchas

- Shell strings with backticks, `$"`, apostrophes or `$$` get mangled by the tool that runs Bash (heredocs fail on them): write files with the editor tools.
- The dev compose mounts the volume into sqlite-web at `/matsmtp`, never `/data`: the image would copy its root-owned `/data` into a fresh volume and the app could not write.
  sqlite-web runs as uid 1654 (the app user) for the same reason.
- SmtpServer 11: `EndpointListener` has no public constructor (no custom listener factory); the receiver binds IPv4 `0.0.0.0`. A filter refuses with
  `SmtpResponseException`; MailKit reports a 530 as `ServiceNotAuthenticatedException`.
- On Windows a certificate for TLS needs a persisted key (`CertificateService.KeyFlags`); in the container keys stay ephemeral.
- SMBLibrary only talks SMB over TCP port 445. The dev compose has an explicit subnet (`10.253.26.0/24`).

## Git

Branches: `main` (release), `dev` (development). Commit and push **under the user's name** only: no `Co-Authored-By` trailer, no "Generated with" line (the global rule of
the user overrides any default). Versions: release `<major>.<minor>.<build>-<date>`, dev `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, set by the
GitHub action). Screenshots in `docs/images` show sample data only.
