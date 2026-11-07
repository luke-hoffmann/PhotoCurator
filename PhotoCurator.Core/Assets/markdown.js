// A write-up's Markdown as a list of blocks the visual editor can show and edit, and back. Pure functions, no DOM, so
// they're unit-tested in Node (tools/e2e/markdown.test.mjs).
//
// Blocks: paragraph, heading (level 1-6), list (bullet or numbered), quote, photo (![Caption](path)) and raw. Anything
// the visual editor doesn't model (code, tables, HTML, a photo in the middle of a sentence...) becomes a raw block that
// keeps its Markdown exactly, so nothing typed in the Markdown pane is ever lost.

let nextId = 1;
const id = () => nextId++;

const HEADING = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?[ \t]*#*[ \t]*$/;
const IMAGE_LINE = /^!\[((?:[^\]\\]|\\.)*)\]\((?:<([^>\n]*)>|([^)\s]+))(?:\s+"([^"\n]*)")?\)$/;
const FENCE = /^ {0,3}(`{3,}|~{3,})/;
// Top-level list items only: a marker indented two or more spaces is a nested list, which is kept as written.
const BULLET = /^ ?([-*+])[ \t]+(.*)$/;
const NUMBERED = /^ ?(\d{1,9})[.)][ \t]+(.*)$/;

export function block(type, fields = {}) {
  return { id: id(), type, ...fields };
}

/** Markdown → blocks. */
export function parseBlocks(markdown) {
  const lines = String(markdown ?? '').replace(/\r\n?/g, '\n').split('\n');
  const blocks = [];
  let i = 0;
  while (i < lines.length) {
    if (!lines[i].trim()) {
      i++;
      continue;
    }
    const fence = lines[i].match(FENCE);
    if (fence) {
      const start = i++;
      while (i < lines.length && !lines[i].trimStart().startsWith(fence[1])) i++;
      i = Math.min(i + 1, lines.length);
      blocks.push(block('raw', { md: lines.slice(start, i).join('\n') }));
      continue;
    }
    const chunk = [];
    while (i < lines.length && lines[i].trim() && !FENCE.test(lines[i])) chunk.push(lines[i++]);
    blocks.push(...classify(chunk));
  }
  return blocks;
}

/** Splits one run of non-blank lines into blocks: headings and photo lines stand alone, the rest groups up. */
function classify(lines) {
  const out = [];
  let run = [];
  const flush = () => {
    if (run.length) out.push(classifyRun(run));
    run = [];
  };
  for (const line of lines) {
    const heading = line.match(HEADING);
    if (heading && run.length === 0) {
      out.push(block('heading', { level: heading[1].length, md: (heading[2] ?? '').trim() }));
      continue;
    }
    if (IMAGE_LINE.test(line.trim())) {
      flush();
      out.push(photoFrom(line.trim()));
      continue;
    }
    run.push(line);
  }
  flush();
  return out;
}

function classifyRun(lines) {
  const text = lines.join('\n');
  if (lines.every(l => /^ {0,3}>/.test(l))) {
    return block('quote', { md: lines.map(l => l.replace(/^ {0,3}> ?/, '')).join('\n') });
  }

  const numbered = NUMBERED.test(lines[0]);
  if (numbered || BULLET.test(lines[0])) {
    const pattern = numbered ? NUMBERED : BULLET;
    const items = [];
    for (const line of lines) {
      const m = line.match(pattern);
      if (m) items.push(m[2]);
      else if (/^\s{2,}\S/.test(line) && items.length && !BULLET.test(line.trim()) && !NUMBERED.test(line.trim())) items[items.length - 1] += '\n' + line.trim();
      else return block('raw', { md: text }); // nested or mixed lists: keep as written
    }
    return block('list', { ordered: numbered, items });
  }

  const unusual =
    /^\s*</.test(text) ||                       // HTML
    /^\s*\|.*\|\s*$/m.test(text) ||             // table
    /^ {4,}\S/.test(lines[0]) ||                // indented code
    /!\[[^\]]*\]\(/.test(text) ||                // a photo inside a sentence
    /^\s*(=+|-{3,}|\*{3,}|_{3,})\s*$/m.test(text); // setext heading or divider
  return unusual ? block('raw', { md: text }) : block('paragraph', { md: text });
}

function photoFrom(line) {
  const m = line.match(IMAGE_LINE);
  return block('photo', { caption: m[1].replace(/\\(.)/g, '$1'), path: decodePath(m[2] ?? m[3]), title: m[4] ?? null });
}

function decodePath(path) {
  try { return decodeURIComponent(path); } catch { return path; }
}

/** Blocks → Markdown. Empty text blocks (just created, nothing typed yet) are left out. */
export function serializeBlocks(blocks) {
  return blocks.map(serializeBlock).filter(text => text.trim().length > 0).join('\n\n');
}

function serializeBlock(b) {
  switch (b.type) {
    case 'paragraph': return guardStart(b.md);
    case 'heading': return b.md.trim() ? `${'#'.repeat(b.level)} ${b.md.replace(/\s*\n\s*/g, ' ').trim()}` : '';
    case 'list': return b.items.filter(t => t.trim()).map((t, i) => {
      const marker = b.ordered ? `${i + 1}. ` : '- ';
      return marker + t.trim().replace(/\n/g, '\n' + ' '.repeat(marker.length));
    }).join('\n');
    case 'quote': return b.md.trim() ? b.md.split('\n').map(l => `> ${l}`.trimEnd()).join('\n') : '';
    case 'photo': return photoMarkdown(b.caption, b.path, b.title);
    default: return b.md;
  }
}

/** A paragraph that starts like a heading, list or quote would turn into one when read back: escape that first character. */
function guardStart(md) {
  return md.replace(/^( {0,3})(#{1,6}(?:\s|$)|[-*+]\s|\d{1,9}[.)]\s|>)/, (all, space, marker) => `${space}\\${marker}`);
}

/** ![Caption](path), with <angle brackets> around paths that need them (spaces, parentheses). */
export function photoMarkdown(caption, path, title = null) {
  const text = String(caption ?? '').replace(/\\/g, '\\\\').replace(/\[/g, '\\[').replace(/\]/g, '\\]').replace(/\s*\n\s*/g, ' ');
  const target = /[\s()<>]/.test(path) ? `<${path}>` : path;
  return `![${text}](${target}${title ? ` "${title}"` : ''})`;
}

// ---------- inline: **bold**, *italic*, `code`, [links](url), line breaks ----------

const escapeHtml = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const INLINE = new RegExp([
  /\\([\\`*_{}[\]()#+\-.!<>~|])/.source,                                  // 1: escaped character
  /(`+)([\s\S]*?[^`])\2(?!`)/.source,                                     // 2,3: code
  /\*\*(?=\S)([\s\S]*?\S)\*\*(?!\*)/.source,                              // 4: **bold** (closing ** not followed by *: **a *b***)
  /(?<![A-Za-z0-9_])__(?=\S)([\s\S]*?\S)__(?![A-Za-z0-9_])/.source,      // 5: __bold__
  /\*(?=[^\s*])([\s\S]*?[^\s*])\*/.source,                                // 6: *italic*
  /(?<![A-Za-z0-9_])_(?=[^\s_])([\s\S]*?[^\s_])_(?![A-Za-z0-9_])/.source, // 7: _italic_
  /\[([^\]\n]*)\]\(([^)\s]+)\)/.source,                                   // 8,9: [link](url)
  /( {2,}|\\)\n/.source,                                                  // 10: hard line break
  /\n/.source                                                             // soft line break
].join('|'), 'g');

/** Inline Markdown → HTML for the visual editor. Soft line breaks become <br data-soft> so they round-trip as soft. */
export function inlineToHtml(md) {
  let html = '';
  let last = 0;
  INLINE.lastIndex = 0;
  const source = String(md ?? '');
  const re = new RegExp(INLINE.source, 'g');
  let m;
  while ((m = re.exec(source))) {
    html += escapeHtml(source.slice(last, m.index));
    if (m[1] !== undefined) html += escapeHtml(m[1]);
    else if (m[2] !== undefined) html += `<code>${escapeHtml(m[3])}</code>`;
    else if (m[4] !== undefined) html += `<strong>${inlineToHtml(m[4])}</strong>`;
    else if (m[5] !== undefined) html += `<strong>${inlineToHtml(m[5])}</strong>`;
    else if (m[6] !== undefined) html += `<em>${inlineToHtml(m[6])}</em>`;
    else if (m[7] !== undefined) html += `<em>${inlineToHtml(m[7])}</em>`;
    else if (m[8] !== undefined) html += `<a href="${escapeHtml(m[9])}">${inlineToHtml(m[8])}</a>`;
    else if (m[10] !== undefined) html += '<br>';
    else html += '<br data-soft="">';
    last = re.lastIndex;
  }
  return html + escapeHtml(source.slice(last));
}

/** Plain text typed in the visual editor → Markdown that reads back as the same text. */
export function escapeText(text) {
  const alnum = c => c !== undefined && /[A-Za-z0-9]/.test(c);
  return String(text)
    .replace(/[\\`*[\]<]/g, '\\$&')
    .replace(/_/g, (u, i, s) => (alnum(s[i - 1]) && alnum(s[i + 1]) ? '_' : '\\_'));
}

/** The text of a caption or heading, without Markdown, for display where formatting doesn't apply. */
export function plainText(md) {
  return String(md ?? '').replace(/\\(.)/g, '$1').replace(/[*_`]/g, '');
}
