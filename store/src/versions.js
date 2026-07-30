'use strict';

/**
 * Just enough semantic versioning to decide whether a release is newer.
 *
 * A dependency is not worth it for this: the whole contract is "is B newer than A", the
 * rules are short, and getting it wrong means either nagging people about updates that do
 * not exist or silently never offering one. Both are worth a test rather than trust.
 */

/**
 * Parses "v1.2.3-beta.1" into its parts. Returns null for anything unparseable, so
 * callers can decide what to do rather than getting NaN comparisons.
 */
function parse(version) {
  if (typeof version !== 'string') return null;

  const match = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(version.trim());
  if (!match) return null;

  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease: match[4] ? match[4].split('.') : [],
  };
}

/**
 * Returns -1, 0, or 1. Unparseable versions sort below parseable ones so a malformed
 * tag on the server can never masquerade as an upgrade.
 */
function compare(a, b) {
  const left = parse(a);
  const right = parse(b);

  if (!left && !right) return 0;
  if (!left) return -1;
  if (!right) return 1;

  for (const part of ['major', 'minor', 'patch']) {
    if (left[part] !== right[part]) return left[part] < right[part] ? -1 : 1;
  }

  // 1.0.0-beta is *older* than 1.0.0. A release with no prerelease tag wins.
  if (left.prerelease.length === 0 && right.prerelease.length === 0) return 0;
  if (left.prerelease.length === 0) return 1;
  if (right.prerelease.length === 0) return -1;

  const length = Math.max(left.prerelease.length, right.prerelease.length);
  for (let i = 0; i < length; i++) {
    const l = left.prerelease[i];
    const r = right.prerelease[i];

    if (l === undefined) return -1;
    if (r === undefined) return 1;
    if (l === r) continue;

    const lNum = /^\d+$/.test(l);
    const rNum = /^\d+$/.test(r);

    // Numeric identifiers always compare lower than alphanumeric ones.
    if (lNum && rNum) return Number(l) < Number(r) ? -1 : 1;
    if (lNum) return -1;
    if (rNum) return 1;
    return l < r ? -1 : 1;
  }

  return 0;
}

function isNewer(candidate, current) {
  return compare(candidate, current) > 0;
}

/** Display form, without the leading v that GitHub tags usually carry. */
function clean(version) {
  const parsed = parse(version);
  if (!parsed) return String(version ?? '');

  const core = `${parsed.major}.${parsed.minor}.${parsed.patch}`;
  return parsed.prerelease.length ? `${core}-${parsed.prerelease.join('.')}` : core;
}

module.exports = { parse, compare, isNewer, clean };
