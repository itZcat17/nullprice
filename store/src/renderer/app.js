'use strict';

/**
 * Renderer. Runs sandboxed with no Node access — everything privileged goes through the
 * narrow `window.nullprice` bridge, which only accepts catalogue ids.
 */

const GLYPHS = {
  capture:
    '<rect x="4" y="6" width="16" height="12" fill="none" stroke="currentColor" stroke-width="1.5" stroke-dasharray="3 2"/><rect x="1" y="3" width="4" height="4" fill="currentColor"/><rect x="19" y="17" width="4" height="4" fill="currentColor"/>',
  clip:
    '<rect x="3" y="3" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="7" y="7" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="11" y="11" width="10" height="10" fill="currentColor" opacity="0.25"/>',
  ferry:
    '<rect x="2" y="4" width="5" height="16" fill="currentColor" opacity="0.35"/><rect x="17" y="4" width="5" height="16" fill="currentColor" opacity="0.35"/><path d="M8 12h9M13 8l4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.8"/>',
  batch:
    '<rect x="3" y="3" width="7" height="7" fill="currentColor" opacity="0.8"/><rect x="14" y="3" width="7" height="7" fill="currentColor" opacity="0.5"/><rect x="3" y="14" width="7" height="7" fill="currentColor" opacity="0.5"/><rect x="14" y="14" width="7" height="7" fill="currentColor" opacity="0.25"/>',
  expand:
    '<rect x="3" y="6" width="6" height="3" fill="currentColor"/><rect x="3" y="15" width="18" height="3" fill="currentColor" opacity="0.45"/><path d="M11 7.5h9" stroke="currentColor" stroke-width="1.5" stroke-dasharray="2 2"/>',
  compare:
    '<rect x="2" y="4" width="8" height="16" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="14" y="4" width="8" height="16" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="4" y="8" width="4" height="2" fill="currentColor"/><rect x="16" y="8" width="4" height="2" fill="currentColor" opacity="0.35"/><rect x="4" y="14" width="4" height="2" fill="currentColor" opacity="0.35"/><rect x="16" y="14" width="4" height="2" fill="currentColor"/>',
  corral:
    '<rect x="2" y="5" width="20" height="14" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="5" y="9" width="4" height="4" fill="currentColor"/><rect x="11" y="9" width="4" height="4" fill="currentColor" opacity="0.6"/><rect x="17" y="9" width="2" height="4" fill="currentColor" opacity="0.3"/>',
  span:
    '<rect x="1" y="5" width="10" height="9" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="13" y="5" width="10" height="9" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="4" y="17" width="16" height="2" fill="currentColor" opacity="0.4"/>',
  purge:
    '<path d="M4 6h16l-1.5 14H5.5z" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M9 3h6" stroke="currentColor" stroke-width="1.5"/><path d="M10 10v6M14 10v6" stroke="currentColor" stroke-width="1.5" opacity="0.5"/>',
  sheaf:
    '<rect x="3" y="2" width="12" height="16" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="7" y="5" width="12" height="16" fill="#fff" stroke="currentColor" stroke-width="1.5"/><path d="M10 11h6M10 15h6" stroke="currentColor" stroke-width="1.4" opacity="0.55"/>',
};

const STATUS = { available: 'Available', planned: 'Planned', building: 'In development' };

const THEMES = {
  light: 'Light — cool grey, pine green',
  graphite: 'Graphite — cool neutral dark',
  ledger: 'Ledger — warm dark, brass on ink',
  blueprint: 'Blueprint — deep navy, teal',
};

const THEME_KEY = 'nullprice.theme';

/** id -> { state, received, total, path } */
const progress = new Map();

let apps = [];
let feed = null;
let currentId = null;
let currentView = 'catalogue';

const el = {
  grid: document.getElementById('grid'),
  nav: document.getElementById('nav'),
  build: document.getElementById('build'),
  countCatalogue: document.getElementById('count-catalogue'),
  countDownloads: document.getElementById('count-downloads'),
  downloads: document.getElementById('downloads'),
  main: document.getElementById('main'),
  swatches: document.getElementById('swatches'),
  themeName: document.getElementById('theme-name'),
};

// ---- theme ----------------------------------------------------------------

/**
 * Applies a theme by stamping data-theme on the root. With no stored choice the
 * attribute is left off entirely, which lets the prefers-color-scheme block in the
 * stylesheet follow the operating system instead.
 */
function applyTheme(name) {
  if (name) {
    document.documentElement.setAttribute('data-theme', name);
  } else {
    document.documentElement.removeAttribute('data-theme');
  }

  const effective = name || (matchMedia('(prefers-color-scheme: dark)').matches ? 'graphite' : 'light');

  for (const button of el.swatches.querySelectorAll('.swatch')) {
    button.setAttribute('aria-pressed', button.dataset.swatch === effective ? 'true' : 'false');
  }

  el.themeName.textContent = name
    ? THEMES[name]
    : `Following Windows — ${THEMES[effective].split(' — ')[0].toLowerCase()}`;
}

