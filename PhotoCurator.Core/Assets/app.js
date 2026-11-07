// PhotoCurator: open a folder, browse every photo in it, sort photos into folders, tag and caption them.
// This module owns the app state and wires the pieces together: sidebar, grid, selection bar, detail view, dialogs.

import { get, post, listen } from './api.js';
import { hydrateIcons, icon } from './icons.js';
import { Grid } from './grid.js';
import { Sidebar, parentOf } from './sidebar.js';
import { Detail } from './detail.js';
import { TagInput } from './tags.js';
import { DuplicatesView } from './duplicates.js';
import { importPhotos } from './import.js';
import { WriteUpView } from './writeup.js';
import {
  el, toast, showDialog, isDialogOpen, confirmInPlace, promptDialog, jobDialog,
  openPopover, popoverOpen, openMenu
} from './dialogs.js';

const $ = id => document.getElementById(id);

const state = {
  info: null,
  library: null,
  byId: new Map(),
  view: { kind: 'all' },
  search: '',
  sort: readPref('sort', 'name'),
  subfolders: readPref('subfolders', 'true') === 'true',
  list: [],
  jobs: new Map(),
  jobWatchers: new Map()
};

// ---------- startup ----------

hydrateIcons();

const grid = new Grid($('grid'), {
  onOpen: index => openDetail(index),
  onSelectionChange: count => updateSelectionBar(count),
  onContextMenu: (index, event) => photoMenu(index, event)
});

const sidebar = new Sidebar($('sidebar'), {
  onSelect: view => setView(view),
  onDrop: (ids, folder) => movePhotos(ids, folder),
  onFolderMenu: (folder, x, y) => folderMenu(folder, x, y),
  onNewFolder: parent => newFolder(parent)
});

const detail = new Detail({
  photo: id => state.byId.get(id),
  knownTags: () => state.library?.tags ?? [],
  rootName: () => state.library?.root.name ?? '',
  canReveal: () => !!state.info?.canReveal,
  moveTo: (ids, anchor) => moveWithPicker(ids, anchor),
  onSaved: result => afterChange(result, { quiet: true }),
  onClose: lastId => {
    const index = state.list.findIndex(p => p.id === lastId);
    if (index >= 0) {
      grid.focusIndex = index;
      grid.scrollToIndex(index);
    }
    $('grid').focus({ preventScroll: true });
    grid.refreshStates();
    updateSelectionBar(grid.selection.size);
  }
});

const duplicates = new DuplicatesView($('duplicates'), {
  onChange: result => afterChange(result, { quiet: !!result.quiet }),
  onOpen: (ids, index) => {
    $('selectionBar').hidden = true;
    detail.open(ids, index);
  }
});

const writeup = new WriteUpView($('writeup'), {
  library: () => state.library,
  onSaved: result => updateUndo(result.undo),
  onHistory: () => drawUndo()
});

$('sort').value = state.sort;
$('subfolders').checked = state.subfolders;
$('tileSize').value = readPref('tile', '200');
grid.setTileSize(Number($('tileSize').value));

listen({
  library: () => scheduleRefresh(),
  job: job => {
    state.jobs.set(job.id, job);
    state.jobWatchers.get(job.id)?.(job);
  }
});

// The app window asks the page to save any edit still waiting out its typing pause before it closes.
window.chrome?.webview?.addEventListener('message', async event => {
  if (event.data !== 'flush') return;
  try { await Promise.all([detail.flush(), writeup.flush()]); } finally { window.chrome.webview.postMessage('flushed'); }
});

start();

async function start() {
  try {
    state.info = await get('/api/state');
  } catch (e) {
    document.body.textContent = e.message;
    return;
  }
  if (state.info.folder) await showLibrary();
  else showWelcome();
}

// ---------- welcome + opening folders ----------

function showWelcome() {
  $('app').hidden = true;
  $('welcome').hidden = false;
  document.title = 'PhotoCurator';
  $('welcomePath').placeholder = state.info?.separator === '\\' ? 'C:\\Users\\you\\Pictures\\Photos' : `${state.info?.home ?? ''}/Pictures`;
  const recent = state.info?.recent ?? [];
  $('recentSection').hidden = recent.length === 0;
  $('recentList').replaceChildren(...recent.map(r => el('li', {},
    el('button', { type: 'button', onClick: () => openFolder(r.path) },
      icon('folder'),
      el('span', { class: 'recent-text' }, el('span', { text: r.name }), el('span', { class: 'recent-path', text: r.path }))))));
  if (!state.info?.nativePicker) $('welcomePath').focus();
}

