'use strict';

// Synthetic files in this suite are isolated test fixtures, never release inputs.
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {spawnSync, spawn} = require('node:child_process');
const {KEY_ID, compareVersions, validatePayload, parseStrictJson, verifyEnvelope, assertNoSymlinks, describeFile} = require('./manifest.cjs');
const {windowsVersion, validateWindows, validateLinux} = require('./binary.cjs');
const {locked, atomicWrite, privateKey, generateKey, options, prepare} = require('./release.cjs');
const pair = crypto.generateKeyPairSync('ec', {namedCurve: 'prime256v1'});

function payload() {
  return {schemaVersion: 1, channel: 'stable', version: '1.3.0', minimumVersion: '1.2.0', mandatory: false,
    publishedAt: '2026-10-29T12:00:00.000Z', notes: 'Synthetic unit test metadata.', artifacts: ['linux', 'windows'].map(platform => {
      const fileName = platform === 'linux' ? 'DusmpLauncher' : 'DusmpLauncher.exe';
      return {os: platform, arch: 'x64', fileName, url: `https://dusmp.online/launcher/releases/1.3.0/${platform}-x64/${fileName}`,
        size: 80000000, sha256: 'a'.repeat(64)};
    })};
}

function envelope(value = payload()) {
  const bytes = Buffer.from(JSON.stringify(value));
  return {schemaVersion: 1, keyId: KEY_ID, payload: bytes.toString('base64'),
    signature: crypto.sign('sha256', bytes, {key: pair.privateKey, dsaEncoding: 'ieee-p1363'}).toString('base64')};
}

function temp(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'dusmp-release-test-'));
  t.after(() => fs.rmSync(directory, {recursive: true, force: true}));
  return directory;
}

test('accepts exact signed bytes with P-256/P1363 and the stable contract', () => {
  assert.deepEqual(verifyEnvelope(envelope(), pair.publicKey), payload());
});

test('rejects payload/signature tampering and an untrusted signing key', () => {
  const changed = envelope();
  changed.payload = Buffer.from(JSON.stringify({...payload(), mandatory: true})).toString('base64');
  assert.throws(() => verifyEnvelope(changed, pair.publicKey), /signature/i);
  const signature = envelope();
  signature.signature = Buffer.alloc(64).toString('base64');
  assert.throws(() => verifyEnvelope(signature, pair.publicKey), /signature/i);
  assert.throws(() => verifyEnvelope(envelope()), /signature/i);
});

test('rejects unknown keys, schema and noncanonical base64', () => {
  for (const mutate of [e => {e.keyId = 'attacker';}, e => {e.schemaVersion = 2;}, e => {e.extra = true;},
    e => {e.payload += '\n';}, e => {e.signature = e.signature.replace(/=$/, '');}]) {
    const value = envelope(); mutate(value); assert.throws(() => verifyEnvelope(value, pair.publicKey));
  }
});

test('refuses duplicate JSON keys including escaped equivalents and nested objects', () => {
  for (const text of ['{"version":1,"version":2}', '{"version":1,"\\u0076ersion":2}',
    '{"nested":{"key":1,"key":2}}', '[{"key":1,"key":2}]']) assert.throws(() => parseStrictJson(text), /Duplicate/);
  assert.deepEqual(parseStrictJson('{"a":{"key":1},"b":{"key":2},"values":["x,y",{"quoted\\\"key":3}]}'),
    {a: {key: 1}, b: {key: 2}, values: ['x,y', {'quoted"key': 3}]});
});

test('requires both platforms once and refuses unsupported architectures', () => {
  for (const mutate of [p => {p.artifacts.pop();}, p => {p.artifacts[1] = p.artifacts[0];},
    p => {p.artifacts[0].arch = 'arm64';}, p => {p.artifacts[0].os = 'macos';}]) {
    const value = payload(); mutate(value); assert.throws(() => validatePayload(value));
  }
});

