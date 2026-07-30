'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');

const { downloadRelease, resolveCatalogue } = require('../src/downloader');

const CATALOGUE = path.join(__dirname, '..', 'catalogue.json');

async function sandbox() {
  const dir = await fsp.mkdtemp(path.join(os.tmpdir(), 'nullprice-test-'));
  return { dir, cleanup: () => fsp.rm(dir, { recursive: true, force: true }) };
}

function loadCatalogue() {
  const parsed = JSON.parse(fs.readFileSync(CATALOGUE, 'utf8'));
  return resolveCatalogue(parsed, path.dirname(CATALOGUE));
}

test('catalogue parses and every entry has the fields the UI reads', () => {
  const cat = loadCatalogue();
  assert.ok(Array.isArray(cat.apps));
  assert.equal(cat.apps.length, 10);

  for (const app of cat.apps) {
    assert.ok(app.id, 'id');
    assert.ok(app.name, `name for ${app.id}`);
    assert.ok(app.tagline, `tagline for ${app.id}`);
    assert.ok(app.replaces, `replaces for ${app.id}`);
    assert.ok(app.theirPrice, `theirPrice for ${app.id}`);
    assert.ok(app.requirements, `requirements for ${app.id}`);
    assert.ok(Array.isArray(app.description) && app.description.length > 0);
    assert.ok(Array.isArray(app.features) && app.features.length > 0);
    assert.ok(['available', 'planned', 'building'].includes(app.status));
  }
});

test('an entry marked available actually has a release attached', () => {
  for (const app of loadCatalogue().apps) {
    if (app.status !== 'available') continue;
    assert.ok(app.download, `${app.id} is available but has no download block`);
    assert.ok(app.download.filename, `${app.id} release needs a filename`);
    assert.match(app.download.sha256, /^[0-9a-f]{64}$/, `${app.id} needs a real sha256`);
    assert.ok(Number(app.download.size) > 0, `${app.id} needs a real size`);
  }
});

test('a planned entry offers nothing to download', () => {
  for (const app of loadCatalogue().apps) {
    if (app.status === 'available') continue;
    assert.equal(app.download, null, `${app.id} is not available so must expose no release`);
  }
});

test('downloads the real Ferry release and verifies its checksum', async (t) => {
  const cat = loadCatalogue();
  const ferry = cat.apps.find((a) => a.id === 'ferry');

  if (!fs.existsSync(ferry.download.resolvedPath)) {
    t.skip('Ferry has not been published into store/feed yet');
    return;
  }

  const box = await sandbox();
  try {
    let sawProgress = false;
    let lastReceived = 0;

    const result = await downloadRelease(ferry.download, box.dir, ({ received, total }) => {
      sawProgress = true;
      assert.ok(received >= lastReceived, 'progress must not go backwards');
      assert.equal(total, Number(ferry.download.size));
      lastReceived = received;
    });

    assert.ok(sawProgress, 'progress should have been reported');
    assert.equal(result.verified, true);
    assert.equal(result.sha256, ferry.download.sha256);
    assert.equal(result.bytes, Number(ferry.download.size));

    const landed = await fsp.stat(result.path);
    assert.equal(landed.size, Number(ferry.download.size));

    // No .part file may survive a successful download.
    const leftovers = (await fsp.readdir(box.dir)).filter((f) => f.endsWith('.part'));
    assert.deepEqual(leftovers, []);
  } finally {
    await box.cleanup();
  }
});

test('a corrupted release is rejected and not left on disk', async () => {
  const box = await sandbox();
  try {
    // A small stand-in file, with a checksum that deliberately will not match.
    const payload = crypto.randomBytes(64 * 1024);
    const feedFile = path.join(box.dir, 'source.bin');
    await fsp.writeFile(feedFile, payload);

    const release = {
      filename: 'claimed.bin',
      url: './source.bin',
      resolvedPath: feedFile,
      sha256: 'f'.repeat(64),
      size: payload.length,
    };

    const dest = path.join(box.dir, 'out');
    await assert.rejects(
      () => downloadRelease(release, dest),
      /did not match its published checksum/
    );

    // Neither the finished name nor the partial may remain.
    const files = fs.existsSync(dest) ? await fsp.readdir(dest) : [];
    assert.deepEqual(files, [], 'a failed download must leave nothing behind');
  } finally {
    await box.cleanup();
  }
});

test('a release with a matching checksum is accepted', async () => {
  const box = await sandbox();
  try {
    const payload = crypto.randomBytes(32 * 1024);
    const feedFile = path.join(box.dir, 'good.bin');
    await fsp.writeFile(feedFile, payload);

    const release = {
      filename: 'good.bin',
      url: './good.bin',
      resolvedPath: feedFile,
      sha256: crypto.createHash('sha256').update(payload).digest('hex'),
      size: payload.length,
    };

    const dest = path.join(box.dir, 'out');
    const result = await downloadRelease(release, dest);

    assert.equal(result.verified, true);
    assert.equal(result.bytes, payload.length);
    assert.deepEqual(await fsp.readFile(result.path), payload);
  } finally {
    await box.cleanup();
  }
});

test('an entry with no release attached is refused clearly', async () => {
  const box = await sandbox();
  try {
    await assert.rejects(() => downloadRelease(null, box.dir), /no release attached/);
    await assert.rejects(() => downloadRelease({}, box.dir), /no release attached/);
  } finally {
    await box.cleanup();
  }
});

test('a missing local feed file fails without creating a partial', async () => {
  const box = await sandbox();
  try {
    const release = {
      filename: 'ghost.bin',
      url: './ghost.bin',
      resolvedPath: path.join(box.dir, 'ghost.bin'),
      sha256: 'a'.repeat(64),
      size: 10,
    };

    const dest = path.join(box.dir, 'out');
    await assert.rejects(() => downloadRelease(release, dest));

    const files = fs.existsSync(dest) ? await fsp.readdir(dest) : [];
    assert.deepEqual(files, []);
  } finally {
    await box.cleanup();
  }
});
