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

/** Adds a minimal EXIF block with DateTimeOriginal ("2024:05:01 10:00:00") to a JPEG, like a phone photo has. */
function withExifDate(jpeg, taken) {
  const tiff = Buffer.alloc(64);
  tiff.write('II', 0, 'ascii');
  tiff.writeUInt16LE(42, 2);
  tiff.writeUInt32LE(8, 4);
  tiff.writeUInt16LE(1, 8);                                        // IFD0: one entry,
  tiff.writeUInt16LE(0x8769, 10); tiff.writeUInt16LE(4, 12); tiff.writeUInt32LE(1, 14); tiff.writeUInt32LE(26, 18); // the Exif IFD at 26
  tiff.writeUInt16LE(1, 26);                                       // Exif IFD: one entry,
  tiff.writeUInt16LE(0x9003, 28); tiff.writeUInt16LE(2, 30); tiff.writeUInt32LE(20, 32); tiff.writeUInt32LE(44, 36); // DateTimeOriginal at 44
  Buffer.from(taken + '\0', 'ascii').copy(tiff, 44);
  const header = Buffer.from('Exif\0\0', 'binary');
  const length = 2 + header.length + tiff.length;
  const app1 = Buffer.concat([Buffer.from([0xff, 0xe1, length >> 8, length & 0xff]), header, tiff]);
  return Buffer.concat([jpeg.subarray(0, 2), app1, jpeg.subarray(2)]);
}

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
  const makeJpeg = (seed, width = 1200, height = 900, quality = 0.85) => page.evaluate(([seed, width, height, quality]) => {
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
    return new Promise(resolve => canvas.toBlob(blob => blob.arrayBuffer().then(b => resolve([...new Uint8Array(b)])), 'image/jpeg', quality));
  }, [seed, width, height, quality]).then(bytes => Buffer.from(bytes));

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

  // A Google Photos album that keeps growing, downloaded whole each time.
  const album = (n, files) => {
    const dir = path.join(WORK, `download-${n}`, 'Field Work album');
    fs.mkdirSync(dir, { recursive: true });
    for (const [name, bytes] of Object.entries(files)) fs.writeFileSync(path.join(dir, name), bytes);
    return dir;
  };
  const shots = {
    'shot-a.jpg': withExifDate(await makeJpeg(41), '2024:05:01 10:00:00'),
    'shot-b.jpg': withExifDate(await makeJpeg(42), '2024:05:01 10:05:00'),
    'shot-c.jpg': withExifDate(await makeJpeg(43), '2024:05:01 10:10:00')
  };
  const firstDownload = album(1, shots);
  const secondDownload = album(2, {
    ...shots,
    'shot-d.jpg': withExifDate(await makeJpeg(44), '2024:05:02 09:00:00'),
    'shot-e.jpg': withExifDate(await makeJpeg(45), '2024:05:02 09:30:00'),
    // shot-a edited in Google Photos: same moment, different file.
    'shot-a-edited.jpg': withExifDate(await makeJpeg(41, 1200, 900, 0.5), '2024:05:01 10:00:00')
  });
  const INBOX = path.join(COPY, 'Inbox', 'Field Work album');

  const importFrom = async (source, expect) => {
    await page.click('#importButton');
    await page.fill('.dialog input', source);
    await page.click('.dialog button:text-is("Add")');
    await page.click('.dialog >> text=Check for new photos');
    await page.waitForSelector(`.dialog h2 >> text=${expect}`);
  };

  await test('import a downloaded album', async () => {
    await importFrom(firstDownload, 'Ready to import');
    assert.match(await page.textContent('.import-summary'), /3 new/);
    await shot('10-import-summary');
    await page.click('.dialog >> text=Import 3');
    await page.waitForSelector('.toast >> text=Imported 3 photos into Inbox/Field Work album');
    assert.deepEqual(files(INBOX), ['shot-a.jpg', 'shot-b.jpg', 'shot-c.jpg']);
    await until(async () => (await page.textContent('#viewTitle')) === 'Inbox / Field Work album', 'the inbox folder view');
  });

  await test('caption one and sort it into Field Work', async () => {
    const a = page.locator('.tile', { hasText: 'shot-a.jpg' });
    await a.hover();
    await a.locator('.check').click();
    await page.keyboard.press('c');
    await page.keyboard.type('The keeper');
    await page.keyboard.press('Enter');
    await until(() => exists(path.join(INBOX, 'shot-a.jpg.md')) && read(path.join(INBOX, 'shot-a.jpg.md')).includes('caption: The keeper'), 'the caption');
    await page.keyboard.press('m');
    await page.keyboard.type('Field Work');
    await page.keyboard.press('Enter');
    await until(() => exists(path.join(COPY, 'Field Work', 'shot-a.jpg')), 'shot-a in Field Work');
    await page.keyboard.press('Escape');
  });

  await test('downloading the whole album again imports only what is new', async () => {
    await importFrom(secondDownload, 'Ready to import');
    const summary = await page.textContent('.import-summary');
    assert.match(summary, /6 photos in this download/);
    assert.match(summary, /3 already in your folder/);
    assert.match(summary, /2 new/);
    assert.match(summary, /1 looks like an edited version/);
    assert.match(await page.textContent('.edited-list'), /shot-a-edited\.jpg[\s\S]*Field Work\/shot-a\.jpg/);
    await shot('11-import-again');
    await page.click('.dialog >> text=Import 3');
    await page.waitForSelector('.toast >> text=Imported 3 photos');
    assert.deepEqual(files(INBOX), ['shot-a-edited.jpg', 'shot-b.jpg', 'shot-c.jpg', 'shot-d.jpg', 'shot-e.jpg']);
    assert.ok(read(path.join(COPY, 'Field Work', 'shot-a.jpg.md')).includes('caption: The keeper'));
  });

  await test('the same download a third time has nothing new', async () => {
    await importFrom(secondDownload, 'Nothing new');
    await page.click('.dialog >> text=Done');
    assert.deepEqual(files(INBOX), ['shot-a-edited.jpg', 'shot-b.jpg', 'shot-c.jpg', 'shot-d.jpg', 'shot-e.jpg']);
  });

  await test('Duplicates: keep the captioned one, set the edit aside, undo', async () => {
    await until(async () => (await page.locator('.side-row >> text=Duplicates').count()) === 1, 'the Duplicates entry', 15000);
    await page.click('.side-row >> text=Duplicates');
    await page.waitForSelector('.dupe-card');
    assert.equal(await page.locator('.dupe-card').count(), 1);
    assert.equal(await page.locator('.dupe-item.kept').textContent().then(t => t.includes('shot-a.jpg') && t.includes('The keeper')), true);
    await shot('12-duplicates');
    await page.click('.dupe-card >> text=Keep the selected one, set the others aside');
    const setAside = path.join(COPY, 'Duplicates set aside');
    await until(() => exists(path.join(setAside, 'shot-a-edited.jpg')), 'the edit set aside');
    assert.ok(exists(path.join(COPY, 'Field Work', 'shot-a.jpg')));
    await page.waitForSelector('.dupes-empty');
    await page.keyboard.press('Control+z');
    await until(() => exists(path.join(INBOX, 'shot-a-edited.jpg')) && !exists(setAside), 'the set-aside undone');
  });

  await test('the photo view shows the whole photo, however big', async () => {
    const big = path.join(COPY, 'Big photos');
    fs.mkdirSync(big);
    fs.writeFileSync(path.join(big, 'portrait.jpg'), await makeJpeg(31, 3000, 4000));
    fs.writeFileSync(path.join(big, 'panorama.jpg'), await makeJpeg(32, 6000, 2000));
    await until(async () => (await page.locator('.side-row[data-folder="Big photos"]').count()) === 1, 'the new folder');
    for (const [width, height] of [[1280, 800], [1920, 1080]]) {
      await page.setViewportSize({ width, height });
      await page.click('.side-row[data-folder="Big photos"]');
      for (const [name, w, h] of [['panorama.jpg', 6000, 2000], ['portrait.jpg', 3000, 4000]]) {
        await page.locator('.tile', { hasText: name }).click();
        await page.waitForSelector(`#detailName >> text=${name}`);
        await until(() => page.evaluate(() => {
          const img = document.getElementById('detailImage');
          return img.src.includes('/api/image') && img.complete && img.naturalWidth > 0;
        }), `the full ${name}`);
        const fit = await page.evaluate(() => {
          const img = document.getElementById('detailImage');
          const box = img.getBoundingClientRect();
          const stage = document.querySelector('.detail-stage').getBoundingClientRect();
          const scale = Math.min(box.width / img.naturalWidth, box.height / img.naturalHeight);
          return {
            natural: [img.naturalWidth, img.naturalHeight],
            shown: [img.naturalWidth * scale, img.naturalHeight * scale],
            box: [box.left, box.top, box.right, box.bottom],
            stage: [stage.left, stage.top, stage.right, stage.bottom]
          };
        });
        assert.deepEqual(fit.natural, [w, h]);
        const [left, top, right, bottom] = fit.box;
        const [sLeft, sTop, sRight, sBottom] = fit.stage;
        assert.ok(left >= sLeft && top >= sTop && right <= sRight && bottom <= sBottom, `${name} at ${width}×${height} spills out of the view: ${JSON.stringify(fit)}`);
        // As big as fits: the picture touches the frame on one side.
        assert.ok(Math.abs(fit.shown[0] - (right - left)) < 1 || Math.abs(fit.shown[1] - (bottom - top)) < 1, `${name} isn't using the space: ${JSON.stringify(fit)}`);
        await shot(`fit-${name.replace('.jpg', '')}-${width}`);
        await page.keyboard.press('Escape');
        await page.waitForSelector('#detail', { state: 'hidden' });
      }
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  // ---------- write-ups: a folder's story, written on the page or in Markdown ----------

  const ATTIC = path.join(COPY, 'Field Work', 'Attic');
  const writeUp = path.join(ATTIC, 'folder.md');
  const onDisk = () => exists(writeUp) ? read(writeUp) : '';
  const expectOnDisk = (expected, what = 'the write-up on disk') =>
    until(() => onDisk() === expected, what).catch(e => { throw new Error(`${e.message}\n--- on disk ---\n${onDisk()}--- expected ---\n${expected}`); });
  const front = (extra = '') => `---\ntitle: Attic insulation\nsummary: Out with the old.\n${extra}---\n\n`;
  const intro = 'We pulled 40 years of **dusty** insulation out by hand.\n\n## Demo day\n\n';
  const a1 = '![Pulling it out](IMG_A1.jpg)';
  const b1 = '![](Before/IMG_B1.jpg)';
  const story = (...photos) => intro + photos.map(p => p + '\n\n').join('') + 'Suits and masks.\n\nThe end.\n';

  await test('write-up: a folder has a Write-up tab next to its photos', async () => {
    fs.mkdirSync(path.join(ATTIC, 'Before'), { recursive: true });
    fs.writeFileSync(path.join(ATTIC, 'IMG_A1.jpg'), await makeJpeg(41, 1200, 900));
    fs.writeFileSync(path.join(ATTIC, 'IMG_A2.jpg'), await makeJpeg(42, 900, 1200));
    fs.writeFileSync(path.join(ATTIC, 'Before', 'IMG_B1.jpg'), await makeJpeg(43, 1200, 800));
    await until(async () => (await page.locator('.side-row[data-folder="Field Work/Attic"]').count()) === 1, 'the new folder');
    await page.click('.side-row[data-folder="Field Work/Attic"]');
    await page.click('#viewTabs [data-tab="writeup"]');
    await page.waitForSelector('#writeup:not([hidden])');
    assert.equal(await page.isVisible('#grid'), false);
    await until(async () => (await page.locator('.wu-thumb').count()) === 3, 'the folder\'s photos (and its subfolder\'s) in the strip');
    assert.equal(await page.getAttribute('.story-title', 'placeholder'), 'Attic', 'the title defaults to the folder name');
    assert.ok(!exists(writeUp), 'nothing is written before you write');
    await shot('10-writeup-new');
  });

  await test('write-up: type the title, summary and story right on the page', async () => {
    await page.click('.story-title');
    await page.keyboard.type('Attic insulation');
    await page.keyboard.press('Enter'); // on to the summary
    await page.keyboard.type('Out with the old.');
    await page.keyboard.press('Enter'); // on to the story
    await page.keyboard.type('We pulled 40 years of ');
    await page.keyboard.press('Control+b');
    await page.keyboard.type('dusty');
    await page.keyboard.press('Control+b');
    await page.keyboard.type(' insulation out by hand.');
    await page.keyboard.press('Enter');
    await page.keyboard.type('## Demo day'); // "## " makes a heading, like in Markdown
    await page.keyboard.press('Enter');
    await page.keyboard.type('Suits and masks.');
    await expectOnDisk(front() + intro + 'Suits and masks.\n');
    assert.equal(await page.locator('.sb-heading .sb-text').textContent(), 'Demo day');
    assert.equal(await page.locator('.sb-paragraph strong').textContent(), 'dusty');
    assert.equal(await page.inputValue('.md-front textarea[aria-label="Title"]'), 'Attic insulation', 'the Markdown side follows');
    await until(async () => (await page.locator('.side-row[data-folder="Field Work/Attic"] .has-writeup').count()) === 1, 'the sidebar to mark the folder as having a write-up');
  });

  await test('write-up: the Markdown side edits the same story', async () => {
    await page.click('.wu-markdown');
    await page.keyboard.press('Control+End');
    await page.keyboard.type('\n\nThe end.');
    await until(async () => (await page.locator('.sb-paragraph').last().textContent()) === 'The end.', 'the page to follow the Markdown');
    await page.fill('.md-front textarea[aria-label="Summary"]', 'Out with the old.');
    assert.equal(await page.inputValue('.story-summary'), 'Out with the old.');
    await expectOnDisk(front() + story());
  });

  await test('write-up: drag a photo in, click another in, caption it in place', async () => {
    const heading = page.locator('.sb-heading');
    const box = await heading.boundingBox();
    await page.locator('.wu-thumb[data-path="IMG_A1.jpg"]').dragTo(heading, { targetPosition: { x: 40, y: box.height - 3 } });
    await page.waitForSelector('.sb-photo img');
    await page.keyboard.type('Pulling it out'); // the new photo's caption has the caret
    await page.locator('.wu-thumb[data-path="Before/IMG_B1.jpg"]').click(); // goes after the block you're in
    await until(async () => (await page.locator('.sb-photo').count()) === 2, 'the clicked photo in the story');
    await expectOnDisk(front() + story(a1, b1));
    assert.deepEqual((await page.locator('.wu-thumb.used').evaluateAll(t => t.map(x => x.dataset.path))).sort(), ['Before/IMG_B1.jpg', 'IMG_A1.jpg']);
    await until(() => page.evaluate(() => [...document.querySelectorAll('.sb-photo img')].every(i => i.complete && i.naturalWidth > 0)), 'the photos to show');
    await page.locator('.sb-photo').first().hover();
    await shot('11-writeup-written');
  });

  await test('write-up: move and remove photos, then undo and redo step by step', async () => {
    const photos = page.locator('.sb-photo');
    await photos.nth(1).hover();
    await photos.nth(1).locator('[data-action="up"]').click();
    await expectOnDisk(front() + story(b1, a1), 'the photo moved up');
    await photos.first().hover();
    await photos.first().locator('[data-action="remove"]').click();
    await expectOnDisk(front() + story(a1), 'the photo removed');
    assert.ok(exists(path.join(ATTIC, 'Before', 'IMG_B1.jpg')), 'removing a photo from the story never touches the photo');
    await page.keyboard.press('Control+z');
    await expectOnDisk(front() + story(b1, a1), 'the removal undone');
    await page.keyboard.press('Control+z');
    await expectOnDisk(front() + story(a1, b1), 'the move undone');
    await page.keyboard.press('Control+Shift+z');
    await expectOnDisk(front() + story(b1, a1), 'the move redone');
    await page.click('#undoButton'); // the toolbar's Undo steps through the write-up too
    await expectOnDisk(front() + story(a1, b1), 'the move undone again');
    assert.equal(await page.locator('.sb-caption').first().textContent(), 'Pulling it out');
  });

  await test('write-up: date and cover', async () => {
    await page.click('.wu-date .wu-chip');
    await page.locator('.wu-date-input').fill('2024-05-01');
    await until(async () => (await page.textContent('.wu-date .wu-chip')).includes('May 1, 2024'), 'the date chip');
    const cover = page.locator('.wu-bar > .wu-chip');
    assert.match(await cover.textContent(), /Auto/, 'the cover is the first photo until you pick one');
    await cover.click();
    await page.click('#popover .photo-pick[title="IMG_A2.jpg"]');
    await until(async () => (await cover.textContent()).trim() === 'Cover', 'the chosen cover');
    await expectOnDisk(front('date: 2024-05-01\ncover: IMG_A2.jpg\n') + story(a1, b1));
  });

  await test('write-up: kept when switching tabs and folders', async () => {
    await page.click('#viewTabs [data-tab="photos"]');
    await page.waitForSelector('#grid:not([hidden])');
    assert.equal(await page.isVisible('#writeup'), false);
    await page.click('#viewTabs [data-tab="writeup"]');
    assert.equal(await page.inputValue('.story-title'), 'Attic insulation');
    await page.click('.side-row[data-folder="Big photos"]'); // another folder opens on the same tab, with its own write-up
    await until(async () => (await page.getAttribute('.story-title', 'placeholder')) === 'Big photos', 'the other folder');
    assert.equal(await page.inputValue('.story-title'), '');
    await page.click('.side-row[data-folder="Field Work/Attic"]');
    await until(async () => (await page.inputValue('.story-title')) === 'Attic insulation', 'the write-up read back');
    assert.equal(await page.locator('.sb-photo').count(), 2);
    assert.ok(!exists(path.join(COPY, 'Big photos', 'folder.md')), 'looking at a folder writes nothing');
  });

  await test('write-up: Write, Split and Markdown layouts', async () => {
    for (const layout of ['write', 'markdown', 'split']) {
      await page.click(`.wu-bar [data-layout="${layout}"]`);
      assert.equal(await page.isVisible('.wu-pane-visual'), layout !== 'markdown', layout);
      assert.equal(await page.isVisible('.wu-pane-md'), layout !== 'write', layout);
      await page.waitForTimeout(150);
      await shot(`12-writeup-${layout}`);
    }
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.waitForTimeout(300);
    await shot('13-writeup-1080p');
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  await test('export turns the write-up into a page that uses the web photos', async () => {
    await page.click('.side-row[data-folder="Field Work"]', { button: 'right' });
    await page.click('#menu >> text=Export for web…');
    await page.fill('.dialog input', SITE);
    await page.click('.dialog button[type=submit]');
    await page.waitForSelector('.dialog >> text=Export finished');
    await page.click('.dialog >> text=Done');
    assert.equal(read(path.join(SITE, 'attic', 'folder.md')),
      '---\ntitle: Attic insulation\nsummary: Out with the old.\ndate: 2024-05-01\ncover: ./img-a2.jpg\n---\n\n' +
      story('![Pulling it out](./img-a1.jpg)', '![](./before/img-b1.jpg)'));
    assert.ok(exists(path.join(SITE, 'attic', 'img-a1.jpg')) && exists(path.join(SITE, 'attic', 'before', 'img-b1.jpg')));
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
