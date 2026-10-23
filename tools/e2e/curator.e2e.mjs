// Browser tests of the app UI, run against the real server (tools/DevHost) in Chromium. Every step checks the files
// on disk, since the folder is the database. Prerequisites:
//   dotnet build tools/DevHost
//   npm install --prefix tools/e2e && node tools/e2e/curator.e2e.mjs
// Environment: SHOTS (screenshot folder, default <tmp>/photocurator-shots), PORT (default: random),
// CHROMIUM (browser executable; default: Playwright's own, honouring PLAYWRIGHT_BROWSERS_PATH).

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const SHOTS = process.env.SHOTS || path.join(os.tmpdir(), 'photocurator-shots');
const PORT = Number(process.env.PORT || 20000 + Math.floor(Math.random() * 20000));
const TOKEN = 'e2e-token';
const BASE = `http://127.0.0.1:${PORT}`;
const WORK = fs.mkdtempSync(path.join(os.tmpdir(), 'PhotoCurator-e2e-'));
const LIBRARY = path.join(WORK, 'Downloads');
const COPIES = path.join(WORK, 'copies');
const SITE = path.join(WORK, 'site', 'src', 'content', 'field');
fs.mkdirSync(SHOTS, { recursive: true });
fs.mkdirSync(COPIES);

const files = dir => fs.existsSync(dir) ? fs.readdirSync(dir).sort() : [];
const read = file => fs.readFileSync(file, 'utf8');
const exists = file => fs.existsSync(file);

async function until(check, what, timeout = 10000) {
  const end = Date.now() + timeout;
  let last;
  while (Date.now() < end) {
    try {
      if (await check()) return;
    } catch (e) {
      last = e;
    }
    await new Promise(r => setTimeout(r, 100));
  }
  throw new Error(`Timed out waiting for ${what}${last ? `: ${last.message}` : ''}`);
}

// ---------- server ----------

const host = spawn('dotnet', [path.join(root, 'tools/DevHost/bin/Debug/net10.0/DevHost.dll'), '--port', String(PORT), '--token', TOKEN, '--data', path.join(WORK, 'appdata')], { stdio: ['ignore', 'pipe', 'pipe'] });
host.stderr.on('data', data => process.stderr.write(`[server] ${data}`));
process.on('exit', () => {
  try { host.kill('SIGKILL'); } catch { /* already gone */ }
});
await until(async () => (await fetch(`${BASE}/api/state?t=${TOKEN}`)).ok, 'the dev host to start', 30000);

const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
const errors = [];
let step = 'start';
let passed = 0;

async function test(name, body) {
  step = name;
  await body();
  passed++;
  console.log(`  ok  ${name}`);
}

