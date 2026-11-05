#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {spawnSync} = require('node:child_process');
const contract = require('./manifest.cjs');
const {validateLinux, validateWindows} = require('./binary.cjs');
const {requireThat, assertNoSymlinks, compareVersions, describeFile, verifyEnvelope, signPayload, verifyDirectory} = contract;
const ROOT = path.resolve(__dirname, '../..');
const DEFAULT_KEY = path.join(os.homedir(), '.local/share/DUSMPRelease/signing-key.pem');

function options(argv) {
  const result = {command: argv[0] || 'help', version: null, minimumVersion: '1.2.0', mandatory: false, skipBuild: false,
    siteRoot: path.resolve(ROOT, '../DUSMP site'), outputDir: null, keyFile: process.env.DUSMP_RELEASE_KEY_FILE || DEFAULT_KEY,
    notes: 'Atualizações de distribuição e instalação segura do DUSMP Launcher.'};
  const values = {'--version': 'version', '--minimum-version': 'minimumVersion', '--site-root': 'siteRoot', '--output-dir': 'outputDir', '--key-file': 'keyFile', '--notes-file': 'notesFile'};
  for (let i = 1; i < argv.length; i++) {
    const item = argv[i];
    if (item === '--mandatory') result.mandatory = true;
    else if (item === '--skip-build') result.skipBuild = true;
    else if (Object.hasOwn(values, item)) {
      requireThat(i + 1 < argv.length && !argv[i + 1].startsWith('--'), `Missing value for ${item}.`);
      result[values[item]] = argv[++i];
    } else throw new Error(`Unknown option: ${item}`);
  }
  if (!result.version && result.command !== 'keygen' && result.command !== 'help') {
    result.version = fs.readFileSync(path.join(ROOT, 'src/DusmpLauncher/DusmpLauncher.csproj'), 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1];
  }
  if (result.notesFile) {
    assertNoSymlinks(result.notesFile);
    requireThat(fs.statSync(result.notesFile).size <= 32000, 'Release notes file is too large.');
    result.notes = fs.readFileSync(result.notesFile, 'utf8').trim();
  }
  return result;
}

function run(command, args, settings = {}) {
  const result = spawnSync(command, args, {cwd: ROOT, stdio: 'inherit', ...settings});
  if (result.error) throw result.error;
  requireThat(result.status === 0, `${path.basename(command)} failed (exit ${result.status}).`);
  return result;
}

