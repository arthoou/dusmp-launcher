#!/usr/bin/env node
'use strict';
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { verifyEnvelope, parseStrictJson } = require('./manifest.cjs');

// Publication QA only. This override is never available to the launcher's updater.
const base = new URL(process.argv[2] || 'https://dusmp.online');
if (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname)))
  throw new Error('HTTP is allowed only for an isolated local test server.');
const request = (pathname, options = {}) => fetch(new URL(pathname, base), {
  redirect: 'error', signal: AbortSignal.timeout(120000), headers: { 'Accept-Encoding': 'identity', ...options.headers }, ...options,
});

(async () => {
  const response = await request('/launcher/releases/stable.json');
  assert.equal(response.status, 200);
  const release = verifyEnvelope(parseStrictJson(await response.text()));
  const metadata = await (await request('/api/launcher/releases')).json();
  assert.deepEqual(metadata, release, 'Website metadata must be the same verified release as the updater.');
  for (const artifact of release.artifacts) {
    const files = [artifact];
    if (artifact.download && artifact.download.url !== artifact.url) files.push(artifact.download);
    for (const file of files) {
      const pathname = new URL(file.url).pathname;
      const head = await request(pathname, { method: 'HEAD' });
      assert.equal(head.status, 200);
      assert.equal(Number(head.headers.get('content-length')), file.size);
      assert.equal(head.headers.get('etag'), `"sha256-${file.sha256}"`);
      const rangeEnd = Math.min(file.size - 1, 1023);
      const range = await request(pathname, { headers: { Range: `bytes=0-${rangeEnd}`, 'If-Range': head.headers.get('etag') } });
      assert.equal(range.status, 206);
      assert.equal((await range.arrayBuffer()).byteLength, rangeEnd + 1);
      const full = await request(pathname);
      assert.equal(full.status, 200);
      let received = 0;
      const hash = crypto.createHash('sha256');
      for await (const bytes of full.body) { received += bytes.length; hash.update(bytes); }
      assert.equal(received, file.size);
      assert.equal(hash.digest('hex'), file.sha256);
      console.log(`PASS HTTP: ${artifact.os} ${file.fileName}, ${received} bytes, signed SHA-256, HEAD and resume.`);
    }
  }
  const history = await (await request('/launcher/releases/history.json')).json();
  assert(history.releases.some(item => item.version === release.version));
  console.log(`PASS HTTP: signed stable ${release.version}, website metadata, real downloads and release history.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