try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  const shot = name => page.screenshot({ path: path.join(SHOTS, `${name}.png`) });

  // Photos made in the browser: a nested Google-Photos-style download, plus the HEIC test fixture.
  await page.goto('about:blank');
  const makeJpeg = (seed, width = 1200, height = 900) => page.evaluate(([seed, width, height]) => {
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const g = canvas.getContext('2d');
    const hue = (seed * 47) % 360;
    const gradient = g.createLinearGradient(0, 0, width, height);
    gradient.addColorStop(0, `hsl(${hue} 55% 62%)`);
    gradient.addColorStop(1, `hsl(${(hue + 60) % 360} 45% 28%)`);
    g.fillStyle = gradient;
    g.fillRect(0, 0, width, height);
    g.fillStyle = 'rgba(255,255,255,0.85)';
    g.font = 'bold 120px sans-serif';
    g.fillText(`#${seed}`, 60, height - 80);
    return new Promise(resolve => canvas.toBlob(blob => blob.arrayBuffer().then(b => resolve([...new Uint8Array(b)])), 'image/jpeg', 0.85));
  }, [seed, width, height]).then(bytes => Buffer.from(bytes));

  const put = async (rel, seed) => {
    const file = path.join(LIBRARY, rel);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, await makeJpeg(seed));
  };
  for (let i = 1; i <= 6; i++) await put(`Photos-001/2024/IMG_000${i}.jpg`, i);
  fs.copyFileSync(path.join(root, 'PhotoCurator.Core.Tests/Fixtures/sample.heic'), path.join(LIBRARY, 'Photos-001/IMG_7001.HEIC'));
  fs.writeFileSync(path.join(LIBRARY, 'Photos-001/notes.txt'), 'not a photo');
  const originalLayout = JSON.stringify(fs.readdirSync(LIBRARY, { recursive: true }).sort());

  await test('first open asks before working in place, and can make a copy first', async () => {
    await page.goto(`${BASE}/?t=${TOKEN}`);
    await shot('01-welcome');
    await page.fill('#welcomePath', LIBRARY);
    await page.click('#welcomePathForm button[type=submit]');
    await page.waitForSelector('.dialog >> text=Before you start');
    await shot('02-in-place-warning');
    await page.click('.dialog >> text=Make a copy first…');
    await page.fill('.dialog input', COPIES);
    await page.click('.dialog button[type=submit]');
    await page.waitForSelector('#app:not([hidden])');
    await until(() => exists(path.join(COPIES, 'Downloads (copy)', 'Photos-001', 'notes.txt')), 'the copy');
    await until(async () => (await page.textContent('#rootName')) === 'Downloads (copy)', 'the copy to open');
    await page.waitForSelector('.toast >> text=Working on the copy');
  });

  const COPY = path.join(COPIES, 'Downloads (copy)');
  const DL = path.join(COPY, 'Photos-001', '2024');

  await test('every photo at any depth is listed, other files are not', async () => {
    await until(async () => (await page.locator('.tile img.loaded').count()) === 7, 'thumbnails');
    assert.equal(await page.textContent('#viewCount'), '7 photos');
    await shot('03-grid');
  });

  await test('batch-select four photos and create a tag inline', async () => {
    // A folder's own photos come before its subfolders': IMG_7001.HEIC first, then Photos-001/2024/IMG_0001…
    const tile = name => page.locator('.tile', { hasText: name });
    await tile('IMG_0001.jpg').hover();
    await tile('IMG_0001.jpg').locator('.check').click();
    await tile('IMG_0002.jpg').click();
    await tile('IMG_0004.jpg').click({ modifiers: ['Shift'] });
    assert.equal(await page.textContent('#selectionCount'), '4 selected');
    await page.keyboard.press('t');
    await page.keyboard.type('hammer');
    await shot('04-tag-popover');
    await page.keyboard.press('Enter');
    await until(() => ['IMG_0001', 'IMG_0002', 'IMG_0003', 'IMG_0004'].every(n => read(path.join(DL, `${n}.jpg.md`)).includes('tags: [hammer]')), 'four sidecars with the tag');
    assert.ok(!exists(path.join(DL, 'IMG_0005.jpg.md')));
    await page.keyboard.press('Escape');
    await until(async () => (await page.locator('.side-row >> text=hammer').count()) === 1, 'the tag in the sidebar');
  });

  await test('move the selection into a new nested folder', async () => {
    await page.keyboard.press('m');
    await page.keyboard.type('Field Work/Prepping for paint');
    await shot('05-move-popover');
    await page.keyboard.press('Enter');
    const dest = path.join(COPY, 'Field Work', 'Prepping for paint');
    await until(() => files(dest).length === 8, 'eight files in the new folder');
    assert.deepEqual(files(dest).filter(f => f.endsWith('.jpg')), ['IMG_0001.jpg', 'IMG_0002.jpg', 'IMG_0003.jpg', 'IMG_0004.jpg']);
    assert.deepEqual(files(DL), ['IMG_0005.jpg', 'IMG_0006.jpg']);
    await page.waitForSelector('.toast >> text=Moved 4 photos');
  });

  await test('open a photo, caption and tag it with the keyboard, go to the next one', async () => {
    await page.keyboard.press('Escape');
    await page.click('.side-row[data-folder="Field Work/Prepping for paint"]');
    await until(async () => (await page.textContent('#viewCount')) === '4 photos', 'the folder view');
    await page.locator('.tile').first().click();
    await page.waitForSelector('#detail:not([hidden])');
    await page.waitForSelector('#detailName >> text=IMG_0001.jpg');
    await page.keyboard.press('Enter');
    await page.keyboard.type('Sanding the patched drywall');
    await page.keyboard.press('Enter');
    await page.keyboard.type('drywall');
    await page.keyboard.press('Enter');
    await page.keyboard.press('Escape');
    await shot('06-detail');
    await page.keyboard.press('ArrowRight');
    await page.waitForSelector('#detailName >> text=IMG_0002.jpg');
    const sidecar = path.join(COPY, 'Field Work', 'Prepping for paint', 'IMG_0001.jpg.md');
    await until(() => read(sidecar).includes('caption: Sanding the patched drywall') && read(sidecar).includes('tags: [hammer, drywall]'), 'the caption and tag on disk');
    await page.keyboard.press('t');
    await page.keyboard.type('half-typed');
    await page.keyboard.press('Control+Enter');
    await page.waitForSelector('#detailName >> text=IMG_0003.jpg');
    await until(async () => (await page.inputValue('#fieldTags input')) === '', 'uncommitted tag text to be dropped');
    await until(async () => (await page.evaluate(() => document.activeElement.id)) === 'fieldCaption', 'the caption field to have focus');
    await page.keyboard.press('Escape');
    await page.keyboard.press('Escape');
    await page.waitForSelector('#detail', { state: 'hidden' });
  });

  await test('Ctrl+Z undoes edits, then the move', async () => {
    const moved = path.join(COPY, 'Field Work', 'Prepping for paint', 'IMG_0001.jpg.md');
    await page.keyboard.press('Control+z'); // the drywall tag
    await until(() => read(moved).includes('tags: [hammer]\n'), 'the tag undone');
    await page.keyboard.press('Control+z'); // the caption
    await until(() => !read(moved).includes('caption:'), 'the caption undone');
    await page.keyboard.press('Control+z'); // the move
    // Six photos, four of them with the sidecar the "hammer" tag created.
    await until(() => files(DL).length === 10, 'the photos back where they were');
    assert.ok(read(path.join(DL, 'IMG_0001.jpg.md')).includes('tags: [hammer]'));
    assert.deepEqual(files(path.join(COPY, 'Field Work', 'Prepping for paint')), []);
  });

  await test('photos added in Explorer show up without reopening', async () => {
    await page.click('.side-row >> text=All photos');
    const added = path.join(COPY, 'Later batch');
    fs.mkdirSync(added);
    fs.writeFileSync(path.join(added, 'PXL_1.jpg'), await makeJpeg(21));
    fs.writeFileSync(path.join(added, 'PXL_2.jpg'), await makeJpeg(22));
    await until(async () => (await page.textContent('#viewCount')) === '9 photos', 'the new photos', 15000);
  });

  await test('drag a photo onto a folder in the sidebar', async () => {
    const tile = page.locator('.tile', { hasText: 'IMG_0005.jpg' });
    await tile.dragTo(page.locator('.side-row[data-folder="Field Work/Prepping for paint"]'));
    await until(() => exists(path.join(COPY, 'Field Work', 'Prepping for paint', 'IMG_0005.jpg')), 'the dragged photo');
  });

  await test('rename a folder from its menu', async () => {
    await page.click('.side-row[data-folder="Field Work/Prepping for paint"]', { button: 'right' });
    await page.click('#menu >> text=Rename…');
    await page.fill('.dialog input', 'Prep for paint');
    await page.click('.dialog button[type=submit]');
    await until(() => exists(path.join(COPY, 'Field Work', 'Prep for paint', 'IMG_0005.jpg')), 'the renamed folder');
  });

  await test('export a folder for the web', async () => {
    await page.click('.side-row[data-folder="Field Work"]', { button: 'right' });
    await page.click('#menu >> text=Export for web…');
    await page.fill('.dialog input', SITE);
    await shot('07-export');
    await page.click('.dialog button[type=submit]');
    await page.waitForSelector('.dialog >> text=Export finished');
    assert.deepEqual(files(SITE), ['.photocurator-export', 'folder.md', 'prep-for-paint']);
    assert.deepEqual(files(path.join(SITE, 'prep-for-paint')), ['folder.md', 'img-0005.jpg', 'img-0005.md']);
    assert.match(read(path.join(SITE, 'prep-for-paint', 'img-0005.md')), /^---\nimage: \.\/img-0005\.jpg\ndate: /);
    await page.click('.dialog >> text=Done');
  });

  await test('the original folder was never touched', async () => {
    assert.equal(JSON.stringify(fs.readdirSync(LIBRARY, { recursive: true }).sort()), originalLayout);
  });

  await test('large window', async () => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.click('.side-row >> text=All photos');
    await page.waitForTimeout(400);
    await shot('08-grid-1080p');
    await page.locator('.tile').first().click();
    await page.waitForSelector('#detail:not([hidden])');
    await page.waitForTimeout(400);
    await shot('09-detail-1080p');
    await page.keyboard.press('Escape');
  });

  assert.deepEqual(errors, [], 'no errors in the page');
  console.log(`\n${passed} passed. Screenshots: ${SHOTS}`);
} catch (e) {
  console.error(`\nFAILED at "${step}": ${e.message}`);
  const page = browser.contexts()[0]?.pages()[0];
  if (page) {
    await page.screenshot({ path: path.join(SHOTS, 'failure.png') }).catch(() => {});
    console.error('Toasts:', await page.locator('.toast').allTextContents().catch(() => []));
  }
  if (errors.length) console.error('Page errors:', errors);
  process.exitCode = 1;
} finally {
  await browser.close();
  host.kill('SIGKILL');
  fs.rmSync(WORK, { recursive: true, force: true });
}
