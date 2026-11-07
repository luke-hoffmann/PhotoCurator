# PhotoCurator

Sort, tag and caption the photos in any folder, then export web-ready copies for a static site. A single portable
Windows `.exe`; everything it knows lives in that folder as plain files, so there is no database to lose or migrate.

Built for a pile of Google Photos downloads: open the folder, look through every photo (at any depth), sort them
into folders like `Field Work/Prepping for paint` or `Camping/Big Sur 2025`, tag them (`hammer`, `headshot`…) and
write a caption. Give a project folder a write-up, the story with its photos in between. Then export the folders you
want on your website.

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
6. Or drag photos onto a folder in the sidebar. Right-click a folder for **New folder inside**, **Rename**,
   **Write-up** and **Export for web**.
7. **Ctrl+Z** undoes moves, folder changes, tags, captions and write-ups (for as long as the folder is open).

The sidebar's **New** list shows photos you haven't sorted, tagged or captioned yet; **No caption** and **No tags**
are handy to-do lists too.

### Google Photos albums (no duplicates)

Google Photos can't download "only the new ones", so download the **whole album** each time and let PhotoCurator
sort it out:

1. In Google Photos, open the album → ⋮ → **Download all**. Keep the zip file(s); there's no need to unzip.
2. In PhotoCurator, click **Import…**, add the zip file(s) (or a folder) and **Check for new photos**.
3. The summary says what's in the download: photos **already in your folder** are skipped, even ones you've since
   moved, renamed or captioned (they're recognized by their content, not their name); photos that appear **twice in
   the download** come in once; **new** photos come in; and photos that look like an **edited version** of one you
   have (same moment, different file, e.g. edited in Google Photos) come in too, flagged under Duplicates.
4. **Import** copies them into `Inbox/<album name>`, where they show up under **New**, Live Photo clips and Takeout
   `.json` files included. Ctrl+Z takes an import back. The download itself is never changed.

**Duplicates** (in the sidebar when there are any) lists the same photo found more than once in your folder: exact
copies, and different files of the same moment. Pick the one to keep (it's pre-selected: the captioned, already
sorted one); it takes over the others' tags and caption, and the others move to `Duplicates set aside`, which you can
delete in Explorer when you're happy. **Not duplicates** hides a group for good (burst shots, for example).

### Write-ups

Every folder has a **Write-up** tab next to **Photos**: the story of that project with its photos in between, the
way a case study or blog post reads. It's saved as `folder.md` in that folder and exported with it.

- **Type straight into the page.** The title and summary are at the top (the title starts out as the folder's name);
  Enter moves on to the story.
- **Add photos** by dragging them in from the strip above, to any spot in the story, or by clicking one to add it
  after the paragraph you're in. Write its caption right under it. Photos already in the story have a check mark.
- Start a line with `## ` for a heading, `- ` for a list, `1. ` for a numbered list or `> ` for a quote, like in
  Markdown. **Ctrl+B** bold, **Ctrl+I** italic, **Ctrl+K** link, **Shift+Enter** a line break.
- **Hover** over a paragraph or photo for its controls: **+** adds a block below, ⠿ drags it somewhere else, and a
  photo's toolbar moves it up or down, swaps in another photo, or takes it out of the story (the photo itself stays
  in the folder).
- **Split** shows the Markdown beside the page, **Write** only the page, **Markdown** only the text. Edit either side
  and the other follows. Anything the page can't show as-is (tables, code, HTML) is kept exactly as you wrote it.
- **Cover** picks the project's main photo (by default, the story's first photo). **Add date** says when it happened.
- It saves as you type. **Ctrl+Z** steps back through your edits (a burst of typing is one step, moving or removing a
  photo is one step), **Ctrl+Shift+Z** forward again.

It's plain Markdown, so any text editor works too. Photo paths are relative to the folder, so a write-up can use
photos from the folders inside it:

```markdown
---
title: Attic insulation
summary: Pulling out 40 years of old insulation and blowing in R-49.
date: 2024-05-01
cover: IMG_0966.HEIC
---

The attic had decades of old, dusty insulation. We pulled it all out by hand before air sealing.

## Demo day

![Pulling old insulation out of the attic](IMG_0172.HEIC)

![Where we started](<Before photos/IMG_0001.HEIC>)
```

Folders with a write-up have a small page icon in the sidebar.

### Keyboard

| Where | Keys |
|---|---|
| Grid | arrows move · Space select · Shift+arrows extend · Enter open · Ctrl+A all · Esc clear · T / M / C act on the selection · Ctrl+F or / search · Ctrl + / Ctrl − thumbnail size |
| Photo | ← → previous/next · Enter caption · T tags · Ctrl+Enter save and next · Alt+← → or PageUp/PageDown while typing · Esc back |
| Write-up | Enter new paragraph · Shift+Enter line break · `## ` heading · `- ` list · `> ` quote · Ctrl+B / Ctrl+I / Ctrl+K · Ctrl+Z / Ctrl+Shift+Z undo/redo in the write-up |
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
    folder.md                      the folder's write-up (or just its title)
    img-4821.jpg                   ≤ 2400 px, upright, sRGB, no metadata (so no GPS location)
    img-4821.md                    the sidecar, plus image: ./img-4821.jpg
