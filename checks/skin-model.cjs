// The player model is a reproduction of NameMC's viewer, and every number in it
// was read out of NameMC's own three.js scene. None of them is arbitrary, and
// none of them is obvious from the code alone, so this pins them down: the
// geometry, the texture rectangles, the legacy mirroring, the walk, and the
// shading — which is checked against brightnesses measured from NameMC's
// renderer rather than against the formula that produced them.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '../src/DusmpLauncher');
const html = fs.readFileSync(path.join(root, 'UI/profile.html'), 'utf8');
const css = fs.readFileSync(path.join(root, 'UI/css/profile.css'), 'utf8');

// ---- a DOM just real enough to build the model in ----

function element(tag = 'div') {
  const classes = new Set();
  const listeners = new Map();
  const node = {
    tag, dataset: {}, children: [], hidden: false, disabled: false, title: '', attributes: {}, value: '',
    style: {
      setProperty(name, value) { this[name] = String(value); },
      removeProperty(name) { delete this[name]; },
      getPropertyValue(name) { return this[name] ?? ''; },
    },
    classList: {
      add(n) { classes.add(n); }, remove(n) { classes.delete(n); },
      toggle(n, on) { const v = on === undefined ? !classes.has(n) : on; v ? classes.add(n) : classes.delete(n); return v; },
      contains(n) { return classes.has(n); },
    },
    setAttribute(n, v) { this.attributes[n] = v; },
    getAttribute(n) { return n in this.attributes ? this.attributes[n] : null; },
    addEventListener(n, h) { listeners.set(n, h); },
    // The page wires its buttons through onclick, not addEventListener, so a
    // double that only knew about listeners would quietly drop every click.
    fire(n, e) { listeners.get(n)?.(e ?? {}); if (n === 'click') node.onclick?.(e ?? {}); },
    append(...kids) { node.children.push(...kids); },
    replaceChildren() { node.children = []; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
    closest() { return null; },
    setPointerCapture() {}, releasePointerCapture() {},
    remove() {},
  };
  Object.defineProperty(node, 'className', {
    get() { return [...classes].join(' '); },
    set(v) { classes.clear(); for (const c of String(v).split(/\s+/).filter(Boolean)) classes.add(c); },
  });
  let content = '';
  Object.defineProperty(node, 'textContent', {
    get() { return content; },
    set(v) { content = String(v ?? ''); if (content === '') node.children = []; },
  });
  return node;
}

const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(m => [m[1], element()]));

// measureSkin probes the texture's height to tell a 64x64 sheet from a 64x32
// one. The test drives that by deciding what the next probe will report.
let probeHeight = 64;
const probes = [];
class FakeImage {
  set src(value) { this._src = value; probes.push(() => this.onload?.()); }
  get src() { return this._src; }
  get naturalHeight() { return probeHeight; }
  get naturalWidth() { return 64; }
}
const settleProbes = () => { while (probes.length) probes.shift()(); };

let receive;
const sent = [];
let respond = () => ({});
let frame = null;

