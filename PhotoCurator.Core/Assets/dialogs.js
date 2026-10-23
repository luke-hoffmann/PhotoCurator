// Toasts, modal dialogs, popovers and context menus. Each closes on Escape and gives focus back where it was.

import { icon } from './icons.js';

const $ = id => document.getElementById(id);

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (value !== undefined && value !== null && value !== false) node.setAttribute(key, value === true ? '' : value);
  }
  node.append(...children.filter(c => c !== null && c !== undefined && c !== false));
  return node;
}

// ---------- toasts ----------

export function toast(message, { error = false, action = null, timeout = 5000 } = {}) {
  const box = el('div', { class: 'toast' + (error ? ' error' : ''), role: error ? 'alert' : 'status' },
    el('span', { class: 'toast-text', text: message }));
  if (action) {
    box.append(el('button', {
      class: 'button', type: 'button', text: action.label,
      onClick: () => { box.remove(); action.run(); }
    }));
  }
  const holder = $('toasts');
  holder.append(box);
  while (holder.children.length > 2) holder.firstElementChild.remove();
  setTimeout(() => box.remove(), error ? Math.max(timeout, 8000) : timeout);
}

// ---------- modal dialogs ----------

let dialogOpen = false;
export const isDialogOpen = () => dialogOpen;

/** Shows a dialog built by build(close). Resolves with whatever close(value) was called with (null on Escape). */
export function showDialog(build) {
  return new Promise(resolve => {
    const layer = $('dialogLayer');
    const previous = document.activeElement;
    let done = false;
    const close = value => {
      if (done) return;
      done = true;
      dialogOpen = false;
      layer.hidden = true;
      layer.replaceChildren();
      document.removeEventListener('keydown', onKey, true);
      previous?.focus?.({ preventScroll: true });
      resolve(value);
    };
    const onKey = event => {
      if (event.key === 'Escape' && !layer.querySelector('[data-busy]')) {
        event.preventDefault();
        event.stopPropagation();
        close(null);
      }
    };
    const box = el('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true' });
    build(box, close);
    layer.replaceChildren(box);
    layer.hidden = false;
    dialogOpen = true;
    document.addEventListener('keydown', onKey, true);
    (box.querySelector('[autofocus]') ?? box.querySelector('input, .button.primary, button'))?.focus();
  });
}

/** The first-open warning. Resolves to { choice: 'copy' | 'continue', dontAskAgain } or null. */
export function confirmInPlace(folder) {
  return showDialog((box, close) => {
    const dontAsk = el('input', { type: 'checkbox' });
    box.append(
      el('h2', { text: 'Before you start' }),
      el('div', { class: 'dialog-path', text: folder.path }),
      el('p', {}, 'PhotoCurator works on this folder ', el('strong', { text: 'in place' }),
        ': it moves photos into the folders you choose and writes a small .md file next to each photo you sort, tag or caption.'),
      el('p', { text: 'Your photos themselves are never edited or deleted, and Ctrl+Z undoes changes while the folder is open. If you want to keep this folder exactly as it is, make a copy first and work on the copy.' }),
      el('label', { class: 'checkbox' }, dontAsk, "Don't ask again for this folder"),
      el('div', { class: 'dialog-actions' },
        el('button', { class: 'button', type: 'button', onClick: () => close({ choice: 'copy', dontAskAgain: dontAsk.checked }) }, icon('copy'), 'Make a copy first…'),
        el('span', { class: 'spacer' }),
        el('button', { class: 'button subtle', type: 'button', text: 'Cancel', onClick: () => close(null) }),
        el('button', { class: 'button primary', type: 'button', text: 'Continue', autofocus: true, onClick: () => close({ choice: 'continue', dontAskAgain: dontAsk.checked }) })
      )
    );
  });
}

/** A one-field dialog. Resolves to the trimmed text, or null. */
export function promptDialog({ title, label, value = '', okLabel = 'OK', hint = '', placeholder = '' }) {
  return showDialog((box, close) => {
    const input = el('input', { class: 'input', type: 'text', value, placeholder, autofocus: true, spellcheck: 'false' });
    const form = el('form', {},
      el('h2', { text: title }),
      label ? el('p', { text: label }) : null,
      input,
      hint ? el('p', { class: 'small hint-line', text: hint }) : null,
      el('div', { class: 'dialog-actions' },
        el('button', { class: 'button subtle', type: 'button', text: 'Cancel', onClick: () => close(null) }),
        el('button', { class: 'button primary', type: 'submit', text: okLabel })
      )
    );
    form.addEventListener('submit', event => {
      event.preventDefault();
      const text = input.value.trim();
      if (text) close(text);
    });
    box.append(form);
    setTimeout(() => input.select(), 0);
  });
}