$('welcomeOpen').addEventListener('click', async () => {
  const path = await chooseFolder('Choose a folder of photos');
  if (path) openFolder(path);
});

$('welcomePathForm').addEventListener('submit', event => {
  event.preventDefault();
  const path = $('welcomePath').value.trim();
  if (path) openFolder(path);
});

/** The OS folder picker in the app; a typed path in the dev host (no native dialogs there). */
async function chooseFolder(title, initial) {
  if (state.info?.nativePicker) {
    try {
      const result = await post('/api/pick-folder', { title, initial });
      return result.path ?? null;
    } catch (e) {
      toast(e.message, { error: true });
      return null;
    }
  }
  return promptDialog({ title, label: 'Paste or type the full path of a folder.', okLabel: 'Choose', placeholder: state.info?.home ?? '' });
}

async function openFolder(path, { acknowledge = false } = {}) {
  let result;
  try {
    result = await post('/api/open', { path, acknowledge });
  } catch (e) {
    toast(e.message, { error: true });
    return;
  }

  if (result.needsAcknowledge) {
    const answer = await confirmInPlace(result);
    if (!answer) return;
    if (answer.choice === 'copy') {
      await copyThenOpen(result.path);
      return;
    }
    try {
      result = await post('/api/open', { path: result.path, acknowledge: true, dontAskAgain: answer.dontAskAgain });
    } catch (e) {
      toast(e.message, { error: true });
      return;
    }
  }
  state.info = result;
  await showLibrary({ fresh: true });
}

async function copyThenOpen(source) {
  const destination = await chooseFolder('Where should the copy go?', parentOfPath(source));
  if (!destination) return;
  let started;
  try {
    started = await post('/api/copy', { source, destination });
  } catch (e) {
    toast(e.message, { error: true });
    return;
  }
  const job = await followJob(started.job, 'Copying the folder');
  if (job.state === 'done') {
    await openFolder(job.result.path, { acknowledge: true });
    toast(`Working on the copy: ${job.result.path}`);
  } else if (job.state === 'failed') {
    toast(`The copy didn't finish: ${job.error}`, { error: true });
  }
}

function parentOfPath(path) {
  const sep = state.info?.separator ?? '/';
  const index = path.lastIndexOf(sep);
  return index > 0 ? path.slice(0, index) : path;
}

/** Shows a progress dialog for a background job until it finishes; resolves with the final job. */
function followJob(job, title) {
  const dialog = jobDialog(title, () => post('/api/job/cancel').catch(() => {}));
  const update = j => dialog.update(j);
  state.jobWatchers.set(job.id, update);
  update(state.jobs.get(job.id) ?? job);
  return dialog.promise.then(final => {
    state.jobWatchers.delete(job.id);
    return final ?? state.jobs.get(job.id) ?? job;
  });
}

// ---------- the library ----------

async function showLibrary({ fresh = false } = {}) {
  $('welcome').hidden = true;
  $('app').hidden = false;
  if (fresh) {
    state.view = { kind: 'all' };
    state.search = '';
    $('search').value = '';
    grid.clearSelection();
    $('grid').scrollTop = 0;
  }
  await refresh();
  sidebar.useRoot(state.library.root.path);
  render();
  $('grid').focus({ preventScroll: true });
}

let refreshTimer = 0;
function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => refresh().then(render), 150);
}

async function refresh() {
  if ($('app').hidden && !state.info?.folder) return;
  try {
    const library = await get('/api/library');
    state.library = library;
    state.byId = new Map(library.photos.map(p => [p.id, p]));
    document.title = `${library.root.name} · PhotoCurator`;
  } catch (e) {
    if (e.status === 409) return; // no folder open
    toast(e.message, { error: true });
  }
}

function render() {
  const library = state.library;
  if (!library) return;
  $('rootName').textContent = library.root.name;
  $('folderButton').title = `${library.root.path}\nOpen another folder`;

  if (state.view.kind === 'folder' && !library.folders.some(f => f.path === state.view.value)) state.view = { kind: 'all' };
  if (state.view.kind === 'tag' && !library.tags.some(t => t.name === state.view.value)) state.view = { kind: 'all' };

  const showDuplicates = state.view.kind === 'duplicates';
  const showWriteUp = isWriteUp();
  state.list = showDuplicates ? [] : filtered(library.photos);
  grid.setItems(showWriteUp ? [] : state.list);
  $('grid').hidden = showDuplicates || showWriteUp;
  $('duplicates').hidden = !showDuplicates;
  $('writeup').hidden = !showWriteUp;
  if (showDuplicates) duplicates.load();
  if (showWriteUp) writeup.show(state.view.value).then(() => writeup.reloadIfIdle());
  sidebar.render({ library, view: state.view, counts: { ...counts(library.photos), duplicates: library.duplicates } });
  renderHeader();
  updateSelectionBar(grid.selection.size);
  updateUndo(library.undo);
  detail.refresh();
}

