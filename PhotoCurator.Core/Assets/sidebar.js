// Left column: smart filters, the folder tree (drop photos on a folder to move them there) and the tag list.

import { icon } from './icons.js';
import { el } from './dialogs.js';

const DROP_TYPE = 'application/x-photocurator-ids';

export const parentOf = path => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');

export class Sidebar {
  constructor(root, { onSelect, onDrop, onFolderMenu, onNewFolder }) {
    this.root = root;
    this.onSelect = onSelect;
    this.onDrop = onDrop;
    this.onFolderMenu = onFolderMenu;
    this.onNewFolder = onNewFolder;
    this.expanded = new Set();
    this.storageKey = '';
    this.lastArgs = null;
  }

  /** Remembers which folders are open, per photo folder. */
  useRoot(rootPath) {
    this.storageKey = `photocurator-expanded:${rootPath}`;
    try { this.expanded = new Set(JSON.parse(localStorage.getItem(this.storageKey) ?? '[]')); }
    catch { this.expanded = new Set(); }
  }

  saveExpanded() {
    try { localStorage.setItem(this.storageKey, JSON.stringify([...this.expanded])); } catch { /* private mode */ }
  }

  reveal(folder) {
    for (let p = parentOf(folder); p; p = parentOf(p)) this.expanded.add(p);
    if (folder) this.saveExpanded();
  }

  render(args) {
    this.lastArgs = args;
    const { library, view, counts } = args;
    const isView = (kind, value = '') => view.kind === kind && (view.value ?? '') === value;

    const smart = el('div', { class: 'side-section' },
      this.row({ icon: 'photos', label: 'All photos', count: counts.all, active: isView('all'), onClick: () => this.onSelect({ kind: 'all' }) }),
      this.row({ icon: 'sparkle', label: 'New', count: counts.new, active: isView('new'), title: 'Not yet sorted, tagged or captioned', onClick: () => this.onSelect({ kind: 'new' }) }),
      this.row({ icon: 'captionOff', label: 'No caption', count: counts.nocaption, active: isView('nocaption'), onClick: () => this.onSelect({ kind: 'nocaption' }) }),
      this.row({ icon: 'tagOff', label: 'No tags', count: counts.notags, active: isView('notags'), onClick: () => this.onSelect({ kind: 'notags' }) })
    );

    const children = new Map();
    for (const folder of library.folders) {
      if (!folder.path) continue;
      const parent = parentOf(folder.path);
      if (!children.has(parent)) children.set(parent, []);
      children.get(parent).push(folder);
    }
    const rootFolder = library.folders.find(f => f.path === '');
    const tree = el('div', { class: 'tree' });
    tree.append(this.folderRow(rootFolder ?? { path: '', name: library.root.name, count: 0, total: 0 }, 0, false, isView('folder', ''), library.root.name));
    const addLevel = (parent, depth) => {
      for (const folder of children.get(parent) ?? []) {
        const hasKids = children.has(folder.path);
        tree.append(this.folderRow(folder, depth, hasKids, isView('folder', folder.path)));
        if (hasKids && this.expanded.has(folder.path)) addLevel(folder.path, depth + 1);
      }
    };
    addLevel('', 1);

    const newFolder = el('button', { class: 'button subtle icon-button', type: 'button', title: 'New folder', onClick: () => this.onNewFolder(view.kind === 'folder' ? view.value : '') }, icon('folderPlus'));
    const folders = el('div', { class: 'side-section' },
      el('div', { class: 'side-head' }, el('h2', { class: 'section-label', text: 'Folders' }), newFolder),
      tree);

    const tags = el('div', { class: 'side-section' }, el('div', { class: 'side-head' }, el('h2', { class: 'section-label', text: 'Tags' })));
    if (!library.tags.length) {
      tags.append(el('div', { class: 'side-empty', text: 'No tags yet. Select photos and press T.' }));
    }
    for (const tag of library.tags) {
      tags.append(this.row({ dot: true, label: tag.name, count: tag.count, active: isView('tag', tag.name), onClick: () => this.onSelect({ kind: 'tag', value: tag.name }) }));
    }

    const scroll = this.root.scrollTop;
    this.root.replaceChildren(smart, folders, tags);
    this.root.scrollTop = scroll;
  }

  row({ icon: iconName, dot, label, count, active, title, onClick }) {
    const button = el('button', { class: 'side-row' + (active ? ' active' : ''), type: 'button', title: title ?? label, onClick });
    if (dot) button.append(el('span', { class: 'tag-dot' }));
    if (iconName) button.append(icon(iconName));
    button.append(el('span', { class: 'label', text: label }), el('span', { class: 'count', text: count ?? '' }));
    return button;
  }

  folderRow(folder, depth, hasKids, active, displayName) {
    const row = el('div', {
      class: 'side-row' + (active ? ' active' : ''), role: 'button', tabindex: '0',
      title: folder.path || displayName, 'data-folder': folder.path
    });
    row.style.paddingLeft = `${8 + depth * 14}px`;

    const open = this.expanded.has(folder.path);
    const twisty = el('span', { class: 'twisty' + (open ? ' open' : '') + (hasKids ? '' : ' leaf') }, icon('chevronRight'));
    twisty.addEventListener('click', event => {
      event.stopPropagation();
      if (open) this.expanded.delete(folder.path);
      else this.expanded.add(folder.path);
      this.saveExpanded();
      this.render(this.lastArgs);
    });
    if (depth > 0) row.append(twisty);
    row.append(icon('folder'), el('span', { class: 'label', text: displayName ?? folder.name }), el('span', { class: 'count', text: folder.total }));

    const more = el('button', { class: 'row-more', type: 'button', title: 'Folder actions', tabindex: '-1' }, icon('more'));
    more.addEventListener('click', event => {
      event.stopPropagation();
      const rect = more.getBoundingClientRect();
      this.onFolderMenu(folder.path, rect.left, rect.bottom + 4);
    });
    row.append(more);

    const select = () => this.onSelect({ kind: 'folder', value: folder.path });
    row.addEventListener('click', select);
    row.addEventListener('keydown', event => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        select();
      }
    });
    row.addEventListener('contextmenu', event => {
      event.preventDefault();
      this.onFolderMenu(folder.path, event.clientX, event.clientY);
    });

    let expandTimer = 0;
    row.addEventListener('dragover', event => {
      if (!event.dataTransfer.types.includes(DROP_TYPE)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      row.classList.add('drop-target');
      if (hasKids && !open && !expandTimer) {
        expandTimer = setTimeout(() => {
          this.expanded.add(folder.path);
          this.saveExpanded();
          this.render(this.lastArgs);
        }, 800);
      }
    });
    row.addEventListener('dragleave', () => {
      row.classList.remove('drop-target');
      clearTimeout(expandTimer);
      expandTimer = 0;
    });
    row.addEventListener('drop', event => {
      event.preventDefault();
      row.classList.remove('drop-target');
      clearTimeout(expandTimer);
      const ids = JSON.parse(event.dataTransfer.getData(DROP_TYPE) || '[]');
      if (ids.length) this.onDrop(ids, folder.path);
    });
    return row;
  }
}