test('refuses arbitrary origins, credentials, queries and path traversal', () => {
  for (const url of ['http://dusmp.online/launcher/releases/1.3.0/linux-x64/DusmpLauncher',
    'https://dusmp.online.evil.test/launcher/releases/1.3.0/linux-x64/DusmpLauncher',
    'https://admin@dusmp.online/launcher/releases/1.3.0/linux-x64/DusmpLauncher',
    'https://dusmp.online:8443/launcher/releases/1.3.0/linux-x64/DusmpLauncher',
    'https://dusmp.online/launcher/releases/1.3.0/linux-x64/DusmpLauncher?x=1',
    'https://dusmp.online/launcher/releases/1.3.0/linux-x64/../DusmpLauncher',
    'https://dusmp.online/launcher/releases/1.3.0/linux-x64/%44usmpLauncher']) {
    const value = payload(); value.artifacts[0].url = url; assert.throws(() => validatePayload(value));
  }
});

test('refuses corrupt size, hash, names, dates and minimum versions', () => {
  for (const mutate of [p => {p.artifacts[0].size = 0;}, p => {p.artifacts[0].size = 1024 ** 3 + 1;},
    p => {p.artifacts[0].sha256 = 'x'.repeat(64);}, p => {p.artifacts[0].fileName = '../launcher';},
    p => {p.minimumVersion = '2.0.0';}, p => {p.version = '1.03.0';}, p => {p.publishedAt = 'yesterday';}]) {
    const value = payload(); mutate(value); assert.throws(() => validatePayload(value));
  }
});

test('checks optional installer metadata as strictly as executable metadata', () => {
  const value = payload();
  value.artifacts[0].download = {url: 'https://dusmp.online/launcher/releases/1.3.0/linux-x64/DUSMP-Launcher-1.3.0-linux-x64.tar.gz',
    fileName: 'DUSMP-Launcher-1.3.0-linux-x64.tar.gz', size: 100000000, sha256: 'b'.repeat(64), format: 'tar.gz', compatibility: 'Synthetic Linux test.'};
  validatePayload(value);
  value.artifacts[0].download.url = 'https://evil.test/install.tar.gz';
  assert.throws(() => validatePayload(value));
});

test('compares numeric versions and rejects malformed versions', () => {
  assert.equal(compareVersions('1.10.0', '1.9.0'), 1);
  assert.equal(compareVersions('1.3.0', '1.3.0'), 0);
  assert.equal(compareVersions('1.2.0', '1.3.0'), -1);
  for (const version of ['1.3', '01.3.0', '1.3.0-beta', '65536.0.0', '../1.3.0']) assert.throws(() => compareVersions(version, '1.3.0'));
});

test('artifact hashing reads real bytes and refuses symbolic paths', async t => {
  const directory = temp(t), file = path.join(directory, 'file');
  fs.writeFileSync(file, 'real test bytes');
  assert.deepEqual(await describeFile(file), {size: 15, sha256: crypto.createHash('sha256').update('real test bytes').digest('hex')});
  fs.symlinkSync(file, path.join(directory, 'link'));
  assert.throws(() => assertNoSymlinks(path.join(directory, 'link')), /Symlink/);
});

test('publication lock excludes concurrent publishers and is released after failures', async t => {
  const directory = temp(t);
  await locked(directory, async () => {
    await assert.rejects(locked(directory, async () => {}), /lock exists/);
  });
  await assert.rejects(locked(directory, async () => {throw new Error('interrupted fixture');}), /interrupted fixture/);
  await locked(directory, async () => {});
  assert.equal(fs.existsSync(path.join(directory, '.publish.lock')), false);
});

test('atomic manifest replacement keeps complete bytes', t => {
  const directory = temp(t), file = path.join(directory, 'stable.json');
  atomicWrite(file, JSON.stringify({version: '1.2.0'}));
  atomicWrite(file, JSON.stringify({version: '1.3.0'}));
  assert.equal(JSON.parse(fs.readFileSync(file, 'utf8')).version, '1.3.0');
  assert.deepEqual(fs.readdirSync(directory), ['stable.json']);
});

test('key generation refuses overwrite and production signing refuses foreign keys', t => {
  const directory = temp(t), file = path.join(directory, 'signing-key.pem');
  generateKey(file);
  assert.throws(() => generateKey(file), /overwrite/);
  assert.throws(() => privateKey(file), /does not match/);
  if (process.platform !== 'win32') {
    assert.equal(fs.statSync(file).mode & 0o777, 0o600);
    fs.chmodSync(file, 0o644);
    assert.throws(() => privateKey(file), /0600/);
  }
});