function counts(photos) {
  let fresh = 0;
  let nocaption = 0;
  let notags = 0;
  for (const p of photos) {
    if (!p.sidecar) fresh++;
    if (!p.caption) nocaption++;
    if (!p.tags.length) notags++;
  }
  return { all: photos.length, new: fresh, nocaption, notags };
}

function filtered(photos) {
  const view = state.view;
  const terms = state.search.toLowerCase().split(/\s+/).filter(Boolean);
  const inFolder = folder => p => (state.subfolders ? folder === '' || p.folder === folder || p.folder.startsWith(folder + '/') : p.folder === folder);
  const test = {
    all: () => true,
    new: p => !p.sidecar,
    nocaption: p => !p.caption,
    notags: p => !p.tags.length,
    folder: inFolder(view.value ?? ''),
    tag: p => p.tags.includes(view.value)
  }[view.kind] ?? (() => true);

  let list = photos.filter(p => test(p) && terms.every(term =>
    p.name.toLowerCase().includes(term) || p.caption.toLowerCase().includes(term) ||
    p.folder.toLowerCase().includes(term) || p.tags.some(t => t.includes(term))));

  if (state.sort === 'date') {
    const key = p => p.date ?? localIso(p.modified);
    list = [...list].sort((a, b) => key(a).localeCompare(key(b)) || a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }));
  }
  return list;
}

