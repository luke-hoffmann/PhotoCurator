// One photo, big, with its caption, date, tags and folder. Every edit saves by itself a moment after you stop typing.
// The list of photos is frozen when the view opens, so working through "No caption" doesn't skip ahead as photos
// stop matching.

import { get, post, put, imageUrl, thumbUrl } from './api.js';
import { TagInput } from './tags.js';
import { toast } from './dialogs.js';

const $ = id => document.getElementById(id);
const DEBOUNCE_MS = 400;

const sourceLabels = {
  sidecar: '',
  camera: '· from the camera',
  takeout: '· from Google Takeout',
  file: '· from the file date'
};

export class Detail {
  /**
   * @param {object} app  photo(id), knownTags(), moveTo(ids, anchor) → moved map, onClose(lastId), canReveal()
   */
  constructor(app) {
    this.app = app;
    this.ids = [];
    this.index = 0;
    this.data = null;
    this.pending = new Map();
    this.chain = Promise.resolve();
    this.loadToken = 0;

    this.root = $('detail');
    this.image = $('detailImage');
    this.caption = $('fieldCaption');
    this.date = $('fieldDate');
    this.alt = $('fieldAlt');
    this.description = $('fieldDescription');
    this.folder = $('fieldFolder');
    this.saveState = $('detailSaveState');

    this.tags = new TagInput({
      known: () => app.knownTags(),
      onAdd: tag => this.setTags([...this.data.tags, tag]),
      onRemove: tag => this.setTags(this.data.tags.filter(t => t !== tag))
    });
    $('fieldTags').append(this.tags.element);

    this.caption.addEventListener('input', () => this.schedule('caption', this.caption.value.replace(/\s*\n\s*/g, ' ')));
    this.caption.addEventListener('keydown', event => {
      if (event.key === 'Enter' && !event.ctrlKey && !event.metaKey) {
        event.preventDefault();
        this.tags.focus();
      }
    });
    this.alt.addEventListener('input', () => this.schedule('alt', this.alt.value));
    this.description.addEventListener('input', () => this.schedule('description', this.description.value));
    this.date.addEventListener('change', async () => {
      if (!this.date.value) return;
      await this.flush();
      this.save('date', this.date.value);
    });
    this.folder.addEventListener('click', () => this.chooseFolder());
    $('detailForm').addEventListener('submit', event => event.preventDefault());
    $('detailClose').addEventListener('click', () => this.close());
    $('detailPrev').addEventListener('click', () => this.go(-1));
    $('detailNext').addEventListener('click', () => this.go(1));
    $('detailReveal').addEventListener('click', () => post('/api/reveal', { id: this.currentId }).catch(e => toast(e.message, { error: true })));
    this.image.addEventListener('dblclick', () => this.close());
  }

  get isOpen() {
    return !this.root.hidden;
  }

  get currentId() {
    return this.ids[this.index];
  }

  open(ids, index) {
    this.ids = [...ids];
    this.index = Math.max(0, Math.min(index, ids.length - 1));
    this.root.hidden = false;
    $('detailReveal').hidden = !this.app.canReveal();
    this.load();
    this.root.focus?.();
  }

  async close() {
    await this.flush();
    const last = this.currentId;
    this.root.hidden = true;
    this.image.removeAttribute('src');
    this.data = null;
    this.app.onClose(last);
  }

  async go(step) {
    const next = this.index + step;
    if (next < 0 || next >= this.ids.length) return;
    await this.flush();
    this.index = next;
    await this.load();
  }

  /** Photos were moved (by this view or elsewhere): follow them to their new ids. */
  remap(moved) {
    if (!moved) return;
    this.ids = this.ids.map(id => moved[id] ?? id);
    if (this.data && moved[this.data.id]) this.data.id = moved[this.data.id];
  }

  /** The folder changed on disk. Refresh what's shown unless the person is typing. */
  refresh() {
    if (!this.isOpen) return;
    const photo = this.app.photo(this.currentId);
    if (!photo) return;
    const editing = this.root.contains(document.activeElement) && document.activeElement.matches('input, textarea');
    if (this.pending.size === 0 && !editing) this.load({ keepImage: photo.v === this.data?.v });
    this.position();
  }

  async load({ keepImage = false } = {}) {
    const id = this.currentId;
    const photo = this.app.photo(id);
    const token = ++this.loadToken;
    this.position();
    $('detailName').textContent = photo?.name ?? id;
    $('detailMeta').textContent = '';
    if (!keepImage && photo) this.showImage(photo, token);

    let data;
    try {
      data = await get(`/api/photo?id=${encodeURIComponent(id)}`);
    } catch (e) {
      if (token === this.loadToken) this.showError(e.message);
      return;
    }
    if (token !== this.loadToken) return;
    this.data = data;
    this.fill(data);
  }