test('prepare accepts release flags and requires an explicit output directory', async t => {
  const directory = temp(t), notesFile = path.join(directory, 'notes.txt');
  fs.writeFileSync(notesFile, 'Release prepared for upload.\n');
  const opts = options(['prepare', '--version', '1.3.1', '--minimum-version', '1.3.0', '--mandatory',
    '--notes-file', notesFile, '--key-file', path.join(directory, 'key.pem'), '--skip-build', '--output-dir', path.join(directory, 'package')]);
  assert.equal(opts.command, 'prepare');
  assert.equal(opts.version, '1.3.1');
  assert.equal(opts.minimumVersion, '1.3.0');
  assert.equal(opts.mandatory, true);
  assert.equal(opts.skipBuild, true);
  assert.equal(opts.notes, 'Release prepared for upload.');
  assert.equal(opts.outputDir, path.join(directory, 'package'));
  assert.throws(() => options(['prepare', '--output-dir']), /Missing value/);
  await assert.rejects(prepare({...opts, outputDir: null}), /--output-dir is required/);
});

test('prepare refuses existing, symbolic and invalid output paths before reading the signing key', async t => {
  const directory = temp(t), existing = path.join(directory, 'existing');
  fs.mkdirSync(existing);
  fs.writeFileSync(path.join(existing, 'manifest.json'), 'existing bytes');
  const opts = options(['prepare', '--version', '1.3.0', '--skip-build', '--key-file', path.join(directory, 'absent-key.pem')]);
  await assert.rejects(prepare({...opts, outputDir: existing}), /already exists/);
  assert.equal(fs.readFileSync(path.join(existing, 'manifest.json'), 'utf8'), 'existing bytes');
  await assert.rejects(prepare({...opts, outputDir: path.join(directory, 'missing', 'package')}), /ENOENT/);
  await assert.rejects(prepare({...opts, outputDir: path.join(existing, 'manifest.json', 'package')}), /existing directory/);
  fs.symlinkSync(existing, path.join(directory, 'link'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(prepare({...opts, outputDir: path.join(directory, 'link', 'package')}), /Symlink/);
  await assert.rejects(prepare({...opts, outputDir: path.join(directory, 'link')}), /Symlink/);
  assert.deepEqual(fs.readdirSync(directory).sort(), ['existing', 'link']);
});

function syntheticElf(version) {
  const bytes = Buffer.alloc(8192), base = 0x400000n, message = Buffer.from(`${version}\n`);
  Buffer.from([0x7f, 0x45, 0x4c, 0x46, 2, 1, 1]).copy(bytes);
  bytes.writeUInt16LE(2, 16); bytes.writeUInt16LE(62, 18); bytes.writeUInt32LE(1, 20);
  bytes.writeBigUInt64LE(base + 120n, 24); bytes.writeBigUInt64LE(64n, 32);
  bytes.writeUInt16LE(64, 52); bytes.writeUInt16LE(56, 54); bytes.writeUInt16LE(1, 56);
  bytes.writeUInt32LE(1, 64); bytes.writeUInt32LE(5, 68);
  bytes.writeBigUInt64LE(base, 80); bytes.writeBigUInt64LE(base, 88);
  bytes.writeBigUInt64LE(BigInt(bytes.length), 96); bytes.writeBigUInt64LE(BigInt(bytes.length), 104);
  bytes.writeBigUInt64LE(4096n, 112);
  // A tiny isolated Linux fixture: write the version to stdout, then exit 0.
  const code = Buffer.from([0xb8, 1, 0, 0, 0, 0xbf, 1, 0, 0, 0, 0x48, 0x8d, 0x35, 16, 0, 0, 0,
    0xba, message.length, 0, 0, 0, 0x0f, 0x05, 0xb8, 60, 0, 0, 0, 0x31, 0xff, 0x0f, 0x05]);
  code.copy(bytes, 120); message.copy(bytes, 120 + code.length);
  return bytes;
}

test('prepare produces a verified upload package without changing stable or requiring a site project',
  {skip: process.platform !== 'linux' || process.arch !== 'x64'}, async t => {
    const directory = temp(t), root = path.join(directory, 'isolated-launcher'), scripts = path.join(root, 'packaging/release');
    fs.mkdirSync(scripts, {recursive: true});
    for (const name of ['release.cjs', 'manifest.cjs', 'binary.cjs']) fs.copyFileSync(path.join(__dirname, name), path.join(scripts, name));
    fs.writeFileSync(path.join(scripts, 'release-public-key.pem'), pair.publicKey.export({type: 'spki', format: 'pem'}));
    fs.mkdirSync(path.join(root, 'packaging/linux'));
    fs.copyFileSync(path.resolve(__dirname, '../linux/install-launcher.sh'), path.join(root, 'packaging/linux/install-launcher.sh'));
    const key = path.join(directory, 'ephemeral-key.pem');
    fs.writeFileSync(key, pair.privateKey.export({type: 'pkcs8', format: 'pem'}), {mode: 0o600});
    for (const [platform, extension, bytes] of [['linux', '', syntheticElf('1.3.0')], ['win', '.exe', syntheticPe()]]) {
      for (const [component, name] of [['launcher', 'DusmpLauncher'], ['updater', 'DusmpUpdater']]) {
        const target = path.join(root, 'dist', platform, component); fs.mkdirSync(target, {recursive: true});
        fs.writeFileSync(path.join(target, name + extension), bytes, {mode: 0o755});
      }
    }
    const stable = path.join(directory, 'stable.json'), catalog = path.join(directory, 'modpack-catalog.json');
    fs.writeFileSync(stable, 'stable sentinel'); fs.writeFileSync(catalog, 'catalog sentinel');
    const isolated = require(path.join(scripts, 'release.cjs')), output = path.join(directory, 'upload-package');
    const args = ['prepare', '--version', '1.3.0', '--output-dir', output, '--key-file', key, '--skip-build',
      '--site-root', path.join(directory, 'site-does-not-exist')];
    await isolated.main(args);
    const signed = JSON.parse(fs.readFileSync(path.join(output, 'manifest.json'), 'utf8'));
    const release = verifyEnvelope(signed, pair.publicKey);
    assert.equal(release.version, '1.3.0');
    await require(path.join(scripts, 'manifest.cjs')).verifyDirectory(output, signed);
    assert.deepEqual(fs.readdirSync(output).sort(), ['linux-x64', 'manifest.json', 'windows-x64']);
    assert.deepEqual(fs.readdirSync(path.join(output, 'linux-x64')).sort(), ['DUSMP-Launcher-1.3.0-linux-x64.tar.gz', 'DusmpLauncher']);
    assert.deepEqual(fs.readdirSync(path.join(output, 'windows-x64')), ['DusmpLauncher.exe']);
    assert.equal(fs.readFileSync(stable, 'utf8'), 'stable sentinel');
    assert.equal(fs.readFileSync(catalog, 'utf8'), 'catalog sentinel');
    assert.equal(fs.existsSync(path.join(directory, 'site-does-not-exist')), false);
    await assert.rejects(isolated.main(args), /already exists/);
    const failed = path.join(directory, 'failed-package');
    await assert.rejects(isolated.main(['prepare', '--version', '1.3.1', '--output-dir', failed, '--key-file', key, '--skip-build']), /launcher-version/);
    assert.equal(fs.existsSync(failed), false);
    assert.equal(fs.readdirSync(directory).some(name => name.startsWith('.staging-') || name.endsWith('.lock')), false);
    assert.equal(fs.readFileSync(stable, 'utf8'), 'stable sentinel');
  });

function syntheticPe() {
  const b = Buffer.alloc(8192), pe = 128, optional = pe + 24, section = optional + 240, resource = 1024;
  b.write('MZ'); b.writeUInt32LE(pe, 0x3c); b.write('PE\0\0', pe, 'binary');
  b.writeUInt16LE(0x8664, pe + 4); b.writeUInt16LE(1, pe + 6); b.writeUInt16LE(240, pe + 20); b.writeUInt16LE(0x20b, optional);
  b.writeUInt32LE(4096, optional + 128); b.writeUInt32LE(4096, section + 12); b.writeUInt32LE(4096, section + 16); b.writeUInt32LE(resource, section + 20);
  for (const [offset, id, pointer] of [[0, 16, 0x80000020], [32, 1, 0x80000040], [64, 1033, 96]]) {
    b.writeUInt16LE(1, resource + offset + 14); b.writeUInt32LE(id, resource + offset + 16); b.writeUInt32LE(pointer, resource + offset + 20);
  }
  b.writeUInt32LE(4096 + 256, resource + 96); b.writeUInt32LE(128, resource + 100);
  const fixed = resource + 256 + 64;
  b.writeUInt32LE(0xfeef04bd, fixed); b.writeUInt32LE((1 << 16) | 3, fixed + 16); b.writeUInt32LE(0, fixed + 20);
  return b;
}

test('PE parser validates x64, exact product version and malformed resources without executing Windows', t => {
  const directory = temp(t), file = path.join(directory, 'synthetic.exe'), bytes = syntheticPe();
  fs.writeFileSync(file, bytes); assert.equal(windowsVersion(bytes), '1.3.0'); validateWindows(file, '1.3.0');
  assert.throws(() => validateWindows(file, '1.3.1'), /version/);
  const wrongArchitecture = Buffer.from(bytes); wrongArchitecture.writeUInt16LE(0x14c, 132);
  assert.throws(() => windowsVersion(wrongArchitecture), /x86-64/);
  assert.throws(() => windowsVersion(Buffer.alloc(10)), /PE/);
});

test('ELF parser refuses wrong platform and publishing requires a working version CLI', t => {
  const directory = temp(t), file = path.join(directory, 'synthetic-elf'), bytes = Buffer.alloc(8192);
  Buffer.from([0x7f, 0x45, 0x4c, 0x46, 2, 1]).copy(bytes); bytes.writeUInt16LE(3, 16); bytes.writeUInt16LE(62, 18);
  fs.writeFileSync(file, bytes, {mode: 0o755}); validateLinux(file, '1.3.0', false);
  assert.throws(() => validateLinux(file, '1.3.0'), /launcher-version/);
  bytes.writeUInt16LE(183, 18); fs.writeFileSync(file, bytes); assert.throws(() => validateLinux(file, '1.3.0', false), /x86-64/);
});

function linuxInstaller(t, overrides = {}) {
  const directory = temp(t), home = path.join(directory, 'home with spaces $percent%'), bundle = path.join(directory, 'bundle');
  fs.mkdirSync(bundle);
  for (const name of ['DusmpLauncher', 'DusmpUpdater']) fs.writeFileSync(path.join(bundle, name), '#!/bin/sh\nprintf "1.3.0\\n"\n', {mode: 0o755});
  fs.writeFileSync(path.join(bundle, 'README.txt'), 'Synthetic installer test.');
  fs.copyFileSync(path.resolve(__dirname, '../linux/install-launcher.sh'), path.join(bundle, 'install.sh'));
  const env = {...process.env, HOME: home, XDG_CONFIG_HOME: path.join(home, '.config'), XDG_DATA_HOME: '', DUSMP_LAUNCHER_HOME: '', ...overrides};
  return {directory, home, bundle, env, install: () => spawnSync('bash', [path.join(bundle, 'install.sh')], {env, encoding: 'utf8'})};
}

test('Linux installer creates desktop and menu shortcuts, preserves user data and retains the preceding installation', {skip: process.platform !== 'linux'}, t => {
  const {home, bundle, install} = linuxInstaller(t);
  fs.writeFileSync(path.join(bundle, 'icon.png'), 'Synthetic icon.');
  const data = path.join(home, '.local/share/DusmpLauncher'); fs.mkdirSync(data, {recursive: true});
  fs.writeFileSync(path.join(data, 'config.json'), 'player sentinel');
  let result = install(); assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.readFileSync(path.join(data, 'config.json'), 'utf8'), 'player sentinel');
  assert.equal(fs.readlinkSync(path.join(home, '.local/bin/DusmpLauncher')), path.join(home, '.local/lib/DUSMPLauncher/DusmpLauncher'));
  const menu = path.join(home, '.local/share/applications/dusmp-launcher.desktop'), shortcut = path.join(home, 'Desktop/DUSMP Launcher.desktop');
  const desktop = fs.readFileSync(menu, 'utf8');
  assert.ok(desktop.includes('\\$percent%%'));
  assert.ok(desktop.includes(`Icon=${path.join(home, '.local/lib/DUSMPLauncher/icon.png')}`));
  assert.equal(fs.readFileSync(shortcut, 'utf8'), desktop);
  assert.equal(fs.statSync(menu).mode & 0o777, 0o644);
  assert.equal(fs.statSync(shortcut).mode & 0o777, 0o755);
  assert.equal(fs.readFileSync(path.join(home, '.local/lib/DUSMPLauncher/icon.png'), 'utf8'), 'Synthetic icon.');
  result = install(); assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.readFileSync(shortcut, 'utf8'), desktop);
  assert.deepEqual(fs.readdirSync(path.dirname(shortcut)), ['DUSMP Launcher.desktop']);
  assert.ok(fs.readdirSync(path.join(home, '.local/lib')).some(n => n.startsWith('DUSMPLauncher.previous.')));
  assert.equal(fs.readFileSync(path.join(data, 'config.json'), 'utf8'), 'player sentinel');
});