```

A write-up's photos point at the exported copies (`![Sanding the patch](./img-4821.jpg)`), and so does its cover
(the story's first photo when none was picked). A photo from outside the exported folder can't come along: it's left
out of the page, and the export says so.

Exporting again rewrites only what changed and removes only files an earlier export wrote (they're listed in
`.photocurator-export`), so anything else in the destination is left alone. The destination must be empty the first
time, and outside the open folder. Your originals are never touched.

For an [Astro](https://astro.build) site, this content config reads the export (checked against Astro 7.4):

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
  schema: ({ image }) =>
    z.object({
      title: z.string(),
      summary: z.string().optional(),
      date: z.coerce.date().optional(),
      cover: image().optional()
    })
});

export const collections = { field, fieldFolders };
```

Then `<Image src={photo.data.image} alt={photo.data.alt ?? photo.data.caption} />` from `astro:assets` makes the
responsive WebP/AVIF versions at build time. A page per write-up, with its photos optimized the same way (each
caption becomes the photo's alt text):

```astro
---
// src/pages/field/[...slug].astro
import { getCollection, render } from 'astro:content';

export async function getStaticPaths() {
  const folders = await getCollection('fieldFolders');
  return folders
    .filter(folder => folder.id !== 'folder') // the top folder.md is the /field page itself
    .map(folder => ({ params: { slug: folder.id.replace(/\/folder$/, '') }, props: { folder } }));
}

const { folder } = Astro.props;
const { Content } = await render(folder);
---
<article>
  <h1>{folder.data.title}</h1>
  {folder.data.summary && <p>{folder.data.summary}</p>}
  <Content />
</article>
```

## Get the exe

PhotoCurator.exe is a self-contained build artifact and isn't committed to this repo. Build it yourself (on Windows,
or anywhere with the .NET 10 SDK):

```powershell
dotnet publish PhotoCuratorApp -c Release -r win-x64
```

The finished file is at `PhotoCuratorApp\bin\Release\net10.0-windows\win-x64\publish\PhotoCurator.exe` (about
75 MB). Copy just that one file anywhere: no .NET install, no Node.js.

**Close PhotoCurator before you publish.** While it's open, Windows locks the exe, the publish fails ("being used by
another process") and the old exe stays. To check which build you're running, click the folder name at the top left:
the menu ends with `PhotoCurator · build <commit>`, the first 7 characters of the commit it was built from (the same
ones GitHub Desktop shows in History).

## Requirements

- Windows 10 or 11, x64.
- The Microsoft Edge WebView2 Runtime, which Windows 10/11 already include. Without it PhotoCurator opens in your
  default browser instead and keeps a small window open as its off switch.

## What it does (and doesn't)

- **Never edits, re-encodes or deletes a photo.** It moves files, creates folders, writes `.md` sidecars and copies
  in imports; that's all. There is no delete; a "Rejects" folder is just another folder, and duplicates are set
  aside, not deleted. (Undoing an import removes the copies that import made, if they're unchanged.)
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
| `PhotoCurator.Core/` | Everything except the window: `Library/` (scan, sidecars, moves, undo, duplicates), `Media/` (dates, fingerprints, import, thumbnails, export, copy; Magick.NET), `Server/` (Kestrel on 127.0.0.1 + JSON API), and the UI in `Assets/` (HTML/CSS/JS modules, embedded). Plain .NET 10, builds and tests on any OS. |
| `PhotoCuratorApp/` | The Windows window (WinForms + WebView2): folder picker, Show in Explorer, single-file publish settings. |
| `PhotoCurator.Core.Tests/` | Unit and server tests (xUnit), including a small HEIC fixture. |
| `tools/DevHost/` | Runs the real server and UI on any OS, in a browser, for development. |
| `tools/e2e/` | Browser tests of the whole flow in Chromium (Playwright), checking the files on disk at every step, and unit tests of the write-up editor's Markdown model. |

### Developing

```bash
dotnet test PhotoCurator.Core.Tests                                  # library + server tests (Linux/macOS/Windows)
dotnet run --project tools/DevHost -- --port 8790 --open ~/Pictures   # UI at the printed URL (no native dialogs; paths are typed)

# browser tests: needs Chromium (set PLAYWRIGHT_BROWSERS_PATH, CHROMIUM, or let Playwright download one)
dotnet build tools/DevHost && npm install --prefix tools/e2e && node tools/e2e/curator.e2e.mjs
node --test tools/e2e/markdown.test.mjs                               # the write-up editor's Markdown model
```

`PhotoCuratorApp` compiles anywhere (`EnableWindowsTargeting`), but the window itself only runs on Windows.

## Third-party software

Magick.NET / ImageMagick (Apache-2.0, ImageMagick license; includes libheif and libde265, LGPL) for reading photos,
YamlDotNet (MIT) for the sidecars, and Microsoft Edge WebView2 for the window. See `THIRD_PARTY_NOTICES.md`.
