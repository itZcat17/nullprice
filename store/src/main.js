'use strict';

const { app, BrowserWindow, ipcMain, shell, dialog } = require('electron');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');

const { downloadRelease, resolveCatalogue } = require('./downloader');
const { createInstaller } = require('./installer');
const { createWindowsPlatform } = require('./windows-platform');
const releases = require('./releases');

/**
 * Nullprice store — main process.
 *
 * Everything privileged lives here. The renderer can only name catalogue ids; it can
 * never hand over a URL to fetch or a path to execute, which is why the download and
 * install logic is up here rather than behind a generic bridge.
 */

const CATALOGUE_FILE = 'catalogue.json';

let catalogue = null;
let installer = null;
let platform = null;
let mainWindow = null;

const inFlight = new Map();

function cataloguePath() {
  return app.isPackaged
    ? path.join(process.resourcesPath, CATALOGUE_FILE)
    : path.join(__dirname, '..', CATALOGUE_FILE);
}

function downloadDir() {
  return path.join(app.getPath('userData'), 'downloads');
}

async function loadCatalogue() {
  const file = cataloguePath();
  const parsed = JSON.parse(await fsp.readFile(file, 'utf8'));
  catalogue = resolveCatalogue(parsed, path.dirname(file));
  return catalogue;
}

function findApp(id) {
  return catalogue ? catalogue.apps.find((a) => a.id === id) || null : null;
}

// ---- uninstall entry point ------------------------------------------------

/**
 * Windows invokes this through the Add/Remove Programs entry, which is why the store can
 * be launched as a headless uninstaller rather than only as a window.
 */
function uninstallTargetFromArgv(argv) {
  const index = argv.indexOf('--uninstall');
  return index >= 0 && argv[index + 1] ? argv[index + 1] : null;
}

async function runHeadlessUninstall(id) {
  try {
    await installer.uninstall(id);
  } catch (err) {
    dialog.showErrorBox('Uninstall failed', err.message);
  }
  app.exit(0);
}

// ---- ipc -----------------------------------------------------------------

ipcMain.handle('catalogue:load', async () => {
  if (!catalogue) await loadCatalogue();
  return { ...catalogue, installed: await installer.list() };
});

ipcMain.handle('installed:list', async () => installer.list());

/**
 * Checks every app that has an update source. One repo being unreachable must not stop
 * the others being checked, so failures are reported per app rather than thrown.
 */
ipcMain.handle('updates:check', async () => {
  if (!catalogue) await loadCatalogue();
  const ledger = await installer.list();

  const results = await Promise.all(
    catalogue.apps.map(async (entry) => {
      const installedVersion = ledger[entry.id] ? ledger[entry.id].version : null;

      try {
        const latest = await releases.findLatest(entry);
        if (!latest) {
          return { id: entry.id, status: 'unavailable', installedVersion };
        }

        return {
          id: entry.id,
          status: releases.statusFor(installedVersion, latest.version),
          installedVersion,
          latestVersion: latest.version,
          notes: latest.notes,
          source: latest.source,
        };
      } catch (err) {
        return { id: entry.id, status: 'check-failed', installedVersion, error: err.message };
      }
    })
  );

  return results;
});

/**
 * Download, verify, then install. One handler rather than three so a half-finished
 * sequence can never be left for the renderer to reason about.
 */
ipcMain.handle('install:start', async (event, id) => {
  const entry = findApp(id);
  if (!entry) throw new Error('Unknown application.');
  if (inFlight.has(id)) throw new Error(`${entry.name} is already being installed.`);

  const controller = new AbortController();
  inFlight.set(id, controller);

  const send = (channel, payload) => {
    if (!event.sender.isDestroyed()) event.sender.send(channel, payload);
  };

  try {
    const latest = await releases.findLatest(entry, { signal: controller.signal });
    if (!latest) throw new Error(`${entry.name} is not available to download yet.`);

    send('install:phase', { id, phase: 'downloading', version: latest.version });

    const downloaded = await downloadRelease(
      latest.release,
      downloadDir(),
      ({ received, total }) => send('install:progress', { id, received, total }),
      controller.signal
    );

    send('install:phase', { id, phase: 'installing', version: latest.version });

    const record = await installer.install(entry, latest.release, downloaded.path);

    // The staged download is disposable once the binary is in place.
    await fsp.rm(downloaded.path, { force: true });

    send('install:done', { id, record, verified: downloaded.verified });
    return record;
  } finally {
    inFlight.delete(id);
  }
});