test('Linux installer respects localized desktop and custom XDG configuration and menu paths', {skip: process.platform !== 'linux'}, t => {
  const fixture = linuxInstaller(t), {directory, home, env, install} = fixture;
  env.XDG_CONFIG_HOME = path.join(directory, 'config with spaces');
  env.XDG_DATA_HOME = path.join(directory, 'custom data');
  fs.mkdirSync(env.XDG_CONFIG_HOME, {recursive: true});
  fs.writeFileSync(path.join(env.XDG_CONFIG_HOME, 'user-dirs.dirs'), '# Localized user desktop\nXDG_DESKTOP_DIR="$HOME/Área de trabalho"\nXDG_DOWNLOAD_DIR="$HOME/Downloads"\n');
  const result = install(); assert.equal(result.status, 0, result.stderr);
  const desktop = fs.readFileSync(path.join(home, 'Área de trabalho/DUSMP Launcher.desktop'), 'utf8');
  assert.equal(fs.readFileSync(path.join(env.XDG_DATA_HOME, 'applications/dusmp-launcher.desktop'), 'utf8'), desktop);
  assert.equal(fs.existsSync(path.join(home, 'Desktop')), false);
  assert.match(desktop, /Icon=application-x-executable/);
});

test('Linux installer uses xdg-user-dir when available and falls back safely for malicious user-dir configuration', {skip: process.platform !== 'linux'}, t => {
  const {directory, env, install} = linuxInstaller(t), tools = path.join(directory, 'tools'), custom = path.join(directory, 'External desktop');
  const home = env.HOME = path.join(directory, 'xdg-home'); env.XDG_CONFIG_HOME = path.join(home, '.config');
  fs.mkdirSync(tools); env.PATH = `${tools}${path.delimiter}${process.env.PATH}`;
  env.TEST_DESKTOP = custom;
  fs.writeFileSync(path.join(tools, 'xdg-user-dir'), '#!/bin/sh\nprintf "%s\\n" "$TEST_DESKTOP"\n', {mode: 0o755});
  let result = install(); assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(path.join(custom, 'DUSMP Launcher.desktop')), true);
  fs.mkdirSync(env.XDG_CONFIG_HOME, {recursive: true});
  const sentinel = path.join(directory, 'config-was-executed');
  fs.writeFileSync(path.join(env.XDG_CONFIG_HOME, 'user-dirs.dirs'), `XDG_DESKTOP_DIR="$HOME/Área segura"\nprintf hacked > "${sentinel}"\n`);
  // This command would execute the config if the installer called it.
  fs.writeFileSync(path.join(tools, 'xdg-user-dir'), '#!/bin/sh\n. "$XDG_CONFIG_HOME/user-dirs.dirs"\nprintf "%s\\n" "$XDG_DESKTOP_DIR"\n', {mode: 0o755});
  result = install(); assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(sentinel), false);
  assert.equal(fs.existsSync(path.join(home, 'Área segura/DUSMP Launcher.desktop')), true);
  fs.writeFileSync(path.join(env.XDG_CONFIG_HOME, 'user-dirs.dirs'), `XDG_DESKTOP_DIR="$(touch '${sentinel}')"\n`);
  result = install(); assert.equal(result.status, 0, result.stderr);
  assert.equal(fs.existsSync(sentinel), false);
  assert.equal(fs.existsSync(path.join(home, 'Desktop/DUSMP Launcher.desktop')), true);
});

