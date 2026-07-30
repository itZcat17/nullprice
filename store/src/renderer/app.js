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

let apps = [];
let feed = null;
let installed = {};
let updates = {};
const busy = new Map();

let currentId = null;
let currentView = 'catalogue';

const el = {
  grid: document.getElementById('grid'),
  nav: document.getElementById('nav'),
  build: document.getElementById('build'),
  countCatalogue: document.getElementById('count-catalogue'),
  countInstalled: document.getElementById('count-installed'),
  installedList: document.getElementById('installed'),
  main: document.getElementById('main'),
  swatches: document.getElementById('swatches'),
  themeName: document.getElementById('theme-name'),
  checkButton: document.getElementById('check-updates'),
  checkState: document.getElementById('check-state'),
  storeUpdate: document.getElementById('store-update'),
};

// ---- theme ----------------------------------------------------------------

function applyTheme(name) {
  if (name) document.documentElement.setAttribute('data-theme', name);
  else document.documentElement.removeAttribute('data-theme');

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
    // Locked-down profile; fall back to following the OS.
  }

  applyTheme(THEMES[stored] ? stored : null);

  el.swatches.addEventListener('click', (event) => {
    const button = event.target.closest('.swatch');
    if (!button) return;
    applyTheme(button.dataset.swatch);
    try {
      localStorage.setItem(THEME_KEY, button.dataset.swatch);
    } catch {
      // Not remembering the choice is not worth interrupting anyone over.
    }
  });

  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (!document.documentElement.hasAttribute('data-theme')) applyTheme(null);
  });
}

// ---- helpers --------------------------------------------------------------

function svg(name, size) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" aria-hidden="true">${GLYPHS[name] || ''}</svg>`;
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

function cleanError(message) {
  return String(message || 'Something went wrong.')
    .replace(/^Error invoking remote method '[^']+':\s*/, '')
    .replace(/^Error:\s*/, '');
}

/** One place that decides what state an app is in, so cards and detail never disagree. */
function stateOf(id) {
  const app = find(id);
  const job = busy.get(id);
  if (job) return job.phase;

  if (installed[id]) {
    return updates[id] && updates[id].status === 'update-available' ? 'update-available' : 'installed';
  }

  return app && app.status === 'available' ? 'installable' : 'unavailable';
}

// ---- views ----------------------------------------------------------------

function show(view) {
  currentView = view;
  for (const section of document.querySelectorAll('.view')) {
    section.hidden = section.id !== `view-${view}`;
  }
  for (const button of el.nav.querySelectorAll('button')) {
    const isCurrent =
      button.dataset.view === view || (view === 'detail' && button.dataset.view === 'catalogue');
    button.setAttribute('aria-current', isCurrent ? 'true' : 'false');
  }
  el.main.scrollTop = 0;
  if (view === 'installed') renderInstalled();
}

function renderGrid() {
  el.grid.innerHTML = apps
    .map((a) => {
      const state = stateOf(a.id);
      const badge =
        state === 'update-available'
          ? '<span class="chip update">Update</span>'
          : state === 'installed'
            ? '<span class="chip installed">Installed</span>'
            : `<span class="chip ${a.status === 'planned' ? '' : a.status}">${STATUS[a.status] || a.status}</span>`;

      return `
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
        ${badge}
      </button>`;
    })
    .join('');
}

function actionMarkup(a) {
  const state = stateOf(a.id);
  const job = busy.get(a.id);

  if (state === 'downloading' || state === 'installing') {
    const label = state === 'downloading' ? 'Cancel' : 'Installing…';
    const disabled = state === 'installing' ? ' disabled' : '';
    return (
      `<button class="btn secondary" id="cancel"${disabled}>${label}</button>` +
      '<div class="meter"><span id="bar"></span></div>'
    );
  }

  if (state === 'update-available') {
    const to = updates[a.id] ? updates[a.id].latestVersion : '';
    return (
      `<button class="btn" id="install">Update to ${esc(to)}</button>` +
      '<button class="btn secondary" id="launch">Open</button>' +
      '<button class="btn secondary" id="uninstall">Uninstall</button>'
    );
  }

  if (state === 'installed') {
    return (
      '<button class="btn" id="launch">Open</button>' +
      '<button class="btn secondary" id="uninstall">Uninstall</button>'
    );
  }

  if (state === 'installable') return '<button class="btn" id="install">Install</button>';

  return '<button class="btn" disabled>Not yet available</button>';
}

