// The visual side of the write-up editor: the story as it will read, editable in place. Each paragraph, heading, list,
// quote and photo is a block. Type anywhere; Enter starts a new paragraph; drag photos in from the strip (or press +);
// drag a block by its handle to move it. Every change is turned back into Markdown (markdown.js), which is what's saved.
//
// Markdown shortcuts at the start of a paragraph: "# " "## " "### " heading, "- " list, "1. " numbered list, "> " quote.
// Ctrl+B bold, Ctrl+I italic, Ctrl+K link, Shift+Enter line break.

import { parseBlocks, serializeBlocks, inlineToHtml, escapeText, block } from './markdown.js';
import { icon } from './icons.js';

const PHOTO_TYPE = 'application/x-photocurator-story-photo';
const BLOCK_TYPE = 'application/x-photocurator-story-block';
const TEXT_TYPES = new Set(['paragraph', 'heading', 'quote']);

export class StoryEditor {
  /**
   * @param {HTMLElement} root
   * @param {object} host  photo(path) → { url, missing }, onChange(markdown), pickPhoto(anchor) → Promise<path|null>,
   *                       captionFor(path) → default caption for a newly placed photo
   */
  constructor(root, host) {
    this.root = root;
    this.host = host;
    this.blocks = [];
    this.active = -1;
    this.dragFrom = -1;

    this.page = document.createElement('div');
    this.page.className = 'story-page';
    this.tail = document.createElement('div');
    this.tail.className = 'story-tail';
    this.tail.title = 'Click to keep writing';
    this.indicator = document.createElement('div');
    this.indicator.className = 'story-drop';
    this.indicator.hidden = true;
    root.append(this.page, this.tail, this.indicator);

    this.page.addEventListener('input', event => this.onInput(event));
    this.page.addEventListener('keydown', event => this.onKey(event));
    this.page.addEventListener('paste', event => this.onPaste(event));
    this.page.addEventListener('focusin', event => {
      const index = this.indexOf(event.target);
      if (index >= 0) this.active = index;
    });
    this.page.addEventListener('click', event => this.onClick(event));
    this.tail.addEventListener('click', () => this.keepWriting());
    // The whole pane takes drops (above the first block means "at the top"), so a photo never lands in the title.
    root.addEventListener('dragover', event => this.onDragOver(event));
    root.addEventListener('drop', event => this.onDrop(event));
    root.addEventListener('dragleave', event => {
      if (!root.contains(event.relatedTarget)) this.indicator.hidden = true;
    });
  }

  // ---------- model ----------

  get markdown() {
    return serializeBlocks(this.blocks);
  }

  /** Loads Markdown (from disk or the Markdown pane) without reporting a change. */
  setMarkdown(markdown) {
    this.blocks = parseBlocks(markdown);
    this.render();
  }

  get hasFocus() {
    return this.page.contains(document.activeElement);
  }

  changed() {
    this.host.onChange(this.markdown);
  }

  insertPhoto(path, index = this.active >= 0 ? this.active + 1 : this.blocks.length) {
    const photo = block('photo', { caption: this.host.captionFor(path) ?? '', path, title: null });
    // A photo placed into an empty paragraph replaces it, so the story doesn't collect blank lines.
    const target = this.blocks[index - 1];
    if (target && target.type === 'paragraph' && !target.md.trim() && index - 1 === this.active) {
      this.blocks.splice(index - 1, 1, photo);
      index--;
    } else {
      this.blocks.splice(index, 0, photo);
    }
    this.render();
    this.changed();
    this.focusCaption(index);
    return index;
  }

  insertBlock(index, type) {
    const fields = type === 'heading' ? { level: 2, md: '' } : type === 'list' ? { ordered: false, items: [''] } : { md: '' };
    this.blocks.splice(index, 0, block(type, fields));
    this.render();
    this.focus(index, 'start');
    this.changed();
  }

  removeBlock(index) {
    this.blocks.splice(index, 1);
    this.render();
    this.changed();
    if (this.blocks.length) this.focus(Math.min(index, this.blocks.length - 1), 'end');
  }

