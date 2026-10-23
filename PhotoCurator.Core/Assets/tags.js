// Tag field with inline creation: type, then Enter (or comma) adds the highlighted suggestion or a brand-new tag.

import { icon } from './icons.js';

export function normalizeTag(text) {
  return text.trim().replace(/\s+/g, ' ').toLowerCase().slice(0, 60);
}

export class TagInput {
  /**
   * @param {object} options
   * @param {() => {name: string, count: number}[]} options.known  every tag in the library, most used first
   * @param {(tag: string) => Promise<void>|void} options.onAdd
   * @param {(tag: string) => Promise<void>|void} options.onRemove
   * @param {string} [options.placeholder]
   */
  constructor({ known, onAdd, onRemove, placeholder = 'Add a tag…' }) {
    this.known = known;
    this.onAdd = onAdd;
    this.onRemove = onRemove;
    this.tags = [];
    this.active = -1;
    this.options = [];

    this.element = document.createElement('div');
    this.element.className = 'tag-input';
    this.chips = document.createElement('div');
    this.chips.style.display = 'contents';
    this.input = document.createElement('input');
    this.input.type = 'text';
    this.input.placeholder = placeholder;
    this.input.spellcheck = false;
    this.input.setAttribute('aria-label', 'Add a tag');
    this.list = document.createElement('div');
    this.list.className = 'suggestions';
    this.list.hidden = true;
    this.list.setAttribute('role', 'listbox');
    this.element.append(this.chips, this.input, this.list);

    this.element.addEventListener('mousedown', event => {
      if (event.target === this.element) {
        event.preventDefault();
        this.input.focus();
      }
    });
    this.input.addEventListener('input', () => this.suggest());
    this.input.addEventListener('focus', () => this.suggest());
    this.input.addEventListener('blur', () => setTimeout(() => { this.list.hidden = true; }, 120));
    this.input.addEventListener('keydown', event => this.keydown(event));
  }

  setTags(tags) {
    this.tags = [...tags];
    this.chips.replaceChildren(...this.tags.map(tag => this.chip(tag)));
  }

  focus() {
    this.input.focus();
  }

  chip(tag) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.textContent = tag;
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.title = `Remove "${tag}"`;
    remove.append(icon('close'));
    remove.addEventListener('click', async event => {
      event.stopPropagation();
      await this.onRemove(tag);
      this.input.focus();
    });
    chip.append(remove);
    return chip;
  }

  suggest() {
    const typed = normalizeTag(this.input.value);
    const known = this.known().filter(t => !this.tags.includes(t.name));
    let matches = typed
      ? known.filter(t => t.name.includes(typed)).sort((a, b) => Number(b.name.startsWith(typed)) - Number(a.name.startsWith(typed)))
      : known;
    matches = matches.slice(0, 8);
    this.options = matches.map(t => ({ name: t.name, count: t.count }));
    if (typed && !known.some(t => t.name === typed) && !this.tags.includes(typed)) {
      this.options.push({ name: typed, create: true });
    }
    this.active = typed && this.options.length ? 0 : -1;
    this.renderList();
  }

  renderList() {
    if (!this.options.length || document.activeElement !== this.input) {
      this.list.hidden = true;
      return;
    }
    this.list.replaceChildren(...this.options.map((option, index) => {
      const row = document.createElement('div');
      row.className = 'suggestion' + (index === this.active ? ' active' : '') + (option.create ? ' create' : '');
      row.setAttribute('role', 'option');
      const label = document.createElement('span');
      label.textContent = option.create ? `Create tag "${option.name}"` : option.name;
      row.append(label);
      if (!option.create) {
        const count = document.createElement('span');
        count.className = 'count';
        count.textContent = option.count;
        row.append(count);
      }
      row.addEventListener('mousedown', event => {
        event.preventDefault();
        this.commit(option.name);
      });
      return row;
    }));
    this.list.hidden = false;
  }

  async commit(name) {
    const tag = normalizeTag(name);
    this.input.value = '';
    this.list.hidden = true;
    if (!tag || this.tags.includes(tag)) return;
    await this.onAdd(tag);
    this.suggest();
  }

  keydown(event) {
    const typed = this.input.value.trim();
    if ((event.key === 'Enter' || event.key === ',' || (event.key === 'Tab' && typed)) && !event.ctrlKey && !event.metaKey) {
      if (!typed && this.active < 0) return;
      event.preventDefault();
      event.stopPropagation();
      this.commit(this.active >= 0 ? this.options[this.active].name : typed);
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (!this.options.length) return;
      event.preventDefault();
      event.stopPropagation();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      this.active = (this.active + step + this.options.length) % this.options.length;
      this.renderList();
    } else if (event.key === 'Backspace' && !this.input.value && this.tags.length) {
      event.preventDefault();
      this.onRemove(this.tags[this.tags.length - 1]);
    } else if (event.key === 'Escape') {
      event.stopPropagation();
      if (!this.list.hidden) this.list.hidden = true;
      else this.input.blur();
    }
  }
}
