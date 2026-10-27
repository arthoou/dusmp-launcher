const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// A small DOM double lets us check host events without opening a desktop window.
const root = path.join(__dirname, '../src/DusmpLauncher');
const html = fs.readFileSync(path.join(root, 'UI/index.html'), 'utf8');
function element() {
  const classes = new Set();
  const listeners = new Map();
  const node = {
    innerHTML: '', dataset: {}, children: [], hidden: false,
    // A real CSSStyleDeclaration, not a bare object: the page sets and clears
    // custom properties on it, and a plain {} has no setProperty/removeProperty.
    style: { setProperty(name, value) { this[name] = value; }, removeProperty(name) { delete this[name]; },
             getPropertyValue(name) { return this[name] ?? ''; } },
    disabled: false, tabIndex: 0, title: '', attributes: {},
    classList: {
      add(name) { classes.add(name); },
      remove(name) { classes.delete(name); },
      toggle(name, enabled) {
        const on = enabled === undefined ? !classes.has(name) : enabled;
        on ? classes.add(name) : classes.delete(name);
        return on;
      },
      contains(name) { return classes.has(name); }
    },
    setAttribute(name, value) { this.attributes[name] = value; },
    getAttribute(name) { return name in this.attributes ? this.attributes[name] : null; },
    addEventListener(name, handler) { listeners.set(name, handler); },
    // Lets a test drive the row the way a player would. Some of the page's
    // buttons are wired through onclick rather than addEventListener, and a
    // double that only knew about listeners dropped those clicks in silence.
    fire(name, event) { listeners.get(name)?.(event ?? {}); if (name === 'click') node.onclick?.(event ?? {}); },
    querySelector() { return null; },
    replaceChildren() { this.children = []; },
    append(...kids) { this.children.push(...kids); }, remove() {},
    // Menus position themselves inside the window, so they measure first.
    getBoundingClientRect() { return { width: 172, height: 96, top: 0, left: 0 }; }
  };
  // Assigning textContent replaces every child, which is how the account list
  // is emptied before it is rebuilt. A plain property left the old rows behind
  // and made the list look like it doubled on every render.
  let content = '';
  Object.defineProperty(node, 'textContent', {
    enumerable: true,
    get() { return content; },
    set(value) { content = String(value ?? ''); if (content === '') node.children = []; }
  });
  return node;
}
const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], element()]));
const screens = [...html.matchAll(/id="([^"]+)" data-screen="([^"]+)"/g)].map(match => {
  const el = elements.get(match[1]); el.dataset.screen = match[2]; return el;
});
// Elements the markup starts hidden really do start hidden: the drawer and the
// menu are only ever shown by the code under test, and a double that reported
// them visible from the start would have let a drawer that never opens pass.
for (const [, id] of [...html.matchAll(/<[^>]*\bid="([^"]+)"[^>]*\bhidden\b[^>]*>/g)].map(m => [m[0], m[1]])) {
  const el = elements.get(id);
  if (el) el.hidden = true;
}

const menuItems = new Map([...html.matchAll(/data-account-action="([^"]+)"/g)].map(match => {
  const item = element();
  item.dataset.accountAction = match[1];
  return [match[1], item];
}));
elements.get('account-menu').querySelector = selector => {
  const match = /data-account-action="([^"]+)"/.exec(selector);
  return match ? menuItems.get(match[1]) || null : null;
};

