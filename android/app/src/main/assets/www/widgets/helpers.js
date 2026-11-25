import { $, LOCALE, cfg, h, hardwareHeat, hardwarePowerText, icon, ringSvg, safeFetch, sensorNumber, sensorPower, sizeCanvas, store, tp, tpv } from '../main.js';

// 现有的统计、图表、内存卡片与独立 GPU 卡片共用硬件信息。
export const hardwareIcon = (kind) => kind === 'gpu' ? 'gpu' : kind === 'ram' ? 'memory-stick' : kind === 'storage' ? 'hard-drive' : 'cpu';
export function hardwareValues(kind, reading, missing, badge = false) {
  const values = h('span', 'hw-values'), watts = hardwarePowerText(reading);
  // Windows 可能只提供占功耗上限的比例。明确显示为百分比，不将其当作实测瓦数。
  const power = watts || (kind === 'gpu' && sensorPower(reading.powerPct) ? tpv('{n}% of limit', { n: Math.round(reading.powerPct) }) : '');
  values.dataset.kind = kind;
  values.dataset.heat = hardwareHeat(reading.temp, kind);
  if (badge) values.append(icon(hardwareIcon(kind), 'hw-icon'));
  if (sensorNumber(reading.temp) || !power) values.append(h('span', 'hw-temp', icon('thermometer'),
    h('span', 'hw-number', sensorNumber(reading.temp) ? `${Math.round(reading.temp)}°C` : '—')));
  if (power) {
    const value = h('span', 'hw-power', icon('zap'), h('span', 'hw-number', power));
    value.dataset.unit = watts ? 'watt' : 'percent';
    values.append(value);
  }
  const label = kind === 'gpu' ? tp('GPU') : kind === 'ram' ? tp('RAM') : kind === 'storage' ? tp('DISK') : tp('CPU');
  values.title = `${label} · ${tp('Temperature')}: ${sensorNumber(reading.temp) ? Math.round(reading.temp) + '°C' : missing} · ${tp('Power')}: ${power || tp('No power data')}`;
  values.setAttribute('aria-label', values.title);
  return values;
}

// ---------------------------------------------------------------- more widgets: helpers
export const inflight = {};
/** fetch() + JSON with a localStorage cache; returns stale data when offline. */
export function cached(key, url, maxAgeMs) {
  const c = store.get('nc:' + key, null);
  if (c && Date.now() - c.at < maxAgeMs) return Promise.resolve(c.data);
  if (inflight[key]) return inflight[key];
  const ctl = new AbortController();
  let timer;
  const deadline = new Promise((_, reject) => {
    timer = setTimeout(() => { ctl.abort(); reject(new Error('timeout')); }, 10_000);
  });
  const request = safeFetch(url, { signal: ctl.signal })
    .then((r) => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); });
  inflight[key] = Promise.race([request, deadline])
    .then((data) => { store.set('nc:' + key, { at: Date.now(), data }); return data; })
    .catch((e) => { if (c) return c.data; throw e; })
    .finally(() => { clearTimeout(timer); delete inflight[key]; });
  return inflight[key];
}

/** Calls load() now and then every intervalMs while the widget is on a visible page. */
export function every(intervalMs, load, retryMs = 60_000) {
  let next = 0;
  return () => {
    if (Date.now() < next) return;
    next = Date.now() + intervalMs;
    // a load that returns false failed (offline, service down): try again soon, not after the whole interval
    Promise.resolve(load()).then((ok) => { if (ok === false) next = Math.min(next, Date.now() + retryMs); }, () => {});
  };
}

export function sparkLine(canvas, data, col) {
  if (!canvas.clientWidth || !data || data.length < 2) return;
  const [g, w, hh, dpr] = sizeCanvas(canvas);
  g.clearRect(0, 0, w, hh);
  const min = Math.min(...data), max = Math.max(...data), span = max - min || 1;
  g.beginPath();
  data.forEach((v, i) => { const x = (i / (data.length - 1)) * w, y = hh - 2 * dpr - ((v - min) / span) * (hh - 4 * dpr); i ? g.lineTo(x, y) : g.moveTo(x, y); });
  g.strokeStyle = col; g.lineWidth = 1.5 * dpr; g.lineJoin = 'round'; g.stroke();
}

export const fmtNum = (v) => (v == null ? '—' : v >= 1000 ? v.toLocaleString(LOCALE, { maximumFractionDigits: 0 }) : v >= 10 ? v.toFixed(2) : v.toPrecision(4));
export const pctSpan = (p) => { const e = h('span', 'chg ' + (p >= 0 ? 'up' : 'down'), `${p >= 0 ? '▲' : '▼'} ${Math.abs(p).toFixed(2)}%`); return e; };
export const lines = (v, d) => (Array.isArray(v) ? v : typeof v === 'string' ? v.split('\n') : d).map((x) => String(x).trim()).filter(Boolean);

/** Gauge-style dial used by several widgets. */
export function dialEl() {
  const dial = h('div', 'g-dial'), val = h('div', 'g-val');
  dial.append(val);
  let svg = null;
  dial.set = (pct, text, unit, elapsed) => {
    if (svg) svg.remove();
    dial.insertAdjacentHTML('afterbegin', ringSvg(pct, elapsed)); // numbers only
    svg = dial.firstElementChild;
    val.replaceChildren(String(text), unit ? h('small', null, unit) : '');
  };
  return dial;
}

export function canKillAnywhere() {
  return (cfg.pages || []).some((p) => p.enabled !== false && (p.widgets || []).some((w) => w.type === 'processes' && w.options?.allowKill !== false));
}
