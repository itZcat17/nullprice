'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const { pipeline } = require('node:stream/promises');
const { Readable, Transform } = require('node:stream');

/**
 * Fetch-and-verify, deliberately free of any Electron import so it can be exercised
 * without launching a window. This is the part where being wrong actually hurts —
 * a store that hands users an unverified binary is worse than no store — so it is
 * kept independently testable.
 */

/**
 * @param {object} release             one catalogue entry's `download` block
 * @param {string} destDir             where to put the finished file
 * @param {(p: {received:number,total:number}) => void} onProgress
 * @param {AbortSignal} [signal]
 */
async function downloadRelease(release, destDir, onProgress = () => {}, signal = undefined) {
  if (!release || !release.filename) {
    throw new Error('This catalogue entry has no release attached.');
  }

  await fsp.mkdir(destDir, { recursive: true });

  const target = path.join(destDir, release.filename);
  const partial = `${target}.part`;
  const hasher = crypto.createHash('sha256');
  const total = Number(release.size) || 0;
  let received = 0;

  const tap = new Transform({
    transform(chunk, _encoding, callback) {
      hasher.update(chunk);
      received += chunk.length;
      onProgress({ received, total });
      callback(null, chunk);
    },
  });

  const source = await openSource(release, signal);

  try {
    await pipeline(source, tap, fs.createWriteStream(partial), { signal });
  } catch (err) {
    await fsp.rm(partial, { force: true });
    throw err;
  }

  const actual = hasher.digest('hex');
  const expected = String(release.sha256 || '').toLowerCase();

  if (expected && actual !== expected) {
    // A binary that failed verification is deleted, not quarantined. Unlike a user's
    // own data it has no evidentiary value, and every reason not to remain on disk
    // where something might later execute it.
    await fsp.rm(partial, { force: true });
    throw new Error(
      'The download did not match its published checksum and was discarded. ' +
        'This usually means the transfer was corrupted — try again.'
    );
  }

  await fsp.rm(target, { force: true });
  await fsp.rename(partial, target);

  return { path: target, sha256: actual, bytes: received, verified: Boolean(expected) };
}

async function openSource(release, signal) {
  // A resolved local path means the local test feed, used in development and to
  // exercise this module without hosting anything.
  if (release.resolvedPath) {
    await fsp.access(release.resolvedPath, fs.constants.R_OK);
    return fs.createReadStream(release.resolvedPath);
  }

  const response = await fetch(release.url, { signal });
  if (!response.ok) {
    throw new Error(`The download server returned ${response.status}. Try again later.`);
  }
  return Readable.fromWeb(response.body);
}

/**
 * Resolves `./`-relative release URLs against the catalogue's own location, so the same
 * catalogue file works in development and inside a packaged build.
 */
function resolveCatalogue(parsed, catalogueDir) {
  for (const app of parsed.apps) {
    const release = app.download;
    if (release && typeof release.url === 'string' && release.url.startsWith('./')) {
      release.resolvedPath = path.resolve(catalogueDir, release.url);
    }
  }
  return parsed;
}

module.exports = { downloadRelease, resolveCatalogue };