let receive;
// Commands the page sent, and the canned answers a test wants to give back.
const sent = [];
let respond = () => ({});
const body = element();
const context = {
  console: { log: console.log, error(error) { throw error; } },
  setTimeout() {}, clearTimeout() {},
  // The news rows measure their own overflow in a frame callback; run it now so
  // the code path is exercised instead of silently skipped.
  requestAnimationFrame(callback) { callback(0); },
  document: {
    documentElement: {}, body, addEventListener() {}, createElement: element,
    getElementById(id) { return elements.get(id) || null; },
    querySelector(selector) { return selector === '.screen.is-active' ? screens.find(s => s.classList.contains('is-active')) || null : null; },
    querySelectorAll(selector) {
      if (selector === '.screen') return screens;
      if (selector === '.account-row') return elements.get('account-list').children.flatMap(li => li.children);
      if (selector === '.account-row.is-target') return elements.get('account-list').children.flatMap(li => li.children).filter(row => row.classList.contains('is-target'));
      return [];
    }
  },
  window: {
    innerWidth: 1180, innerHeight: 760,
    external: {
      receiveMessage(callback) { receive = callback; },
      // Answer synchronously: send() registers its pending entry before calling
      // this, so the reply always finds it. Without a reply every await in the
      // page hangs and nothing past the first command can be tested at all.
      sendMessage(raw) {
        const { id, command, payload } = JSON.parse(raw);
        sent.push({ command, payload });
        receive(JSON.stringify({ kind: 'response', id, ok: true, result: respond(command, payload) ?? {} }));
      }
    }
  }
};
vm.runInNewContext(fs.readFileSync(path.join(root, 'UI/js/app.js'), 'utf8'), context);
assert.equal(typeof receive, 'function', 'Bootstrap did not subscribe to backend messages');
const emit = (event, payload) => receive(JSON.stringify({ kind: 'event', event, payload }));
const table = JSON.parse(fs.readFileSync(path.join(root, 'Localization/Languages/pt-BR.json'), 'utf8'));
emit('language', { tag: 'pt-BR', table });

emit('screen', { screen: 'error', gameState: 'idle', fatalErrorKey: 'error.api.manifestMissing', fatalErrorDetail: 'HTTP 404' });
assert.equal(elements.get('error-message').textContent, table['error.api.manifestMissing']);
assert.equal(elements.get('error-detail').textContent, 'HTTP 404');
assert(elements.get('screen-error').classList.contains('is-active'));

emit('screen', { screen: 'main', readyToPlay: true, gameState: 'idle', profile: { type: 'microsoft', username: 'Player' } });
const play = elements.get('btn-play');
assert.equal(play.disabled, false, 'Idle string-enum incorrectly disables Play');
assert.equal(play.textContent, table['main.play']);
assert.equal(play.dataset.action, 'play');
assert.equal(elements.get('main-account-kind').textContent, 'Microsoft');
assert.equal(elements.get('error-message').textContent, '');

// While the game is up the same button becomes Stop: still clickable, and
// flagged so the click handler asks the host to close the game instead.
emit('screen', { screen: 'main', readyToPlay: true, gameState: 'running' });
assert.equal(play.textContent, table['main.stop']);
assert.equal(play.dataset.action, 'stop');
assert.equal(play.disabled, false, 'Stop must stay clickable while the game is running');
assert(play.classList.contains('is-stop'));

// News: a long item has to be expandable, and a machine translation has to say so.
emit('screen', {
  screen: 'main', readyToPlay: true, gameState: 'idle',
  news: [{ id: '1', title: { 'pt-BR': 'Título' }, body: { 'pt-BR': 'Corpo longo'.repeat(40) }, autoTranslated: true }]
});
const [row] = elements.get('news-list').children;
assert(row, 'News was not rendered');
assert(row.innerHTML.includes(table['main.news.autoTranslated']), 'Auto-translated news must be labelled');
assert.equal(row.classList.contains('is-open'), false);
row.fire('click');
assert(row.classList.contains('is-open'), 'Clicking a news item must expand it');
assert.equal(row.attributes['aria-expanded'], 'true');
row.fire('click');
assert.equal(row.classList.contains('is-open'), false, 'Clicking again must collapse it');

// Onboarding order. The backend decides the next screen; the page must follow
// it instead of jumping somewhere of its own choosing. Forcing the screen here
// is what silently undid the play-mode-before-account order and trapped an
// offline sign-in in a loop back to the play-mode screen.
const activeScreen = () => screens.find(s => s.classList.contains('is-active'))?.dataset.screen;

