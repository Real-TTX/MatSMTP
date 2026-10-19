// Renders the PNG icons of the web app manifest and the home screen from the SVGs in wwwroot (no image library needed: the installed Edge draws them).
//   NODE_PATH=tests/e2e/node_modules node tools/icons.js     (playwright-core and Microsoft Edge)
const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright-core'); // tests/e2e has it (npm install there), or NODE_PATH

const web = path.join(__dirname, '..', 'src', 'MatSmtp', 'wwwroot');
const jobs = [
  // [source, target, size]: the tile of logo.svg keeps its round corners, the maskable icon is the full square (the system cuts its own shape)
  ['logo.svg', 'img/icon-192.png', 192],
  ['logo.svg', 'img/icon-512.png', 512],
  ['logo.svg', 'img/favicon-32.png', 32],
  ['img/icon-maskable.svg', 'img/icon-maskable-192.png', 192],
  ['img/icon-maskable.svg', 'img/icon-maskable-512.png', 512],
  // iOS rounds the corners itself and shows transparency as black: the full square.
  ['img/icon-maskable.svg', 'img/apple-touch-icon.png', 180],
];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage();
  for (const [source, target, size] of jobs) {
    const svg = fs.readFileSync(path.join(web, source), 'utf8');
    // Edge times out on a 32 px window: a larger one, the clip keeps the size.
    await page.setViewportSize({ width: Math.max(size, 600), height: Math.max(size, 600) });
    await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('<svg ', `<svg width="${size}" height="${size}" `)}</body></html>`);
    await page.screenshot({ path: path.join(web, target), omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    console.log('wrote', target);
  }
  await browser.close();
})();