function atomicWrite(file, value) {
  assertNoSymlinks(file);
  const temporary = `${file}.tmp-${crypto.randomUUID()}`;
  const fd = fs.openSync(temporary, 'wx', 0o644);
  try { fs.writeFileSync(fd, value); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
  try { fs.renameSync(temporary, file); syncDirectory(path.dirname(file)); }
  finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
}

function syncDirectory(directory) {
  // POSIX fsync persists the rename itself, not just the JSON contents.
  if (process.platform === 'win32') return;
  const fd = fs.openSync(directory, 'r');
  try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
}

function syncTree(directory) {
  for (const item of fs.readdirSync(directory, {withFileTypes: true})) {
    requireThat(!item.isSymbolicLink(), 'Release staging cannot contain symbolic links.');
    const file = path.join(directory, item.name);
    if (item.isDirectory()) syncTree(file);
    else {
      requireThat(item.isFile(), 'Release staging must contain only regular files.');
      const fd = fs.openSync(file, 'r');
      try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
    }
  }
  syncDirectory(directory);
}

function envelopeFile(file) {
  assertNoSymlinks(file);
  requireThat(fs.statSync(file).isFile() && fs.statSync(file).size <= 256 * 1024, 'Manifest file is invalid/too large.');
  const envelope = contract.parseStrictJson(fs.readFileSync(file, 'utf8'));
  verifyEnvelope(envelope);
  return envelope;
}

function privateKey(file) {
  assertNoSymlinks(file);
  const stat = fs.statSync(file);
  requireThat(stat.isFile() && stat.size <= 16384, 'Signing key must be a regular PEM file.');
  if (process.platform !== 'win32') requireThat((stat.mode & 0o077) === 0 && stat.uid === process.getuid(), 'Signing key must be owned by the publisher and have mode 0600.');
  const key = crypto.createPrivateKey(fs.readFileSync(file));
  requireThat(key.asymmetricKeyType === 'ec' && key.asymmetricKeyDetails.namedCurve === 'prime256v1', 'Signing key must use ECDSA P-256.');
  const actual = crypto.createPublicKey(key).export({type: 'spki', format: 'der'});
  const expected = crypto.createPublicKey(contract.PUBLIC_KEY).export({type: 'spki', format: 'der'});
  requireThat(actual.equals(expected), 'Signing key does not match the public key embedded in this release.');
  return key;
}

function generateKey(file) {
  assertNoSymlinks(file);
  requireThat(!fs.existsSync(file), 'Refusing to overwrite an existing signing key. Back it up securely.');
  const publicFile = path.join(path.dirname(file), 'release-public-key.pem');
  assertNoSymlinks(publicFile);
  requireThat(!fs.existsSync(publicFile), 'Refusing to overwrite an existing public key. Choose a separate key directory.');
  fs.mkdirSync(path.dirname(file), {recursive: true, mode: 0o700});
  if (process.platform !== 'win32') fs.chmodSync(path.dirname(file), 0o700);
  const pair = crypto.generateKeyPairSync('ec', {namedCurve: 'prime256v1'});
  fs.writeFileSync(file, pair.privateKey.export({type: 'pkcs8', format: 'pem'}), {flag: 'wx', mode: 0o600});
  fs.writeFileSync(publicFile, pair.publicKey.export({type: 'spki', format: 'pem'}), {flag: 'wx', mode: 0o644});
  console.log(`Signing key created securely. Public key exported to ${publicFile}.`);
  console.log('Before production use, embed this public key in the launcher and website. Replacing an existing key requires a planned rotation.');
}

function releaseRoot(siteRoot) {
  assertNoSymlinks(siteRoot);
  requireThat(fs.statSync(siteRoot).isDirectory() && fs.existsSync(path.join(siteRoot, 'package.json')), 'Site root must be the existing DUSMP site project.');
  const directory = path.resolve(siteRoot, 'data/launcher-releases');
  assertNoSymlinks(directory);
  fs.mkdirSync(directory, {recursive: true});
  return directory;
}

async function locked(directory, action, name = '.publish.lock') {
  const file = path.join(directory, name);
  assertNoSymlinks(file);
  let fd;
  try { fd = fs.openSync(file, 'wx', 0o600); }
  catch (error) { if (error.code === 'EEXIST') throw new Error(`Publication lock exists at ${file}. Confirm the prior publisher exited before removing that lock.`); throw error; }
  fs.writeFileSync(fd, JSON.stringify({pid: process.pid, host: os.hostname(), startedAt: new Date().toISOString()}));
  try { return await action(); } finally { fs.closeSync(fd); fs.unlinkSync(file); }
}

function currentRelease(directory) {
  const stable = path.join(directory, 'stable.json');
  return fs.existsSync(stable) ? verifyEnvelope(envelopeFile(stable)) : null;
}

function requireUpgrade(directory, version) {
  contract.versionParts(version);
  const current = currentRelease(directory);
  requireThat(!current || compareVersions(version, current.version) > 0, 'Publication must increase the stable version. Replacements and downgrades are refused.');
}

function checkSpace(directory, requiredBytes) {
  if (!fs.statfsSync) return;
  const stat = fs.statfsSync(directory);
  requireThat(stat.bavail * stat.bsize >= requiredBytes, 'Insufficient free space for release staging.');
}

function publishProjects(buildRoot, version) {
  const dotnet = process.env.DOTNET || path.join(os.homedir(), '.dotnet/dotnet');
  for (const [osName, rid] of [['linux', 'linux-x64'], ['win', 'win-x64']]) {
    for (const [component, project] of [['launcher', 'DusmpLauncher'], ['updater', 'DusmpUpdater']]) {
      run(dotnet, ['publish', path.join(ROOT, 'src', project), '-c', 'Release', '--self-contained', 'true', '-r', rid,
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        `-p:Version=${version}`, `-p:AssemblyVersion=${version}.0`, `-p:FileVersion=${version}.0`,
        '-o', path.join(buildRoot, osName, component)]);
    }
  }
}

function bundleReadme(version) {
  return `DUSMP Launcher ${version} - Linux x86-64\n\n` +
    'Requirements: glibc 2.38+; GTK 3; WebKitGTK 4.1; graphical desktop.\n' +
    'Debian/Ubuntu packages: libgtk-3-0 (or libgtk-3-0t64), libwebkit2gtk-4.1-0.\n' +
    'Extract this archive, then run: bash install.sh\n' +
    'The installer creates shortcuts in the application menu and on your desktop.\n' +
    'Or run ./DusmpLauncher directly from a writable user-owned directory.\n' +
    'No sudo is needed. Do not move it into /usr/bin or a package-manager-owned directory.\n' +
    'The launcher verifies signed updates automatically before opening the main interface.\n' +
    'Accounts/settings/Minecraft data: ~/.local/share/DusmpLauncher (kept during updates).\n' +
    'DusmpUpdater is the separate optional background modpack updater. The installer does not enable a system service.\n' +
    'The launcher update mechanism does not update package-manager-owned installations.\n' +
    'This is a tar.gz bundle, not an AppImage. WebKitGTK and GTK must be installed.\n';
}

async function createVersion(stage, sources, opts, signingKey) {
  const version = opts.version;
  const linuxSource = path.join(sources, 'linux/launcher/DusmpLauncher');
  const windowsSource = path.join(sources, 'win/launcher/DusmpLauncher.exe');
  validateLinux(linuxSource, version);
  validateWindows(windowsSource, version);
  validateLinux(path.join(sources, 'linux/updater/DusmpUpdater'), version);
  validateWindows(path.join(sources, 'win/updater/DusmpUpdater.exe'), version);
  const artifacts = [];
  for (const platform of ['linux', 'windows']) {
    const platformDirectory = path.join(stage, `${platform}-x64`);
    fs.mkdirSync(platformDirectory);
    const fileName = platform === 'linux' ? 'DusmpLauncher' : 'DusmpLauncher.exe';
    const destination = path.join(platformDirectory, fileName);
    fs.copyFileSync(platform === 'linux' ? linuxSource : windowsSource, destination, fs.constants.COPYFILE_EXCL);
    fs.chmodSync(destination, platform === 'linux' ? 0o755 : 0o644);
    const base = `https://dusmp.online/launcher/releases/${version}/${platform}-x64/`;
    const artifact = {os: platform, arch: 'x64', fileName, url: base + fileName, ...await describeFile(destination)};
    if (platform === 'linux') {
      const bundle = path.join(stage, '.linux-bundle');
      const nested = path.join(bundle, 'DUSMP-Launcher');
      fs.mkdirSync(nested, {recursive: true});
      for (const executable of ['DusmpLauncher', 'DusmpUpdater']) {
        fs.copyFileSync(path.join(sources, `linux/${executable === 'DusmpLauncher' ? 'launcher' : 'updater'}/${executable}`), path.join(nested, executable));
        fs.chmodSync(path.join(nested, executable), 0o755);
      }
      fs.copyFileSync(path.join(ROOT, 'packaging/linux/install-launcher.sh'), path.join(nested, 'install.sh'));
      fs.chmodSync(path.join(nested, 'install.sh'), 0o755);
      const icon = path.join(ROOT, 'src/DusmpLauncher/UI/assets/icon.png');
      if (fs.existsSync(icon)) fs.copyFileSync(icon, path.join(nested, 'icon.png'));
      fs.writeFileSync(path.join(nested, 'README.txt'), bundleReadme(version), {mode: 0o644});
      const downloadName = `DUSMP-Launcher-${version}-linux-x64.tar.gz`;
      const downloadPath = path.join(platformDirectory, downloadName);
      run('tar', ['-czf', downloadPath, '-C', bundle, 'DUSMP-Launcher']);
      // The bundle is generated only from the controlled staging directory.
      const listing = spawnSync('tar', ['-tzf', downloadPath], {encoding: 'utf8', maxBuffer: 64 * 1024});
      requireThat(listing.status === 0 && listing.stdout.split('\n').filter(Boolean).every(p =>
        /^DUSMP-Launcher\/(?:DusmpLauncher|DusmpUpdater|install\.sh|README\.txt|icon\.png)?$/.test(p)), 'Linux archive contains unexpected paths.');
      artifact.download = {url: base + downloadName, fileName: downloadName, ...await describeFile(downloadPath), format: 'tar.gz',
        compatibility: 'Linux x86-64, glibc 2.38+, GTK 3 e WebKitGTK 4.1. Instalação por usuário.'};
      fs.rmSync(bundle, {recursive: true});
    } else {
      artifact.download = {url: artifact.url, fileName, size: artifact.size, sha256: artifact.sha256, format: 'exe',
        compatibility: 'Windows 10/11 x64 com Microsoft WebView2 Runtime. Cria atalhos na área de trabalho e no menu Iniciar ao concluir a configuração.'};
    }
    artifacts.push(artifact);
  }
  const payload = {schemaVersion: 1, channel: 'stable', version, minimumVersion: opts.minimumVersion, mandatory: opts.mandatory,
    publishedAt: new Date().toISOString(), notes: opts.notes, artifacts};
  const envelope = signPayload(payload, signingKey);
  atomicWrite(path.join(stage, 'manifest.json'), JSON.stringify(envelope, null, 2) + '\n');
  await verifyDirectory(stage, envelope);
  return envelope;
}

async function announce(directory, version) {
  requireUpgrade(directory, version);
  const folder = path.join(directory, version);
  const envelope = envelopeFile(path.join(folder, 'manifest.json'));
  const payload = await verifyDirectory(folder, envelope);
  requireThat(payload.version === version, 'Version directory and signed manifest differ.');
  validateLinux(path.join(folder, 'linux-x64/DusmpLauncher'), version, process.platform === 'linux');
  validateWindows(path.join(folder, 'windows-x64/DusmpLauncher.exe'), version);
  const history = [];
  for (const name of fs.readdirSync(directory)) {
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(name)) continue;
    const file = path.join(directory, name, 'manifest.json');
    const prior = envelopeFile(file);
    requireThat(verifyEnvelope(prior).version === name, 'Release history directory mismatch.');
    history.push(prior);
  }
  history.sort((a, b) => compareVersions(verifyEnvelope(b).version, verifyEnvelope(a).version));
  atomicWrite(path.join(directory, 'history.json'), JSON.stringify(history, null, 2) + '\n');
  // All bytes are present, re-read, authenticated and checked before this switch.
  atomicWrite(path.join(directory, 'stable.json'), JSON.stringify(envelope, null, 2) + '\n');
  console.log(`Published local stable release ${version}: Windows x64 + Linux x64. No remote deployment was performed.`);
}