(async () => {
  const settle = () => new Promise(resolve => setImmediate(resolve));

  emit('screen', { screen: 'onboarding-play-mode', firstLaunch: true, gameState: 'idle' });
  assert.equal(activeScreen(), 'onboarding-play-mode', 'The play mode step must be reachable before the account step');

  emit('screen', { screen: 'onboarding-account', firstLaunch: true, gameState: 'idle' });
  assert.equal(activeScreen(), 'onboarding-account');

  // The real backend answers loginOffline and then reports the country step as
  // the current screen; refreshState() is what picks that up.
  respond = command => command === 'getState'
    ? { screen: 'onboarding-country', firstLaunch: true, gameState: 'idle', profile: { type: 'offline', username: 'Steve' } }
    : {};

  elements.get('offline-username').value = 'Steve';
  elements.get('btn-offline-continue').fire('click');
  // Let the click handler's awaits settle; every reply is already synchronous.
  await new Promise(resolve => setImmediate(resolve));

  assert(sent.some(x => x.command === 'loginOffline'), 'The offline name was never submitted');
  assert.notEqual(activeScreen(), 'onboarding-play-mode', 'Offline sign-in must not bounce back to the play mode screen');
  assert.equal(activeScreen(), 'onboarding-country', 'Offline sign-in must follow the backend to the country step');

  // ---- choosing a pack ----
  //
  // Two editions, each its own game version and mod list. Nothing is downloaded
  // before one is chosen, so this screen is what starts the install, and the
  // pill on the home screen offers the same choice again.
  const editionPayload = {
    current: null,
    editions: [
      { id: 'islands', name: 'DUSMP Islands', description: { 'pt-BR': 'A temporada atual.' }, version: '2.0.0', minecraft: '1.21.1', loader: 'neoforge', loaderVersion: '21.1.247', files: 164, available: true },
      { id: 'pre-dusmp', name: 'Pre-DUSMP', description: { 'pt-BR': 'O mapa antigo.' }, version: '0.9.0', minecraft: '1.21.11', loader: 'neoforge', loaderVersion: '21.11.48', files: 120, available: true },
    ],
  };
  respond = command => (command === 'getEditions' || command === 'selectEdition' ? editionPayload : {});
  emit('screen', { screen: 'onboarding-edition', firstLaunch: true, gameState: 'idle' });
  await settle();
  assert.equal(activeScreen(), 'onboarding-edition', 'The pack step must be reachable');

  const cards = elements.get('edition-grid').children;
  assert.equal(cards.length, 2, 'Both packs must be offered');
  assert.equal(cards[0].children[0].textContent, 'DUSMP Islands');
  // The meta line is what tells the two apart: different game versions.
  assert.match(cards[1].children.at(-1).textContent, /1\.21\.11/, 'Each card must show its own Minecraft version');
  assert.match(cards[0].children.at(-1).textContent, /1\.21\.1\b/, 'Islands runs a different version from Pre');

  sent.length = 0;
  cards[1].fire('click');
  await settle();
  assert(sent.some(x => x.command === 'selectEdition' && x.payload.id === 'pre-dusmp'),
    'Choosing a pack must be what asks the host to install it');

  // An edition with nothing published cannot be installed. The host reports the
  // installed one in the same answer, so the test moves both together.
  editionPayload.editions[1].available = false;
  editionPayload.current = 'islands';
  emit('screen', { screen: 'onboarding-edition', firstLaunch: true, gameState: 'idle', edition: 'islands' });
  await settle();
  assert.equal(elements.get('edition-grid').children[1].disabled, true, 'An unpublished pack must not be selectable');

  // The home screen offers the same switch, and marks the one in use.
  const pill = elements.get('edition-switch');
  assert.equal(pill.hidden, false, 'With two packs the home screen must offer the switch');
  assert.equal(pill.children.length, 2);
  assert(pill.children[0].classList.contains('is-current'), 'The installed pack must be marked on the home screen');
  assert.equal(pill.children[1].disabled, true, 'An unpublished pack must not be switchable to');

  // ---- stepping back through the setup ----
  //
  // The host picks the next step from the one it believes is current, so the
  // page has to tell it when the player steps back. Without that, stepping back
  // from the country step and then signing in with the other kind of account
  // left the host looking at a step it had already passed: it moved nobody, and
  // the setup sat there with no way on.
  let hostScreen = 'onboarding-account';
  emit('screen', { screen: hostScreen, firstLaunch: true, gameState: 'idle' });
  assert.equal(activeScreen(), 'onboarding-account');

  // Picking the offline card swaps the two cards for the name form.
  elements.get('btn-offline-open').fire('click');
  await settle();
  assert.equal(elements.get('offline-panel').hidden, false);
  assert.equal(elements.get('account-cards').hidden, true);

  sent.length = 0;
  // What the real host does: it moves its own idea of the step, and the page
  // reads that back.
  respond = (command, payload) => {
    if (command === 'goToScreen') { hostScreen = payload.screen; return {}; }
    return { screen: hostScreen, firstLaunch: true, gameState: 'idle' };
  };
  elements.get('account-back').fire('click');
  await settle();
  assert.deepEqual(sent.find(x => x.command === 'goToScreen'), { command: 'goToScreen', payload: { screen: 'onboarding-play-mode' } },
    'Stepping back must tell the host which step the player is on');

  assert.equal(activeScreen(), 'onboarding-play-mode', 'Back must actually step back');

  // Coming back to the account step offers the choice again. Leaving the name
  // form up meant the Microsoft button was not on screen to be pressed, so the
  // player could no longer change their mind.
  hostScreen = 'onboarding-account';
  emit('screen', { screen: hostScreen, firstLaunch: true, gameState: 'idle' });
  assert.equal(activeScreen(), 'onboarding-account');
  assert.equal(elements.get('account-cards').hidden, false, 'The two cards must be offered again');
  assert.equal(elements.get('offline-panel').hidden, true, 'The offline name form must not still be up');

  sent.length = 0;
  elements.get('country-back').fire('click');
  await settle();
  assert.deepEqual(sent.find(x => x.command === 'goToScreen'), { command: 'goToScreen', payload: { screen: 'onboarding-account' } },
    'The country step must report stepping back to the account step');

  // ---- account drawer ----
  // The caret points up while the list is closed and down while it is open, and
  // the list itself is rebuilt from the host's answer rather than from the
  // screen state, so a removal is visible immediately.
  const accountsPayload = {
    activeId: 'microsoft:aaa',
    accounts: [
      { id: 'microsoft:aaa', type: 'microsoft', username: 'Arthur', uuid: 'aaa', skinUrl: 'https://textures.minecraft.net/texture/abc' },
      { id: 'offline:steve', type: 'offline', username: 'Steve', uuid: 'bbb', skinUrl: null }
    ]
  };
  const closeDrawerForTest = async () => { if (!elements.get('account-drawer').hidden) { elements.get('btn-accounts').fire('click'); await settle(); } };
  respond = command => command === 'listAccounts' || command === 'selectAccount' || command === 'removeAccount'
    ? accountsPayload
    : { screen: 'main', readyToPlay: true, gameState: 'idle', profile: { type: 'microsoft', username: 'Arthur', uuid: 'aaa' } };

  emit('screen', { screen: 'main', readyToPlay: true, gameState: 'idle', profile: { type: 'microsoft', username: 'Arthur', uuid: 'aaa' } });
  await settle();

  const list = elements.get('account-list');
  const caret = elements.get('btn-accounts');
  const drawer = elements.get('account-drawer');
  assert.equal(caret.hidden, false, 'The caret must be shown once the launcher holds an account');
  assert.equal(list.children.length, 2, 'Both signed-in accounts must be listed');


  // Opening the drawer rebuilds the rows from a fresh answer, so they are read
  // back each time rather than held onto.
  const rows = () => list.children.map(li => li.children[0]);
  assert.equal(rows()[0].children[1].children[0].textContent, 'Arthur');
  assert(rows()[0].classList.contains('is-active'), 'The account in use must be marked');
  assert.equal(rows()[1].children[1].children[1].textContent, table['main.account.offline']);

  // Clicking the chip with a second account signed in opens the list instead of
  // going straight to the skin window.
  assert.equal(drawer.hidden, true);
  elements.get('main-player').fire('click');
  await settle();
  assert.equal(drawer.hidden, false, 'The chip must open the drawer when more than one account is signed in');
  assert.equal(caret.getAttribute('aria-expanded'), 'true', 'The caret must flip while the drawer is open');

  elements.get('btn-accounts').fire('click');
  await settle();
  assert.equal(drawer.hidden, true, 'The caret must close the drawer again');
  assert.equal(caret.getAttribute('aria-expanded'), 'false', 'The caret must flip back when the drawer closes');

  // Every open rebuilds the list from a fresh answer, so it must replace the
  // rows rather than append to them.
  elements.get('btn-accounts').fire('click');
  await settle();
  assert.equal(list.children.length, 2, 'Reopening the drawer must replace the rows, not stack a second copy');

  const steveRow = rows()[1];
  list.fire('contextmenu', { target: { closest: selector => (selector === '.account-row' ? steveRow : null) }, preventDefault() {}, clientX: 40, clientY: 60 });
  const menu = elements.get('account-menu');
  assert.equal(menu.hidden, false, 'Right-clicking an account must open its menu');
  assert.equal(menuItems.get('select').disabled, false, 'Selecting another account must be offered');
  assert(steveRow.classList.contains('is-target'), 'The row the menu belongs to must be marked');

  menu.fire('click', { target: { closest: () => menuItems.get('remove') } });
  await settle();
  assert.deepEqual(sent.at(-2) ?? sent.at(-1), { command: 'removeAccount', payload: { id: 'offline:steve' } },
    'Remove must ask the host to forget the account the menu was opened on');

  // The active account's own menu cannot offer to select it again.
  list.fire('contextmenu', { target: { closest: selector => (selector === '.account-row' ? rows()[0] : null) }, preventDefault() {}, clientX: 40, clientY: 60 });
  assert.equal(menuItems.get('select').disabled, true, 'Selecting the account already in use must be disabled');

  // Left-clicking a row switches to it.
  sent.length = 0;
  list.fire('click', { target: { closest: () => rows()[1] } });
  await settle();
  assert(sent.some(x => x.command === 'selectAccount' && x.payload.id === 'offline:steve'), 'Clicking a row must switch to that account');

  // With a single account there is nothing to switch to, so the chip goes
  // straight to the skin window instead of opening a one-row list.
  accountsPayload.accounts = accountsPayload.accounts.slice(0, 1);
  await closeDrawerForTest();
  elements.get('btn-accounts').fire('click');  // reopening picks the shorter list up
  await settle();
  await closeDrawerForTest();
  assert.equal(list.children.length, 1, 'The drawer must follow the host down to a single account');
  sent.length = 0;
  elements.get('main-player').fire('click');
  await settle();
  assert(sent.some(x => x.command === 'openSkinWindow'), 'With one account the chip must open the skin window');
  assert(!sent.some(x => x.command === 'selectAccount'), 'The chip must not switch accounts on its own');

  // The folder button beside the gear opens the launcher folder.
  sent.length = 0;
  elements.get('btn-folder').fire('click');
  await settle();
  assert(sent.some(x => x.command === 'openInstanceFolder'), 'The folder button must open the launcher folder');

  console.log('PASS: bootstrap, backend error display, Play/Stop handling, expandable news, onboarding order, offline sign-in, account drawer, folder button and pack choice.');
})().catch(error => { console.error(error); process.exit(1); });