function localIso(ms) {
  const d = new Date(ms);
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function viewTitle() {
  const v = state.view;
  switch (v.kind) {
    case 'new': return 'New';
    case 'nocaption': return 'No caption';
    case 'notags': return 'No tags';
    case 'tag': return `Tagged "${v.value}"`;
    case 'duplicates': return 'Duplicates';
    case 'folder': return v.value ? v.value.split('/').join(' / ') : state.library.root.name;
    default: return 'All photos';
  }
}

function renderHeader() {
  $('viewTitle').textContent = viewTitle();
  // The tabs are always there (except in Duplicates), so the Write-up is easy to find; it needs a folder.
  const tabs = $('viewTabs');
  const inFolder = state.view.kind === 'folder';
  tabs.hidden = state.view.kind === 'duplicates';
  for (const tab of tabs.querySelectorAll('[data-tab]')) {
    tab.setAttribute('aria-selected', String((inFolder ? state.view.tab ?? 'photos' : 'photos') === tab.dataset.tab));
  }
  const writeUpTab = tabs.querySelector('[data-tab="writeup"]');
  writeUpTab.setAttribute('aria-disabled', String(!inFolder));
  writeUpTab.title = inFolder ? 'The story of this folder, with its photos' : 'Pick a folder in the sidebar to write its story';
  if (isWriteUp()) {
    const n = state.list.length;
    $('viewCount').textContent = `${n.toLocaleString()} photo${n === 1 ? '' : 's'}`;
    $('subfoldersToggle').hidden = true;
    $('selectAll').hidden = true;
    $('emptyState').hidden = true;
    return;
  }
  if (state.view.kind === 'duplicates') {
    const groups = state.library.duplicates;
    $('viewCount').textContent = `${groups.toLocaleString()} group${groups === 1 ? '' : 's'}`;
    $('subfoldersToggle').hidden = true;
    $('selectAll').hidden = true;
    $('emptyState').hidden = true;
    return;
  }
  const n = state.list.length;
  $('viewCount').textContent = `${n.toLocaleString()} photo${n === 1 ? '' : 's'}`;
  $('subfoldersToggle').hidden = state.view.kind !== 'folder';
  $('selectAll').hidden = n === 0;

  const empty = $('emptyState');
  empty.hidden = n > 0;
  if (n > 0) return;
  let title;
  let text;
  if (state.library.photos.length === 0) {
    title = 'No photos here yet';
    text = 'PhotoCurator looks for JPEG, HEIC, PNG, WebP, GIF and TIFF files in this folder and every folder inside it. Add some and they appear here.';
  } else if (state.search) {
    title = `No photos match "${state.search}"`;
    text = 'Try another word, or clear the search.';
  } else if (state.view.kind === 'new') {
    title = 'All caught up';
    text = 'Every photo has been sorted, tagged or captioned.';
  } else if (state.view.kind === 'folder') {
    title = 'This folder is empty';
    text = 'Drag photos onto it in the sidebar, or select photos and press M.';
  } else {
    title = 'Nothing here';
    text = '';
  }
  empty.replaceChildren(el('strong', { text: title }), el('span', { text }));
}

/** A folder's Write-up tab is showing. */
function isWriteUp() {
  return state.view.kind === 'folder' && state.view.tab === 'writeup';
}

function setView(view) {
  // Moving between folders keeps the tab you were on.
  if (view.kind === 'folder' && !view.tab && state.view.kind === 'folder') view = { ...view, tab: state.view.tab };
  writeup.flush();
  state.view = view;
  if (view.kind === 'folder') sidebar.reveal(view.value);
  grid.clearSelection();
  $('grid').scrollTop = 0;
  render();
  $('grid').focus({ preventScroll: true });
}

let lastUndo = null;

function updateUndo(description) {
  lastUndo = description;
  drawUndo();
}

/** In the Write-up tab, Undo steps back through the write-up's own edits first. */
function drawUndo() {
  const button = $('undoButton');
  if (isWriteUp() && writeup.canUndo) {
    button.disabled = false;
    button.title = 'Undo your last change to the write-up (Ctrl+Z)';
    return;
  }
  button.disabled = !lastUndo;
  button.title = lastUndo ? `Undo: ${lastUndo} (Ctrl+Z)` : 'Nothing to undo';
}

function undoButton() {
  if (isWriteUp() && writeup.undo()) return;
  undo();
}

/** After any change: show what happened (with Undo), refresh the photo list right away. */
async function afterChange(result, { quiet = false } = {}) {
  updateUndo(result.undo);
  if (!quiet && result.message) toast(result.message, { action: result.undo ? { label: 'Undo', run: undo } : null });
  grid.remapSelection(result.moved);
  detail.remap(result.moved);
  // Show where photos went (or the folder just made or renamed), even inside collapsed folders.
  if (result.folder) sidebar.reveal(result.folder);
  clearTimeout(refreshTimer);
  await refresh();
  render();
}

async function run(promise) {
  $('saveState').textContent = 'Working…';
  try {
    await writeup.flush();
    const result = await promise;
    await afterChange(result);
    return result;
  } catch (e) {
    toast(e.message, { error: true });
    return null;
  } finally {
    $('saveState').textContent = '';
  }
}

async function undo() {
  try {
    const result = await post('/api/undo');
    await afterChange({ ...result, undo: result.undo });
  } catch (e) {
    toast(e.message, { error: e.status !== 409 });
  }
}

// ---------- selection actions ----------

function updateSelectionBar(count) {
  $('selectionBar').hidden = count === 0 || detail.isOpen || isWriteUp();
  $('selectionCount').textContent = `${count.toLocaleString()} selected`;
}

$('selectionBar').addEventListener('click', event => {
  const button = event.target.closest('[data-action]');
  if (!button) return;
  selectionAction(button.dataset.action, button);
});

$('selectAll').addEventListener('click', () => grid.selectAll());

function selectionAction(action, anchor) {
  const ids = grid.selectedIds;
  if (action === 'clear') return grid.clearSelection();
  if (!ids.length) return;
  anchor ??= $('selectionBar').querySelector(`[data-action="${action}"]`);
  if (action === 'tag') tagPopover(ids, anchor);
  else if (action === 'move') moveWithPicker(ids, anchor);
  else if (action === 'caption') captionPopover(ids, anchor);
}

function tagPopover(ids, anchor) {
  openPopover(anchor, box => {
    const title = el('h3', { text: `Tags for ${ids.length === 1 ? '1 photo' : `${ids.length} photos`}` });
    const some = el('div', { class: 'untag-list' });
    const someLabel = el('p', { class: 'hint', text: 'On some of them (click to add to all):' });
    let current = ids;
    const photos = () => current.map(id => state.byId.get(id)).filter(Boolean);

    const apply = async (add, remove) => {
      const result = await run(post('/api/tags', { ids: current, add, remove }));
      if (result?.moved) current = current.map(id => result.moved[id] ?? id);
      draw();
      input.focus();
    };
    const input = new TagInput({
      known: () => state.library.tags,
      onAdd: tag => apply([tag], []),
      onRemove: tag => apply([], [tag]),
      placeholder: 'Type a tag, then Enter'
    });

    const draw = () => {
      const list = photos();
      const tally = new Map();
      for (const p of list) for (const t of p.tags) tally.set(t, (tally.get(t) ?? 0) + 1);
      const onAll = [...tally].filter(([, n]) => n === list.length).map(([t]) => t);
      const partial = [...tally].filter(([, n]) => n < list.length);
      input.setTags(onAll);
      some.replaceChildren(...partial.map(([tag, n]) => el('button', {
        class: 'chip removable', type: 'button', title: `Add "${tag}" to all ${list.length}`,
        onClick: () => apply([tag], [])
      }, `${tag} · ${n}/${list.length}`)));
      someLabel.hidden = partial.length === 0;
    };

    box.append(title, input.element, someLabel, some, el('p', { class: 'hint', text: 'New tags are created as you type. × removes a tag from all selected photos.' }));
    draw();
  }, { width: 380 });
}

function captionPopover(ids, anchor) {
  openPopover(anchor, (box, close) => {
    const photos = ids.map(id => state.byId.get(id)).filter(Boolean);
    const shared = photos.every(p => p.caption === photos[0]?.caption) ? photos[0]?.caption ?? '' : '';
    const captioned = photos.filter(p => p.caption).length;
    const input = el('input', { class: 'input', type: 'text', value: shared, placeholder: 'Caption for all selected photos' });
    const form = el('form', {},
      el('h3', { text: `Caption ${ids.length === 1 ? '1 photo' : `${ids.length} photos`}` }),
      input,
      captioned && !shared ? el('p', { class: 'hint', text: `${captioned} already ${captioned === 1 ? 'has a caption' : 'have captions'}; this replaces them. Ctrl+Z undoes it.` }) : null,
      el('div', { class: 'dialog-actions' }, el('button', { class: 'button primary', type: 'submit', text: 'Apply' })));
    form.addEventListener('submit', async event => {
      event.preventDefault();
      close();
      await run(post('/api/caption', { ids, caption: input.value }));
    });
    box.append(form);
    setTimeout(() => input.select(), 0);
  });
}

/** Folder picker popover, then move. Resolves with the moved map (old id → new id), or null. */
function moveWithPicker(ids, anchor) {
  return new Promise(resolve => {
    let settled = false;
    let choosing = false;
    const finish = value => {
      if (settled) return;
      settled = true;
      resolve(value);
    };
    openPopover(anchor, (box, close) => {
      const filter = el('input', { class: 'input', type: 'text', placeholder: 'Find a folder, or type a new name', spellcheck: 'false' });
      const list = el('div', { class: 'folder-picker', role: 'listbox' });
      const startFolder = state.view.kind === 'folder' ? state.view.value : '';
      let options = [];
      let active = 0;

      const choose = async option => {
        choosing = true;
        close();
        let folder = option.path;
        if (option.create) {
          folder = await createPath(option.parent, option.name);
          if (folder === null) return finish(null);
        }
        const result = await run(post('/api/move', { ids, folder }));
        finish(result?.moved ?? null);
      };

      const draw = () => {
        const text = filter.value.trim();
        const lower = text.toLowerCase();
        const folders = state.library.folders;
        options = folders
          .filter(f => !lower || f.path.toLowerCase().includes(lower) || (f.path === '' && state.library.root.name.toLowerCase().includes(lower)))
          .map(f => ({ path: f.path, label: f.path ? f.name : state.library.root.name, depth: f.path ? f.path.split('/').length : 0, hint: f.path }));
        if (text && !folders.some(f => f.path.toLowerCase() === lower || f.name.toLowerCase() === lower)) {
          const parent = text.includes('/') ? '' : startFolder;
          options.push({ create: true, parent, name: text, label: `New folder "${text}"`, hint: parent ? `in ${parent}` : `in ${state.library.root.name}` });
        }
        active = Math.min(active, Math.max(0, options.length - 1));
        list.replaceChildren(...options.map((option, index) => {
          const row = el('button', {
            class: 'side-row' + (index === active ? ' highlight' : ''), type: 'button', title: option.hint || option.label,
            onClick: () => choose(option)
          }, icon(option.create ? 'folderPlus' : 'folder'), el('span', { class: 'label', text: option.label }));
          if (!lower && !option.create) row.style.paddingLeft = `${8 + option.depth * 14}px`;
          else if (option.hint && option.path !== '') row.append(el('span', { class: 'count', text: option.create ? option.hint : parentOf(option.hint) }));
          return row;
        }));
        list.children[active]?.scrollIntoView({ block: 'nearest' });
      };

      filter.addEventListener('input', () => { active = 0; draw(); });
      filter.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          active = (active + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % Math.max(1, options.length);
          draw();
        } else if (event.key === 'Enter') {
          event.preventDefault();
          if (options[active]) choose(options[active]);
        }
      });
      box.append(el('h3', { text: `Move ${ids.length === 1 ? '1 photo' : `${ids.length} photos`} to…` }), filter, list,
        el('p', { class: 'hint', text: 'Type "Camping/Big Sur" to create nested folders. Ctrl+Z undoes a move.' }));
      draw();
    }, { width: 360 });
    // Resolve when the popover closes without a choice.
    const observer = new MutationObserver(() => {
      if ($('popover').hidden) {
        observer.disconnect();
        if (!choosing) finish(null);
      }
    });
    observer.observe($('popover'), { attributes: true, attributeFilter: ['hidden'] });
  });
}

