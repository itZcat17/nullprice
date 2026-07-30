'use strict';

const { app, BrowserWindow, ipcMain, shell, dialog } = require('electron');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const { downloadRelease, resolveCatalogue } = require('./downloader');

/**
 * Nullprice store — main process.
 *
 * Everything here runs with full Node privileges, so the renderer never gets direct
 * access to any of it. All the renderer can do is ask for a catalogue entry by id and
 * ask to install one; it can never name an arbitrary URL or path to fetch or execute.
 * That constraint is the whole reason the download logic lives up here.
 */

const CATALOGUE_FILE = 'catalogue.json';

/** Where downloaded installers land. Kept out of the user's Downloads folder deliberately. */
function downloadDir() {
  return path.join(app.getPath('userData'), 'installers');
}

/** Packaged builds read the catalogue from resources; dev reads it from the repo. */
function cataloguePath() {
  return app.isPackaged
    ? path.join(process.resourcesPath, CATALOGUE_FILE)
    : path.join(__dirname, '..', CATALOGUE_FILE);
}

let catalogue = null;
const inFlight = new Map();

async function loadCatalogue() {
  const file = cataloguePath();
  const parsed = JSON.parse(await fsp.readFile(file, 'utf8'));
  catalogue = resolveCatalogue(parsed, path.dirname(file));
  return catalogue;
}

function findApp(id) {
  if (!catalogue) return null;
  return catalogue.apps.find((a) => a.id === id) || null;
}

// ---- ipc -----------------------------------------------------------------

ipcMain.handle('catalogue:load', async () => {
  if (!catalogue) await loadCatalogue();
  return catalogue;
});

ipcMain.handle('install:start', async (event, id) => {
  const entry = findApp(id);
  if (!entry) throw new Error('Unknown application.');
  if (!entry.download) throw new Error(`${entry.name} is not available to download yet.`);
  if (inFlight.has(id)) throw new Error(`${entry.name} is already downloading.`);

  const controller = new AbortController();
  inFlight.set(id, controller);

  const send = (channel, payload) => {
    if (!event.sender.isDestroyed()) event.sender.send(channel, payload);
  };

  try {
    const result = await downloadRelease(
      entry.download,
      downloadDir(),
      ({ received, total }) => send('install:progress', { id, received, total }),
      controller.signal
    );

    send('install:ready', { id, path: result.path, bytes: result.bytes });
    return result;
  } finally {
    inFlight.delete(id);
  }
});

ipcMain.handle('install:cancel', async (_event, id) => {
  const controller = inFlight.get(id);
  if (controller) controller.abort();
  return true;
});

/**
 * Hands a verified installer to the shell. Only ever a path this process produced —
 * the renderer cannot pass one in.
 */
ipcMain.handle('install:run', async (_event, id) => {
  const entry = findApp(id);
  if (!entry || !entry.download) throw new Error('Unknown application.');

  const target = path.join(downloadDir(), entry.download.filename);
  await fsp.access(target, fs.constants.R_OK);

  const result = await shell.openPath(target);
  if (result) throw new Error(result);
  return true;
});

ipcMain.handle('install:reveal', async () => {
  const dir = downloadDir();
  await fsp.mkdir(dir, { recursive: true });
  shell.openPath(dir);
  return dir;
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

// ---- window --------------------------------------------------------------

function createWindow() {
  const win = new BrowserWindow({
    width: 1180,
    height: 780,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: '#eef0f1',
    title: 'Nullprice',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  win.setMenuBarVisibility(false);
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));

  // Nothing in this app should ever spawn a second window.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) shell.openExternal(url);
    return { action: 'deny' };
  });

  return win;
}

app.whenReady().then(async () => {
  try {
    await loadCatalogue();
  } catch (err) {
    dialog.showErrorBox(
      'Catalogue could not be read',
      `Nullprice could not load its catalogue.\n\n${err.message}`
    );
  }

  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
