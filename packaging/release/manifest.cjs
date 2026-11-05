'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const KEY_ID = 'dusmp-release-1';
const HOSTS = new Set(['dusmp.online', 'www.dusmp.online', 'api.dusmp.online']);
const VERSION = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const MAX_BYTES = 1024 * 1024 * 1024;
const PUBLIC_KEY = fs.readFileSync(path.join(__dirname, 'release-public-key.pem'), 'utf8');

function requireThat(value, message) {
  if (!value) throw new Error(message);
}

function versionParts(value) {
  requireThat(typeof value === 'string' && VERSION.test(value), 'Version must be strict major.minor.patch.');
  const parts = value.split('.').map(Number);
  requireThat(parts.every(n => Number.isInteger(n) && n >= 0 && n <= 65535), 'Version components must fit PE version resources.');
  return parts;
}

function compareVersions(a, b) {
  const left = versionParts(a), right = versionParts(b);
  for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return Math.sign(left[i] - right[i]);
  return 0;
}

function object(value, label) {
  requireThat(value && typeof value === 'object' && !Array.isArray(value), `${label} must be an object.`);
}

function fields(value, required, optional, label) {
  object(value, label);
  requireThat(required.every(k => Object.hasOwn(value, k)), `${label} has missing fields.`);
  requireThat(Object.keys(value).every(k => required.includes(k) || optional.includes(k)), `${label} has unknown fields.`);
}

function validateFile(value, version, os, download = false) {
  fields(value, download ? ['url', 'fileName', 'size', 'sha256', 'format', 'compatibility'] :
    ['os', 'arch', 'url', 'fileName', 'size', 'sha256'], download ? [] : ['download'], 'Artifact');
  requireThat(typeof value.fileName === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$/.test(value.fileName), 'Unsafe artifact fileName.');
  requireThat(Number.isSafeInteger(value.size) && value.size > 0 && value.size <= MAX_BYTES, 'Artifact size is invalid.');
  requireThat(typeof value.sha256 === 'string' && /^[a-f0-9]{64}$/.test(value.sha256), 'Artifact SHA-256 is invalid.');
  const url = new URL(value.url);
  requireThat(url.protocol === 'https:' && HOSTS.has(url.hostname) && !url.username && !url.password && !url.port && !url.search && !url.hash,
    'Artifact URL must use an official HTTPS origin.');
  requireThat(url.pathname === `/launcher/releases/${version}/${os}-x64/${value.fileName}`, 'Artifact URL does not match its version/platform/fileName.');
  requireThat(value.url === url.href, 'Artifact URL must be canonical.');
  if (download) {
    requireThat(value.format === (os === 'linux' ? 'tar.gz' : 'exe'), 'Unsupported download format.');
    requireThat(typeof value.compatibility === 'string' && value.compatibility.length > 0 && value.compatibility.length <= 500, 'Compatibility text is invalid.');
    requireThat(value.fileName.endsWith(os === 'linux' ? '.tar.gz' : '.exe'), 'Download extension is invalid.');
  } else {
    requireThat(value.os === os && value.arch === 'x64', 'Unsupported artifact platform.');
    requireThat(value.fileName === (os === 'linux' ? 'DusmpLauncher' : 'DusmpLauncher.exe'), 'Unexpected launcher filename.');
    if (value.download) validateFile(value.download, version, os, true);
  }
}

function validatePayload(payload) {
  fields(payload, ['schemaVersion', 'channel', 'version', 'minimumVersion', 'mandatory', 'publishedAt', 'notes', 'artifacts'], [], 'Release');
  requireThat(payload.schemaVersion === 1 && payload.channel === 'stable', 'Unsupported release schema/channel.');
  requireThat(compareVersions(payload.minimumVersion, payload.version) <= 0, 'Minimum version exceeds release version.');
  requireThat(typeof payload.mandatory === 'boolean', 'Mandatory flag must be boolean.');
  requireThat(typeof payload.publishedAt === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(payload.publishedAt) &&
    new Date(payload.publishedAt).toISOString() === payload.publishedAt, 'Publication timestamp must be canonical UTC ISO-8601.');
  requireThat(typeof payload.notes === 'string' && payload.notes.length <= 16000, 'Release notes are invalid.');
  requireThat(Array.isArray(payload.artifacts) && payload.artifacts.length === 2, 'A release must contain Windows x64 and Linux x64.');
  for (const os of ['windows', 'linux']) {
    const artifacts = payload.artifacts.filter(a => a && a.os === os);
    requireThat(artifacts.length === 1, `Release requires exactly one ${os} artifact.`);
    validateFile(artifacts[0], payload.version, os);
  }
  return payload;
}