function initTheme() {
  let stored = null;
  try {
    stored = localStorage.getItem(THEME_KEY);
  } catch {
    // Private mode or a locked-down profile; fall back to following the OS.
  }

  applyTheme(THEMES[stored] ? stored : null);

  el.swatches.addEventListener('click', (event) => {
    const button = event.target.closest('.swatch');
    if (!button) return;

    const next = button.dataset.swatch;
    applyTheme(next);
    try {
      localStorage.setItem(THEME_KEY, next);
    } catch {
      // Not being able to remember the choice is not worth interrupting anyone over.
    }
  });

  // Only meaningful while no explicit choice is stored.
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (!document.documentElement.hasAttribute('data-theme')) applyTheme(null);
  });
}

// ---- helpers --------------------------------------------------------------

function svg(name, size) {
  const body = GLYPHS[name] || '';
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" aria-hidden="true">${body}</svg>`;
}

function esc(value) {
  return String(value).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])
  );
}

function formatBytes(bytes) {
  if (!bytes) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${bytes} B` : `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

function find(id) {
  return apps.find((a) => a.id === id) || null;
}

// ---- views ----------------------------------------------------------------

function show(view) {
  currentView = view;
  for (const section of document.querySelectorAll('.view')) {
    section.hidden = section.id !== `view-${view}`;
  }
  for (const button of el.nav.querySelectorAll('button')) {
    const isCurrent = button.dataset.view === view || (view === 'detail' && button.dataset.view === 'catalogue');
    button.setAttribute('aria-current', isCurrent ? 'true' : 'false');
  }
  el.main.scrollTop = 0;
  if (view === 'downloads') renderDownloads();
}

function renderGrid() {
  el.grid.innerHTML = apps
    .map(
      (a) => `
      <button class="card" type="button" data-id="${esc(a.id)}">
        <span class="card-top">
          <span class="glyph">${svg(a.glyph, 21)}</span>
          <span>
            <span class="card-name">${esc(a.name)}</span>
            <span class="card-tag">${esc(a.tagline)}</span>
          </span>
        </span>
        <span class="card-foot">
          <span class="replaces">vs ${esc(a.replaces)}</span>
          <span class="prices">
            <span class="was">${esc(a.theirPrice)}</span>
            <span class="now">$0</span>
          </span>
        </span>
        <span class="chip ${a.status === 'available' ? 'available' : ''}">${STATUS[a.status] || a.status}</span>
      </button>`
    )
    .join('');
}

function renderDetail(a) {
  const view = document.getElementById('view-detail');
  const p = progress.get(a.id);
  const available = a.status === 'available' && a.download;

  let action;
  if (!available) {
    action = '<button class="btn" disabled>Not yet available</button>';
  } else if (p && p.state === 'downloading') {
    // No inline style attribute here: the CSP blocks those, so the width is set
    // through the CSSOM once the markup is in the document.
    action =
      '<button class="btn secondary" id="cancel">Cancel</button>' +
      '<div class="meter"><span id="bar"></span></div>';
  } else if (p && p.state === 'ready') {
    action = '<button class="btn" id="run">Run installer</button>';
  } else {
    action = '<button class="btn" id="install">Download</button>';
  }

  const d = a.download || {};

  view.innerHTML = `
    <button class="back" type="button" id="back">&larr; Catalogue</button>
    <div class="detail-head">
      <span class="glyph-lg">${svg(a.glyph, 36)}</span>
      <div class="detail-title">
        <h2 tabindex="-1" id="detail-name">${esc(a.name)}</h2>
        <p>${esc(a.tagline)}</p>
        <span class="chip ${a.status === 'available' ? 'available' : ''}">${STATUS[a.status] || a.status}</span>
      </div>
      <div class="get">
        ${action}
        <small id="status-line">Free · no account · no telemetry</small>
      </div>
    </div>
    <div class="detail-body">
      <div class="prose">
        <h3>What it does</h3>
        ${a.description.map((x) => `<p>${esc(x)}</p>`).join('')}
        <h3>${available ? 'Features' : 'Planned features'}</h3>
        <ul class="features">${a.features.map((f) => `<li><span>${esc(f)}</span></li>`).join('')}</ul>
      </div>
      <div>
        <h3>Details</h3>
        <div class="spec-wrap">
          <table class="spec">
            <tbody>
              <tr><th scope="row">Status</th><td>${STATUS[a.status] || a.status}</td></tr>
              <tr><th scope="row">Version</th><td>${esc(d.version || '—')}</td></tr>
              <tr><th scope="row">Size</th><td>${formatBytes(Number(d.size) || 0)}</td></tr>
              <tr><th scope="row">Price</th><td>$0.00</td></tr>
              <tr><th scope="row">Replaces</th><td>${esc(a.replaces)}</td></tr>
              <tr><th scope="row">Their price</th><td>${esc(a.theirPrice)}</td></tr>
              <tr><th scope="row">Requires</th><td>${esc(a.requirements)}</td></tr>
              <tr><th scope="row">Telemetry</th><td>None</td></tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>`;

  view.querySelector('#back').addEventListener('click', () => {
    currentId = null;
    show('catalogue');
  });

  const install = view.querySelector('#install');
  if (install) install.addEventListener('click', () => startInstall(a.id));

  const cancel = view.querySelector('#cancel');
  if (cancel) cancel.addEventListener('click', () => window.nullprice.cancelInstall(a.id));

  const run = view.querySelector('#run');
  if (run) run.addEventListener('click', () => runInstaller(a.id));

  // Restore the meter position for a download already in flight.
  const bar = view.querySelector('#bar');
  if (bar && p && p.total) {
    bar.style.width = `${Math.round((p.received / p.total) * 100)}%`;
  }

  document.getElementById('detail-name').focus();
}

function renderDownloads() {
  const entries = [...progress.entries()].filter(([, p]) => p.state !== 'idle');

  if (entries.length === 0) {
    el.downloads.innerHTML =
      '<div class="empty">Nothing downloaded yet. Pick a tool from the catalogue.</div>';
    return;
  }

  el.downloads.innerHTML =
    '<div class="rows">' +
    entries
      .map(([id, p]) => {
        const a = find(id);
        const detail =
          p.state === 'downloading'
            ? `${formatBytes(p.received)}${p.total ? ` of ${formatBytes(p.total)}` : ''}`
            : p.state === 'ready'
              ? `verified · ${formatBytes(p.received)}`
              : esc(p.error || 'failed');
        return `
          <div class="row">
            <div>
              <div class="row-name">${esc(a ? a.name : id)}</div>
              <div class="row-detail">${detail}</div>
            </div>
            <div>${
              p.state === 'ready'
                ? `<button class="btn secondary" data-run="${esc(id)}">Run installer</button>`
                : ''
            }</div>
          </div>`;
      })
      .join('') +
    '</div>';

  for (const button of el.downloads.querySelectorAll('[data-run]')) {
    button.addEventListener('click', () => runInstaller(button.dataset.run));
  }
}

function updateDownloadCount() {
  const n = [...progress.values()].filter((p) => p.state === 'ready' || p.state === 'downloading').length;
  el.countDownloads.textContent = String(n);
}

// ---- actions --------------------------------------------------------------

async function startInstall(id) {
  progress.set(id, { state: 'downloading', received: 0, total: 0 });
  if (currentId === id) renderDetail(find(id));
  updateDownloadCount();

  try {
    await window.nullprice.startInstall(id);
  } catch (err) {
    const aborted = /abort/i.test(err.message || '');
    progress.set(id, {
      state: aborted ? 'idle' : 'failed',
      received: 0,
      total: 0,
      error: aborted ? null : cleanError(err.message),
    });
    if (currentId === id) {
      renderDetail(find(id));
      const line = document.getElementById('status-line');
      if (line && !aborted) line.textContent = cleanError(err.message);
    }
  }

  updateDownloadCount();
  if (currentView === 'downloads') renderDownloads();
}

async function runInstaller(id) {
  try {
    await window.nullprice.runInstaller(id);
  } catch (err) {
    const line = document.getElementById('status-line');
    if (line) line.textContent = cleanError(err.message);
  }
}

/** Electron prefixes IPC errors with its own noise; strip it so users see the real sentence. */
function cleanError(message) {
  return String(message || 'Something went wrong.').replace(/^Error invoking remote method '[^']+':\s*/, '');
}

// ---- wiring ---------------------------------------------------------------

el.grid.addEventListener('click', (event) => {
  const card = event.target.closest('.card');
  if (!card) return;
  currentId = card.dataset.id;
  renderDetail(find(currentId));
  show('detail');
});

el.nav.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-view]');
  if (!button) return;
  currentId = null;
  show(button.dataset.view);
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && currentView === 'detail') {
    currentId = null;
    show('catalogue');
  }
});