test('Linux installer honors disabled desktop shortcuts without placing a launcher in the home root', {skip: process.platform !== 'linux'}, t => {
  const {home, env, install} = linuxInstaller(t);
  fs.mkdirSync(env.XDG_CONFIG_HOME, {recursive: true});
  fs.writeFileSync(path.join(env.XDG_CONFIG_HOME, 'user-dirs.dirs'), 'XDG_DESKTOP_DIR="$HOME/"\n');
  const result = install(); assert.equal(result.status, 0, result.stderr);
  assert.match(result.stderr, /Desktop shortcuts are disabled/);
  assert.equal(fs.existsSync(path.join(home, 'DUSMP Launcher.desktop')), false);
  assert.equal(fs.existsSync(path.join(home, '.local/share/applications/dusmp-launcher.desktop')), true);
});

test('Linux installer refuses redirected installation, menu and desktop directories before installing', {skip: process.platform !== 'linux'}, async t => {
  for (const target of ['.local/lib', '.local/share/applications', 'Desktop']) await t.test(target, t => {
    const {directory, home, install} = linuxInstaller(t), targetPath = path.join(home, target), outside = path.join(directory, 'outside');
    fs.mkdirSync(path.dirname(targetPath), {recursive: true}); fs.mkdirSync(outside);
    fs.symlinkSync(outside, targetPath, 'dir');
    const result = install(); assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /Symbolic links are refused/);
    assert.equal(fs.existsSync(path.join(home, '.local/lib/DUSMPLauncher')), false);
    assert.deepEqual(fs.readdirSync(outside), []);
  });
});