  moveBlock(from, to) {
    if (to === from || to === from + 1) return;
    const [moved] = this.blocks.splice(from, 1);
    this.blocks.splice(to > from ? to - 1 : to, 0, moved);
    this.render();
    this.changed();
  }

  keepWriting() {
    const last = this.blocks[this.blocks.length - 1];
    if (last && last.type === 'paragraph' && !last.md.trim()) return this.focus(this.blocks.length - 1, 'end');
    this.insertBlock(this.blocks.length, 'paragraph');
  }

  // ---------- rendering ----------

  render() {
    const focusedId = this.blocks[this.active]?.id;
    this.page.replaceChildren(...this.blocks.map((b, i) => this.renderBlock(b, i)));
    if (!this.blocks.length) {
      const empty = document.createElement('div');
      empty.className = 'story-empty';
      empty.innerHTML = '<strong>Tell the story…</strong><span>Drag photos in from the strip above whenever you like.</span>';
      empty.addEventListener('click', () => this.keepWriting());
      this.page.append(empty);
    }
    this.active = this.blocks.findIndex(b => b.id === focusedId);
  }

  renderBlock(b, index) {
    const wrap = document.createElement('div');
    wrap.className = `sb sb-${b.type}`;
    wrap.dataset.index = index;

    const gutter = document.createElement('div');
    gutter.className = 'sb-gutter';
    const add = button('sb-add', 'Add a block below', icon('plus'));
    add.dataset.action = 'add';
    const handle = document.createElement('span');
    handle.className = 'sb-handle';
    handle.title = 'Drag to move';
    handle.draggable = true;
    handle.append(icon('grip'));
    handle.addEventListener('dragstart', event => {
      this.dragFrom = index;
      event.dataTransfer.setData(BLOCK_TYPE, String(index));
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setDragImage(wrap, 20, 20);
      wrap.classList.add('dragging');
    });
    handle.addEventListener('dragend', () => {
      wrap.classList.remove('dragging');
      this.dragFrom = -1;
      this.indicator.hidden = true;
    });
    gutter.append(add, handle);

    let body;
    switch (b.type) {
      case 'paragraph':
        body = editable('p', inlineToHtml(b.md), 'Write something, or press + for a photo or heading');
        break;
      case 'heading':
        body = editable(b.level <= 1 ? 'h2' : 'h3', inlineToHtml(b.md), 'Heading');
        body.classList.add(`level-${Math.min(b.level, 3)}`);
        break;
      case 'quote':
        body = editable('blockquote', inlineToHtml(b.md), 'Quote');
        break;
      case 'list': {
        body = document.createElement(b.ordered ? 'ol' : 'ul');
        body.className = 'sb-text sb-list';
        body.contentEditable = 'true';
        body.innerHTML = (b.items.length ? b.items : ['']).map(item => `<li>${inlineToHtml(item) || '<br>'}</li>`).join('');
        break;
      }
      case 'photo':
        body = this.renderPhoto(b, index);
        break;
      default: {
        body = document.createElement('pre');
        body.className = 'sb-raw';
        body.contentEditable = 'plaintext-only';
        body.spellcheck = false;
        body.textContent = b.md;
        body.title = 'Kept as Markdown: edit it here or in the Markdown pane';
      }
    }
    body.classList.add('sb-body');
    wrap.append(gutter, body);
    return wrap;
  }

