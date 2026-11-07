// The Write-up tab: a folder's story. A slim bar (cover, date, layout), the folder's photos in a strip, then the story
// twice: as Markdown and as it will read (editable, title and summary on top). Edit either side; the other follows.
// Saves by itself to folder.md.

import { get, put, imageUrl, thumbUrl } from './api.js';
import { el, toast, openPopover, openMenu } from './dialogs.js';
import { icon } from './icons.js';
import { photoMarkdown, parseBlocks } from './markdown.js';
import { StoryEditor, PHOTO_TYPE } from './story.js';

const SAVE_DELAY = 700;
const TYPING_BURST = 1000; // ms between edits that still count as the same undo step
const LAYOUTS = [['write', 'Write'], ['split', 'Split'], ['markdown', 'Markdown']];

export class WriteUpView {
  /**
   * @param {HTMLElement} root
   * @param {object} app  library(), onSaved(result)
   */
  constructor(root, app) {
    this.root = root;
    this.app = app;
    this.folder = null;
    this.data = null;
    this.timer = 0;
    this.chain = Promise.resolve();
    this.dirty = false;
    this.lastFocus = 'visual';
    this.past = [];
    this.future = [];
    this.present = null;
    this.lastEdit = 0;
    this.build();
  }

  build() {
    // Title and summary live in two places that stay in sync: at the top of the page as it will read, and as
    // front-matter fields at the top of the Markdown pane (so they're editable in every layout).
    this.storyTitle = el('textarea', { class: 'story-title', rows: '1', spellcheck: 'true', 'aria-label': 'Title' });
    this.storySummary = el('textarea', { class: 'story-summary', rows: '1', spellcheck: 'true', placeholder: 'Add a one-line summary', 'aria-label': 'Summary' });
    this.mdTitle = el('textarea', { rows: '1', spellcheck: 'true', 'aria-label': 'Title' });
    this.mdSummary = el('textarea', { rows: '1', spellcheck: 'true', placeholder: 'One line about the project', 'aria-label': 'Summary' });
    this.titleFields = [this.storyTitle, this.mdTitle];
    this.summaryFields = [this.storySummary, this.mdSummary];

    this.coverButton = el('button', { class: 'wu-chip', type: 'button' });
    this.dateInput = el('input', { class: 'wu-date-input', type: 'date', tabindex: '-1', 'aria-hidden': 'true' });
    this.dateLabel = el('span');
    this.dateClear = el('button', { class: 'wu-chip-clear', type: 'button', title: 'Remove the date', 'aria-label': 'Remove the date' }, icon('close'));
    this.dateButton = el('button', { class: 'wu-chip', type: 'button', title: 'When the project happened (optional)' }, icon('calendar'), this.dateLabel);
    this.saveState = el('span', { class: 'save-state wu-save' });
    this.layoutControl = el('div', { class: 'segmented small', role: 'tablist', 'aria-label': 'Layout' },
      ...LAYOUTS.map(([value, label]) => el('button', { type: 'button', role: 'tab', 'data-layout': value, text: label })));

    this.strip = el('div', { class: 'wu-strip-row' });
    this.textarea = el('textarea', {
      class: 'wu-markdown', spellcheck: 'true', 'aria-label': 'Markdown',
      placeholder: 'The story appears here as Markdown while you write. You can type here too:\n\n## A heading\n\nA paragraph with **bold** and *italic*.\n\n![A caption](IMG_0001.jpg)'
    });
    const visualScroll = el('div', { class: 'wu-visual' });
    this.panes = el('div', { class: 'wu-panes' },
      el('section', { class: 'wu-pane wu-pane-md' },
        el('div', { class: 'md-front' },
          el('label', {}, el('span', { text: 'title:' }), this.mdTitle),
          el('label', {}, el('span', { text: 'summary:' }), this.mdSummary)),
        this.textarea),
      el('section', { class: 'wu-pane wu-pane-visual' }, visualScroll));

    this.root.replaceChildren(
      el('div', { class: 'wu-bar' },
        this.coverButton,
        el('span', { class: 'wu-date' }, this.dateButton, this.dateClear, this.dateInput),
        el('span', { class: 'wu-spacer' }),
        this.saveState,
        this.layoutControl),
      el('section', { class: 'wu-strip', 'aria-label': 'Photos in this folder' },
        el('div', { class: 'wu-strip-label' }, el('strong', { text: 'Photos in this folder' }), el('span', { text: 'Drag into the story, or click to add' })),
        this.strip),
      this.panes);

    this.editor = new StoryEditor(visualScroll, {
      photo: path => this.photoFor(path),
      captionFor: path => this.photoFor(path)?.caption ?? '',
      onChange: markdown => {
        this.setTextarea(markdown);
        this.schedule();
      },
      pickPhoto: anchor => this.pickPhoto(anchor, 'Add a photo'),
      openAddMenu: (anchor, choose) => {
        const rect = anchor.getBoundingClientRect();
        openMenu(rect.right + 6, rect.top - 4, [
          { label: 'Text', icon: 'caption', run: () => choose('paragraph') },
          { label: 'Heading', icon: 'heading', run: () => choose('heading') },
          { label: 'Photo…', icon: 'photos', run: () => choose('photo') },
          { label: 'List', icon: 'list', run: () => choose('list') },
          { label: 'Quote', icon: 'quote', run: () => choose('quote') }
        ]);
      }
    });
    visualScroll.prepend(el('div', { class: 'story-head' }, this.storyTitle, this.storySummary));

    for (const [fields, next] of [[this.titleFields, 'summary'], [this.summaryFields, 'story']]) {
      for (const field of fields) {
        field.addEventListener('input', () => {
          const value = field.value.replace(/\s*\n\s*/g, ' ');
          if (value !== field.value) field.value = value;
          for (const other of fields) if (other !== field) other.value = value;
          this.fitHead();
          this.schedule();
        });
        field.addEventListener('keydown', event => {
          if (event.key !== 'Enter') return;
          event.preventDefault();
          const inMarkdown = field === this.mdTitle || field === this.mdSummary;
          if (next === 'summary') (inMarkdown ? this.mdSummary : this.storySummary).focus();
          else if (inMarkdown) this.textarea.focus();
          else this.editor.keepWriting();
        });
      }
    }

    this.coverButton.addEventListener('click', () => this.chooseCover());
    this.dateButton.addEventListener('click', () => {
      try { this.dateInput.showPicker(); } catch { this.dateInput.focus(); }
    });
    this.dateInput.addEventListener('change', () => {
      this.newStep();
      this.drawDate();
      this.schedule();
    });
    this.dateClear.addEventListener('click', () => {
      this.newStep();
      this.dateInput.value = '';
      this.drawDate();
      this.schedule();
    });

    let typing = 0;
    this.textarea.addEventListener('input', () => {
      this.schedule();
      clearTimeout(typing);
      typing = setTimeout(() => this.editor.setMarkdown(this.textarea.value), 180);
      this.markUsed();
    });
    this.textarea.addEventListener('focus', () => { this.lastFocus = 'markdown'; });
    this.textarea.addEventListener('dragover', event => {
      if (event.dataTransfer.types.includes(PHOTO_TYPE)) this.textarea.classList.add('drop-ready');
    });
    this.textarea.addEventListener('dragleave', () => this.textarea.classList.remove('drop-ready'));
    this.textarea.addEventListener('drop', () => this.textarea.classList.remove('drop-ready'));
    visualScroll.addEventListener('focusin', () => { this.lastFocus = 'visual'; });
    // Moving, replacing or removing a block, or adding one, is its own undo step, not part of the typing around it.
    visualScroll.addEventListener('click', event => {
      if (event.target.closest('.sb-tools, .sb-gutter')) this.newStep();
    }, true);
    visualScroll.addEventListener('drop', () => this.newStep(), true);
    this.root.addEventListener('keydown', event => {
      if (this.key(event)) event.stopPropagation();
    });

    this.layoutControl.addEventListener('click', event => {
      const choice = event.target.closest('[data-layout]')?.dataset.layout;
      if (choice) this.setLayout(choice, true);
    });
    let saved = null;
    try { saved = localStorage.getItem('photocurator-writeup-layout'); } catch { /* private mode */ }
    this.setLayout(saved ?? (window.innerWidth >= 1280 ? 'split' : 'write'), false);
    const resized = new ResizeObserver(() => this.fitHead());
    resized.observe(visualScroll);
    resized.observe(this.textarea);
  }