/** Creates "a/b/c" under parent, reusing folders that already exist. Returns the folder's path or null. */
async function createPath(parent, typed) {
  let path = parent;
  for (const name of typed.split('/').map(s => s.trim()).filter(Boolean)) {
    const next = path ? `${path}/${name}` : name;
    if (!state.library.folders.some(f => f.path.toLowerCase() === next.toLowerCase())) {
      try {
        const result = await post('/api/folders', { parent: path, name });
        await afterChange(result, { quiet: true });
        path = result.folder;
        continue;
      } catch (e) {
        toast(e.message, { error: true });
        return null;
      }
    }
    path = state.library.folders.find(f => f.path.toLowerCase() === next.toLowerCase()).path;
  }
  return path;
}

async function movePhotos(ids, folder) {
  return run(post('/api/move', { ids, folder }));
}

// ---------- folders ----------

async function newFolder(parent) {
  const where = parent ? parent : state.library.root.name;
  const name = await promptDialog({ title: 'New folder', label: `Inside ${where}`, okLabel: 'Create', placeholder: 'e.g. Field Work' });
  if (!name) return;
  const result = await run(post('/api/folders', { parent, name }));
  if (result?.folder) setView({ kind: 'folder', value: result.folder });
}

async function renameFolder(path) {
  const current = path.split('/').pop();
  const name = await promptDialog({ title: 'Rename folder', value: current, okLabel: 'Rename' });
  if (!name || name === current) return;
  const wasViewing = state.view.kind === 'folder' && state.view.value === path;
  const result = await run(post('/api/folders/rename', { path, name }));
  if (result?.folder && wasViewing) setView({ kind: 'folder', value: result.folder });
}