  showImage(photo, token) {
    $('detailUnavailable').hidden = true;
    this.image.hidden = false;
    // The grid's thumbnail first (already cached), then the full picture when it arrives.
    this.image.src = thumbUrl(photo);
    const full = new Image();
    full.onload = () => {
      if (token === this.loadToken) this.image.src = full.src;
    };
    full.onerror = () => {
      if (token !== this.loadToken) return;
      this.image.hidden = true;
      $('detailUnavailable').hidden = false;
    };
    full.src = imageUrl(photo);

    const next = this.app.photo(this.ids[this.index + 1]);
    if (next) new Image().src = imageUrl(next);
  }

  fill(data) {
    const error = $('detailError');
    const form = $('detailForm');
    if (data.error) {
      error.textContent = `This photo's .md file couldn't be read, so it can't be edited here until it's fixed or deleted: ${data.error}`;
      error.hidden = false;
      form.inert = true;
    } else {
      error.hidden = true;
      form.inert = false;
    }

    const parts = [];
    if (data.width && data.height) parts.push(`${data.width} × ${data.height}`);
    if (data.bytes) parts.push(formatSize(data.bytes));
    $('detailMeta').textContent = parts.join(' · ');

    this.caption.value = data.caption ?? '';
    this.date.value = toInputDate(data.date);
    $('dateSource').textContent = sourceLabels[data.dateSource] ?? '';
    this.tags.setTags(data.tags ?? []);
    this.tags.clearText();
    this.alt.value = data.alt ?? '';
    this.description.value = data.description ?? '';
    $('fieldFolderName').textContent = data.folder || this.app.rootName();
    if ((data.alt || data.description) && !$('moreFields').open) $('moreFields').open = true;
    this.saveState.textContent = data.sidecar ? '' : 'Nothing saved yet';
    this.saveState.classList.remove('error');
  }

  showError(message) {
    const error = $('detailError');
    error.textContent = message;
    error.hidden = false;
    $('detailForm').inert = true;
  }

  position() {
    $('detailPosition').textContent = `${this.index + 1} of ${this.ids.length}`;
    $('detailPrev').disabled = this.index <= 0;
    $('detailNext').disabled = this.index >= this.ids.length - 1;
  }

  // ---------- saving ----------

  schedule(field, value) {
    const existing = this.pending.get(field);
    if (existing) clearTimeout(existing.timer);
    const timer = setTimeout(() => {
      this.pending.delete(field);
      this.save(field, value);
    }, DEBOUNCE_MS);
    this.pending.set(field, { timer, value });
    this.saveState.textContent = 'Editing…';
    this.saveState.classList.remove('error');
  }

  async flush() {
    for (const [field, { timer, value }] of this.pending) {
      clearTimeout(timer);
      this.save(field, value);
    }
    this.pending.clear();
    await this.chain;
  }

  save(field, value) {
    // Captured now: by the time the request runs, the view may show the next photo.
    const data = this.data;
    const body = { id: data?.id ?? this.currentId, field, [field]: value };
    this.saveState.textContent = 'Saving…';
    this.chain = this.chain.then(async () => {
      try {
        const result = await put('/api/photo', body);
        this.app.onSaved(result);
        if (data) {
          data[field] = value;
          data.sidecar = true;
        }
        if (this.pending.size === 0) this.saveState.textContent = 'Saved';
      } catch (e) {
        this.saveState.textContent = 'Not saved';
        this.saveState.classList.add('error');
        toast(e.message, { error: true });
      }
    });
    return this.chain;
  }

  async setTags(tags) {
    if (!this.data) return;
    this.data.tags = tags;
    this.tags.setTags(tags);
    // Save typed text first, so edits land (and undo) in the order they were made.
    await this.flush();
    await this.save('tags', tags);
  }

  async chooseFolder() {
    await this.flush();
    const moved = await this.app.moveTo([this.currentId], this.folder);
    if (!moved) return;
    this.remap(moved);
    await this.load({ keepImage: true });
  }

  // ---------- keys ----------

  key(event) {
    const typing = event.target.matches?.('input, textarea, select');
    const ctrl = event.ctrlKey || event.metaKey;

    if (ctrl && event.key === 'Enter') {
      this.go(1).then(() => this.focusCaption());
      return true;
    }
    if ((event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) || event.key === 'PageUp' || event.key === 'PageDown') {
      const step = event.key === 'ArrowLeft' || event.key === 'PageUp' ? -1 : 1;
      const refocus = typing ? event.target : null;
      this.go(step).then(() => refocus?.focus());
      return true;
    }
    if (typing) {
      if (event.key === 'Escape') {
        event.target.blur();
        return true;
      }
      return false;
    }
    if (event.key === 'ArrowLeft') this.go(-1);
    else if (event.key === 'ArrowRight') this.go(1);
    else if (event.key === 'Escape') this.close();
    else if (event.key === 'Enter' || event.key.toLowerCase() === 'c') this.focusCaption();
    else if (event.key.toLowerCase() === 't' && !ctrl) this.tags.focus();
    else return false;
    return true;
  }

  focusCaption() {
    this.caption.focus();
    const end = this.caption.value.length;
    this.caption.setSelectionRange(end, end);
  }
}

function toInputDate(value) {
  if (!value) return '';
  return value.length === 10 ? `${value}T00:00:00` : value.slice(0, 19);
}

function formatSize(bytes) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