ipcMain.handle('install:cancel', async (_event, id) => {
  const controller = inFlight.get(id);
  if (controller) controller.abort();
  return true;
});

ipcMain.handle('install:uninstall', async (_event, id) => {
  const entry = findApp(id);
  if (!entry) throw new Error('Unknown application.');
  return installer.uninstall(id);
});

/** Launches an installed app. Only ever a path this process recorded. */
ipcMain.handle('app:launch', async (_event, id) => {
  const ledger = await installer.list();
  const record = ledger[id];
  if (!record) throw new Error('That application is not installed.');

  await fsp.access(record.exePath, fs.constants.R_OK);

  const result = await shell.openPath(record.exePath);
  if (result) throw new Error(result);
  return true;
});

ipcMain.handle('app:reveal', async (_event, id) => {
  const ledger = await installer.list();
  const record = ledger[id];
  if (!record) throw new Error('That application is not installed.');

  shell.showItemInFolder(record.exePath);
  return true;
});

ipcMain.handle('shell:external', async (_event, url) => {
  // Only http(s) may leave the app, so a catalogue entry can never launch a local handler.
  const parsed = new URL(url);
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
    throw new Error('Refused to open a non-web link.');
  }
  await shell.openExternal(url);
  return true;
});

// ---- store self-update ----------------------------------------------------

/**
 * The store updates itself the same way the tools do, through GitHub releases.
 * electron-updater is loaded lazily and failures are non-fatal: being unable to check for
 * an update is never a reason to stop someone using the app.
 */
function startSelfUpdate() {
  if (!app.isPackaged) return;

  try {
    const { autoUpdater } = require('electron-updater');
    autoUpdater.autoDownload = true;
    autoUpdater.autoInstallOnAppQuit = true;

    autoUpdater.on('update-downloaded', (info) => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send('store:update-ready', { version: info.version });
      }
    });

    autoUpdater.on('error', (err) => {
      console.warn('Store update check failed:', err.message);
    });

    autoUpdater.checkForUpdates();
  } catch (err) {
    console.warn('Self-update unavailable:', err.message);
  }
}

// ---- window --------------------------------------------------------------

function createWindow() {
  const win = new BrowserWindow({
    width: 1180,
    height: 800,
    minWidth: 940,
    minHeight: 620,
    backgroundColor: '#eef0f1',
    title: 'Nullprice',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  win.setMenuBarVisibility(false);
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
  win.once('ready-to-show', () => win.show());

  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) shell.openExternal(url);
    return { action: 'deny' };
  });

  return win;
}

// A second launch should focus the existing window, not open a rival copy that would
// fight over the same install ledger.
if (!app.requestSingleInstanceLock()) {
  app.exit(0);
} else {
  app.on('second-instance', () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(async () => {
    platform = createWindowsPlatform({ app, shell });
    installer = createInstaller(platform);

    const uninstallId = uninstallTargetFromArgv(process.argv);
    if (uninstallId) {
      await runHeadlessUninstall(uninstallId);
      return;
    }

    try {
      await loadCatalogue();
    } catch (err) {
      dialog.showErrorBox(
        'Catalogue could not be read',
        `Nullprice could not load its catalogue.\n\n${err.message}`
      );
    }

    mainWindow = createWindow();
    startSelfUpdate();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) mainWindow = createWindow();
    });
  });
}

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
