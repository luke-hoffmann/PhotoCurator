# PhotoCurator

Sort, tag and caption the photos in any folder, then export web-ready copies for a static site. A single portable
Windows `.exe`; everything it knows lives in that folder as plain files, so there is no database to lose or migrate.

Built for a pile of Google Photos downloads: open the folder, look through every photo (at any depth), sort them
into folders like `Field Work/Prepping for paint` or `Camping/Big Sur 2025`, tag them (`hammer`, `headshot`…) and
write a caption. Then export the folders you want on your website.

## Use

1. Double-click `PhotoCurator.exe` and choose a folder (see [Get the exe](#get-the-exe)).
2. The first time, PhotoCurator explains that it works **in place** and offers **Make a copy first…**, which copies
   the folder somewhere you pick and opens the copy. Choose **Continue** to work on the folder itself.
3. Every photo in the folder and all folders inside it shows up in one grid. Drop more photos into the folder at
   any time; they appear by themselves.
4. **Click** a photo to see it big, with its caption, date, tags and folder. **Enter** edits the caption, **Enter**
   again goes to tags, **Ctrl+Enter** moves on to the next photo. Everything saves as you type.
5. **Select** photos with the round check (or Ctrl-click, Shift-click, Ctrl+A). Once something is selected, clicks
   add to the selection. The bar at the bottom then:
   - **Tags… (T)**: type a tag and press Enter; new tags are created on the spot. × removes a tag from all of them.
   - **Move to… (M)**: pick a folder, or type a new name. `Camping/Big Sur` creates both folders.
   - **Caption… (C)**: one caption for all of them.
6. Or drag photos onto a folder in the sidebar. Right-click a folder for **New folder inside**, **Rename** and
   **Export for web**.
7. **Ctrl+Z** undoes moves, folder changes, tags and captions (for as long as the folder is open).

The sidebar's **New** list shows photos you haven't sorted, tagged or captioned yet; **No caption** and **No tags**
are handy to-do lists too.

### Keyboard

| Where | Keys |
|---|---|
| Grid | arrows move · Space select · Shift+arrows extend · Enter open · Ctrl+A all · Esc clear · T / M / C act on the selection · Ctrl+F or / search · Ctrl + / Ctrl − thumbnail size |
| Photo | ← → previous/next · Enter caption · T tags · Ctrl+Enter save and next · Alt+← → or PageUp/PageDown while typing · Esc back |
| Anywhere | Ctrl+Z undo |

## How your folder is laid out

Sorting is folders. A photo you sort is **moved** into the folder you choose, together with everything that belongs
to it: its sidecar, Google Takeout `.json` files and a Live Photo `.mov` with the same name.

Captions, dates and tags live in a small Markdown file next to each photo, named after the photo's full file name:

```
Field Work/Prepping for paint/IMG_4821.HEIC
Field Work/Prepping for paint/IMG_4821.HEIC.md
```

```yaml
---
caption: Sanding the patched drywall before primer
date: 2025-02-14T10:31:00
tags: [drywall, sanding]
alt: Luke sanding a patched wall with a pole sander
---

Optional longer description, in Markdown.
```

- The file is created the first time a photo is sorted, tagged or captioned, with the date it was taken: from the
  camera (EXIF), else Google Takeout's `.json`, else the file's date.
- Keys are always written in that order. Keys you add by hand (`featured: true`, …) are kept.
- If a `.md` can't be read (broken YAML), the photo gets a warning badge and PhotoCurator won't overwrite it.

## Export for web

Right-click a folder → **Export for web…** and pick a destination, for example your site's `src/content/field`.
PhotoCurator writes:

```
src/content/field/
  folder.md                        title: Field Work
  prepping-for-paint/
    folder.md                      title: Prepping for paint
    img-4821.jpg                   ≤ 2400 px, upright, sRGB, no metadata (so no GPS location)
    img-4821.md                    the sidecar, plus image: ./img-4821.jpg
```

Exporting again rewrites only what changed and removes only files an earlier export wrote (they're listed in
`.photocurator-export`), so anything else in the destination is left alone. The destination must be empty the first
time, and outside the open folder. Your originals are never touched.

For an [Astro](https://astro.build) site, this content config reads the export (checked against Astro 7.3):

```ts
// src/content.config.ts
import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { z } from 'astro/zod';

const field = defineCollection({
  loader: glob({ base: './src/content/field', pattern: ['**/*.md', '!**/folder.md'] }),
  schema: ({ image }) =>
    z.object({
      image: image(),
      caption: z.string().default(''),
      date: z.coerce.date(),
      tags: z.array(z.string()).default([]),
      alt: z.string().optional()
    })
});

const fieldFolders = defineCollection({
  loader: glob({ base: './src/content/field', pattern: '**/folder.md' }),
  schema: z.object({ title: z.string() })
});

export const collections = { field, fieldFolders };
```

Then `<Image src={photo.data.image} alt={photo.data.alt ?? photo.data.caption} />` from `astro:assets` makes the
responsive WebP/AVIF versions at build time.

## Get the exe

PhotoCurator.exe is a self-contained build artifact and isn't committed to this repo. Build it yourself (on Windows,
or anywhere with the .NET 10 SDK):

```powershell
dotnet publish PhotoCuratorApp -c Release -r win-x64
```

The finished file is at `PhotoCuratorApp\bin\Release\net10.0-windows\win-x64\publish\PhotoCurator.exe` (about
75 MB). Copy just that one file anywhere: no .NET install, no Node.js.

## Requirements

- Windows 10 or 11, x64.
- The Microsoft Edge WebView2 Runtime, which Windows 10/11 already include. Without it PhotoCurator opens in your
  default browser instead and keeps a small window open as its off switch.

## What it does (and doesn't)

- **Never edits, re-encodes or deletes a photo.** It moves files, creates folders and writes `.md` sidecars; that's
  all. There is no delete; a "Rejects" folder is just another folder.
- **Never overwrites.** A clash when moving becomes `IMG_1 (1).HEIC` (its sidecar and companions are renamed to match).
- Writes every sidecar atomically (temp file, then replace), so a crash never leaves half a file.
- Skips hidden and system folders, `.git`, and folders an export wrote.
- Listens only on `127.0.0.1`, on a random port, and every request needs a per-launch token, so neither other
  computers nor web pages you visit can reach it.
- Keeps its own state (recent folders, thumbnail cache, WebView2 data) in `%LOCALAPPDATA%\PhotoCurator`. The photo
  folder only ever gains folders and `.md` files.
- Reads JPEG, HEIC/HEIF, PNG, WebP, AVIF, GIF and TIFF. Videos are left where they are (except a Live Photo clip,
  which travels with its photo).

## Project layout

| Folder | What it is |
|---|---|
| `PhotoCurator.Core/` | Everything except the window: `Library/` (scan, sidecars, moves, undo), `Media/` (dates, thumbnails, export, copy; Magick.NET), `Server/` (Kestrel on 127.0.0.1 + JSON API), and the UI in `Assets/` (HTML/CSS/JS modules, embedded). Plain .NET 10, builds and tests on any OS. |
| `PhotoCuratorApp/` | The Windows window (WinForms + WebView2): folder picker, Show in Explorer, single-file publish settings. |
| `PhotoCurator.Core.Tests/` | Unit and server tests (xUnit), including a small HEIC fixture. |
| `tools/DevHost/` | Runs the real server and UI on any OS, in a browser, for development. |
| `tools/e2e/` | Browser tests of the whole flow in Chromium (Playwright), checking the files on disk at every step. |

### Developing

```bash
dotnet test PhotoCurator.Core.Tests                                  # library + server tests (Linux/macOS/Windows)
dotnet run --project tools/DevHost -- --port 8790 --open ~/Pictures   # UI at the printed URL (no native dialogs; paths are typed)

# browser tests: needs Chromium (set PLAYWRIGHT_BROWSERS_PATH, CHROMIUM, or let Playwright download one)
dotnet build tools/DevHost && npm install --prefix tools/e2e && node tools/e2e/curator.e2e.mjs
```

`PhotoCuratorApp` compiles anywhere (`EnableWindowsTargeting`), but the window itself only runs on Windows.

## Third-party software

Magick.NET / ImageMagick (Apache-2.0, ImageMagick license; includes libheif and libde265, LGPL) for reading photos,
YamlDotNet (MIT) for the sidecars, and Microsoft Edge WebView2 for the window. See `THIRD_PARTY_NOTICES.md`.