const testDotnet = process.env.DOTNET || path.join(os.homedir(), '.dotnet/dotnet');
test('Linux installer respects a real .NET FileShare.None runtime lock',
  {skip: process.platform !== 'linux' || !fs.existsSync(testDotnet), timeout: 45000}, async t => {
    const directory = temp(t), project = path.join(directory, 'LockProbe');
    fs.mkdirSync(project);
    fs.writeFileSync(path.join(project, 'LockProbe.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>');
    fs.writeFileSync(path.join(project, 'Program.cs'),
      'Directory.CreateDirectory(Path.GetDirectoryName(args[0])!); using var held = new FileStream(args[0], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Console.WriteLine("LOCKED"); Console.Out.Flush(); Console.ReadLine();');
    const build = spawnSync(testDotnet, ['build', project, '-c', 'Release', '--nologo', '--ignore-failed-sources'],
      {encoding: 'utf8', timeout: 30000, maxBuffer: 256 * 1024});
    assert.equal(build.status, 0, build.stderr + build.stdout);
    const home = path.join(directory, 'home'), bundle = path.join(directory, 'bundle'); fs.mkdirSync(bundle);
    for (const name of ['DusmpLauncher', 'DusmpUpdater']) fs.writeFileSync(path.join(bundle, name), '#!/bin/sh\nprintf "1.3.0\\n"\n', {mode: 0o755});
    fs.writeFileSync(path.join(bundle, 'README.txt'), 'Synthetic lock test.');
    fs.copyFileSync(path.resolve(__dirname, '../linux/install-launcher.sh'), path.join(bundle, 'install.sh'));
    const lock = path.join(home, '.local/share/DusmpLauncher/updates/instance.lock');
    const child = spawn(testDotnet, [path.join(project, 'bin/Release/net8.0/LockProbe.dll'), lock], {stdio: ['pipe', 'pipe', 'pipe']});
    t.after(() => {if (child.exitCode === null) child.kill('SIGKILL');});
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error('The native .NET lock probe did not become ready.')), 10000);
      child.stdout.on('data', data => {if (data.toString().includes('LOCKED')) {clearTimeout(timeout); resolve();}});
      child.once('exit', code => {clearTimeout(timeout); reject(new Error(`Lock probe exited early: ${code}`));});
      child.once('error', error => {clearTimeout(timeout); reject(error);});
    });
    const installed = spawnSync('bash', [path.join(bundle, 'install.sh')],
      {encoding: 'utf8', env: {...process.env, HOME: home, XDG_DATA_HOME: '', DUSMP_LAUNCHER_HOME: ''}, timeout: 10000});
    assert.equal(installed.status, 1);
    assert.match(installed.stderr, /Close DUSMP Launcher/);
    assert.equal(fs.existsSync(path.join(home, '.local/lib/DUSMPLauncher')), false);
    child.stdin.end('\n');
    await new Promise(resolve => child.once('exit', resolve));
  });
