'use strict';

const fs = require('node:fs');
const {spawnSync} = require('node:child_process');
const {requireThat, versionParts, assertNoSymlinks} = require('./manifest.cjs');

function readBinary(filePath) {
  assertNoSymlinks(filePath);
  requireThat(fs.statSync(filePath).isFile(), 'Executable must be a regular file.');
  return fs.readFileSync(filePath);
}

function validateLinux(filePath, version, runVersion = true) {
  const bytes = readBinary(filePath);
  requireThat(bytes.length > 4096 && bytes.subarray(0, 4).equals(Buffer.from([0x7f, 0x45, 0x4c, 0x46])) &&
    bytes[4] === 2 && bytes[5] === 1 && bytes.readUInt16LE(18) === 62, 'Linux artifact must be an ELF x86-64 executable.');
  requireThat([2, 3].includes(bytes.readUInt16LE(16)), 'Linux ELF is not executable.');
  if (runVersion) {
    const result = spawnSync(filePath, ['--launcher-version'], {encoding: 'utf8', timeout: 10000, maxBuffer: 64 * 1024});
    requireThat(!result.error && result.status === 0 && result.stdout.trim() === version,
      'Linux executable --launcher-version does not match the release. Rebuild before publishing.');
  }
}

// Read the top-level PE VERSIONINFO resource, rather than accidentally matching
// versions embedded in .NET runtime/dependency assemblies in a single-file app.
function windowsVersion(bytes) {
  requireThat(bytes.length > 4096 && bytes.toString('ascii', 0, 2) === 'MZ', 'Windows artifact must be a PE executable.');
  const pe = bytes.readUInt32LE(0x3c);
  requireThat(pe + 24 < bytes.length && bytes.toString('binary', pe, pe + 4) === 'PE\0\0', 'Invalid PE header.');
  requireThat(bytes.readUInt16LE(pe + 4) === 0x8664, 'Windows executable must target x86-64.');
  const count = bytes.readUInt16LE(pe + 6), optionalSize = bytes.readUInt16LE(pe + 20), optional = pe + 24;
  requireThat(optionalSize >= 136 && optional + optionalSize + count * 40 <= bytes.length && bytes.readUInt16LE(optional) === 0x20b,
    'Windows executable must use a valid PE32+ header.');
  const rvaOffset = rva => {
    for (let i = 0; i < count; i++) {
      const section = optional + optionalSize + i * 40;
      const size = bytes.readUInt32LE(section + 16), start = bytes.readUInt32LE(section + 12);
      const offset = bytes.readUInt32LE(section + 20) + rva - start;
      if (rva >= start && rva - start < size && offset >= 0 && offset < bytes.length) return offset;
    }
    throw new Error('Invalid PE resource RVA.');
  };
  const resourceRva = bytes.readUInt32LE(optional + 112 + 16);
  requireThat(resourceRva > 0, 'Windows executable has no version resource.');
  const base = rvaOffset(resourceRva);
  function entries(relative) {
    const offset = base + relative;
    requireThat(offset >= base && offset + 16 <= bytes.length, 'Truncated PE resource directory.');
    const n = bytes.readUInt16LE(offset + 12) + bytes.readUInt16LE(offset + 14);
    requireThat(n <= 4096 && offset + 16 + n * 8 <= bytes.length, 'Invalid PE resource entry count.');
    return Array.from({length: n}, (_, i) => ({id: bytes.readUInt32LE(offset + 16 + i * 8), value: bytes.readUInt32LE(offset + 20 + i * 8)}));
  }
  const type = entries(0).find(item => item.id === 16);
  requireThat(type && (type.value & 0x80000000), 'PE VERSIONINFO is missing.');
  let level = entries(type.value & 0x7fffffff);
  requireThat(level.length > 0, 'Empty PE VERSIONINFO.');
  let entry = level[0];
  for (let depth = 0; entry.value & 0x80000000; depth++) {
    requireThat(depth < 3, 'PE resource recursion exceeds the version resource structure.');
    level = entries(entry.value & 0x7fffffff);
    requireThat(level.length > 0, 'Empty PE resource language.');
    entry = level[0];
  }
  const data = base + entry.value;
  requireThat(data + 16 <= bytes.length, 'Truncated PE resource data.');
  const start = rvaOffset(bytes.readUInt32LE(data)), size = bytes.readUInt32LE(data + 4);
  requireThat(size >= 64 && size <= 128 * 1024 && start + size <= bytes.length, 'Invalid PE version resource size.');
  const info = bytes.subarray(start, start + size);
  const fixed = info.indexOf(Buffer.from([0xbd, 0x04, 0xef, 0xfe]));
  requireThat(fixed >= 0 && fixed <= 128 && fixed + 24 <= info.length, 'PE fixed version info is missing.');
  const majorMinor = info.readUInt32LE(fixed + 16), patchRevision = info.readUInt32LE(fixed + 20);
  requireThat((patchRevision & 0xffff) === 0, 'Release PE revision must be zero.');
  return `${majorMinor >>> 16}.${majorMinor & 0xffff}.${patchRevision >>> 16}`;
}

function validateWindows(filePath, version) {
  versionParts(version);
  requireThat(windowsVersion(readBinary(filePath)) === version, 'Windows executable PE product version does not match the release. Rebuild before publishing.');
}

module.exports = {validateLinux, validateWindows, windowsVersion};
