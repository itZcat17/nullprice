'use strict';

const { execFile } = require('node:child_process');
const fsp = require('node:fs/promises');
const path = require('node:path');
const { promisify } = require('node:util');

const run = promisify(execFile);

const UNINSTALL_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall';

/**
 * The Windows half of installing: shortcuts via the Electron shell, and Add/Remove
 * Programs entries via reg.exe.
 *
 * HKCU rather than HKLM so no administrator prompt is needed. Windows shows per-user
 * entries in Settings > Apps exactly like machine-wide ones.
 */
function createWindowsPlatform({ app, shell }) {
  const root = path.join(app.getPath('appData'), 'Nullprice');

  return {
    root,

    desktopDir: app.getPath('desktop'),

    startMenuDir: path.join(
      app.getPath('appData'),
      'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Nullprice'
    ),

    /**
     * Windows caches shortcut icons aggressively, so an existing link is removed rather
     * than overwritten — otherwise an updated app keeps the old version's icon.
     */
    async writeShortcut(linkPath, { target, description }) {
      await fsp.rm(linkPath, { force: true });

      const ok = shell.writeShortcutLink(linkPath, 'create', {
        target,
        cwd: path.dirname(target),
        description: description || '',
        icon: target,
        iconIndex: 0,
      });

      if (!ok) throw new Error(`Could not create the shortcut at ${linkPath}.`);
    },

    async removeShortcut(linkPath) {
      if (linkPath) await fsp.rm(linkPath, { force: true });
    },

    uninstallCommandFor(id) {
      // In development the "app" is Electron itself, so the command has to carry the
      // project path or the uninstaller would launch a bare Electron.
      return app.isPackaged
        ? `"${process.execPath}" --uninstall ${id}`
        : `"${process.execPath}" "${app.getAppPath()}" --uninstall ${id}`;
    },

    async writeUninstallEntry(id, info) {
      const key = `${UNINSTALL_KEY}\\Nullprice.${id}`;

      const values = [
        ['DisplayName', 'REG_SZ', info.displayName],
        ['DisplayVersion', 'REG_SZ', info.version],
        ['Publisher', 'REG_SZ', info.publisher],
        ['InstallLocation', 'REG_SZ', info.installLocation],
        ['DisplayIcon', 'REG_SZ', info.displayIcon],
        ['UninstallString', 'REG_SZ', info.uninstallCommand],
        ['NoModify', 'REG_DWORD', '1'],
        ['NoRepair', 'REG_DWORD', '1'],
        ['EstimatedSize', 'REG_DWORD', String(info.estimatedSizeKb || 0)],
      ];

      for (const [name, type, data] of values) {
        await run('reg', ['add', key, '/v', name, '/t', type, '/d', String(data), '/f']);
      }
    },

    async removeUninstallEntry(id) {
      const key = `${UNINSTALL_KEY}\\Nullprice.${id}`;
      try {
        await run('reg', ['delete', key, '/f']);
      } catch {
        // reg.exe exits non-zero when the key is already gone, which is the desired state.
      }
    },
  };
}

module.exports = { createWindowsPlatform };
