'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');

const versions = require('../src/versions');
const releases = require('../src/releases');
const { createInstaller } = require('../src/installer');

// ---- versions -------------------------------------------------------------

test('parses ordinary and prefixed versions', () => {
  assert.deepEqual(versions.parse('1.2.3'), { major: 1, minor: 2, patch: 3, prerelease: [] });
  assert.deepEqual(versions.parse('v1.2.3'), { major: 1, minor: 2, patch: 3, prerelease: [] });
  assert.equal(versions.parse('nonsense'), null);
  assert.equal(versions.parse(undefined), null);
});

test('orders releases correctly', () => {
  assert.equal(versions.compare('1.0.0', '1.0.0'), 0);
  assert.equal(versions.compare('1.0.1', '1.0.0'), 1);
  assert.equal(versions.compare('1.1.0', '1.0.9'), 1);
  assert.equal(versions.compare('2.0.0', '1.99.99'), 1);
  assert.equal(versions.compare('v1.0.0', '1.0.0'), 0);
});

test('a prerelease is older than the release it precedes', () => {
  assert.equal(versions.compare('1.0.0-beta', '1.0.0'), -1);
  assert.equal(versions.compare('1.0.0-beta.1', '1.0.0-beta.2'), -1);
  assert.equal(versions.compare('1.0.0-alpha', '1.0.0-beta'), -1);
  assert.equal(versions.compare('1.0.0-2', '1.0.0-11'), -1);
});

test('a malformed version can never look like an upgrade', () => {
  // Otherwise a bad tag on the server would nag every user forever.
  assert.equal(versions.isNewer('garbage', '1.0.0'), false);
  assert.equal(versions.isNewer('1.0.1', '1.0.0'), true);
});

test('cleans a tag for display', () => {
  assert.equal(versions.clean('v2.3.4'), '2.3.4');
  assert.equal(versions.clean('2.3.4-rc.1'), '2.3.4-rc.1');
});

// ---- release lookup -------------------------------------------------------

function githubApp(overrides = {}) {
  return {
    id: 'ferry',
    name: 'Ferry',
    tagline: 'Verified copy',
    updates: { provider: 'github', owner: 'someone', repo: 'nullprice-ferry' },
    download: {
      version: '0.1.0',
      kind: 'installer',
      filename: 'Ferry-0.1.0-portable.exe',
      url: './feed/Ferry-0.1.0-portable.exe',
      sha256: 'a'.repeat(64),
      size: 100,
    },
    ...overrides,
  };
}

function fakeFetch(status, payload) {
  return async () => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => payload,
  });
}

test('uses the pinned catalogue release when no provider is configured', async () => {
  const app = githubApp({ updates: null });
  const latest = await releases.findLatest(app);

  assert.equal(latest.version, '0.1.0');
  assert.equal(latest.source, 'catalogue');
});

test('finds a newer GitHub release and maps it to a downloadable release', async () => {
  const payload = {
    tag_name: 'v0.2.0',
    body: 'Fixed a thing.',
    assets: [
      { name: 'Ferry-0.2.0-setup.exe', browser_download_url: 'https://example/f.exe', size: 5000 },
      { name: 'Ferry-0.2.0-setup.exe.sha256', browser_download_url: 'https://example/f.sha256', size: 64 },
    ],
  };

  const latest = await releases.findLatest(githubApp(), { fetchImpl: fakeFetch(200, payload) });

  assert.equal(latest.source, 'github');
  assert.equal(latest.version, '0.2.0');
  assert.equal(latest.release.filename, 'Ferry-0.2.0-setup.exe');
  assert.equal(latest.release.url, 'https://example/f.exe');
  assert.equal(latest.release.sha256Url, 'https://example/f.sha256');
  assert.equal(latest.notes, 'Fixed a thing.');
});

test('a repo with no releases falls back to the pinned release', async () => {
  const latest = await releases.findLatest(githubApp(), { fetchImpl: fakeFetch(404, {}) });

  assert.equal(latest.source, 'catalogue');
  assert.equal(latest.version, '0.1.0');
});

test('drafts and prereleases are ignored', async () => {
  const draft = { tag_name: 'v9.0.0', draft: true, assets: [] };
  const pre = { tag_name: 'v9.0.0', prerelease: true, assets: [] };

  for (const payload of [draft, pre]) {
    const latest = await releases.findLatest(githubApp(), { fetchImpl: fakeFetch(200, payload) });
    assert.equal(latest.version, '0.1.0', 'must not offer an unpublished release');
  }
});

test('a GitHub error is surfaced rather than silently swallowed', async () => {
  await assert.rejects(
    () => releases.findLatest(githubApp(), { fetchImpl: fakeFetch(500, {}) }),
    /GitHub returned 500/
  );
});

test('asset patterns select the right file', () => {
  const assets = [
    { name: 'notes.txt' },
    { name: 'Batch-1.0.0-setup.exe' },
    { name: 'Ferry-1.0.0-setup.exe' },
    { name: 'Ferry-1.0.0-setup.exe.sha256' },
  ];

  assert.equal(releases.pickAsset(assets, 'Ferry-*-setup.exe').name, 'Ferry-1.0.0-setup.exe');
  assert.equal(releases.pickAsset(assets, null).name, 'Batch-1.0.0-setup.exe');
  assert.equal(releases.pickAsset([{ name: 'a.sha256' }], null), null);
});

test('reports install status against what is available', () => {
  assert.equal(releases.statusFor(null, '1.0.0'), 'not-installed');
  assert.equal(releases.statusFor('1.0.0', '1.0.0'), 'up-to-date');
  assert.equal(releases.statusFor('1.0.0', '1.1.0'), 'update-available');
  assert.equal(releases.statusFor('2.0.0', '1.0.0'), 'up-to-date');
});

