import { h, bridge, haptic, toast, t, LOCALE } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- this tablet's own apps
// The apps installed on the phone/tablet itself (not the computer): tap one to open it. No agent and no network:
// the list comes through the bridge and every icon is served by the native WebViewAssetLoader from the phone's cache
// (see PhoneApps.kt), so the widget keeps working over USB, over Wi-Fi and while the computer is off.
const ICON_BASE = 'https://appassets.androidplatform.net/phoneicons/';

W.phoneapps = {
  needs: ['tick'],
  create(box, { o }) {
    const confirm = o.confirm === true; // a launcher opens on one tap by default
    const sortLocale = LOCALE || undefined;
    let apps = null, q = '', busy = null, armed = null, armTimer = 0, lastKey = '';

    const head = h('div', 'w-title', o.title || t('phoneapps.title'), h('span', 'ap-count'));
    const search = h('input', 'ap-search');
    search.type = 'search';
    search.placeholder = t('phoneapps.search');
    search.autocomplete = 'off';
    search.spellcheck = false;
    search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); render(true); });
    const grid = h('div', 'ap-grid');
    box.append(head, ...(o.search === false ? [] : [search]), grid);

    const available = () => !!(bridge && bridge.phoneApps && bridge.launchApp);

    const load = () => {
      if (!available()) return false;
      try {
        const list = JSON.parse(bridge.phoneApps() || '[]');
        apps = Array.isArray(list) ? list.filter((a) => a && a.id) : [];
        return true;
      } catch (_) {
        return false;
      }
    };

    const disarm = () => { clearTimeout(armTimer); if (armed) { armed = null; render(true); } };

    const open = (a) => {
      haptic();
      busy = a.id;
      render(true);
      let ok = false;
      try { ok = bridge.launchApp(a.id) === true; } catch (_) { ok = false; }
      if (!ok) toast(t('phoneapps.failed', { name: a.name }), 'err');
      busy = null;
      render(true);
    };

    const tile = (a) => {
      const img = h('img', 'ap-ico');
      img.loading = 'lazy';
      img.decoding = 'async';
      img.alt = '';
      img.src = `${ICON_BASE}${a.id}.png`;
      // a package without a cached icon (or a broken one) falls back to its initial instead of an empty tile
      img.addEventListener('error', () => {
        img.replaceWith(h('span', 'ap-ico ap-letter', (a.name || '?').slice(0, 1).toUpperCase()));
      }, { once: true });
      const isArmed = armed === a.id;
      const b = h('button', 'ap-tile' + (busy === a.id ? ' busy' : '') + (isArmed ? ' armed' : ''),
        img, h('span', 'ap-name', isArmed ? t('phoneapps.confirm') : a.name));
      b.title = a.name;
      b.addEventListener('click', () => {
        if (!confirm || armed === a.id) { clearTimeout(armTimer); armed = null; open(a); return; }
        haptic();
        armed = a.id;
        clearTimeout(armTimer);
        armTimer = setTimeout(disarm, 4000);
        render(true);
      });
      return b;
    };

    const render = (force = false) => {
      if (!available()) {
        if (lastKey !== 'no-bridge' || force) {
          lastKey = 'no-bridge';
          head.querySelector('.ap-count').textContent = '';
          grid.replaceChildren(h('div', 'empty', t('phoneapps.needsApp')));
        }
        return;
      }
      if (!apps) load();
      const all = apps || [];
      const items = all.filter((a) => !q || String(a.name).toLowerCase().includes(q))
        .sort((x, y) => String(x.name).localeCompare(String(y.name), sortLocale, { sensitivity: 'base' }));
      const key = `${all.length}|${q}|${busy}|${armed}`;
      if (!force && key === lastKey) return;
      lastKey = key;
      head.querySelector('.ap-count').textContent = all.length ? ` ${q ? items.length + '/' : ''}${all.length}` : '';
      if (!all.length) { grid.replaceChildren(h('div', 'empty', t('phoneapps.noApps'))); return; }
      if (!items.length) { grid.replaceChildren(h('div', 'empty', `${t('phoneapps.noMatch')} "${search.value.trim()}"`)); return; }
      grid.replaceChildren(...items.map(tile));
    };

    // re-read the list now and then, so an app installed or removed on the phone shows up without a reload
    const tick = every(10 * 60_000, () => { const had = apps?.length; load(); if (apps?.length !== had) lastKey = ''; render(); return true; }, 30_000);
    render(true);
    return { update: () => { tick(); render(); } };
  },
};