  /** The title and summary grow to fit their text, on the page and in the Markdown pane. */
  fitHead() {
    for (const area of [this.storyTitle, this.storySummary, this.mdTitle, this.mdSummary]) {
      area.style.height = 'auto';
      area.style.height = `${area.scrollHeight}px`;
    }
  }

  drawDate() {
    const value = this.dateInput.value;
    if (value) {
      const [y, m, d] = value.split('-').map(Number);
      this.dateLabel.textContent = new Date(y, m - 1, d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    } else {
      this.dateLabel.textContent = 'Add date';
    }
    this.dateButton.classList.toggle('empty', !value);
    this.dateClear.hidden = !value;
  }

  setLayout(layout, remember) {
    this.panes.dataset.layout = layout;
    for (const option of this.layoutControl.children) option.setAttribute('aria-selected', String(option.dataset.layout === layout));
    if (remember) {
      try { localStorage.setItem('photocurator-writeup-layout', layout); } catch { /* private mode */ }
    }
  }

  // ---------- loading ----------

  /** Shows a folder's write-up; loads it only when the folder changed. */
  async show(folder) {
    if (this.folder === folder && this.data) {
      this.refreshPhotos();
      return;
    }
    await this.flush();
    this.folder = folder;
    this.data = null;
    this.root.classList.add('loading');
    let data;
    try {
      data = await get(`/api/writeup?folder=${encodeURIComponent(folder)}`);
    } catch (e) {
      this.root.classList.remove('loading');
      this.showError(e.message);
      return;
    }
    if (this.folder !== folder) return;
    this.fill(data);
  }

  /** The folder changed on disk (maybe an undo). Reload if nobody is typing here. */
  async reloadIfIdle() {
    if (!this.folder || !this.data || this.dirty || this.timer || this.root.contains(document.activeElement)) return;
    const folder = this.folder;
    let data;
    try { data = await get(`/api/writeup?folder=${encodeURIComponent(folder)}`); } catch { return; }
    if (this.folder !== folder || this.dirty || this.root.contains(document.activeElement)) return;
    const same = ['title', 'summary', 'date', 'cover', 'body'].every(key => (data[key] ?? '') === (this.data[key] ?? ''));
    if (!same) this.fill(data);
  }

  fill(data) {
    this.root.classList.remove('loading', 'failed');
    this.data = { ...data };
    const folderName = this.folderName();
    for (const field of this.titleFields) {
      field.placeholder = folderName || 'Title';
      field.value = data.title ?? '';
    }
    for (const field of this.summaryFields) field.value = data.summary ?? '';
    this.dateInput.value = (data.date ?? '').slice(0, 10);
    this.drawDate();
    requestAnimationFrame(() => this.fitHead());
    this.setTextarea(data.body ?? '');
    this.editor.setMarkdown(data.body ?? '');
    this.refreshPhotos();
    this.drawCover();
    this.saveState.textContent = '';
    this.saveState.classList.remove('error');
    this.past = [];
    this.future = [];
    this.present = this.snapshot();
    this.lastEdit = 0;
    this.app.onHistory?.();
  }

  showError(message) {
    this.root.classList.add('failed');
    this.saveState.textContent = 'Can\'t open';
    this.saveState.classList.add('error');
    toast(message, { error: true });
  }

  setTextarea(markdown) {
    const ta = this.textarea;
    if (ta.value !== markdown) {
      const top = ta.scrollTop;
      const [start, end] = [ta.selectionStart, ta.selectionEnd];
      ta.value = markdown;
      if (document.activeElement === ta) ta.setSelectionRange(Math.min(start, markdown.length), Math.min(end, markdown.length));
      ta.scrollTop = top;
    }
    this.markUsed();
  }

  folderName() {
    if (this.folder === null) return '';
    return this.folder ? this.folder.split('/').pop() : this.app.library()?.root.name ?? '';
  }

  // ---------- photos ----------

  /** Photos in this folder and the folders inside it, as paths relative to this folder. */
  photos() {
    const library = this.app.library();
    if (!library || this.folder === null) return [];
    const prefix = this.folder ? this.folder + '/' : '';
    return library.photos
      .filter(p => !this.folder || p.folder === this.folder || p.folder.startsWith(prefix))
      .map(p => ({ ...p, path: p.id.slice(prefix.length) }));
  }

  photoFor(path) {
    const library = this.app.library();
    if (!library) return null;
    const id = resolve(this.folder ?? '', path);
    const photo = id && library.photos.find(p => p.id === id);
    return photo ? { url: imageUrl(photo), thumb: thumbUrl(photo), caption: photo.caption, missing: false } : { missing: true };
  }

  refreshPhotos() {
    const photos = this.photos();
    // Only rebuild when the folder's photos changed: saves happen often and shouldn't disturb a drag.
    const key = `${this.folder}|${photos.map(p => `${p.id}|${p.v}|${p.caption}`).join('/')}`;
    if (key === this.stripKey) {
      this.markUsed();
      return;
    }
    this.stripKey = key;
    if (!photos.length) {
      this.strip.replaceChildren(el('span', { class: 'wu-strip-empty', text: 'No photos in this folder yet. Move some here from the Photos tab.' }));
      return;
    }
    this.strip.replaceChildren(...photos.map(photo => {
      // A div, not a <button>: buttons don't drag reliably in every browser engine.
      const thumb = el('div', {
        class: 'wu-thumb', role: 'button', tabindex: '0', draggable: 'true', 'data-path': photo.path,
        title: `${photo.caption || photo.name}\nDrag into the story, or click to add it where you are typing`
      }, el('img', { alt: '', loading: 'lazy', draggable: 'false', src: thumbUrl(photo) }), el('span', { class: 'wu-used', title: 'In the story' }, icon('check')));
      thumb.addEventListener('dragstart', event => {
        event.dataTransfer.setData(PHOTO_TYPE, photo.path);
        // The Markdown pane takes the same photo as text, dropped where the mouse is.
        event.dataTransfer.setData('text/plain', `\n\n${photoMarkdown(photo.caption ?? '', photo.path)}\n\n`);
        event.dataTransfer.effectAllowed = 'copy';
      });
      thumb.addEventListener('click', () => this.insertPhoto(photo.path));
      thumb.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          this.insertPhoto(photo.path);
        }
      });
      return thumb;
    }));
    this.markUsed();
    if (!this.editor.hasFocus) this.editor.render();
  }

  /** Clicking a photo in the strip: into the Markdown at the caret, or into the story after the block being edited. */
  insertPhoto(path) {
    this.newStep();
    if (this.lastFocus === 'markdown' && this.panes.dataset.layout !== 'write') {
      const ta = this.textarea;
      const at = ta.selectionEnd ?? ta.value.length;
      const before = ta.value.slice(0, at).replace(/\n*$/, '');
      const after = ta.value.slice(at).replace(/^\n*/, '');
      const snippet = photoMarkdown(this.photoFor(path)?.caption ?? '', path);
      ta.value = [before, snippet, after].filter(Boolean).join('\n\n');
      const caret = (before ? before.length + 2 : 0) + snippet.length;
      ta.focus();
      ta.setSelectionRange(caret, caret);
      ta.dispatchEvent(new Event('input'));
      return;
    }
    this.editor.insertPhoto(path);
  }

  markUsed() {
    const used = new Set(parseBlocks(this.textarea.value).filter(b => b.type === 'photo').map(b => normalize(b.path)));
    for (const thumb of this.strip.querySelectorAll('.wu-thumb')) thumb.classList.toggle('used', used.has(normalize(thumb.dataset.path)));
  }

  pickPhoto(anchor, title) {
    return new Promise(resolve => {
      let chosen = null;
      const close = openPopover(anchor, (box, closeIt) => {
        const photos = this.photos();
        box.append(el('h3', { text: title }));
        if (!photos.length) box.append(el('p', { class: 'hint', text: 'This folder has no photos yet.' }));
        box.append(el('div', { class: 'photo-picker' }, ...photos.map(photo => el('button', {
          type: 'button', class: 'photo-pick', title: photo.caption || photo.name,
          onClick: () => { chosen = photo.path; closeIt(); }
        }, el('img', { alt: '', loading: 'lazy', src: thumbUrl(photo) })))));
      }, { width: 420 });
      // Resolve when the popover goes away, whichever way.
      const box = document.getElementById('popover');
      const watch = new MutationObserver(() => {
        if (box.hidden) {
          watch.disconnect();
          resolve(chosen);
        }
      });
      watch.observe(box, { attributes: true, attributeFilter: ['hidden'] });
      void close;
    });
  }

  drawCover() {
    const cover = this.data?.cover;
    const photo = cover ? this.photoFor(cover) : null;
    const first = parseBlocks(this.textarea.value).find(b => b.type === 'photo');
    const shown = photo && !photo.missing ? photo : first ? this.photoFor(first.path) : null;
    this.coverButton.replaceChildren(
      shown && !shown.missing ? el('img', { alt: '', src: shown.thumb }) : icon('photos'),
      el('span', { text: 'Cover' }),
      ...(cover ? [] : [el('span', { class: 'wu-chip-note', text: 'Auto' })]));
    this.coverButton.title = cover
      ? `Cover: ${cover}. Click to change.`
      : 'The story\'s first photo is the cover. Click to choose one.';
  }

  async chooseCover() {
    const path = await new Promise(resolve => {
      let chosen;
      openPopover(this.coverButton, (box, close) => {
        const photos = this.photos();
        box.append(
          el('h3', { text: 'Cover photo' }),
          el('button', {
            type: 'button', class: 'button subtle wu-auto' + (this.data?.cover ? '' : ' selected'),
            onClick: () => { chosen = ''; close(); }
          }, icon('sparkle'), 'Automatic: the story\'s first photo'),
          el('div', { class: 'photo-picker' }, ...photos.map(photo => el('button', {
            type: 'button', class: 'photo-pick' + (photo.path === this.data?.cover ? ' selected' : ''), title: photo.caption || photo.name,
            onClick: () => { chosen = photo.path; close(); }
          }, el('img', { alt: '', loading: 'lazy', src: thumbUrl(photo) })))));
      }, { width: 420 });
      const box = document.getElementById('popover');
      const watch = new MutationObserver(() => {
        if (box.hidden) {
          watch.disconnect();
          resolve(chosen);
        }
      });
      watch.observe(box, { attributes: true, attributeFilter: ['hidden'] });
    });
    if (path === undefined || !this.data) return;
    this.newStep();
    this.data.cover = path || null;
    this.drawCover();
    this.schedule();
  }

  // ---------- undo ----------
  // The write-up keeps its own history, so Ctrl+Z steps back through edits made here, on either side: a burst of
  // typing is one step, and adding, moving or removing a photo is one step. Every step is saved like any other edit.

  snapshot() {
    return {
      title: this.storyTitle.value, summary: this.storySummary.value, date: this.dateInput.value,
      cover: this.data?.cover ?? null, body: this.textarea.value
    };
  }

  /** The next edit starts a new undo step. */
  newStep() {
    this.lastEdit = 0;
  }

  record() {
    if (!this.present) return;
    const now = this.snapshot();
    if (sameSnapshot(now, this.present)) return;
    if (Date.now() - this.lastEdit > TYPING_BURST) {
      this.past.push(this.present);
      if (this.past.length > 300) this.past.shift();
    }
    this.future = [];
    this.present = now;
    this.lastEdit = Date.now();
    this.app.onHistory?.();
  }

  get canUndo() {
    return this.past.length > 0;
  }

  get canRedo() {
    return this.future.length > 0;
  }

  undo() {
    if (!this.canUndo) return false;
    this.future.push(this.snapshot());
    this.restore(this.past.pop());
    return true;
  }

  redo() {
    if (!this.canRedo) return false;
    this.past.push(this.snapshot());
    this.restore(this.future.pop());
    return true;
  }

  restore(state) {
    for (const field of this.titleFields) field.value = state.title;
    for (const field of this.summaryFields) field.value = state.summary;
    this.dateInput.value = state.date;
    this.drawDate();
    this.data.cover = state.cover;
    this.setTextarea(state.body);
    this.editor.setMarkdown(state.body);
    this.fitHead();
    this.markUsed();
    this.drawCover();
    this.present = state;
    this.lastEdit = 0;
    this.dirty = true;
    this.saveState.textContent = 'Editing…';
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = 0;
      this.save();
    }, SAVE_DELAY);
    this.app.onHistory?.();
  }

  /** Ctrl+Z, Ctrl+Shift+Z and Ctrl+Y inside the write-up. True when handled. */
  key(event) {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return false;
    const key = event.key.toLowerCase();
    if (key !== 'z' && key !== 'y') return false;
    event.preventDefault();
    if (key === 'y' || event.shiftKey) this.redo();
    else this.undo();
    return true;
  }

  // ---------- saving ----------

  schedule() {
    if (!this.data) return;
    this.record();
    this.dirty = true;
    this.saveState.textContent = 'Editing…';
    this.saveState.classList.remove('error');
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = 0;
      this.save();
    }, SAVE_DELAY);
    this.drawCoverSoon();
  }

  drawCoverSoon() {
    clearTimeout(this.coverTimer);
    this.coverTimer = setTimeout(() => this.drawCover(), 300);
  }

  async flush() {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = 0;
      this.save();
    }
    await this.chain;
  }

  save() {
    if (!this.data || this.folder === null) return this.chain;
    // Captured now: by the time the request runs, the view may show another folder.
    const body = {
      folder: this.folder,
      title: this.storyTitle.value.trim(),
      summary: this.storySummary.value.trim(),
      date: this.dateInput.value || '',
      cover: this.data.cover ?? '',
      body: this.textarea.value
    };
    const data = this.data;
    this.saveState.textContent = 'Saving…';
    this.chain = this.chain.then(async () => {
      try {
        const result = await put('/api/writeup', body);
        Object.assign(data, { title: body.title, summary: body.summary, date: body.date || null, cover: body.cover || null, body: body.body, exists: true });
        if (data === this.data && !this.timer) {
          this.dirty = false;
          this.saveState.textContent = 'Saved';
        }
        this.app.onSaved(result);
      } catch (e) {
        this.saveState.textContent = 'Not saved';
        this.saveState.classList.add('error');
        toast(e.message, { error: true });
      }
    });
    return this.chain;
  }
}

function sameSnapshot(a, b) {
  return a.title === b.title && a.summary === b.summary && a.date === b.date && a.cover === b.cover && a.body === b.body;
}

function normalize(path) {
  return String(path ?? '').replace(/^\.\//, '');
}

/** A path from the story (relative to the folder) → the photo's id. */
function resolve(folder, path) {
  const parts = folder ? folder.split('/') : [];
  for (const segment of normalize(path).split('/')) {
    if (!segment || segment === '.') continue;
    if (segment === '..') {
      if (!parts.length) return null;
      parts.pop();
    } else {
      parts.push(segment);
    }
  }
  return parts.join('/');
}
