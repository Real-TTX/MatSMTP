// The browser check of MatSMTP: on a FRESH stack it goes through the setup, creates accounts, a relay user, a webhook and rules through the real forms,
// sends mails over real SMTP (curl), opens every page (desktop, dark, phone; no JS error, no horizontal overflow) and takes screenshots.
//   cd tests/e2e && npm install && node flow.js http://localhost:2525 [folder for the screenshots, e.g. ../../docs/images]
const { chromium } = require('playwright-core');
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const base = process.argv[2] || 'http://localhost:2525';
const out = process.argv[3];
const EMAIL = 'admin@example.com', PASS = 'Relay-Demo-2026!';
const problems = [];
// A switch of the toolkit hides its checkbox: the label is what a person clicks.
async function sw(page, name, on) {
  const box = page.locator(`input[type=checkbox][name="${name}"]`);
  if ((await box.isChecked()) !== on) { await page.locator(`label.mat-switch:has(input[name="${name}"])`).click(); }
}

async function save(page) {
  await Promise.all([page.waitForLoadState('networkidle'), page.click('.mat-btn-row button.mat-btn--primary')]);
  const err = await page.locator('.mat-error:not(:empty), .mat-summary li').allTextContents();
  if (err.filter(t => t.trim()).length) problems.push(page.url() + ': ' + err.join(' | '));
}

