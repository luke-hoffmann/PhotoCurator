// The photo grid. Virtualized: only the rows on screen (plus a margin) exist in the DOM, so 10,000+ photos scroll
// smoothly. Square tiles fill the width; the slider sets their target size.
//
// Mouse: click opens a photo; the round check (or Ctrl-click) selects; once anything is selected, clicks select too
// (like Google Photos). Shift-click selects a range. Keyboard: arrows move, Space selects, Shift+arrows extend,
// Enter opens, Ctrl+A selects all, Esc clears.

import { icon } from './icons.js';
import { thumbUrl } from './api.js';

const GAP = 14;
const CAPTION = 26;
const OVERSCAN_ROWS = 3;

export class Grid {
  constructor(scroller, { onOpen, onSelectionChange, onContextMenu }) {
    this.scroller = scroller;
    this.onOpen = onOpen;
    this.onSelectionChange = onSelectionChange;
    this.onContextMenu = onContextMenu;

    this.inner = document.createElement('div');
    this.inner.className = 'grid-inner';
    scroller.append(this.inner);

    this.items = [];
    this.selection = new Set();
    this.anchor = -1;
    this.focusIndex = -1;
    this.target = 200;
    this.tile = 200;
    this.cols = 1;
    this.rowHeight = this.tile + CAPTION + GAP;
    this.live = new Map();
    this.spare = [];
    this.frame = 0;

    scroller.addEventListener('scroll', () => this.schedule(), { passive: true });
    new ResizeObserver(() => this.layout()).observe(scroller);
    this.inner.addEventListener('click', event => this.click(event));
    this.inner.addEventListener('dblclick', event => {
      const index = this.indexOf(event.target);
      if (index >= 0 && !event.target.closest('.check')) this.onOpen(index);
    });
    this.inner.addEventListener('contextmenu', event => {
      const index = this.indexOf(event.target);
      if (index < 0) return;
      event.preventDefault();
      if (!this.selection.has(this.items[index].id)) this.selectOnly(index);
      this.onContextMenu(index, event);
    });
    this.inner.addEventListener('dragstart', event => this.dragStart(event));
  }

  // ---------- data ----------

  setItems(items) {
    this.items = items;
    const present = new Set(items.map(p => p.id));
    let changed = false;
    for (const id of this.selection) {
      if (!present.has(id)) {
        this.selection.delete(id);
        changed = true;
      }
    }
    if (this.focusIndex >= items.length) this.focusIndex = items.length - 1;
    if (this.anchor >= items.length) this.anchor = -1;
    this.layout();
    for (const [index, el] of this.live) this.bind(el, index, true);
    if (changed) this.selectionChanged();
  }

  setTileSize(px) {
    this.target = px;
    this.layout();
  }

  /** Keeps the selection pointing at photos that were just moved or renamed. */
  remapSelection(moved) {
    if (!moved) return;
    for (const [from, to] of Object.entries(moved)) {
      if (this.selection.delete(from)) this.selection.add(to);
    }
  }

  get selectedIds() {
    return this.items.filter(p => this.selection.has(p.id)).map(p => p.id);
  }

  // ---------- layout + rendering ----------

  layout() {
    const width = Math.max(0, this.scroller.clientWidth - 44);
    this.cols = Math.max(1, Math.round((width + GAP) / (this.target + GAP)));
    this.tile = Math.max(60, Math.floor((width - (this.cols - 1) * GAP) / this.cols));
    this.rowHeight = this.tile + CAPTION + GAP;
    this.scroller.style.setProperty('--tile', `${this.tile}px`);
    this.inner.style.height = `${Math.ceil(this.items.length / this.cols) * this.rowHeight}px`;
    for (const [index, el] of this.live) this.position(el, index);
    this.render();
  }

