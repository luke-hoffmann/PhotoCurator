// Import from a download (Google Photos album zips, or a folder): choose what to import, check it against the open
// folder, show what's new, then copy just that into Inbox/<album>.

import { post, thumbUrl } from './api.js';
import { el, toast, showDialog } from './dialogs.js';
import { icon } from './icons.js';

/**
 * @param {object} app  nativePicker, chooseFolder(title), followJob(job, title), photo(id), onImported(result)
 */
export async function importPhotos(app) {
  const sources = await chooseSources(app);
  if (!sources?.length) return;

  let started;
  try {
    started = await post('/api/import/check', { sources });
  } catch (e) {
    toast(e.message, { error: true });
    return;
  }
  const checked = await app.followJob(started.job, 'Checking the download against your folder');
  if (checked.state === 'failed') return toast(checked.error, { error: true });
  if (checked.state !== 'done') return;

  const plan = checked.result;
  const go = await summary(app, plan);
  if (!go) return;

  try {
    started = await post('/api/import/run', { id: plan.id });
  } catch (e) {
    toast(e.message, { error: true });
    return;
  }
  const done = await app.followJob(started.job, `Importing into ${plan.destination}`);
  if (done.state === 'failed') return toast(`The import didn't finish: ${done.error}`, { error: true });
  if (done.state !== 'done') return toast('Import cancelled. Photos already copied stay; Ctrl+Z removes them.');
  await app.onImported(done.result);
}

function chooseSources(app) {
  return showDialog((box, close) => {
    const chosen = [];
    const list = el('ul', { class: 'source-list' });
    const importButton = el('button', { class: 'button primary', type: 'button', text: 'Check for new photos', disabled: true, onClick: () => close(chosen) });
    const draw = () => {
      list.replaceChildren(...chosen.map((path, i) => el('li', {},
        icon(path.toLowerCase().endsWith('.zip') ? 'copy' : 'folder'),
        el('span', { class: 'source-path', text: path, title: path }),
        el('button', { class: 'button subtle icon-button', type: 'button', title: 'Remove', onClick: () => { chosen.splice(i, 1); draw(); } }, icon('close')))));
      list.hidden = chosen.length === 0;
      importButton.disabled = chosen.length === 0;
    };
    const add = paths => {
      for (const path of paths) if (path && !chosen.includes(path)) chosen.push(path);
      draw();
    };

    const pickers = el('div', { class: 'path-row' });
    if (app.nativePicker) {
      pickers.append(
        el('button', {
          class: 'button', type: 'button',
          onClick: async () => {
            try { add((await post('/api/pick-files', { title: 'Choose the album zip file(s)' })).paths ?? []); }
            catch (e) { toast(e.message, { error: true }); }
          }
        }, icon('copy'), 'Add zip files…'),
        el('button', {
          class: 'button', type: 'button',
          onClick: async () => { const path = await app.chooseFolder('Choose the downloaded folder'); if (path) add([path]); }
        }, icon('folder'), 'Add a folder…'));
    } else {
      const input = el('input', { class: 'input', type: 'text', placeholder: 'Path to a .zip file or a folder', spellcheck: 'false', autofocus: true });
      const addTyped = () => { const path = input.value.trim(); if (path) { add([path]); input.value = ''; } };
      input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); addTyped(); } });
      // A typed path counts even if Add wasn't pressed.
      input.addEventListener('input', () => { importButton.disabled = chosen.length === 0 && !input.value.trim(); });
      importButton.addEventListener('click', addTyped, { capture: true });
      pickers.append(input, el('button', { class: 'button', type: 'button', text: 'Add', onClick: addTyped }));
    }

    box.append(
      el('h2', { text: 'Import photos' }),
      el('p', { text: 'Download the whole album from Google Photos (zip files are fine, no need to unzip) and add it here. Only photos that aren\'t already in this folder come in, even if you\'ve moved, renamed or captioned them since.' }),
      pickers,
      list,
      el('div', { class: 'dialog-actions' },
        el('button', { class: 'button subtle', type: 'button', text: 'Cancel', onClick: () => close(null) }),
        importButton));
    draw();
  });
}

function summary(app, plan) {
  return showDialog((box, close) => {
    const n = value => value.toLocaleString();
    const plural = (value, one, many) => `${n(value)} ${value === 1 ? one : many}`;
    const lines = el('ul', { class: 'import-summary' },
      el('li', {}, icon('photos'), `${plural(plan.total, 'photo', 'photos')} in this download`),
      plan.existing ? el('li', { class: 'muted' }, icon('check'), `${n(plan.existing)} already in your folder, skipped`) : null,
      plan.repeat ? el('li', { class: 'muted' }, icon('check'), `${plural(plan.repeat, 'photo appears', 'photos appear')} twice in the download, skipped`) : null,
      plan.new ? el('li', { class: 'strong' }, icon('sparkle'), `${n(plan.new)} new`) : null,
      plan.edited.length ? el('li', { class: 'strong' }, icon('copy'), `${plural(plan.edited.length, 'looks like an edited version', 'look like edited versions')} of photos you have: imported, and shown under Duplicates so you can pick`) : null);

    const edits = plan.edited.length
      ? el('div', { class: 'edited-list' }, ...plan.edited.slice(0, 6).map(item => {
        const match = app.photo(item.match);
        return el('div', { class: 'edited-item' },
          match ? el('img', { alt: '', src: thumbUrl(match) }) : null,
          el('span', { class: 'small' }, el('strong', { text: item.name }), el('br'), `matches ${item.match}`));
      }), plan.edited.length > 6 ? el('p', { class: 'small muted', text: `and ${plan.edited.length - 6} more` }) : null)
      : null;

    if (plan.toCopy === 0) {
      box.append(el('h2', { text: 'Nothing new' }), lines,
        el('p', { text: 'Every photo in this download is already in your folder.' }),
        el('div', { class: 'dialog-actions' }, el('button', { class: 'button primary', type: 'button', text: 'Done', autofocus: true, onClick: () => close(false) })));
      return;
    }
    box.append(
      el('h2', { text: 'Ready to import' }),
      lines,
      edits,
      el('p', { class: 'small hint-line' }, 'Goes into ', el('strong', { text: plan.destination }), ', where they show up under New. The download itself isn\'t changed.'),
      el('div', { class: 'dialog-actions' },
        el('button', { class: 'button subtle', type: 'button', text: 'Cancel', onClick: () => close(false) }),
        el('button', { class: 'button primary', type: 'button', autofocus: true, onClick: () => close(true) }, icon('export'), `Import ${n(plan.toCopy)}`)));
  });
}
