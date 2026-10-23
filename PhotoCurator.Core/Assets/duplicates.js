// The Duplicates view: each group of copies side by side. Pick the one to keep; the others' tags and caption move onto
// it and they go to "Duplicates set aside" (nothing is deleted; Ctrl+Z undoes).

import { get, post, thumbUrl } from './api.js';
import { el, toast } from './dialogs.js';
import { icon } from './icons.js';

const kinds = {
  file: { title: 'Same file', note: 'Byte-for-byte copies.' },
  moment: { title: 'Same moment', note: 'Different files of the same shot: an edited or recompressed copy, or a burst taken in the same second.' }
};

export class DuplicatesView {
  /**
   * @param {HTMLElement} root
   * @param {object} app  onChange(result) after keeping/dismissing; onOpen(ids, index) to open a photo big
   */
  constructor(root, app) {
    this.root = root;
    this.app = app;
    this.data = { groups: [], checking: 0 };
    this.loading = null;
  }

  get groupCount() {
    return this.data.groups.length;
  }

  async load() {
    const loading = get('/api/duplicates');
    this.loading = loading;
    try {
      const data = await loading;
      if (this.loading !== loading) return;
      this.data = data;
    } catch (e) {
      toast(e.message, { error: true });
    }
    this.render();
  }

  render() {
    const { groups, checking } = this.data;
    const exact = groups.filter(g => g.kind === 'file');
    const children = [];

    const intro = el('div', { class: 'dupes-intro' },
      el('p', { text: 'Pick the copy to keep in each group. It takes over the others\' tags and caption, and the others move to "Duplicates set aside". Nothing is deleted: check that folder and delete it in Explorer when you\'re happy. Ctrl+Z undoes.' }));
    if (exact.length > 1) {
      intro.append(el('button', {
        class: 'button', type: 'button',
        onClick: () => this.keep({ allExact: true })
      }, icon('copy'), `Set aside all exact copies (${exact.length} groups)`));
    }
    children.push(intro);
    if (checking > 0) children.push(el('p', { class: 'notice info', text: `Still reading ${checking.toLocaleString()} photos; more duplicates may show up in a moment.` }));

    if (groups.length === 0) {
      children.push(el('div', { class: 'dupes-empty' },
        el('strong', { text: 'No duplicates' }),
        el('span', { text: checking > 0 ? 'Nothing so far.' : 'Every photo in this folder is one of a kind.' })));
    }
    groups.forEach((group, index) => children.push(this.card(group, index)));
    this.root.replaceChildren(...children);
  }

  card(group, index) {
    const name = `keep-${index}`;
    const items = group.photos.map((photo, i) => {
      const radio = el('input', { type: 'radio', name, value: photo.id });
      radio.checked = photo.id === group.keep;
      const img = el('img', { alt: '', loading: 'lazy', src: thumbUrl(photo) });
      img.addEventListener('dblclick', () => this.app.onOpen(group.photos.map(p => p.id), i));
      const facts = [photo.width && photo.height ? `${photo.width} × ${photo.height}` : null, formatSize(photo.size)].filter(Boolean).join(' · ');
      const label = el('label', { class: 'dupe-item' + (radio.checked ? ' kept' : ''), title: photo.id },
        el('div', { class: 'dupe-thumb' }, img, el('span', { class: 'dupe-badge' }, icon('check'), 'Keep')),
        el('div', { class: 'dupe-meta' },
          el('span', { class: 'dupe-choice' }, radio, el('strong', { text: photo.name })),
          el('span', { class: 'muted small dupe-folder' }, icon('folder'), photo.folder || 'Top level'),
          photo.caption ? el('span', { class: 'dupe-caption', text: photo.caption }) : el('span', { class: 'muted small', text: 'No caption' }),
          photo.tags.length ? el('span', { class: 'dupe-tags' }, ...photo.tags.map(t => el('span', { class: 'chip static', text: t }))) : null,
          el('span', { class: 'muted small', text: facts })));
      radio.addEventListener('change', () => {
        for (const other of label.parentElement.children) other.classList.toggle('kept', other === label);
      });
      return label;
    });

    const row = el('div', { class: 'dupe-row' }, ...items);
    const keep = () => {
      const kept = row.querySelector('input:checked')?.value;
      this.keep({ groups: [{ keep: kept, others: group.photos.map(p => p.id).filter(id => id !== kept) }] });
    };
    const actions = el('div', { class: 'dupe-actions' },
      el('button', { class: 'button primary', type: 'button', onClick: keep }, 'Keep the selected one, set the others aside'));
    if (group.kind === 'moment') {
      actions.append(el('button', {
        class: 'button subtle', type: 'button', title: 'For example burst shots: stop showing this group',
        onClick: () => this.dismiss(group.photos.map(p => p.id))
      }, 'Not duplicates'));
    }

    const kind = kinds[group.kind];
    return el('section', { class: 'dupe-card', 'data-kind': group.kind },
      el('header', {}, el('strong', { text: kind.title }), el('span', { class: 'muted small', text: ` · ${group.photos.length} photos · ${kind.note}` })),
      row,
      actions);
  }

  async keep(body) {
    try {
      const result = await post('/api/duplicates/keep', body);
      await this.app.onChange(result);
      await this.load();
    } catch (e) {
      toast(e.message, { error: true });
    }
  }

  async dismiss(ids) {
    try {
      const result = await post('/api/duplicates/dismiss', { ids });
      toast(result.message);
      await this.app.onChange({ ...result, quiet: true });
      await this.load();
    } catch (e) {
      toast(e.message, { error: true });
    }
  }
}

function formatSize(bytes) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