function folderMenu(path, x, y) {
  const items = [
    { label: 'New folder inside…', icon: 'folderPlus', run: () => newFolder(path) }
  ];
  items.push({ label: 'Write-up', icon: 'doc', run: () => setView({ kind: 'folder', value: path, tab: 'writeup' }) });
  if (path) items.push({ label: 'Rename…', icon: 'rename', run: () => renameFolder(path) });
  items.push({ label: 'Export for web…', icon: 'export', run: () => exportFolder(path) }, 'separator', { note: path || state.library.root.path });
  openMenu(x, y, items);
}

async function exportFolder(folder) {
  const name = folder ? folder.split('/').pop() : state.library.root.name;
  let last = null;
  try { last = (await get(`/api/export-destination?folder=${encodeURIComponent(folder)}`)).destination; } catch { /* first export */ }

  const destination = await showDialog((box, close) => {
    const input = el('input', { class: 'input', type: 'text', value: last ?? '', spellcheck: 'false', placeholder: 'e.g. C:\\Users\\you\\GITHUB\\lukehoffmann.com\\src\\content\\field', autofocus: true });
    const choose = el('button', {
      class: 'button', type: 'button', text: 'Choose…',
      onClick: async () => {
        const picked = await chooseFolder('Export to which folder?', input.value || undefined);
        if (picked) input.value = picked;
      }
    });
    const form = el('form', {},
      el('h2', { text: `Export "${name}" for the web` }),
      el('p', { text: 'Writes web-ready copies of the photos in this folder and its subfolders: JPEGs up to 2400 px, upright, with location and all other metadata removed, plus a .md file per photo with its caption, date and tags, and a folder.md per folder.' }),
      el('label', { class: 'field-label', text: 'Destination' }),
      el('div', { class: 'path-row' }, input, choose),
      el('p', { class: 'small hint-line', text: 'Use an empty folder, like your website\'s src/content/field. Exporting again updates only what changed, and only removes files a previous export wrote. Your originals are not touched.' }),
      el('div', { class: 'dialog-actions' },
        el('button', { class: 'button subtle', type: 'button', text: 'Cancel', onClick: () => close(null) }),
        el('button', { class: 'button primary', type: 'submit' }, icon('export'), 'Export')));
    form.addEventListener('submit', event => {
      event.preventDefault();
      if (input.value.trim()) close(input.value.trim());
    });
    box.append(form);
  });
  if (!destination) return;

  let started;
  try {
    started = await post('/api/export', { folder, destination });
  } catch (e) {
    toast(e.message, { error: true });
    return;
  }
  const job = await followJob(started.job, `Exporting "${name}"`);
  if (job.state === 'failed') return toast(`Export failed: ${job.error}`, { error: true });
  if (job.state !== 'done') return toast('Export cancelled. Files already written are kept; exporting again finishes the job.');

  const r = job.result;
  await showDialog((box, close) => {
    box.append(
      el('h2', { text: 'Export finished' }),
      el('div', { class: 'dialog-path', text: r.destination }),
      el('p', { text: `${r.photos} photo${r.photos === 1 ? '' : 's'} exported: ${r.written} file${r.written === 1 ? '' : 's'} written, ${r.unchanged} unchanged, ${r.removed} removed.` }),
      r.problems.length ? el('ul', { class: 'problems' }, ...r.problems.map(p => el('li', { text: p }))) : null,
      el('div', { class: 'dialog-actions' }, el('button', { class: 'button primary', type: 'button', text: 'Done', autofocus: true, onClick: () => close(true) })));
  });
}