async function stageVersion(destination, opts) {
  assertNoSymlinks(destination);
  requireThat(!fs.existsSync(destination), 'Version directory already exists. It cannot be overwritten.');
  const directory = path.dirname(destination), key = privateKey(opts.keyFile);
  checkSpace(directory, 1024 * 1024 * 1024);
  const stage = fs.mkdtempSync(path.join(directory, '.staging-'));
  let buildRoot;
  try {
    buildRoot = opts.skipBuild ? path.join(ROOT, 'dist') : fs.mkdtempSync(path.join(os.tmpdir(), 'dusmp-release-build-'));
    if (!opts.skipBuild) publishProjects(buildRoot, opts.version);
    await createVersion(stage, buildRoot, opts, key);
    syncTree(stage);
    assertNoSymlinks(destination);
    requireThat(!fs.existsSync(destination), 'Version directory already exists. It cannot be overwritten.');
    fs.renameSync(stage, destination);
    syncDirectory(directory);
  } finally {
    if (fs.existsSync(stage)) fs.rmSync(stage, {recursive: true});
    if (!opts.skipBuild && buildRoot && fs.existsSync(buildRoot)) fs.rmSync(buildRoot, {recursive: true});
  }
}

async function prepare(opts) {
  requireThat(typeof opts.outputDir === 'string' && opts.outputDir.trim().length > 0, '--output-dir is required for prepare.');
  const destination = path.resolve(opts.outputDir), directory = path.dirname(destination);
  assertNoSymlinks(directory);
  requireThat(fs.statSync(directory).isDirectory(), 'Output parent must be an existing directory.');
  assertNoSymlinks(destination);
  const lockName = `.prepare-${crypto.createHash('sha256').update(destination).digest('hex').slice(0, 16)}.lock`;
  return locked(directory, async () => {
    await stageVersion(destination, opts);
    console.log(`Prepared signed release ${opts.version} at ${destination}. Upload manifest.json and its three artifact files in the launcher panel. No stable release was changed.`);
  }, lockName);
}

