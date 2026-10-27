// Skin and cape window.
//
// A window of its own, the way Prism opens its account editor, so the player can
// leave it beside the launcher while they try skins. It speaks the same bridge
// protocol as the main page — Photino gives every window its own
// window.external — so nothing here needs a second backend.
(() => {
  'use strict';

  const $ = id => document.getElementById(id);
  let table = {}, state = { profile: {} };

  // Same request/response shape as the main page, minus the screen plumbing.
  const pending = new Map();
  let nextId = 0;
  function send(command, payload = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++nextId);
      pending.set(id, { resolve, reject });
      window.external.sendMessage(JSON.stringify({ id, command, payload }));
    });
  }
  window.external.receiveMessage(raw => {
    let message; try { message = JSON.parse(raw); } catch { return; }
    if (message.kind === 'response') {
      const entry = pending.get(message.id);
      if (!entry) return;
      pending.delete(message.id);
      message.ok ? entry.resolve(message.result)
                 : entry.reject(Object.assign(Error(message.error?.message || 'failed'), { key: message.error?.key }));
      return;
    }
    // The launcher pushes a new state when the account or skin changes.
    if (message.kind === 'event' && message.event === 'screen') { state = message.payload || state; renderAppearance(); }
    if (message.kind === 'event' && message.event === 'language') { table = message.payload?.table || table; applyText(); }
  });

  const t = (key, args = {}) => Object.entries(args)
    .reduce((s, [k, v]) => s.replaceAll(`{${k}}`, v), table[key] ?? key);
  const text = (id, value) => { const el = $(id); if (el) el.textContent = value ?? ''; };
  function escapeHtml(x) { return String(x ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
  function applyText() {
    document.querySelectorAll('[data-i18n]').forEach(e => e.textContent = t(e.dataset.i18n));
    document.querySelectorAll('[data-i18n-aria-label]').forEach(e => e.setAttribute('aria-label', t(e.dataset.i18nAriaLabel)));
    document.title = t('profile.title');
  }

  function bind(id, handler) { const el = $(id); if (el) el.onclick = handler; }

// ---------- the player model ----------
//
// A reproduction of NameMC's viewer, built out of CSS 3D boxes instead of
// WebGL: one box per body part, six faces each, every face showing its
// rectangle of the skin PNG. The numbers below are not guesses — they were read
// out of NameMC's own three.js scene (camera, lights, geometry, animation), so
// the same skin lands in the same pose under the same light.
//
// CSS and three.js disagree about which way Y points: up there, down here. Every
// rotation about X therefore carries the opposite sign to NameMC's, and the
// light's Y component is negated. Rotations about Y and translations along Z
// need no change.

let appearance = null;
let selectedSkin = null;
let skinVariant = 'classic';

// NameMC's resting pose: 30 degrees of yaw, 21 of pitch (its phi, negated for
// CSS), and the walk frozen at its full extension — which is why its stills
// always show the legs apart rather than a figure standing to attention.
const DEFAULT_YAW = 30, DEFAULT_PITCH = -21, DEFAULT_TIME = 90;
let yaw = DEFAULT_YAW, pitch = DEFAULT_PITCH;
let walking = true, walkStart = null, walkFrame = null, walkTime = DEFAULT_TIME;

// Ambient 0.7 plus a directional 0.3 from (0.678, 0.284, 0.678), both white:
// NameMC's entire lighting rig. A face ends up at 0.7 + 0.3 * max(0, n·L) of
// its texture colour, which on a white cube measures 230/230/200/178/178/178
// for front/right/top/back/left/bottom — exactly what this reproduces.
const LIGHT = [0.678, -0.284, 0.678];
const AMBIENT = 0.7, DIFFUSE = 0.3;

// Face normals in CSS space: X right, Y down, Z towards the viewer.
const FACE_NORMALS = {
  front: [0, 0, 1], back: [0, 0, -1],
  right: [1, 0, 0], left: [-1, 0, 0],
  top: [0, -1, 0], bottom: [0, 1, 0],
};
const FACE_ORDER = ['front', 'right', 'back', 'left', 'top', 'bottom'];

// The limbs swing by different amounts, so each one gets its own shading set
// rather than sharing one with whatever else happens to move with it.
const SHADE_SETS = ['s0', 's1', 's2', 's3', 's4', 's5'];

// Where each limb pivots, in skin pixels from the top of the model: the
// shoulder two pixels into the arm, the hip at the very top of the leg.
const LIMB_PIVOT = { 'right-arm': 10, 'left-arm': 10, 'right-leg': 20, 'left-leg': 20 };

// NameMC nudges parts that would otherwise share a plane apart by a thousandth
// of a pixel, each by a different multiple, so no two coplanar faces ever tie
// for depth. Without it the sleeves flicker against the jacket.
const EPS = 0.001;

const rad = d => d * Math.PI / 180;
function rotX(v, deg) { const c = Math.cos(rad(deg)), s = Math.sin(rad(deg)); return [v[0], v[1] * c - v[2] * s, v[1] * s + v[2] * c]; }
function rotY(v, deg) { const c = Math.cos(rad(deg)), s = Math.sin(rad(deg)); return [v[0] * c + v[2] * s, v[1], -v[0] * s + v[2] * c]; }

function setVars(el, vars) { for (const [k, v] of Object.entries(vars)) el.style.setProperty(`--${k}`, v); }

// The six faces of a box, laid out the way Minecraft packs them into a skin.
// (u, v) is the top-left of the part's block; w/h/d are its texture sizes, which
// are not the box's sizes once an overlay has been inflated.
function faceRects(u, v, w, h, d) {
  return {
    left: { u, v: v + d, fw: d, fh: h },
    front: { u: u + d, v: v + d, fw: w, fh: h },
    right: { u: u + d + w, v: v + d, fw: d, fh: h },
    back: { u: u + d + w + d, v: v + d, fw: w, fh: h },
    top: { u: u + d, v, fw: w, fh: d },
    // Minecraft stores the underside upside down; the CSS flips it back.
    bottom: { u: u + d + w, v, fw: w, fh: d },
  };
}

// How big each face is, in box units rather than texture units. The two differ
// on the second layer: a hat is a 9x9x9 box wearing an 8x8 texture, and sizing
// its faces from the texture left them 8 units wide inside a 9 unit box — the
// layer sat up against one corner with a gap down the other two sides.
function faceBox(name, w, h, d) {
  if (name === 'front' || name === 'back') return [w, h];
  if (name === 'left' || name === 'right') return [d, h];
  return [w, d];
}

function fillPart(part, c, tw, th) {
  setVars(part, { x: c.x, y: c.y, z: c.z || 0, w: c.w, h: c.h, d: c.d, py: c.limb ? LIMB_PIVOT[c.limb] - c.y : 0 });
  const faces = faceRects(c.u, c.v, c.uw, c.uh, c.ud);
  for (const name of FACE_ORDER) {
    // A mirrored limb swaps its two side faces as well as flipping each one,
    // which is how vanilla builds a left arm out of the right arm's texture.
    const rect = faces[c.mirror && (name === 'left' || name === 'right')
      ? (name === 'left' ? 'right' : 'left')
      : name];
    const [fw, fh] = faceBox(name, c.w, c.h, c.d);
    const face = document.createElement('span');
    // The swap above picks the right rectangle; the class is what flips it.
    face.className = `face ${name}${c.mirror ? ' mirror' : ''}`;
    // sx and sy stretch the texture so its rectangle covers the whole face,
    // whatever the box has been inflated to.
    setVars(face, { ...rect, fw, fh, tw, th, sx: fw / rect.fw, sy: fh / rect.fh });
    // The texture lives on a child so it can be mirrored without touching the
    // face's own 3D transform, which decides where the face is and which way it
    // points.
    face.append(document.createElement('i'));
    part.append(face);
  }
}

// Skins come in two shapes. The modern sheet is 64x64 and gives every limb its
// own texture plus a second "overlay" layer. The original is 64x32: it has no
// overlay beyond the hat, and the left arm and leg are the right ones mirrored.
// Drawing a 64x32 skin with the modern coordinates samples empty space and
// produces a scrambled figure, so the layout is chosen per skin.
let legacySkin = false;

function measureSkin(url) {
  if (!url) return;
  const probe = new Image();
  probe.onload = () => {
    const legacy = probe.naturalHeight === 32;
    if (legacy === legacySkin) return;
    legacySkin = legacy;
    buildModel();
    applyShading();
  };
  probe.src = url;
}

function buildModel() {
  const model = $('player-model'), cape = $('cape-model');
  model.replaceChildren();
  cape.replaceChildren();
  cape.className = 'part cape-model';
  // 10x16x1, hung from the top of the back and sitting in the one pixel of
  // space behind the body. Its own 180 degree turn puts the outer face
  // outwards, so the rectangle at (1,1) is the side the world sees.
  fillPart(cape, { x: 0, y: 8, z: -2.5, w: 10, h: 16, d: 1, u: 0, v: 0, uw: 10, uh: 16, ud: 1 }, 64, 32);
  model.append(cape);

  const slim = skinVariant === 'slim';
  const aw = slim ? 3 : 4;            // arm width
  const ax = slim ? 5.5 : 6;          // arm centre, either side of the spine
  const th = legacySkin ? 32 : 64;
  // A legacy sheet has no left limbs of its own: they are the right ones
  // mirrored, read from the same block of texture.
  const leftArm = legacySkin ? { u: 40, v: 16, mirror: true } : { u: 32, v: 48 };
  const leftLeg = legacySkin ? { u: 0, v: 16, mirror: true } : { u: 16, v: 48 };

  // Vertical centres, in skin pixels from the top of the model, so an inflated
  // overlay can be placed around the part it covers rather than beside it.
  const bodyMid = 14, armMid = 14, legMid = 26;
  const box = (mid, h) => mid - h / 2;

  const parts = [
    { x: 0, y: 0, w: 8, h: 8, d: 8, u: 0, v: 0, uw: 8, uh: 8, ud: 8 },
    { x: 0, y: box(bodyMid, 12 + EPS), w: 8 + EPS, h: 12 + EPS, d: 4 + EPS, u: 16, v: 16, uw: 8, uh: 12, ud: 4 },
    { x: -ax, y: 8, w: aw, h: 12, d: 4, u: 40, v: 16, uw: aw, uh: 12, ud: 4, limb: 'right-arm' },
    { x: ax, y: 8, w: aw, h: 12, d: 4, ...leftArm, uw: aw, uh: 12, ud: 4, limb: 'left-arm' },
    { x: -2, y: 20, w: 4, h: 12, d: 4, u: 0, v: 16, uw: 4, uh: 12, ud: 4, limb: 'right-leg' },
    { x: 2, y: 20, w: 4, h: 12, d: 4, ...leftLeg, uw: 4, uh: 12, ud: 4, limb: 'left-leg' },
    // The second layer, inflated around the first: half a pixel on the head,
    // a quarter everywhere else, each with its own multiple of EPS.
    { x: 0, y: -0.5, w: 9, h: 9, d: 9, u: 32, v: 0, uw: 8, uh: 8, ud: 8, overlay: true },
    { x: 0, y: box(bodyMid, 12.5 + EPS), w: 8.5 + EPS, h: 12.5 + EPS, d: 4.5 + EPS, u: 16, v: 32, uw: 8, uh: 12, ud: 4, overlay: true },
    { x: -ax, y: box(armMid, 12.5 + EPS * 4), w: aw + 0.5 + EPS * 4, h: 12.5 + EPS * 4, d: 4.5 + EPS * 4, u: 40, v: 32, uw: aw, uh: 12, ud: 4, overlay: true, limb: 'right-arm' },
    { x: ax, y: box(armMid, 12.5 + EPS * 4), w: aw + 0.5 + EPS * 4, h: 12.5 + EPS * 4, d: 4.5 + EPS * 4, u: 48, v: 48, uw: aw, uh: 12, ud: 4, overlay: true, limb: 'left-arm' },
    { x: -2, y: box(legMid, 12.5 + EPS * 2), w: 4.5 + EPS * 2, h: 12.5 + EPS * 2, d: 4.5 + EPS * 2, u: 0, v: 32, uw: 4, uh: 12, ud: 4, overlay: true, limb: 'right-leg' },
    { x: 2, y: box(legMid, 12.5 + EPS * 3), w: 4.5 + EPS * 3, h: 12.5 + EPS * 3, d: 4.5 + EPS * 3, u: 0, v: 48, uw: 4, uh: 12, ud: 4, overlay: true, limb: 'left-leg' },
  ];
  // Which shading set a part reads: one per limb, because the arms and legs
  // swing by different amounts and so catch the light differently.
  const SHADE_OF = { 'right-arm': 's1', 'left-arm': 's2', 'right-leg': 's3', 'left-leg': 's4' };
  for (const c of parts) {
    // Only the hat exists as an overlay on a legacy sheet; the rest would read
    // from rows the texture does not have.
    if (legacySkin && c.overlay && c.u !== 32) continue;
    const part = document.createElement('div');
    part.className = ['part', c.overlay ? 'overlay' : '', c.limb ? `limb-${c.limb}` : '', c.limb ? `shade-${SHADE_OF[c.limb]}` : ''].filter(Boolean).join(' ');
    fillPart(part, c, 64, th);
    model.append(part);
  }
}

// Shading, recomputed whenever the model or a limb moves.
//
// The light is fixed in the world while the model turns inside it, so a face's
// brightness depends on where that face is pointing right now. Each set is six
// numbers — one per face direction — published as custom properties that the
// faces read; a black veil at 1 - brightness does the darkening, which needs no
// filter and so cannot flatten the 3D scene the way filter: brightness() would.
const shadeCache = new Map();

function shadeSet(name, local) {
  const model = $('player-model');
  if (!model) return;
  for (const [face, n0] of Object.entries(FACE_NORMALS)) {
    const n = rotX(rotY(local ? local(n0) : n0, yaw), pitch);
    const lit = AMBIENT + DIFFUSE * Math.max(0, n[0] * LIGHT[0] + n[1] * LIGHT[1] + n[2] * LIGHT[2]);
    const key = `--${name}-${face}`;
    const value = lit.toFixed(4);
    if (shadeCache.get(key) === value) continue;
    shadeCache.set(key, value);
    model.style.setProperty(key, value);
  }
}

function applyShading() {
  const a = Math.sin(rad(walkTime));
  shadeSet('s0');
  shadeSet('s1', n => rotX(n, 18 * a));
  shadeSet('s2', n => rotX(n, -18 * a));
  shadeSet('s3', n => rotX(n, -20 * a));
  shadeSet('s4', n => rotX(n, 20 * a));
  shadeSet('s5', n => rotX(rotY(n, 180), capeTilt()));
}

// NameMC's walk: the arms swing 18 degrees either way, the legs 20, one full
// stride every 1.5 seconds. The cape rides a quarter of that frequency, hanging
// at 18 degrees and breathing 6 degrees around it — and because the clock wraps
// at four full turns rather than one, the cape's slower wave stays continuous
// across the wrap.
const WALK_PERIOD_MS = 1500, WALK_WRAP_DEG = 1440;

const capeTilt = () => -18 + 6 * Math.sin(rad(walkTime / 4));

function applyPose() {
  const model = $('player-model');
  if (!model) return;
  const a = Math.sin(rad(walkTime));
  setVars(model, {
    yaw: `${yaw.toFixed(2)}deg`,
    pitch: `${pitch.toFixed(2)}deg`,
    'swing-right-arm': `${(18 * a).toFixed(2)}deg`,
    'swing-left-arm': `${(-18 * a).toFixed(2)}deg`,
    'swing-right-leg': `${(-20 * a).toFixed(2)}deg`,
    'swing-left-leg': `${(20 * a).toFixed(2)}deg`,
    'cape-tilt': `${capeTilt().toFixed(2)}deg`,
  });
  applyShading();
}

function applyPreview() {
  const model = $('player-model');
  const url = selectedSkin?.url || state.profile?.skinUrl;
  if (url) { model.style.setProperty('--skin-url', `url("${url}")`); measureSkin(url); }
  $('btn-model-classic').classList.toggle('is-active', skinVariant === 'classic');
  $('btn-model-slim').classList.toggle('is-active', skinVariant === 'slim');
  buildModel();
  applyPose();

  const cape = (appearance?.capes || []).find(c => c.active);
  $('cape-model').hidden = !cape;
  if (cape) $('cape-model').style.setProperty('--cape-url', `url("${cape.url}")`);
}

function renderAppearance() {
  text('profile-name', appearance?.username || state.profile?.username || '');
  text('profile-kind', t(state.profile?.type === 'microsoft' ? 'main.account.microsoft' : 'main.account.offline'));

  const skins = appearance?.skins || [];
  $('skin-list').replaceChildren();
  for (const skin of skins.slice(0, 8)) {
    const tile = document.createElement('button');
    tile.type = 'button';
    tile.className = `skin-tile${selectedSkin?.id === skin.id ? ' is-active' : ''}`;
    tile.dataset.skinId = skin.id;
    const head = document.createElement('span');
    head.className = 'skin-head';
    head.style.setProperty('--tile-url', `url("${skin.url}")`);
    tile.append(head);
    tile.onclick = () => { selectedSkin = skin; skinVariant = skin.variant || skinVariant; renderAppearance(); };
    $('skin-list').append(tile);
  }

  const capes = appearance?.capes || [];
  $('cape-block').hidden = capes.length === 0;
  $('cape-list').replaceChildren();
  if (capes.length) {
    const none = document.createElement('button');
    none.type = 'button';
    none.className = `cape-tile${capes.some(c => c.active) ? '' : ' is-active'}`;
    none.innerHTML = `<span class="no-cape-mark">&times;</span><span>${escapeHtml(t('profile.noCape'))}</span>`;
    none.onclick = () => changeCape(null);
    $('cape-list').append(none);
    for (const cape of capes) {
      const tile = document.createElement('button');
      tile.type = 'button';
      tile.className = `cape-tile${cape.active ? ' is-active' : ''}`;
      const thumb = document.createElement('span');
      thumb.className = 'cape-thumb';
      thumb.style.setProperty('--cape-url', `url("${cape.url}")`);
      tile.append(thumb, Object.assign(document.createElement('span'), { textContent: cape.alias || '' }));
      tile.onclick = () => changeCape(cape.id);
      $('cape-list').append(tile);
    }
  }
  applyPreview();
}

async function loadAppearance() {
  text('profile-error', ''); text('profile-status', '');
  // An offline account has no profile on Mojang's side, so the screen shows
  // the local name and nothing to edit rather than failing.
  const editable = state.profile?.type === 'microsoft';
  for (const id of ['btn-skin-choose', 'btn-skin-reset', 'btn-model-classic', 'btn-model-slim']) $(id).disabled = !editable;
  if (!editable) { appearance = null; selectedSkin = null; renderAppearance(); text('profile-status', t('profile.offlineOnly')); return; }
  try {
    appearance = await send('getAppearance');
    skinVariant = appearance?.variant || 'classic';
    selectedSkin = (appearance?.skins || []).find(x => x.active) || null;
    renderAppearance();
  } catch (e) { renderAppearance(); text('profile-error', errorText(e)); }
}

async function appearanceAction(command, payload, busyKey) {
  text('profile-error', ''); text('profile-status', t(busyKey));
  for (const id of ['btn-skin-choose', 'btn-skin-reset']) $(id).disabled = true;
  try {
    const next = await send(command, payload);
    // A null result means the player closed the file picker: not an error.
    if (next) { appearance = next; skinVariant = next.variant || skinVariant; selectedSkin = (next.skins || []).find(x => x.active) || null; }
    renderAppearance();
    text('profile-status', t(next ? 'profile.saved' : 'profile.cancelled'));
    /* the launcher refreshes itself through the state event */
  } catch (e) { text('profile-status', ''); text('profile-error', errorText(e)); }
  finally { for (const id of ['btn-skin-choose', 'btn-skin-reset']) $(id).disabled = false; }
}

const changeCape = capeId => appearanceAction('setCape', { capeId }, 'profile.working');

function errorText(e) { return e?.key && table[e.key] ? t(e.key) : (e?.message || t('error.unknown')); }

// Wheel zoom. --skin-scale drives every dimension in the model, so changing it
// scales the whole figure consistently; the model box grows with it so the
// figure stays centred instead of drifting off the top of the stage.
const MIN_SCALE = 3.5, MAX_SCALE = 16, BASE_SCALE = 7;
let skinScale = BASE_SCALE;

function applyScale() {
  const stage = $('profile-stage'), model = $('player-model');
  if (!stage || !model) return;
  stage.style.setProperty('--skin-scale', `${skinScale}px`);
  // 30 skin pixels across and 32 down covers the figure including the overlay.
  model.style.width = `${30 * skinScale}px`;
  model.style.height = `${32 * skinScale}px`;
}

function zoomBy(factor) {
  skinScale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, skinScale * factor));
  applyScale();
}