  renderPhoto(b, index) {
    const figure = document.createElement('figure');
    figure.className = 'sb-figure';
    // The frame hugs the picture, so the tools sit on its corner whatever its shape.
    const frame = document.createElement('div');
    frame.className = 'sb-frame';
    const photo = this.host.photo(b.path);
    if (photo && !photo.missing) {
      const img = document.createElement('img');
      img.src = photo.url;
      img.alt = b.caption;
      img.decoding = 'async';
      img.draggable = false;
      frame.append(img);
    } else {
      frame.classList.add('missing');
      const missing = document.createElement('div');
      missing.className = 'sb-missing';
      missing.append(icon('warning'), document.createTextNode(`"${b.path}" isn't in this folder any more.`));
      frame.append(missing);
    }
    figure.append(frame);
    const caption = document.createElement('figcaption');
    caption.className = 'sb-caption';
    caption.contentEditable = 'plaintext-only';
    caption.dataset.placeholder = 'Add a caption';
    caption.textContent = b.caption;
    markEmpty(caption);
    figure.append(caption);

    const tools = document.createElement('div');
    tools.className = 'sb-tools';
    const up = button('', 'Move up', icon('arrowUp'));
    up.dataset.action = 'up';
    up.disabled = index === 0;
    const down = button('', 'Move down', icon('arrowDown'));
    down.dataset.action = 'down';
    down.disabled = index === this.blocks.length - 1;
    const replace = button('', 'Use a different photo', icon('photos'));
    replace.dataset.action = 'replace';
    const remove = button('danger', 'Remove from the story (the photo stays in the folder)', icon('close'));
    remove.dataset.action = 'remove';
    tools.append(up, down, replace, remove);
    frame.append(tools);
    return figure;
  }

  // ---------- focus + caret ----------

  indexOf(node) {
    const wrap = node?.closest?.('.sb');
    return wrap && this.page.contains(wrap) ? Number(wrap.dataset.index) : -1;
  }

  bodyOf(index) {
    return this.page.querySelector(`.sb[data-index="${index}"] .sb-body`);
  }

  focus(index, where = 'end') {
    const b = this.blocks[index];
    if (!b) return;
    if (b.type === 'photo') return this.focusCaption(index);
    const el = this.bodyOf(index);
    if (!el) return;
    el.focus({ preventScroll: true });
    setCaret(el, where);
    this.active = index;
    el.scrollIntoView({ block: 'nearest' });
  }

  focusCaption(index) {
    const caption = this.page.querySelector(`.sb[data-index="${index}"] .sb-caption`);
    if (!caption) return;
    caption.focus({ preventScroll: true });
    setCaret(caption, 'end');
    this.active = index;
    caption.closest('.sb').scrollIntoView({ block: 'nearest' });
  }

  // ---------- events ----------

  onInput(event) {
    const index = this.indexOf(event.target);
    const b = this.blocks[index];
    if (!b) return;
    markEmpty(event.target);
    if (event.target.classList.contains('sb-caption')) {
      b.caption = event.target.textContent.replace(/\s*\n\s*/g, ' ');
    } else if (b.type === 'raw') {
      b.md = event.target.textContent;
    } else if (b.type === 'list') {
      b.items = [...event.target.querySelectorAll('li')].map(li => htmlToInline(li));
      if (!b.items.length) b.items = [''];
    } else {
      b.md = htmlToInline(event.target);
      if (b.type === 'heading') b.md = b.md.replace(/\s*\n\s*/g, ' ');
      if (b.type === 'paragraph' && this.shortcut(index)) return;
    }
    this.changed();
  }