// ---------- photo context menu ----------

function photoMenu(index, event) {
  const photo = state.list[index];
  const ids = grid.selection.size ? grid.selectedIds : [photo.id];
  const anchor = { getBoundingClientRect: () => new DOMRect(event.clientX, event.clientY, 1, 1), contains: () => false };
  const items = [
    { label: 'Open', icon: 'photos', run: () => openDetail(index) },
    { label: ids.length > 1 ? `Tags for ${ids.length} photos…` : 'Tags…', icon: 'tag', run: () => tagPopover(ids, anchor) },
    { label: 'Move to…', icon: 'move', run: () => moveWithPicker(ids, anchor) },
    { label: 'Caption…', icon: 'caption', run: () => captionPopover(ids, anchor) }
  ];
  if (state.info?.canReveal) items.push({ label: 'Show in Explorer', icon: 'reveal', run: () => post('/api/reveal', { id: photo.id }) });
  items.push('separator', { note: photo.id });
  openMenu(event.clientX, event.clientY, items);
}

// ---------- detail ----------

function openDetail(index) {
  $('selectionBar').hidden = true;
  detail.open(state.list.map(p => p.id), index);
}

// ---------- top bar ----------

$('folderButton').addEventListener('click', event => {
  const rect = event.currentTarget.getBoundingClientRect();
  const current = state.library?.root.path;
  const recent = (state.info?.recent ?? []).filter(r => r.path !== current);
  openMenu(rect.left, rect.bottom + 4, [
    {
      label: 'Open another folder…', icon: 'folder',
      run: async () => {
        const path = await chooseFolder('Choose a folder of photos');
        if (path) openFolder(path);
      }
    },
    ...(recent.length ? ['separator', ...recent.map(r => ({ label: r.name, icon: 'folder', run: () => openFolder(r.path) }))] : []),
    'separator',
    { label: 'Back to the start screen', icon: 'chevronLeft', run: async () => { state.info = await get('/api/state'); showWelcome(); } },
    { note: current ?? '' },
    { note: `PhotoCurator · build ${state.info?.build ?? '?'}` }
  ]);
});

let searchTimer = 0;
$('search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    state.search = $('search').value.trim();
    render();
  }, 120);
});
$('search').addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    $('search').value = '';
    state.search = '';
    render();
    $('grid').focus();
  } else if (event.key === 'ArrowDown' || event.key === 'Enter') {
    event.preventDefault();
    $('grid').focus();
    if (grid.focusIndex < 0 && state.list.length) grid.focusIndex = 0;
    grid.refreshStates();
  }
});

$('sort').addEventListener('change', () => {
  state.sort = $('sort').value;
  writePref('sort', state.sort);
  render();
});

$('subfolders').addEventListener('change', () => {
  state.subfolders = $('subfolders').checked;
  writePref('subfolders', String(state.subfolders));
  render();
});