function smtp(from, to, subject, port = 2025, extra = []) {
  const file = path.join(__dirname, 'mail.txt');
  fs.writeFileSync(file, `From: ${from}\r\nTo: ${to}\r\nSubject: ${subject}\r\nMessage-ID: <${Date.now()}${Math.random().toString(16).slice(2)}@demo>\r\n\r\nHallo,\r\ndies ist eine Beispielnachricht.\r\n`);
  try {
    execFileSync('curl', ['-s', '--url', `smtp://localhost:${port}`, '--mail-from', from, '--mail-rcpt', to, '-T', file, ...extra], { stdio: 'pipe' });
    return 'ok';
  } catch (e) { return 'refused: ' + (e.stderr || e.message).toString().trim(); }
}

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 }, deviceScaleFactor: out ? 2 : 1 });
  const page = await ctx.newPage();
  page.on('pageerror', e => problems.push('JS: ' + e.message));
  page.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()); });

  await page.goto(base + '/');
  if (page.url().includes('/Account/Setup')) {
    await page.fill('[name="Input.DisplayName"]', 'Alex Admin');
    await page.fill('[name="Input.Email"]', EMAIL);
    await page.fill('[name="Input.Password"]', PASS);
    await page.fill('[name="Input.PasswordRepeat"]', PASS);
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
    console.log('after setup:', page.url());

    // accounts
    await page.goto(base + '/Accounts/Edit');
    await page.fill('[name="Input.Name"]', 'Mailpit (Test)');
    await page.fill('[name="Input.Description"]', 'Fängt im Dev-Stack alles ab');
    await page.fill('[name="Input.Host"]', 'mailpit');
    await page.fill('[name="Input.Port"]', '1025');
    await page.selectOption('[name="Input.Security"]', 'None');
    await sw(page, 'Input.UseAuth', false);
    await sw(page, 'Input.IsDefault', true);
    await save(page);
    await page.goto(base + '/Accounts/Edit');
    await page.fill('[name="Input.Name"]', 'Newsletter-Versand');
    await page.fill('[name="Input.Host"]', 'mailpit');
    await page.fill('[name="Input.Port"]', '1025');
    await page.selectOption('[name="Input.Security"]', 'None');
    await sw(page, 'Input.UseAuth', false);
    await sw(page, 'Input.IsDefault', false);
    await page.fill('[name="Input.MaxPerMinute"]', '30');
    await save(page);
    await page.goto(base + '/Accounts/Edit');
    await page.fill('[name="Input.Name"]', 'Microsoft 365');
    await page.fill('[name="Input.Host"]', 'smtp.office365.com');
    await page.fill('[name="Input.UserName"]', 'relay@example.com');
    await page.fill('[name="Input.Password"]', 'beispiel');
    await page.fill('[name="Input.ForceFromAddress"]', 'relay@example.com');
    await sw(page, 'Input.IsDefault', false);
    await sw(page, 'Input.IsEnabled', false);
    await save(page);

    // relay user
    await page.goto(base + '/RelayUsers/Edit');
    await page.fill('[name="Input.UserName"]', 'shop');
    await page.fill('[name="Input.Description"]', 'Onlineshop (Bestellbestätigungen)');
    await page.fill('[name="Input.Password"]', 'Shop-Relay-Passwort-2026');
    await page.fill('[name="Input.AllowedSenders"]', 'shop.example.com');
    await save(page);

    // webhook
    await page.goto(base + '/Webhooks/Edit');
    await page.fill('[name="Input.Name"]', 'Ticketsystem');
    await page.fill('[name="Input.Description"]', 'Legt aus Support-Mails Tickets an');
    await page.fill('[name="Input.Url"]', 'http://mailpit:8025/api/v1/info');
    await page.selectOption('[name="Input.Payload"]', 'Content');
    await save(page);

    // rules
    const rule = async (f) => { await page.goto(base + '/Rules/Edit'); await f(); await save(page); };
    await rule(async () => {
      await page.fill('[name="Input.Name"]', 'Shop: Absender vereinheitlichen');
      await page.fill('[name="Input.MatchSenders"]', 'shop.example.com');
      await page.selectOption('[name="Input.Action"]', 'Continue');
      await page.fill('[name="Input.RewriteFrom"]', 'noreply@example.com');
      await page.fill('[name="Input.RewriteFromName"]', 'Beispiel-Shop');
      await sw(page, 'Input.ReplyToOriginal', true);
    });
    await rule(async () => {
      await page.fill('[name="Input.Name"]', 'Newsletter über eigenes Konto');
      await page.fill('[name="Input.MatchSenders"]', 'newsletter@example.com');
      await page.selectOption('[name="Input.Action"]', 'Relay');
      await page.selectOption('[name="Input.AccountId"]', { label: 'Newsletter-Versand' });
      await page.fill('[name="Input.AddBcc"]', 'archiv@example.com');
    });
    await rule(async () => {
      await page.fill('[name="Input.Name"]', 'Support an das Ticketsystem');
      await page.fill('[name="Input.MatchRecipients"]', 'support@example.com');
      await page.selectOption('[name="Input.Action"]', 'Relay');
      await page.selectOption('[name="Input.WebhookId"]', { label: 'Ticketsystem' });
    });
    await rule(async () => {
      await page.fill('[name="Input.Name"]', 'Keine Testdomains');
      await page.fill('[name="Input.MatchRecipients"]', '*.invalid\nexample.invalid');
      await page.selectOption('[name="Input.Action"]', 'Reject');
      await page.fill('[name="Input.RejectMessage"]', 'An Testdomains wird nicht versendet.');
    });

    // mails over real SMTP
    console.log(smtp('bestellung@shop.example.com', 'kunde@example.org', 'Ihre Bestellung 10482'));
    console.log(smtp('bestellung@shop.example.com', 'kundin@example.net', 'Ihre Bestellung 10483'));
    console.log(smtp('newsletter@example.com', 'leser@example.org', 'Neuigkeiten im Oktober'));
    console.log(smtp('monitoring@example.com', 'it@example.com', 'Backup erfolgreich', 2587, ['--ssl-reqd', '-k']));
    console.log(smtp('crm@example.com', 'support@example.com', 'Rückruf erbeten'));
    console.log(smtp('app@example.com', 'test@demo.invalid', 'Testlauf'));
    console.log(smtp('drucker@example.com', 'buchhaltung@example.com', 'Scan vom Multifunktionsgerät'));
    await new Promise(r => setTimeout(r, 4000));
  } else {
    await page.fill('[name="Input.Email"]', EMAIL);
    await page.fill('[name="Input.Password"]', PASS);
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
  }

  const first = await page.evaluate(async b => { const r = await fetch(b + '/Messages?sort=received&dir=asc'); const t = await r.text(); const m = t.match(/Details\?id=(\d+)/); return m ? m[1] : null; }, base);
  const pages = [
    ['dashboard', '/'], ['messages', '/Messages'], ['queue', '/Messages/Queue'], ['message', '/Messages/Details?id=' + first],
    ['rules', '/Rules'], ['rule-edit', '/Rules/Edit?id=1'], ['rule-test', '/RuleTest'], ['accounts', '/Accounts'], ['account-edit', '/Accounts/Edit?id=1'],
    ['relayusers', '/RelayUsers'], ['webhooks', '/Webhooks'], ['webhook-edit', '/Webhooks/Edit?id=1'], ['tools-send', '/Tools/Send'],
    ['admin-smtp', '/Admin/Smtp'], ['admin-delivery', '/Admin/Delivery'], ['admin-backup', '/Admin/Backup'], ['admin-backup-targets', '/Admin/Backup/Targets/Edit'],
    ['admin-backup-plans', '/Admin/Backup/Plans/Edit'], ['admin-users', '/Admin/Users'], ['admin-shares', '/Admin/Shares'], ['admin-system', '/Admin/System'],
    ['admin-activity', '/Admin/Activity'], ['account-security', '/Account/Security'],
  ];
  const dir = out || path.join(__dirname, 'shots');
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, url] of pages) {
    const r = await page.goto(base + url, { waitUntil: 'networkidle' });
    if (!r.ok()) problems.push(`${r.status()} ${url}`);
    await page.screenshot({ path: path.join(dir, name + '.png'), fullPage: !out });
  }

  // a share link (anonymous view)
  await page.goto(base + '/Messages/Details?id=' + first, { waitUntil: 'networkidle' });
  await Promise.all([page.waitForLoadState('networkidle'), page.click('form[action*="handler=Share"] button[type=submit]')]);
  const link = (await page.locator('[id^="link-"]').first().textContent()).trim();
  const anon = await browser.newContext({ viewport: { width: 1360, height: 900 }, deviceScaleFactor: out ? 2 : 1 });
  const ap = await anon.newPage();
  await ap.goto(link, { waitUntil: 'networkidle' });
  await ap.screenshot({ path: path.join(dir, 'share.png'), fullPage: !out });

  // dark and mobile
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto(base + '/', { waitUntil: 'networkidle' });
  await page.screenshot({ path: path.join(dir, 'dark-dashboard.png') });
  await page.goto(base + '/Messages', { waitUntil: 'networkidle' });
  await page.screenshot({ path: path.join(dir, 'dark-messages.png') });
  const mobile = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, storageState: await ctx.storageState() });
  const mp = await mobile.newPage();
  for (const [name, url] of [['mobile-dashboard', '/'], ['mobile-messages', '/Messages'], ['mobile-rule-edit', '/Rules/Edit?id=1']]) {
    await mp.goto(base + url, { waitUntil: 'networkidle' });
    await mp.screenshot({ path: path.join(dir, name + '.png') });
    const overflow = await mp.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    if (overflow > 1) problems.push(`mobile overflow ${overflow}px on ${url}`);
  }

  console.log(problems.length ? 'PROBLEMS:\n' + problems.join('\n') : 'no problems');
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