(() => {
  const stage = $('profile-stage');
  if (!stage) return;
  stage.addEventListener('wheel', e => {
    e.preventDefault();
    // A fixed ratio per notch, so zooming feels the same at every distance.
    zoomBy(e.deltaY < 0 ? 1.12 : 1 / 1.12);
  }, { passive: false });
  // Double click returns to the size the window opened at.
  stage.addEventListener('dblclick', () => { skinScale = BASE_SCALE; applyScale(); });
  applyScale();
})();

// Drag to turn the model, the way every skin editor does it.
(() => {
  const stage = $('profile-stage');
  if (!stage) return;
  let dragging = false, startX = 0, startY = 0, startYaw = 0, startPitch = 0;
  stage.addEventListener('pointerdown', e => {
    // The pause button lives inside the stage, and capturing the pointer here
    // swallowed its click entirely.
    if (e.target.closest('button')) return;
    dragging = true; startX = e.clientX; startY = e.clientY; startYaw = yaw; startPitch = pitch;
    stage.setPointerCapture(e.pointerId);
  });
  stage.addEventListener('pointermove', e => {
    if (!dragging) return;
    // One degree per pixel, the gain NameMC uses, with the tilt free to run all
    // the way to looking straight down on the model or straight up at it.
    // Dragging up tilts the model up: the cursor pulls the figure rather than
    // pushing it.
    yaw = startYaw + (e.clientX - startX);
    pitch = Math.max(-90, Math.min(90, startPitch - (e.clientY - startY)));
    applyPose();
  });
  const stop = e => { dragging = false; try { stage.releasePointerCapture(e.pointerId); } catch {} };
  stage.addEventListener('pointerup', stop);
  stage.addEventListener('pointercancel', stop);
})();