$('tileSize').addEventListener('input', () => {
  grid.setTileSize(Number($('tileSize').value));
  writePref('tile', $('tileSize').value);
});

$('undoButton').addEventListener('click', undoButton);
$('viewTabs').addEventListener('click', event => {
  const tab = event.target.closest('[data-tab]')?.dataset.tab;
  if (tab === 'writeup' && state.view.kind !== 'folder') {
    toast('Pick a folder in the sidebar first. Each folder has its own write-up.');
    return;
  }
  if (!tab || state.view.kind !== 'folder' || (state.view.tab ?? 'photos') === tab) return;
  setView({ ...state.view, tab });
});
$('importButton').addEventListener('click', () => importPhotos({
  nativePicker: !!state.info?.nativePicker,
  chooseFolder,
  followJob,
  photo: id => state.byId.get(id),
  onImported: async result => {
    await refresh();
    updateUndo(state.library.undo);
    const text = result.imported === 0 ? 'Nothing was imported.' : `Imported ${result.imported.toLocaleString()} photo${result.imported === 1 ? '' : 's'} into ${result.folder}`;
    toast(result.problems.length ? `${text}. ${result.problems.length} couldn't be copied: ${result.problems[0]}` : text,
      { error: result.problems.length > 0, action: result.imported ? { label: 'Undo', run: undo } : null });
    if (result.imported) {
      sidebar.reveal(result.folder);
      setView({ kind: 'folder', value: result.folder });
    } else {
      render();
    }
  }
}));
$('grid').addEventListener('focus', () => grid.refreshStates());
$('grid').addEventListener('blur', () => grid.refreshStates());

// ---------- keyboard ----------

document.addEventListener('keydown', event => {
  if (isDialogOpen() || $('app').hidden && !detail.isOpen) return;
  const ctrl = event.ctrlKey || event.metaKey;
  const typing = event.target.matches?.('input, textarea, select') || event.target.isContentEditable;

  // In the write-up, Ctrl+Z steps through its own edits (WriteUpView.key). Only with none left does it reach the
  // folder-wide undo, and never from inside a text field.
  if (isWriteUp() && !detail.isOpen && !popoverOpen() && (writeup.canUndo || writeup.canRedo || typing) && writeup.key(event)) return;
  if (ctrl && event.key.toLowerCase() === 'z' && !event.shiftKey && !typing) {
    event.preventDefault();
    undo();
    return;
  }
  if (detail.isOpen) {
    if (detail.key(event)) event.preventDefault();
    return;
  }
  if (popoverOpen()) return;

  if (ctrl && (event.key === '=' || event.key === '+' || event.key === '-')) {
    event.preventDefault();
    const slider = $('tileSize');
    slider.value = String(Number(slider.value) + (event.key === '-' ? -20 : 20));
    slider.dispatchEvent(new Event('input'));
    return;
  }
  if ((ctrl && event.key.toLowerCase() === 'f') || (event.key === '/' && !typing)) {
    event.preventDefault();
    $('search').focus();
    $('search').select();
    return;
  }
  if (typing) return;
  // The write-up has its own keys (typing, Enter, arrows); the grid's shortcuts stay out of its way.
  if (isWriteUp()) return;

  if (event.key === 'Escape') {
    grid.clearSelection();
    return;
  }
  if (!ctrl && !event.altKey && grid.selection.size) {
    const action = { t: 'tag', m: 'move', c: 'caption' }[event.key.toLowerCase()];
    if (action) {
      event.preventDefault();
      selectionAction(action);
      return;
    }
  }
  if (!ctrl && !event.altKey && !grid.selection.size && grid.focusIndex >= 0) {
    // With nothing selected, the shortcuts act on the photo under the keyboard focus.
    const action = { t: 'tag', m: 'move', c: 'caption' }[event.key.toLowerCase()];
    if (action) {
      event.preventDefault();
      grid.toggle(grid.focusIndex);
      selectionAction(action);
      return;
    }
  }
  if (state.view.kind !== 'duplicates' && (event.target === $('grid') || event.target === document.body)) {
    if (grid.key(event)) event.preventDefault();
  }
});

// ---------- small helpers ----------

function readPref(key, fallback) {
  try { return localStorage.getItem(`photocurator-${key}`) ?? fallback; } catch { return fallback; }
}

function writePref(key, value) {
  try { localStorage.setItem(`photocurator-${key}`, value); } catch { /* private mode */ }
}
