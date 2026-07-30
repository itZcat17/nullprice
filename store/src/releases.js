'use strict';

const versions = require('./versions');

/**
 * Finds the newest published release for an app.
 *
 * Each tool gets its own GitHub repository and its own releases, so shipping a fix to one
 * never means re-releasing the other nine. The store polls each app's repo and offers
 * whatever is newer than what is installed.
 *
 * The provider is pluggable so the whole path is testable without a network or a
 * published repository — `local` resolves against the catalogue's own pinned release.
 */

const GITHUB_API = 'https://api.github.com';

/**
 * @param {object} app                  a catalogue entry
 * @param {object} [options]
 * @param {typeof fetch} [options.fetchImpl]  injected in tests
 * @param {AbortSignal} [options.signal]
 * @returns {Promise<{version: string, release: object, notes: string｜null, source: string}|null>}
 */
async function findLatest(app, options = {}) {
  const updates = app.updates;

  // No update source configured: the pinned catalogue release is all there is.
  if (!updates || updates.provider === 'local') {
    return app.download
      ? { version: app.download.version, release: app.download, notes: null, source: 'catalogue' }
      : null;
  }

  if (updates.provider !== 'github') {
    throw new Error(`Unknown update provider "${updates.provider}" for ${app.id}.`);
  }

  const doFetch = options.fetchImpl || fetch;
  const url = `${GITHUB_API}/repos/${updates.owner}/${updates.repo}/releases/latest`;

  const response = await doFetch(url, {
    signal: options.signal,
    headers: {
      Accept: 'application/vnd.github+json',
      'User-Agent': 'Nullprice-Store',
    },
  });

  // A tool whose repo has no releases yet is normal, not an error.
  if (response.status === 404) return fallback(app);

  if (!response.ok) {
    throw new Error(`GitHub returned ${response.status} checking ${app.name} for updates.`);
  }

  const payload = await response.json();
  if (payload.draft || payload.prerelease) return fallback(app);

  const release = toRelease(app, payload, updates);
  if (!release) return fallback(app);

  return {
    version: release.version,
    release,
    notes: typeof payload.body === 'string' ? payload.body.trim() : null,
    source: 'github',
  };
}

function fallback(app) {
  return app.download
    ? { version: app.download.version, release: app.download, notes: null, source: 'catalogue' }
    : null;
}

/**
 * Turns a GitHub release payload into the same shape the downloader already understands,
 * so nothing downstream needs to know where a release came from.
 */
function toRelease(app, payload, updates) {
  const version = versions.clean(payload.tag_name);
  if (!versions.parse(version)) return null;

  const asset = pickAsset(payload.assets || [], updates.assetPattern);
  if (!asset) return null;

  const checksum = findChecksum(payload.assets || [], asset.name);

  return {
    version,
    kind: app.download?.kind || 'installer',
    filename: asset.name,
    url: asset.browser_download_url,
    // GitHub does not publish a SHA-256 for assets. If the release ships a matching
    // .sha256 file we verify against it; otherwise verification is skipped and the UI
    // says so rather than pretending the download was checked.
    sha256: '',
    sha256Url: checksum ? checksum.browser_download_url : null,
    size: Number(asset.size) || 0,
  };
}

/**
 * Picks the installer from a release's assets. `assetPattern` may contain `*`; without
 * one, the first .exe that is not a checksum file wins.
 */
function pickAsset(assets, assetPattern) {
  const candidates = assets.filter((a) => !/\.(sha256|txt|yml|blockmap)$/i.test(a.name));

  if (assetPattern) {
    const rx = new RegExp(
      '^' + assetPattern.split('*').map(escapeRegex).join('.*') + '$',
      'i'
    );
    const matched = candidates.find((a) => rx.test(a.name));
    if (matched) return matched;
  }

  return candidates.find((a) => /\.exe$/i.test(a.name)) || candidates[0] || null;
}

function findChecksum(assets, assetName) {
  const wanted = `${assetName}.sha256`.toLowerCase();
  return assets.find((a) => a.name.toLowerCase() === wanted) || null;
}

function escapeRegex(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Compares what is installed against what is available.
 * @returns {'not-installed'|'up-to-date'|'update-available'}
 */
function statusFor(installedVersion, latestVersion) {
  if (!installedVersion) return 'not-installed';
  if (!latestVersion) return 'up-to-date';
  return versions.isNewer(latestVersion, installedVersion) ? 'update-available' : 'up-to-date';
}

module.exports = { findLatest, pickAsset, statusFor, toRelease };