const context = {
  console: { log: console.log, error(e) { throw e; } },
  setTimeout() {}, clearTimeout() {},
  requestAnimationFrame(cb) { frame = cb; return 1; },
  cancelAnimationFrame() { frame = null; },
  performance: { now: () => 0 },
  Image: FakeImage,
  document: {
    documentElement: {}, title: '', addEventListener() {}, createElement: element,
    getElementById(id) { return elements.get(id) || null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
  },
  window: {
    external: {
      receiveMessage(cb) { receive = cb; },
      sendMessage(raw) {
        const { id, command, payload } = JSON.parse(raw);
        sent.push({ command, payload });
        receive(JSON.stringify({ kind: 'response', id, ok: true, result: respond(command, payload) ?? {} }));
      },
    },
  },
};
context.window.Image = FakeImage;
context.window.requestAnimationFrame = context.requestAnimationFrame;

const SKIN = 'https://textures.minecraft.net/texture/deadbeef';
respond = command => {
  if (command === 'ready') return { state: { profile: { type: 'microsoft', username: 'Arthur', skinUrl: SKIN } }, language: { tag: 'pt-BR', table: {} } };
  if (command === 'getAppearance') {
    return {
      username: 'Arthur', variant: 'classic',
      skins: [{ id: 's1', url: SKIN, variant: 'classic', active: true }],
      capes: [{ id: 'c1', url: 'https://textures.minecraft.net/texture/cape', alias: 'Cape', active: true }],
    };
  }
  return {};
};

vm.runInNewContext(fs.readFileSync(path.join(root, 'UI/js/profile.js'), 'utf8'), context);
assert.equal(typeof receive, 'function', 'The skin window never subscribed to the host');

// ---- reading the model back ----

const model = elements.get('player-model');
const num = (el, name) => Number(el.style.getPropertyValue(name));
const round = v => Math.round(v * 1e6) / 1e6;
const partsByShape = () => model.children.map(p => ({
  cls: p.className,
  x: num(p, '--x'), y: num(p, '--y'), z: num(p, '--z'),
  w: num(p, '--w'), h: num(p, '--h'), d: num(p, '--d'),
  py: num(p, '--py'),
  faces: Object.fromEntries(p.children.map(f => [
    f.className.replace('face ', '').replace(' mirror', ''),
    {
      u: num(f, '--u'), v: num(f, '--v'),
      // The face is sized in box units and the texture is stretched to fit, so
      // the rectangle it actually samples is the face divided by that stretch.
      fw: round(num(f, '--fw') / num(f, '--sx')),
      fh: round(num(f, '--fh') / num(f, '--sy')),
      box: [num(f, '--fw'), num(f, '--fh')],
      mirror: f.classList.contains('mirror'),
    },
  ])),
}));
const find = (parts, test) => parts.find(test);

(async () => {
  await new Promise(resolve => setImmediate(resolve));
  settleProbes();
  await new Promise(resolve => setImmediate(resolve));

  // ---- geometry, in skin pixels from the top of the model ----
  //
  // NameMC measures from the model's centre with Y upwards; these are the same
  // boxes read from the top with Y downwards, which is how CSS lays them out.
  let parts = partsByShape();
  const base = parts.filter(p => !p.cls.includes('overlay') && !p.cls.includes('cape'));
  assert.equal(base.length, 6, 'A 64x64 skin has six body parts');

  const head = base[0], body = base[1];
  assert.deepEqual([head.x, head.y, head.w, head.h, head.d], [0, 0, 8, 8, 8], 'Head box');
  assert.deepEqual([body.x, body.w, body.d], [0, 8.001, 4.001], 'Body box, nudged off the arms by a thousandth');
  assert.ok(Math.abs(body.y + body.h / 2 - 14) < 1e-9, 'The body is centred 14 pixels down');

  const rightArm = find(parts, p => p.cls.includes('limb-right-arm') && !p.cls.includes('overlay'));
  const leftArm = find(parts, p => p.cls.includes('limb-left-arm') && !p.cls.includes('overlay'));
  const rightLeg = find(parts, p => p.cls.includes('limb-right-leg') && !p.cls.includes('overlay'));
  assert.deepEqual([rightArm.x, rightArm.y, rightArm.w, rightArm.h, rightArm.d], [-6, 8, 4, 12, 4], 'Classic right arm');
  assert.deepEqual([leftArm.x, leftArm.w], [6, 4], 'Classic left arm');
  assert.deepEqual([rightLeg.x, rightLeg.y, rightLeg.w, rightLeg.h, rightLeg.d], [-2, 20, 4, 12, 4], 'Right leg');
  // Shoulders two pixels into the arm, hips at the very top of the leg.
  assert.equal(rightArm.py, 2, 'The arm pivots at the shoulder');
  assert.equal(rightLeg.py, 0, 'The leg pivots at the hip');

  const hat = parts.find(p => p.cls.includes('overlay') && p.w === 9);
  assert.deepEqual([hat.x, hat.y, hat.w, hat.h, hat.d], [0, -0.5, 9, 9, 9], 'The hat is inflated half a pixel on every side');
  const jacket = parts.find(p => p.cls.includes('overlay') && p.w > 8.4 && p.w < 8.6);
  assert.ok(Math.abs(jacket.y + jacket.h / 2 - 14) < 1e-9, 'The jacket stays centred on the body it covers');
  const sleeve = parts.find(p => p.cls.includes('overlay') && p.cls.includes('right-arm'));
  assert.ok(Math.abs(sleeve.y + sleeve.h / 2 - 14) < 1e-9, 'The sleeve stays centred on the arm it covers');
  assert.equal(sleeve.py, 14 - sleeve.h / 2 === sleeve.y ? 10 - sleeve.y : sleeve.py, 'sanity');
  assert.ok(Math.abs(sleeve.py - (10 - sleeve.y)) < 1e-9, 'The sleeve pivots on the same shoulder as the arm');

  // Overlays that touch each other get their own multiple of a thousandth of a
  // pixel, so no two coplanar faces ever tie for depth and flicker. The two
  // sleeves are allowed to match: they are on opposite sides of the body and
  // share no plane. The two trouser legs do touch, at the centre line.
  const widthOf = test => parts.find(p => p.cls.includes('overlay') && test(p)).w;
  const jacketW = widthOf(p => p.w > 8.4 && p.w < 8.6);
  const sleeveW = widthOf(p => p.cls.includes('right-arm'));
  const rightPantW = widthOf(p => p.cls.includes('right-leg'));
  const leftPantW = widthOf(p => p.cls.includes('left-leg'));
  assert.notEqual(rightPantW, leftPantW, 'The trouser legs touch at the centre line and must not tie for depth');
  assert.equal(new Set([jacketW, sleeveW, rightPantW, leftPantW]).size, 4, 'Overlays that overlap must each have their own size');

  // ---- texture rectangles ----
  //
  // Minecraft packs a box as left, front, right, back across the row below top
  // and bottom. Checked here against the head, whose numbers everyone knows.
  assert.deepEqual(head.faces.left, { u: 0, v: 8, fw: 8, fh: 8, box: [8, 8], mirror: false });
  assert.deepEqual(head.faces.front, { u: 8, v: 8, fw: 8, fh: 8, box: [8, 8], mirror: false });
  assert.deepEqual(head.faces.right, { u: 16, v: 8, fw: 8, fh: 8, box: [8, 8], mirror: false });
  assert.deepEqual(head.faces.back, { u: 24, v: 8, fw: 8, fh: 8, box: [8, 8], mirror: false });
  assert.deepEqual(head.faces.top, { u: 8, v: 0, fw: 8, fh: 8, box: [8, 8], mirror: false });
  assert.deepEqual(head.faces.bottom, { u: 16, v: 0, fw: 8, fh: 8, box: [8, 8], mirror: false });
  // The underside is stored upside down, and the CSS is what turns it back.
  assert.match(css, /\.bottom > i \{ --fy: -1; \}/, 'The underside must be flipped back');

  assert.deepEqual([body.faces.front.u, body.faces.front.v, Math.round(body.faces.front.fw), Math.round(body.faces.front.fh)], [20, 20, 8, 12], 'Body front at (20,20)');
  assert.deepEqual([rightArm.faces.front.u, rightArm.faces.front.v, rightArm.faces.front.fw, rightArm.faces.front.fh], [44, 20, 4, 12], 'Right arm front at (44,20)');
  assert.deepEqual([leftArm.faces.front.u, leftArm.faces.front.v], [36, 52], 'A 64x64 left arm has a texture of its own at (32,48)');
  assert.deepEqual([rightLeg.faces.front.u, rightLeg.faces.front.v], [4, 20], 'Right leg front at (4,20)');

  // A face covers its whole side of the box, whatever that box was inflated to.
  // The hat is the loud case: a 9x9x9 box carrying an 8x8 texture, which, sized
  // from the texture instead, left the layer pushed into one corner with a gap
  // down the other two sides.
  for (const [name, f] of Object.entries(hat.faces)) {
    assert.deepEqual(f.box, [9, 9], `The hat's ${name} face must fill the inflated box, not the 8x8 texture on it`);
    assert.deepEqual([f.fw, f.fh], [8, 8], `The hat's ${name} face still samples an 8x8 rectangle`);
  }
  for (const [name, f] of Object.entries(body.faces)) {
    const [bw, bh] = { front: [body.w, body.h], back: [body.w, body.h], left: [body.d, body.h], right: [body.d, body.h], top: [body.w, body.d], bottom: [body.w, body.d] }[name];
    assert.deepEqual(f.box, [bw, bh], `The body's ${name} face must match the body box`);
  }
  const sleevePart = parts.find(p => p.cls.includes('overlay') && p.cls.includes('right-arm'));
  assert.deepEqual(sleevePart.faces.front.box, [sleevePart.w, sleevePart.h], 'The sleeve must fill its own inflated box');

  const cape = model.children.find(p => p.className.includes('cape-model'));
  const capeFaces = Object.fromEntries(cape.children.map(f => [f.className.replace('face ', ''), [num(f, '--u'), num(f, '--v'), num(f, '--fw'), num(f, '--fh')]]));
  assert.deepEqual([num(cape, '--w'), num(cape, '--h'), num(cape, '--d')], [10, 16, 1], 'Cape box');
  assert.deepEqual([num(cape, '--y'), num(cape, '--z')], [8, -2.5], 'The cape hangs from the top of the back, in the pixel behind the body');
  assert.deepEqual(capeFaces.front, [1, 1, 10, 16], 'The outer side of the cape is the rectangle at (1,1)');
  assert.deepEqual(capeFaces.back, [12, 1, 10, 16], 'The inner side of the cape is the rectangle at (12,1)');

  // ---- slim arms ----
  elements.get('btn-model-slim').fire('click');
  await new Promise(resolve => setImmediate(resolve));
  settleProbes();
  parts = partsByShape();
  const slimArm = find(parts, p => p.cls.includes('limb-right-arm') && !p.cls.includes('overlay'));
  assert.deepEqual([slimArm.x, slimArm.w], [-5.5, 3], 'A slim arm is three pixels wide and sits half a pixel closer in');
  assert.deepEqual([slimArm.faces.front.u, slimArm.faces.front.v, slimArm.faces.front.fw], [44, 20, 3], 'Slim front face is three pixels wide');
  assert.deepEqual([slimArm.faces.right.u, slimArm.faces.right.v, slimArm.faces.right.fw], [47, 20, 4], 'Slim side face still reads four pixels of depth, from one column earlier');
  elements.get('btn-model-classic').fire('click');
  await new Promise(resolve => setImmediate(resolve));
  settleProbes();

  // ---- a legacy 64x32 sheet ----
  //
  // It has no left limbs and no second layer beyond the hat: the left arm and
  // leg are the right ones mirrored, which swaps their two side faces as well
  // as flipping each one.
  probeHeight = 32;
  elements.get('btn-model-classic').fire('click');
  await new Promise(resolve => setImmediate(resolve));
  settleProbes();
  await new Promise(resolve => setImmediate(resolve));
  parts = partsByShape();
  assert.equal(parts.filter(p => p.cls.includes('overlay')).length, 1, 'A legacy sheet has only the hat as a second layer');
  const legacyLeftArm = find(parts, p => p.cls.includes('limb-left-arm'));
  const legacyLeftLeg = find(parts, p => p.cls.includes('limb-left-leg'));
  assert.ok(Object.values(legacyLeftArm.faces).every(f => f.mirror), 'Every face of a legacy left arm is mirrored');
  // Right arm block at (40,16): -X at 40, front at 44, +X at 48, back at 52.
  assert.deepEqual([legacyLeftArm.faces.front.u, legacyLeftArm.faces.front.v, legacyLeftArm.faces.front.mirror], [44, 20, true], 'Legacy left arm front is the right arm front, mirrored');
  assert.deepEqual([legacyLeftArm.faces.left.u, legacyLeftArm.faces.left.v], [48, 20], 'Mirroring swaps the two side faces');
  assert.deepEqual([legacyLeftArm.faces.right.u, legacyLeftArm.faces.right.v], [40, 20], 'Mirroring swaps the two side faces');
  assert.deepEqual([legacyLeftLeg.faces.front.u, legacyLeftLeg.faces.front.v], [4, 20], 'Legacy left leg comes from the right leg at (0,16)');
  assert.deepEqual([legacyLeftLeg.faces.left.u, legacyLeftLeg.faces.left.v], [8, 20]);
  assert.match(css, /\.mirror > i \{ --fx: -1; \}/, 'A mirrored face must actually be flipped');
  probeHeight = 64;

  // ---- the camera ----
  //
  // A 38 degree lens sixty skin-pixels back. Written as a multiple of the zoom
  // so the lens stays put while the wheel changes the size.
  assert.match(css, /perspective: calc\(60 \* var\(--skin-scale\)\)/, 'The camera must sit sixty skin-pixels back');

  // ---- the walk ----
  const swing = name => parseFloat(model.style.getPropertyValue(`--swing-${name}`));
  // It opens on the frame NameMC shows in its stills: the stride fully extended.
  assert.ok(Math.abs(swing('right-arm') - 18) < 1e-6, 'The arms reach 18 degrees');
  assert.ok(Math.abs(swing('left-arm') + 18) < 1e-6, 'The arms swing in opposition');
  assert.ok(Math.abs(swing('right-leg') + 20) < 1e-6, 'The legs reach 20 degrees');
  assert.ok(Math.abs(swing('left-leg') - 20) < 1e-6, 'The legs swing in opposition');
  // Opposite limbs move together: a right arm forward goes with a left leg forward.
  assert.ok(swing('right-arm') * swing('left-leg') > 0, 'Opposite arm and leg swing together');
  const tilt = parseFloat(model.style.getPropertyValue('--cape-tilt'));
  assert.ok(tilt >= -24 && tilt <= -12, `The cape hangs 18 degrees out, breathing 6 either way, not ${tilt}`);

  // ---- the light ----
  //
  // Ambient 0.7 and a directional 0.3 from (0.678, 0.284, 0.678). At the pose
  // the window opens on, NameMC's own renderer puts a white texture at 240 on
  // the face, 217 on the top and 178 on the side the light cannot reach. These
  // are those three numbers, read back off the model.
  const shade = (set, face) => Number(model.style.getPropertyValue(`--${set}-${face}`));
  // Within half a byte of what NameMC's renderer produced: the ambient 0.7
  // lands on 178.5 exactly, and which way that tie falls is the rasteriser's
  // business, not this model's.
  const matchesByte = (set, face, expected, why) => {
    const got = shade(set, face) * 255;
    assert.ok(Math.abs(got - expected) <= 0.5 + 1e-6, `${why}: ${set}-${face} is ${got.toFixed(1)}, NameMC renders ${expected}`);
  };
  matchesByte('s0', 'front', 240, 'Front face brightness must match NameMC');
  matchesByte('s0', 'top', 217, 'Top face brightness must match NameMC');
  matchesByte('s0', 'left', 178, 'Unlit faces sit at the ambient 0.7');
  matchesByte('s0', 'back', 178, 'Unlit faces sit at the ambient 0.7');
  matchesByte('s0', 'bottom', 178, 'Unlit faces sit at the ambient 0.7');
  // A limb that has swung is no longer facing where the body faces, so it gets
  // a set of its own rather than sharing the body's.
  assert.notEqual(shade('s1', 'front'), shade('s0', 'front'), 'A swung arm must be lit for where it is pointing');
  assert.notEqual(shade('s3', 'front'), shade('s1', 'front'), 'Arms and legs swing by different amounts and so differ');
  for (const set of ['s0', 's1', 's2', 's3', 's4', 's5']) {
    for (const face of ['front', 'back', 'left', 'right', 'top', 'bottom']) {
      const v = shade(set, face);
      assert.ok(v >= 0.7 - 1e-9 && v <= 1 + 1e-9, `${set}-${face} outside the 0.7 to 1.0 the rig can produce`);
    }
  }
  // Painting black at 1 - brightness is the same as multiplying by it, and
  // unlike a filter it cannot flatten the 3D scene.
  assert.match(css, /opacity: calc\(1 - var\(--shade, 1\)\)/, 'The shading veil must darken by 1 - brightness');
  // A face is one flat quad: left in 3D, its texture and veil keep their own
  // backface-visibility and draw straight through the faces in front of them.
  assert.match(css, /\.screen-profile \.face \{[^}]*transform-style: flat;/s, 'A face must be flat so backface culling covers its contents');

  console.log('PASS: model geometry, texture rectangles, slim arms, legacy mirroring, camera, walk and NameMC-matched shading.');
})().catch(error => { console.error(error); process.exit(1); });
