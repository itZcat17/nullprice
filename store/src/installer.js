'use strict';

const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');

/**
 * Installs catalogue apps as real, per-user Windows applications.
 *
 * Per-user rather than per-machine on purpose: installing under LocalAppData needs no
 * administrator prompt, which matters for a set of free tools people are trying out. The
 * cost is that an app is installed for one account rather than the whole machine.
 *
 * An install is four things, and a failed install must leave none of them behind:
 *   1. the binary, in a version-stamped folder
 *   2. a Desktop shortcut
 *   3. a Start Menu shortcut, under a Nullprice folder
 *   4. an Add/Remove Programs entry, so Windows can uninstall it the normal way
 *
 * Everything Windows-specific is injected rather than imported, so the sequencing and the
 * rollback are testable without writing to a real desktop or a real registry.
 */
function createInstaller(platform) {
  const root = platform.root;
  const appsDir = path.join(root, 'apps');
  const ledgerPath = path.join(root, 'installed.json');

  function versionDir(id, version) {
    return path.join(appsDir, id, version);
  }

  async function readLedger() {
    try {
      const raw = await fsp.readFile(ledgerPath, 'utf8');
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
      // Missing or corrupt ledger means nothing is known to be installed. Recovering by
      // starting fresh is better than refusing to open the store.
      return {};
    }
  }

  async function writeLedger(ledger) {
    await fsp.mkdir(root, { recursive: true });
    const temp = `${ledgerPath}.tmp`;
    await fsp.writeFile(temp, JSON.stringify(ledger, null, 2), 'utf8');
    await fsp.rm(ledgerPath, { force: true });
    await fsp.rename(temp, ledgerPath);
  }

  async function list() {
    return readLedger();
  }

  async function installedVersion(id) {
    const ledger = await readLedger();
    return ledger[id] ? ledger[id].version : null;
  }

  /**
   * @param {object} app             catalogue entry
   * @param {object} release         the release being installed
   * @param {string} downloadedPath  a verified file produced by the downloader
   */
  async function install(app, release, downloadedPath) {
    if (!app || !app.id) throw new Error('Cannot install without a catalogue entry.');
    if (!release || !release.version) throw new Error(`${app.name} has no release to install.`);

    await fsp.access(downloadedPath, fs.constants.R_OK);

    const target = versionDir(app.id, release.version);
    const exePath = path.join(target, release.filename);
    const desktopLink = path.join(platform.desktopDir, `${app.name}.lnk`);
    const startMenuLink = path.join(platform.startMenuDir, `${app.name}.lnk`);

    // Tracks what actually happened, so a failure halfway through can be undone.
    const done = { dir: false, desktop: false, startMenu: false, registry: false };

    try {
      await fsp.mkdir(target, { recursive: true });
      done.dir = true;
      await fsp.copyFile(downloadedPath, exePath);

      await fsp.mkdir(platform.desktopDir, { recursive: true });
      await platform.writeShortcut(desktopLink, {
        target: exePath,
        description: app.tagline || app.name,
      });
      done.desktop = true;

      await fsp.mkdir(platform.startMenuDir, { recursive: true });
      await platform.writeShortcut(startMenuLink, {
        target: exePath,
        description: app.tagline || app.name,
      });
      done.startMenu = true;

      await platform.writeUninstallEntry(app.id, {
        displayName: app.name,
        version: release.version,
        publisher: 'Nullprice',
        installLocation: target,
        displayIcon: exePath,
        uninstallCommand: platform.uninstallCommandFor(app.id),
        estimatedSizeKb: Math.round((Number(release.size) || 0) / 1024),
      });
      done.registry = true;

      const ledger = await readLedger();
      ledger[app.id] = {
        id: app.id,
        name: app.name,
        version: release.version,
        exePath,
        installedAt: new Date().toISOString(),
        desktopLink,
        startMenuLink,
      };
      await writeLedger(ledger);

      // Only once the new version is fully recorded is it safe to drop the old ones.
      await pruneOtherVersions(app.id, release.version);

      return ledger[app.id];
    } catch (err) {
      await rollback(app.id, target, desktopLink, startMenuLink, done);
      throw err;
    }
  }

  async function rollback(id, target, desktopLink, startMenuLink, done) {
    // Best effort throughout: the original failure is the one worth reporting.
    if (done.registry) await safe(() => platform.removeUninstallEntry(id));
    if (done.startMenu) await safe(() => platform.removeShortcut(startMenuLink));
    if (done.desktop) await safe(() => platform.removeShortcut(desktopLink));
    if (done.dir) await safe(() => fsp.rm(target, { recursive: true, force: true }));
  }

  async function pruneOtherVersions(id, keepVersion) {
    const dir = path.join(appsDir, id);
    let entries;
    try {
      entries = await fsp.readdir(dir, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      if (!entry.isDirectory() || entry.name === keepVersion) continue;
      await safe(() => fsp.rm(path.join(dir, entry.name), { recursive: true, force: true }));
    }
  }

  async function uninstall(id) {
    const ledger = await readLedger();
    const record = ledger[id];

    // Remove the traces even when the ledger has lost track, so a half-installed app
    // can still be cleaned up rather than becoming permanent.
    await safe(() => platform.removeUninstallEntry(id));

    if (record) {
      await safe(() => platform.removeShortcut(record.desktopLink));
      await safe(() => platform.removeShortcut(record.startMenuLink));
    }

    await safe(() => fsp.rm(path.join(appsDir, id), { recursive: true, force: true }));

    delete ledger[id];
    await writeLedger(ledger);

    return true;
  }

  return { install, uninstall, list, installedVersion, ledgerPath, appsDir };
}

async function safe(action) {
  try {
    await action();
  } catch {
    // Intentionally swallowed — see call sites.
  }
}

module.exports = { createInstaller };