/** A dialog that follows a background job. update(job) refreshes it; returns a promise of the finished job. */
export function jobDialog(title, onCancel) {
  let update = () => {};
  const promise = showDialog((box, close) => {
    box.dataset.busy = '';
    const bar = el('div');
    const detail = el('p', { class: 'small', text: 'Starting…' });
    const cancel = el('button', { class: 'button', type: 'button', text: 'Cancel', onClick: onCancel });
    box.append(el('h2', { text: title }), el('div', { class: 'progress' }, bar), detail, el('div', { class: 'dialog-actions' }, cancel));
    update = job => {
      const fraction = job.total > 0 ? job.done / job.total : 0;
      bar.style.width = `${Math.round(fraction * 100)}%`;
      if (job.kind === 'copy') detail.textContent = job.total ? `${formatBytes(job.done)} of ${formatBytes(job.total)}` : 'Counting files…';
      else detail.textContent = job.total ? `${job.done} of ${job.total} photos${job.detail ? ` · ${job.detail}` : ''}` : 'Preparing…';
      if (job.state !== 'running') close(job);
    };
  });
  return { promise, update: job => update(job) };
}

export function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

// ---------- popover ----------

let closePopover = null;

/** A panel anchored to an element (above it if there's room below the fold). build(box, close) fills it. */
export function openPopover(anchor, build, { width } = {}) {
  closePopover?.();
  const box = $('popover');
  box.replaceChildren();
  box.style.width = width ? `${width}px` : '';
  const previous = document.activeElement;
  const close = () => {
    if (closePopover !== close) return;
    closePopover = null;
    box.hidden = true;
    box.replaceChildren();
    document.removeEventListener('mousedown', outside, true);
    document.removeEventListener('keydown', onKey, true);
    if (box.contains(document.activeElement) || document.activeElement === document.body) previous?.focus?.({ preventScroll: true });
  };
  const outside = event => {
    if (!box.contains(event.target) && !anchor.contains(event.target)) close();
  };
  const onKey = event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
    }
  };
  closePopover = close;
  build(box, close);
  box.hidden = false;

  const rect = anchor.getBoundingClientRect();
  const boxRect = box.getBoundingClientRect();
  const left = Math.min(window.innerWidth - boxRect.width - 12, Math.max(12, rect.left + rect.width / 2 - boxRect.width / 2));
  const above = rect.top - boxRect.height - 10;
  const top = above > 12 && rect.top > window.innerHeight / 2 ? above : Math.min(rect.bottom + 8, window.innerHeight - boxRect.height - 12);
  box.style.left = `${left}px`;
  box.style.top = `${Math.max(12, top)}px`;

  document.addEventListener('mousedown', outside, true);
  document.addEventListener('keydown', onKey, true);
  box.querySelector('input')?.focus();
  return close;
}

export const popoverOpen = () => closePopover !== null;

// ---------- context menu ----------

/** items: { label, icon, run } | 'separator' | { note } */
export function openMenu(x, y, items) {
  const menu = $('menu');
  const close = () => {
    menu.hidden = true;
    document.removeEventListener('mousedown', outside, true);
    document.removeEventListener('keydown', onKey, true);
  };
  const outside = event => { if (!menu.contains(event.target)) close(); };
  const onKey = event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
    }
  };
  menu.replaceChildren(...items.map(item => {
    if (item === 'separator') return el('hr');
    if (item.note) return el('div', { class: 'menu-note', text: item.note });
    return el('button', {
      type: 'button', role: 'menuitem',
      onClick: () => { close(); item.run(); }
    }, item.icon ? icon(item.icon) : null, item.label);
  }));
  menu.hidden = false;
  const rect = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, window.innerWidth - rect.width - 8)}px`;
  menu.style.top = `${Math.min(y, window.innerHeight - rect.height - 8)}px`;
  document.addEventListener('mousedown', outside, true);
  document.addEventListener('keydown', onKey, true);
  menu.querySelector('button')?.focus();
}