// ---- installer ------------------------------------------------------------

async function sandbox() {
  const dir = await fsp.mkdtemp(path.join(os.tmpdir(), 'nullprice-install-'));

  const shortcuts = new Map();
  const registry = new Map();

  const platform = {
    root: path.join(dir, 'store'),
    desktopDir: path.join(dir, 'Desktop'),
    startMenuDir: path.join(dir, 'StartMenu', 'Nullprice'),
    failShortcutAt: null,

    async writeShortcut(linkPath, options) {
      if (platform.failShortcutAt && linkPath.includes(platform.failShortcutAt)) {
        throw new Error('shortcut refused');
      }
      await fsp.mkdir(path.dirname(linkPath), { recursive: true });
      await fsp.writeFile(linkPath, options.target, 'utf8');
      shortcuts.set(linkPath, options);
    },

    async removeShortcut(linkPath) {
      shortcuts.delete(linkPath);
      await fsp.rm(linkPath, { force: true });
    },

    uninstallCommandFor: (id) => `store.exe --uninstall ${id}`,

    async writeUninstallEntry(id, info) {
      registry.set(id, info);
    },

    async removeUninstallEntry(id) {
      registry.delete(id);
    },
  };

  const source = path.join(dir, 'Ferry-0.2.0-setup.exe');
  await fsp.writeFile(source, 'binary contents');

  return {
    dir,
    platform,
    shortcuts,
    registry,
    source,
    installer: createInstaller(platform),
    cleanup: () => fsp.rm(dir, { recursive: true, force: true }),
  };
}

const APP = { id: 'ferry', name: 'Ferry', tagline: 'Verified copy' };
const RELEASE = { version: '0.2.0', filename: 'Ferry-0.2.0-setup.exe', size: 1024 };

test('install places the binary, both shortcuts, and an uninstall entry', async () => {
  const box = await sandbox();
  try {
    const record = await box.installer.install(APP, RELEASE, box.source);

    assert.equal(record.version, '0.2.0');
    assert.ok(fs.existsSync(record.exePath), 'binary should be installed');
    assert.ok(record.exePath.includes(path.join('ferry', '0.2.0')), 'install is version-stamped');

    assert.ok(fs.existsSync(record.desktopLink), 'desktop shortcut');
    assert.ok(fs.existsSync(record.startMenuLink), 'start menu shortcut');

    const entry = box.registry.get('ferry');
    assert.equal(entry.displayName, 'Ferry');
    assert.equal(entry.version, '0.2.0');
    assert.match(entry.uninstallCommand, /--uninstall ferry/);

    assert.equal(await box.installer.installedVersion('ferry'), '0.2.0');
  } finally {
    await box.cleanup();
  }
});

test('a failed install leaves nothing behind', async () => {
  const box = await sandbox();
  try {
    // Fail at the Start Menu step, after the binary and desktop link already exist.
    box.platform.failShortcutAt = 'StartMenu';

    await assert.rejects(() => box.installer.install(APP, RELEASE, box.source), /shortcut refused/);

    assert.equal(box.shortcuts.size, 0, 'no shortcut may survive');
    assert.equal(box.registry.size, 0, 'no uninstall entry may survive');
    assert.equal(
      fs.existsSync(path.join(box.platform.root, 'apps', 'ferry', '0.2.0')),
      false,
      'the version folder must be removed'
    );
    assert.equal(await box.installer.installedVersion('ferry'), null);
  } finally {
    await box.cleanup();
  }
});

test('updating replaces the old version rather than accumulating', async () => {
  const box = await sandbox();
  try {
    await box.installer.install(APP, RELEASE, box.source);

    const newer = { version: '0.3.0', filename: 'Ferry-0.3.0-setup.exe', size: 2048 };
    const record = await box.installer.install(APP, newer, box.source);

    assert.equal(record.version, '0.3.0');
    assert.equal(await box.installer.installedVersion('ferry'), '0.3.0');

    const kept = await fsp.readdir(path.join(box.platform.root, 'apps', 'ferry'));
    assert.deepEqual(kept, ['0.3.0'], 'old versions are pruned once the new one is recorded');
  } finally {
    await box.cleanup();
  }
});

test('uninstall removes the binary, the shortcuts and the registry entry', async () => {
  const box = await sandbox();
  try {
    const record = await box.installer.install(APP, RELEASE, box.source);
    await box.installer.uninstall('ferry');

    assert.equal(fs.existsSync(record.exePath), false);
    assert.equal(fs.existsSync(record.desktopLink), false);
    assert.equal(fs.existsSync(record.startMenuLink), false);
    assert.equal(box.registry.has('ferry'), false);
    assert.equal(await box.installer.installedVersion('ferry'), null);
  } finally {
    await box.cleanup();
  }
});

test('uninstalling something unknown still clears any stray registry entry', async () => {
  const box = await sandbox();
  try {
    box.registry.set('ghost', { displayName: 'Ghost' });

    await box.installer.uninstall('ghost');

    assert.equal(box.registry.has('ghost'), false);
  } finally {
    await box.cleanup();
  }
});

test('a corrupt ledger does not stop the store from opening', async () => {
  const box = await sandbox();
  try {
    await fsp.mkdir(box.platform.root, { recursive: true });
    await fsp.writeFile(box.installer.ledgerPath, '{ not json', 'utf8');

    assert.deepEqual(await box.installer.list(), {});
    assert.equal(await box.installer.installedVersion('ferry'), null);
  } finally {
    await box.cleanup();
  }
});

test('installing a release with no version is refused clearly', async () => {
  const box = await sandbox();
  try {
    await assert.rejects(
      () => box.installer.install(APP, { filename: 'x.exe' }, box.source),
      /no release to install/
    );
  } finally {
    await box.cleanup();
  }
});