window.nullprice.onProgress(({ id, received, total }) => {
  const existing = progress.get(id) || {};
  progress.set(id, { ...existing, state: 'downloading', received, total });

  if (currentId === id) {
    const bar = document.getElementById('bar');
    if (bar && total) bar.style.width = `${Math.round((received / total) * 100)}%`;
    const line = document.getElementById('status-line');
    if (line) {
      line.textContent = total
        ? `${formatBytes(received)} of ${formatBytes(total)}`
        : formatBytes(received);
    }
  }

  if (currentView === 'downloads') renderDownloads();
});

window.nullprice.onReady(({ id, bytes }) => {
  progress.set(id, { state: 'ready', received: bytes, total: bytes });
  updateDownloadCount();
  if (currentId === id) renderDetail(find(id));
  if (currentView === 'downloads') renderDownloads();
});

(async function boot() {
  initTheme();

  try {
    const data = await window.nullprice.loadCatalogue();
    feed = data.feed;
    apps = data.apps;

    el.build.textContent = `catalogue rev. ${feed.updated}`;
    el.countCatalogue.textContent = String(apps.length);

    renderGrid();
    updateDownloadCount();
    show('catalogue');
  } catch (err) {
    el.grid.innerHTML = `<div class="empty">Could not load the catalogue.<br>${esc(
      cleanError(err.message)
    )}</div>`;
  }
})();