async function publish(opts) {
  const directory = releaseRoot(opts.siteRoot);
  return locked(directory, async () => {
    requireUpgrade(directory, opts.version);
    const destination = path.join(directory, opts.version);
    await stageVersion(destination, opts);
    await announce(directory, opts.version);
  });
}

async function main(argv) {
  const opts = options(argv);
  if (opts.command === 'help' || opts.command === '--help') {
    console.log('node packaging/release/release.cjs publish [--version X.Y.Z] [--minimum-version X.Y.Z] [--mandatory] [--notes-file PATH] [--site-root PATH] [--skip-build]');
    console.log('node packaging/release/release.cjs prepare --version X.Y.Z --output-dir PATH [--minimum-version X.Y.Z] [--mandatory] [--notes-file PATH] [--key-file PATH] [--skip-build]');
    console.log('node packaging/release/release.cjs verify|announce --version X.Y.Z [--site-root PATH]');
    console.log('node packaging/release/release.cjs keygen [--key-file PATH]');
    return;
  }
  if (opts.command === 'keygen') return generateKey(opts.keyFile);
  contract.versionParts(opts.version);
  if (opts.command === 'prepare') return prepare(opts);
  if (opts.command === 'publish') return publish(opts);
  if (opts.command === 'announce') {
    const directory = releaseRoot(opts.siteRoot);
    return locked(directory, () => announce(directory, opts.version));
  }
  if (opts.command === 'verify') {
    const directory = releaseRoot(opts.siteRoot), folder = path.join(directory, opts.version);
    const payload = await verifyDirectory(folder, envelopeFile(path.join(folder, 'manifest.json')));
    requireThat(payload.version === opts.version, 'Version mismatch.');
    validateLinux(path.join(folder, 'linux-x64/DusmpLauncher'), opts.version, process.platform === 'linux');
    validateWindows(path.join(folder, 'windows-x64/DusmpLauncher.exe'), opts.version);
    console.log(`Verified signed release ${opts.version}: both executable headers/versions, sizes, SHA-256 hashes and download files.`);
    return;
  }
  throw new Error('Unknown command. Use help.');
}

if (require.main === module) main(process.argv.slice(2)).catch(error => { console.error(`Release aborted: ${error.message}`); process.exitCode = 1; });
module.exports = {options, atomicWrite, envelopeFile, privateKey, generateKey, locked, requireUpgrade, createVersion, announce, prepare, publish, main};