function renderDetail(a) {
  const view = document.getElementById('view-detail');
  const record = installed[a.id];
  const info = updates[a.id];
  const d = a.download || {};
  const state = stateOf(a.id);

  const latestVersion = info && info.latestVersion ? info.latestVersion : d.version || '—';

  view.innerHTML = `
    <button class="back" type="button" id="back">&larr; Catalogue</button>
    <div class="detail-head">
      <span class="glyph-lg">${svg(a.glyph, 36)}</span>
      <div class="detail-title">
        <h2 tabindex="-1" id="detail-name">${esc(a.name)}</h2>
        <p>${esc(a.tagline)}</p>
        ${
          state === 'update-available'
            ? `<span class="chip update">Update available — ${esc(latestVersion)}</span>`
            : state === 'installed'
              ? `<span class="chip installed">Installed — ${esc(record.version)}</span>`
              : `<span class="chip ${a.status === 'planned' ? '' : a.status}">${STATUS[a.status] || a.status}</span>`
        }
      </div>
      <div class="get">
        ${actionMarkup(a)}
        <small id="status-line">Free · no account · no telemetry</small>
      </div>
    </div>
    <div class="detail-body">
      <div class="prose">
        ${
          info && info.status === 'update-available' && info.notes
            ? `<h3>What changed in ${esc(info.latestVersion)}</h3><p class="notes">${esc(info.notes)}</p>`
            : ''
        }
        <h3>What it does</h3>
        ${a.description.map((x) => `<p>${esc(x)}</p>`).join('')}
        <h3>${a.status === 'available' ? 'Features' : 'Planned features'}</h3>
        <ul class="features">${a.features.map((f) => `<li><span>${esc(f)}</span></li>`).join('')}</ul>
      </div>
      <div>
        <h3>Details</h3>
        <div class="spec-wrap">
          <table class="spec">
            <tbody>
              <tr><th scope="row">Installed</th><td>${record ? esc(record.version) : 'No'}</td></tr>
              <tr><th scope="row">Latest</th><td>${esc(latestVersion)}</td></tr>
              <tr><th scope="row">Size</th><td>${formatBytes(Number(d.size) || 0)}</td></tr>
              <tr><th scope="row">Price</th><td>$0.00</td></tr>
              <tr><th scope="row">Replaces</th><td>${esc(a.replaces)}</td></tr>
              <tr><th scope="row">Their price</th><td>${esc(a.theirPrice)}</td></tr>
              <tr><th scope="row">Updates</th><td>${a.updates ? esc(a.updates.provider) : 'catalogue'}</td></tr>
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

  wire(view, '#install', () => startInstall(a.id));
  wire(view, '#cancel', () => window.nullprice.cancelInstall(a.id));
  wire(view, '#launch', () => launch(a.id));
  wire(view, '#uninstall', () => uninstall(a.id));

  const job = busy.get(a.id);
  const bar = view.querySelector('#bar');
  if (bar && job && job.total) {
    bar.style.width = `${Math.round((job.received / job.total) * 100)}%`;
  }

  document.getElementById('detail-name').focus();
}

function wire(root, selector, handler) {
  const node = root.querySelector(selector);
  if (node) node.addEventListener('click', handler);
}

function renderInstalled() {
  const records = Object.values(installed);

  if (records.length === 0) {
    el.installedList.innerHTML =
      '<div class="empty">Nothing installed yet. Pick a tool from the catalogue.</div>';
    return;
  }

  el.installedList.innerHTML =
    '<div class="rows">' +
    records
      .map((r) => {
        const info = updates[r.id];
        const hasUpdate = info && info.status === 'update-available';
        return `
          <div class="row">
            <div>
              <div class="row-name">${esc(r.name)}</div>
              <div class="row-detail">
                version ${esc(r.version)}${hasUpdate ? ` · ${esc(info.latestVersion)} available` : ''}
              </div>
            </div>
            <div class="row-actions">
              ${hasUpdate ? `<button class="btn" data-update="${esc(r.id)}">Update</button>` : ''}
              <button class="btn secondary" data-launch="${esc(r.id)}">Open</button>
              <button class="btn secondary" data-reveal="${esc(r.id)}">Show files</button>
              <button class="btn secondary" data-uninstall="${esc(r.id)}">Uninstall</button>
            </div>
          </div>`;
      })
      .join('') +
    '</div>';

  bindAll('[data-update]', 'update', startInstall);
  bindAll('[data-launch]', 'launch', launch);
  bindAll('[data-reveal]', 'reveal', (id) => window.nullprice.reveal(id).catch(reportError));
  bindAll('[data-uninstall]', 'uninstall', uninstall);
}

function bindAll(selector, key, handler) {
  for (const button of el.installedList.querySelectorAll(selector)) {
    button.addEventListener('click', () => handler(button.dataset[key]));
  }
}

function refreshCounts() {
  el.countCatalogue.textContent = String(apps.length);
  el.countInstalled.textContent = String(Object.keys(installed).length);
}

function repaint() {
  refreshCounts();
  renderGrid();
  if (currentView === 'installed') renderInstalled();
  if (currentView === 'detail' && currentId) renderDetail(find(currentId));
}

// ---- actions --------------------------------------------------------------

async function startInstall(id) {
  busy.set(id, { phase: 'downloading', received: 0, total: 0 });
  repaint();

  try {
    await window.nullprice.install(id);
    installed = await window.nullprice.listInstalled();

    // The freshly installed version is by definition current.
    if (updates[id]) updates[id] = { ...updates[id], status: 'up-to-date' };
  } catch (err) {
    if (!/abort/i.test(err.message || '')) reportError(err);
  } finally {
    busy.delete(id);
    repaint();
  }
}

async function uninstall(id) {
  const app = find(id);
  if (!confirm(`Uninstall ${app ? app.name : id}?\n\nIts shortcuts and files will be removed.`)) {
    return;
  }

  try {
    await window.nullprice.uninstall(id);
    installed = await window.nullprice.listInstalled();
  } catch (err) {
    reportError(err);
  }
  repaint();
}

async function launch(id) {
  try {
    await window.nullprice.launch(id);
  } catch (err) {
    reportError(err);
  }
}

async function checkUpdates() {
  el.checkButton.disabled = true;
  el.checkState.textContent = 'Checking…';

  try {
    const results = await window.nullprice.checkUpdates();
    updates = Object.fromEntries(results.map((r) => [r.id, r]));

    const pending = results.filter((r) => r.status === 'update-available').length;
    const failed = results.filter((r) => r.status === 'check-failed').length;

    el.checkState.textContent = pending
      ? `${pending} update${pending === 1 ? '' : 's'} available`
      : failed
        ? `${failed} could not be checked`
        : 'Everything is up to date';
  } catch (err) {
    el.checkState.textContent = cleanError(err.message);
  } finally {
    el.checkButton.disabled = false;
    repaint();
  }
}

function reportError(err) {
  const line = document.getElementById('status-line');
  const message = cleanError(err.message);
  if (line) line.textContent = message;
  else el.checkState.textContent = message;
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

el.checkButton.addEventListener('click', checkUpdates);

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && currentView === 'detail') {
    currentId = null;
    show('catalogue');
  }
});

window.nullprice.onProgress(({ id, received, total }) => {
  const job = busy.get(id) || { phase: 'downloading' };
  busy.set(id, { ...job, received, total });

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
});

window.nullprice.onPhase(({ id, phase, version }) => {
  const job = busy.get(id) || {};
  busy.set(id, { ...job, phase });

  if (currentId === id) {
    const line = document.getElementById('status-line');
    if (line && phase === 'installing') {
      line.textContent = `Installing ${version} and creating shortcuts…`;
    }
  }

  repaint();
});

window.nullprice.onDone(({ id, verified }) => {
  if (currentId === id) {
    const line = document.getElementById('status-line');
    if (line) {
      line.textContent = verified
        ? 'Installed and verified against its published checksum.'
        : 'Installed. No checksum was published for this release, so it was not verified.';
    }
  }
});

window.nullprice.onStoreUpdateReady(() => {
  el.storeUpdate.hidden = false;
});

(async function boot() {
  initTheme();

  try {
    const data = await window.nullprice.loadCatalogue();
    feed = data.feed;
    apps = data.apps;
    installed = data.installed || {};

    el.build.textContent = `catalogue rev. ${feed.updated}`;
    repaint();
    show('catalogue');

    // Checked on open rather than on demand only, so an out-of-date tool is visible
    // without anyone having to think to look.
    checkUpdates();
  } catch (err) {
    el.grid.innerHTML = `<div class="empty">Could not load the catalogue.<br>${esc(
      cleanError(err.message)
    )}</div>`;
  }
})();