// The walk runs on a clock measured in degrees: 360 of them every 1.5 seconds,
// wrapping at 1440 so the cape's quarter-speed wave is continuous across the
// wrap rather than jumping.
function stepWalk(now) {
  if (!walking) { walkFrame = null; return; }
  walkStart ??= now - walkTime * (WALK_PERIOD_MS / 360);
  walkTime = ((now - walkStart) * (360 / WALK_PERIOD_MS)) % WALK_WRAP_DEG;
  applyPose();
  walkFrame = requestAnimationFrame(stepWalk);
}

function setWalking(on) {
  walking = on;
  $('btn-model-play').classList.toggle('is-paused', !on);
  if (on) {
    if (walkFrame === null) { walkStart = null; walkFrame = requestAnimationFrame(stepWalk); }
    return;
  }
  if (walkFrame !== null) { cancelAnimationFrame(walkFrame); walkFrame = null; }
  // Pausing holds the frame it was on, which is what NameMC does; the pose it
  // opens at is that same clock at 90 degrees, with the stride fully extended.
  applyPose();
}


  (async () => {
    try {
      const boot = await send('ready');
      table = boot.language?.table || {};
      state = boot.state || state;
      document.documentElement.lang = boot.language?.tag || 'en-US';
      document.documentElement.dir = boot.language?.rtl ? 'rtl' : 'ltr';
    } catch { /* the window still opens; the strings fall back to their keys */ }

    applyText();
    bind('btn-model-classic', () => { skinVariant = 'classic'; renderAppearance(); });
    bind('btn-model-slim', () => { skinVariant = 'slim'; renderAppearance(); });
    bind('btn-model-play', () => setWalking(!walking));
    bind('btn-skin-choose', () => appearanceAction('uploadSkin', { variant: skinVariant }, 'profile.working'));
    bind('btn-skin-reset', () => appearanceAction('resetSkin', {}, 'profile.working'));
    await loadAppearance();
    setWalking(true);
  })();
})();