  /** "# " / "## " / "- " / "1. " / "> " typed at the start of a paragraph turn it into that kind of block. */
  shortcut(index) {
    const b = this.blocks[index];
    const text = this.bodyOf(index)?.textContent ?? '';
    const rules = [
      [/^(#{1,3})\s/, m => block('heading', { level: m[1].length, md: '' })],
      [/^[-*]\s/, () => block('list', { ordered: false, items: [''] })],
      [/^1[.)]\s/, () => block('list', { ordered: true, items: [''] })],
      [/^>\s/, () => block('quote', { md: '' })]
    ];
    for (const [pattern, make] of rules) {
      const m = text.replace(/ /g, ' ').match(pattern);
      if (!m || text.length !== m[0].length) continue;
      this.blocks[index] = make(m);
      this.render();
      this.focus(index, 'start');
      this.changed();
      return true;
    }
    return false;
  }

  onKey(event) {
    const index = this.indexOf(event.target);
    const b = this.blocks[index];
    if (!b) return;
    const el = event.target;
    const ctrl = event.ctrlKey || event.metaKey;

    if (ctrl && event.key.toLowerCase() === 'u') return event.preventDefault(); // no underline in Markdown
    if (ctrl && event.key.toLowerCase() === 'k' && b.type !== 'raw' && !el.classList.contains('sb-caption')) {
      event.preventDefault();
      const url = prompt('Link to (https://…)');
      if (url) document.execCommand('createLink', false, url.trim());
      return;
    }

    if (el.classList.contains('sb-caption')) {
      if (event.key === 'Enter') {
        event.preventDefault();
        const next = this.blocks[index + 1];
        if (next && next.type !== 'photo') this.focus(index + 1, 'start');
        else this.insertBlock(index + 1, 'paragraph');
      }
      return;
    }

    if (event.key === 'Enter' && !event.shiftKey && !ctrl && TEXT_TYPES.has(b.type)) {
      event.preventDefault();
      this.split(index);
      return;
    }
    if (event.key === 'Enter' && event.shiftKey && TEXT_TYPES.has(b.type)) {
      event.preventDefault();
      document.execCommand('insertLineBreak');
      return;
    }
    if (event.key === 'Enter' && b.type === 'list' && !event.shiftKey) {
      const li = closestItem(el);
      if (li && !li.textContent.trim()) {
        // Enter on an empty item ends the list.
        event.preventDefault();
        li.remove();
        b.items = [...el.querySelectorAll('li')].map(item => htmlToInline(item)).filter((t, i, all) => t.trim() || all.length === 1);
        if (!el.querySelector('li')) this.blocks.splice(index, 1, block('paragraph', { md: '' }));
        else this.blocks.splice(index + 1, 0, block('paragraph', { md: '' }));
        this.render();
        this.focus(el.querySelector('li') ? index + 1 : index, 'start');
        this.changed();
      }
      return;
    }

    if (event.key === 'Backspace' && caretAtStart(el)) {
      if (b.type === 'list') {
        const li = closestItem(el);
        if (li && li === el.querySelector('li') && !li.textContent.trim() && b.items.filter(t => t.trim()).length === 0) {
          event.preventDefault();
          this.blocks[index] = block('paragraph', { md: '' });
          this.render();
          this.focus(index, 'start');
          this.changed();
        }
        return;
      }
      if (b.type === 'raw') return;
      event.preventDefault();
      if (b.type !== 'paragraph' && b.md.trim()) {
        // Backspace at the start of a heading or quote turns it back into a paragraph.
        this.blocks[index] = block('paragraph', { md: b.md });
        this.render();
        this.focus(index, 'start');
        this.changed();
        return;
      }
      this.mergeBack(index);
      return;
    }
    if (event.key === 'Delete' && caretAtEnd(el) && TEXT_TYPES.has(b.type)) {
      const next = this.blocks[index + 1];
      if (next && TEXT_TYPES.has(next.type)) {
        event.preventDefault();
        this.mergeBack(index + 1);
      }
      return;
    }

    if ((event.key === 'ArrowUp' || (event.key === 'ArrowLeft' && !event.shiftKey)) && caretAtStart(el) && index > 0) {
      event.preventDefault();
      this.focus(index - 1, 'end');
    } else if ((event.key === 'ArrowDown' || (event.key === 'ArrowRight' && !event.shiftKey)) && caretAtEnd(el) && index < this.blocks.length - 1) {
      event.preventDefault();
      this.focus(index + 1, 'start');
    }
  }

  /** Enter: the text after the caret becomes a new paragraph below. */
  split(index) {
    const b = this.blocks[index];
    const el = this.bodyOf(index);
    const selection = getSelection();
    if (!selection.rangeCount) return;
    const range = selection.getRangeAt(0);
    range.deleteContents();
    const after = document.createRange();
    after.setStart(range.endContainer, range.endOffset);
    after.setEnd(el, el.childNodes.length);
    const holder = document.createElement('div');
    holder.append(after.extractContents());
    b.md = htmlToInline(el);
    this.blocks.splice(index + 1, 0, block('paragraph', { md: htmlToInline(holder) }));
    this.render();
    this.focus(index + 1, 'start');
    this.changed();
  }

  /** Backspace at the start of a block: join it to the text block above, or remove it if it's empty. */
  mergeBack(index) {
    const b = this.blocks[index];
    const previous = this.blocks[index - 1];
    if (previous && TEXT_TYPES.has(previous.type)) {
      const at = plainLength(previous.md);
      previous.md = joinMarkdown(previous.md, b.md);
      this.blocks.splice(index, 1);
      this.render();
      this.focus(index - 1, at);
      this.changed();
    } else if (!b.md.trim() && this.blocks.length > 1) {
      this.blocks.splice(index, 1);
      this.render();
      if (index > 0) this.focus(index - 1, 'end');
      else this.focus(0, 'start');
      this.changed();
    }
  }

  onPaste(event) {
    const index = this.indexOf(event.target);
    const b = this.blocks[index];
    if (!b) return;
    event.preventDefault();
    const text = (event.clipboardData?.getData('text/plain') ?? '').replace(/\r\n?/g, '\n');
    if (!text) return;
    const plainOnly = b.type === 'raw' || b.type === 'list' || event.target.classList.contains('sb-caption');
    const [first, ...rest] = plainOnly ? [text] : text.split(/\n\s*\n/);
    document.execCommand('insertText', false, plainOnly ? first : first.replace(/\s*\n\s*/g, ' '));
    if (!rest.length) return;
    // More paragraphs: each becomes its own block below.
    const added = rest.map(chunk => chunk.trim()).filter(Boolean).map(chunk => block('paragraph', { md: escapeText(chunk.replace(/\s*\n\s*/g, ' ')) }));
    b.md = htmlToInline(this.bodyOf(index));
    this.blocks.splice(index + 1, 0, ...added);
    this.render();
    this.focus(index + added.length, 'end');
    this.changed();
  }

  async onClick(event) {
    const action = event.target.closest('[data-action]');
    if (!action) return;
    const index = this.indexOf(action);
    switch (action.dataset.action) {
      case 'add':
        this.host.openAddMenu(action, type => {
          if (type === 'photo') this.pickInto(action, index + 1);
          else this.insertBlock(index + 1, type);
        });
        break;
      case 'up': this.moveBlock(index, index - 1); break;
      case 'down': this.moveBlock(index, index + 2); break;
      case 'remove': this.removeBlock(index); break;
      case 'replace': {
        const path = await this.host.pickPhoto(action);
        if (!path) return;
        this.blocks[index].path = path;
        this.render();
        this.changed();
        break;
      }
    }
  }

  async pickInto(anchor, index) {
    const path = await this.host.pickPhoto(anchor);
    if (path) this.insertPhoto(path, index);
  }

  // ---------- drag and drop ----------

  dropIndex(clientY) {
    const wraps = [...this.page.querySelectorAll('.sb')];
    for (const wrap of wraps) {
      const rect = wrap.getBoundingClientRect();
      if (clientY < rect.top + rect.height / 2) return Number(wrap.dataset.index);
    }
    return this.blocks.length;
  }

  onDragOver(event) {
    const types = event.dataTransfer.types;
    if (!types.includes(PHOTO_TYPE) && !types.includes(BLOCK_TYPE)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = types.includes(BLOCK_TYPE) ? 'move' : 'copy';
    const index = this.dropIndex(event.clientY);
    const wraps = this.page.querySelectorAll('.sb');
    const rootRect = this.root.getBoundingClientRect();
    let y;
    if (!wraps.length) y = this.page.getBoundingClientRect().top + 8;
    else if (index < wraps.length) y = wraps[index].getBoundingClientRect().top - 6;
    else y = wraps[wraps.length - 1].getBoundingClientRect().bottom + 6;
    const pageRect = this.page.getBoundingClientRect();
    this.indicator.style.top = `${y - rootRect.top + this.root.scrollTop}px`;
    this.indicator.style.left = `${pageRect.left - rootRect.left}px`;
    this.indicator.style.width = `${pageRect.width}px`;
    this.indicator.hidden = false;
  }

  onDrop(event) {
    const types = event.dataTransfer.types;
    if (!types.includes(PHOTO_TYPE) && !types.includes(BLOCK_TYPE)) return;
    event.preventDefault();
    this.indicator.hidden = true;
    const index = this.dropIndex(event.clientY);
    if (types.includes(BLOCK_TYPE)) {
      this.moveBlock(Number(event.dataTransfer.getData(BLOCK_TYPE)), index);
    } else {
      this.active = -1;
      this.insertPhoto(event.dataTransfer.getData(PHOTO_TYPE), index);
    }
  }
}

export { PHOTO_TYPE };

// ---------- helpers ----------

function button(className, title, content) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = className;
  b.title = title;
  b.setAttribute('aria-label', title);
  b.append(content);
  return b;
}

function editable(tag, html, placeholder) {
  const el = document.createElement(tag);
  el.className = 'sb-text';
  el.contentEditable = 'true';
  el.dataset.placeholder = placeholder;
  el.innerHTML = html;
  markEmpty(el);
  return el;
}

/** Placeholders show on [data-empty]; :empty misses the <br> contenteditable leaves behind. */
function markEmpty(el) {
  el.toggleAttribute('data-empty', !el.textContent);
}

function closestItem(listEl) {
  const node = getSelection().anchorNode;
  const el = node?.nodeType === 1 ? node : node?.parentElement;
  const li = el?.closest('li');
  return li && listEl.contains(li) ? li : null;
}

/** The visual editor's HTML → inline Markdown. */
export function htmlToInline(node) {
  let out = '';
  for (const child of node.childNodes) {
    if (child.nodeType === Node.TEXT_NODE) {
      out += escapeText(child.nodeValue.replace(/ /g, ' '));
      continue;
    }
    if (child.nodeType !== Node.ELEMENT_NODE) continue;
    const inner = () => htmlToInline(child);
    switch (child.tagName) {
      case 'B': case 'STRONG': { const t = inner(); out += t.trim() ? `**${t}**` : t; break; }
      case 'I': case 'EM': { const t = inner(); out += t.trim() ? `*${t}*` : t; break; }
      case 'CODE': out += '`' + child.textContent + '`'; break;
      case 'A': { const t = inner(); out += child.getAttribute('href') ? `[${t}](${child.getAttribute('href')})` : t; break; }
      case 'BR': out += child.hasAttribute('data-soft') ? '\n' : '  \n'; break;
      case 'DIV': case 'P': { const t = inner(); out += (out && !out.endsWith('\n') ? '  \n' : '') + t; break; }
      default: out += inner();
    }
  }
  // A trailing <br> is how contenteditable keeps an empty last line; it isn't content.
  return out.replace(/( {2}\n|\n)+$/, '');
}

function plainLength(md) {
  const div = document.createElement('div');
  div.innerHTML = inlineToHtml(md);
  return div.textContent.length;
}

function joinMarkdown(a, b) {
  if (!a.trim()) return b;
  if (!b.trim()) return a;
  return a + b;
}

function caretOffset(el) {
  const selection = getSelection();
  if (!selection.rangeCount || !el.contains(selection.anchorNode)) return -1;
  const range = selection.getRangeAt(0);
  if (!range.collapsed) return -1;
  const before = document.createRange();
  before.selectNodeContents(el);
  before.setEnd(range.startContainer, range.startOffset);
  return before.toString().length;
}

function caretAtStart(el) {
  return caretOffset(el) === 0;
}

function caretAtEnd(el) {
  const offset = caretOffset(el);
  return offset >= 0 && offset === el.textContent.length;
}

/** Puts the caret at 'start', 'end' or a text offset inside el. */
function setCaret(el, where) {
  const selection = getSelection();
  const range = document.createRange();
  if (where === 'start' || where === 0) {
    range.setStart(el, 0);
  } else if (where === 'end' || typeof where !== 'number') {
    range.selectNodeContents(el);
    range.collapse(false);
  } else {
    let left = where;
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node && left > node.nodeValue.length) {
      left -= node.nodeValue.length;
      node = walker.nextNode();
    }
    if (node) range.setStart(node, left);
    else {
      range.selectNodeContents(el);
      range.collapse(false);
    }
  }
  range.collapse(true);
  selection.removeAllRanges();
  selection.addRange(range);
}