  schedule() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.render();
    });
  }

  render() {
    const top = this.scroller.scrollTop;
    const first = Math.max(0, Math.floor(top / this.rowHeight) - OVERSCAN_ROWS) * this.cols;
    const last = Math.min(this.items.length, (Math.ceil((top + this.scroller.clientHeight) / this.rowHeight) + OVERSCAN_ROWS) * this.cols);

    for (const [index, el] of this.live) {
      if (index < first || index >= last) {
        this.live.delete(index);
        el.hidden = true;
        this.spare.push(el);
      }
    }
    for (let index = first; index < last; index++) {
      if (this.live.has(index)) continue;
      const el = this.spare.pop() ?? this.createTile();
      el.hidden = false;
      this.live.set(index, el);
      this.position(el, index);
      this.bind(el, index, false);
    }
    this.scroller.classList.toggle('selecting', this.selection.size > 0);
  }

  createTile() {
    const el = document.createElement('div');
    el.className = 'tile';
    el.draggable = true;
    el.innerHTML = '<div class="thumb"><img alt="" decoding="async" draggable="false"><div class="badges"></div></div><div class="cap"></div>';
    const check = document.createElement('button');
    check.type = 'button';
    check.className = 'check';
    check.tabIndex = -1;
    check.setAttribute('aria-label', 'Select');
    check.append(icon('check'));
    el.querySelector('.thumb').append(check);
    const img = el.querySelector('img');
    img.addEventListener('load', () => img.classList.add('loaded'));
    img.addEventListener('error', () => {
      if (!img.getAttribute('src')) return;
      const note = document.createElement('div');
      note.className = 'unreadable';
      note.textContent = "Can't preview";
      img.after(note);
    });
    this.inner.append(el);
    return el;
  }

  position(el, index) {
    const row = Math.floor(index / this.cols);
    const col = index % this.cols;
    el.style.transform = `translate(${col * (this.tile + GAP)}px, ${row * this.rowHeight}px)`;
    el.dataset.index = index;
  }

  bind(el, index, force) {
    const photo = this.items[index];
    if (!photo) return;
    const key = `${photo.id}|${photo.v}|${photo.caption}|${photo.tags.length}|${photo.error ?? ''}`;
    if (force || el.dataset.key !== key) {
      const img = el.querySelector('img');
      const src = thumbUrl(photo);
      if (img.getAttribute('src') !== src) {
        img.classList.remove('loaded');
        el.querySelector('.unreadable')?.remove();
        img.src = src;
      }
      const cap = el.querySelector('.cap');
      cap.textContent = photo.caption || photo.name;
      cap.classList.toggle('empty', !photo.caption);
      el.title = photo.caption ? `${photo.caption}\n${photo.id}` : photo.id;

      const badges = el.querySelector('.badges');
      badges.replaceChildren();
      if (photo.error) {
        const warn = document.createElement('span');
        warn.className = 'badge warn';
        warn.append(icon('warning'));
        warn.title = `Its .md file couldn't be read: ${photo.error}`;
        badges.append(warn);
      }
      if (photo.tags.length) {
        const tags = document.createElement('span');
        tags.className = 'badge';
        tags.append(icon('tag'), document.createTextNode(photo.tags.length));
        tags.title = photo.tags.join(', ');
        badges.append(tags);
      }
      el.dataset.key = key;
    }
    el.classList.toggle('selected', this.selection.has(photo.id));
    el.classList.toggle('focused', index === this.focusIndex && document.activeElement === this.scroller);
  }

  refreshStates() {
    for (const [index, el] of this.live) this.bind(el, index, false);
    this.scroller.classList.toggle('selecting', this.selection.size > 0);
  }

  scrollToIndex(index) {
    if (index < 0) return;
    const row = Math.floor(index / this.cols);
    const top = row * this.rowHeight;
    const bottom = top + this.rowHeight;
    if (top < this.scroller.scrollTop) this.scroller.scrollTop = top;
    else if (bottom > this.scroller.scrollTop + this.scroller.clientHeight) this.scroller.scrollTop = bottom - this.scroller.clientHeight;
  }

  indexOf(target) {
    const tile = target.closest?.('.tile');
    return tile ? Number(tile.dataset.index) : -1;
  }

  // ---------- selection ----------

  click(event) {
    const index = this.indexOf(event.target);
    if (index < 0) return;
    this.scroller.focus({ preventScroll: true });
    const onCheck = !!event.target.closest('.check');
    if (event.shiftKey && this.anchor >= 0) {
      this.selectRange(this.anchor, index, true);
    } else if (onCheck || event.ctrlKey || event.metaKey || this.selection.size > 0) {
      this.toggle(index);
    } else {
      this.focusIndex = index;
      this.refreshStates();
      this.onOpen(index);
      return;
    }
    this.focusIndex = index;
    this.refreshStates();
  }

  toggle(index) {
    const id = this.items[index].id;
    if (this.selection.has(id)) this.selection.delete(id);
    else this.selection.add(id);
    this.anchor = index;
    this.selectionChanged();
  }

  selectOnly(index) {
    this.selection = new Set([this.items[index].id]);
    this.anchor = index;
    this.focusIndex = index;
    this.selectionChanged();
  }

  selectRange(from, to, add) {
    if (!add) this.selection.clear();
    const [a, b] = from < to ? [from, to] : [to, from];
    for (let i = a; i <= b; i++) this.selection.add(this.items[i].id);
    this.selectionChanged();
  }

  selectAll() {
    this.selection = new Set(this.items.map(p => p.id));
    this.selectionChanged();
  }

  clearSelection() {
    if (!this.selection.size) return;
    this.selection.clear();
    this.anchor = -1;
    this.selectionChanged();
  }

  selectionChanged() {
    this.refreshStates();
    this.onSelectionChange(this.selection.size);
  }

  /** Keyboard handling while the grid has focus. Returns true when the key was used. */
  key(event) {
    const n = this.items.length;
    if (!n) return false;
    const pageRows = Math.max(1, Math.floor(this.scroller.clientHeight / this.rowHeight));
    const moves = {
      ArrowRight: 1, ArrowLeft: -1, ArrowDown: this.cols, ArrowUp: -this.cols,
      PageDown: this.cols * pageRows, PageUp: -this.cols * pageRows, Home: -n, End: n
    };
    if (event.key in moves && !event.altKey) {
      const start = this.focusIndex < 0 ? 0 : this.focusIndex;
      const next = this.focusIndex < 0 ? 0 : Math.min(n - 1, Math.max(0, start + moves[event.key]));
      if (event.shiftKey) {
        if (this.anchor < 0) this.anchor = start;
        this.focusIndex = next;
        this.selectRange(this.anchor, next, false);
      } else {
        this.focusIndex = next;
        this.refreshStates();
      }
      this.scrollToIndex(next);
      return true;
    }
    if (event.key === ' ' && this.focusIndex >= 0) {
      this.toggle(this.focusIndex);
      return true;
    }
    if (event.key === 'Enter' && this.focusIndex >= 0) {
      this.onOpen(this.focusIndex);
      return true;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
      this.selectAll();
      return true;
    }
    return false;
  }

  // ---------- drag to a folder ----------

  dragStart(event) {
    const index = this.indexOf(event.target);
    if (index < 0) return;
    const id = this.items[index].id;
    const ids = this.selection.has(id) ? this.selectedIds : [id];
    event.dataTransfer.setData('application/x-photocurator-ids', JSON.stringify(ids));
    event.dataTransfer.effectAllowed = 'move';

    const label = document.createElement('div');
    label.textContent = ids.length === 1 ? '1 photo' : `${ids.length} photos`;
    Object.assign(label.style, {
      position: 'fixed', top: '-100px', left: '0', padding: '6px 12px', borderRadius: '14px',
      background: '#1e6a3d', color: '#fff', font: '600 13px Segoe UI, system-ui, sans-serif'
    });
    document.body.append(label);
    event.dataTransfer.setDragImage(label, 10, 10);
    setTimeout(() => label.remove(), 0);
  }
}