function canonicalBase64(value, label, maxBytes) {
  requireThat(typeof value === 'string' && value.length > 0 && value.length <= Math.ceil(maxBytes / 3) * 4 &&
    /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value), `${label} base64 is invalid.`);
  const bytes = Buffer.from(value, 'base64');
  requireThat(bytes.length <= maxBytes && bytes.toString('base64') === value, `${label} base64 must be canonical.`);
  return bytes;
}

function parseStrictJson(text) {
  const result = JSON.parse(text);
  const stack = [];
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (character === '"') {
      const start = index++;
      while (index < text.length && text[index] !== '"') {
        if (text[index] === '\\') index++;
        index++;
      }
      const current = stack.at(-1);
      if (current?.object && current.expectKey) {
        const key = JSON.parse(text.slice(start, index + 1));
        requireThat(!current.keys.has(key), 'Duplicate JSON property is refused.');
        current.keys.add(key);
        current.expectKey = false;
      }
    } else if (character === '{' || character === '[') {
      stack.push({object: character === '{', expectKey: true, keys: new Set()});
    } else if (character === '}' || character === ']') stack.pop();
    else if (character === ',' && stack.at(-1)?.object) stack.at(-1).expectKey = true;
  }
  return result;
}

function verifyEnvelope(envelope, publicKey = PUBLIC_KEY) {
  fields(envelope, ['schemaVersion', 'keyId', 'payload', 'signature'], [], 'Signed envelope');
  requireThat(envelope.schemaVersion === 1 && envelope.keyId === KEY_ID, 'Unknown signing key/schema.');
  const bytes = canonicalBase64(envelope.payload, 'Payload', 128 * 1024);
  const signature = canonicalBase64(envelope.signature, 'Signature', 64);
  requireThat(signature.length === 64 && crypto.verify('sha256', bytes, {key: publicKey, dsaEncoding: 'ieee-p1363'}, signature), 'Release signature is invalid.');
  const text = new TextDecoder('utf-8', {fatal: true}).decode(bytes);
  return validatePayload(parseStrictJson(text));
}

function signPayload(payload, privateKey) {
  validatePayload(payload);
  const bytes = Buffer.from(JSON.stringify(payload));
  const envelope = {schemaVersion: 1, keyId: KEY_ID, payload: bytes.toString('base64'),
    signature: crypto.sign('sha256', bytes, {key: privateKey, dsaEncoding: 'ieee-p1363'}).toString('base64')};
  verifyEnvelope(envelope);
  return envelope;
}

function assertNoSymlinks(filePath) {
  const absolute = path.resolve(filePath);
  let current = path.parse(absolute).root;
  for (const segment of absolute.slice(current.length).split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    try { requireThat(!fs.lstatSync(current).isSymbolicLink(), `Symlink paths are refused: ${current}`); }
    catch (error) { if (error.code === 'ENOENT') break; throw error; }
  }
  return absolute;
}

async function describeFile(filePath) {
  assertNoSymlinks(filePath);
  const stat = fs.statSync(filePath);
  requireThat(stat.isFile() && stat.size > 0 && stat.size <= MAX_BYTES, 'Artifact must be a nonempty regular file under 1 GiB.');
  const hash = crypto.createHash('sha256');
  for await (const chunk of fs.createReadStream(filePath)) hash.update(chunk);
  return {size: stat.size, sha256: hash.digest('hex')};
}

async function verifyDirectory(directory, envelope) {
  assertNoSymlinks(directory);
  const payload = verifyEnvelope(envelope);
  for (const artifact of payload.artifacts) {
    for (const item of [artifact, artifact.download].filter(Boolean)) {
      const file = path.join(directory, `${artifact.os}-x64`, item.fileName);
      const actual = await describeFile(file);
      requireThat(actual.size === item.size && actual.sha256 === item.sha256, `Release artifact failed integrity validation: ${item.fileName}`);
    }
  }
  return payload;
}

module.exports = {KEY_ID, PUBLIC_KEY, requireThat, versionParts, compareVersions, validatePayload, parseStrictJson, verifyEnvelope,
  signPayload, assertNoSymlinks, describeFile, verifyDirectory};
