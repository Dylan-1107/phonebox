const { test: base, expect } = require('@playwright/test');
const catalog = require('../../agent/PhoneBox.Agent/widgets.json');
const templates = require('../../agent/PhoneBox.Agent/templates.json');
const system = templates.find(t => t.id === 'system'), home = templates.find(t => t.id === 'home');

const hardware = () => ({
  cpu: 23, cpuName: 'AMD Ryzen 9 7950X', cpuTemp: 63, cpuPower: 95.5,
  ramUsedGb: 22, ramTotalGb: 64, uptimeSec: 3600, netDownBps: 120000, netUpBps: 8000,
  gpu: { name: 'NVIDIA GeForce RTX 4080', util: 38, temp: 57, power: 120, vramUsed: 6, vramTotal: 16, source: 'nvidia-smi' },
  gpus: [
    { name: 'NVIDIA GeForce RTX 4080', temp: 57, power: 120, vramUsed: 6, vramTotal: 16, source: 'nvidia-smi' },
    { name: 'AMD Radeon Graphics', temp: 44, sharedUsed: 0.3, sharedTotal: 32, integrated: true, source: 'windows' },
  ],
  // 这些是逻辑卷，而非物理传感器；绝不能将伪造的温度或功耗关联到它们。
  disks: [{ name: 'C:', temp: 99, power: 999, usedGb: 120, totalGb: 1000 }, { name: 'D:', usedGb: 80, totalGb: 500 }],
  temperatures: { lhmConnected: true,
    ram: [{ id: 'a2', name: 'DIMM A2', temp: 40, power: 4, source: 'lhm' }, { id: 'b2', name: 'DIMM B2', temp: 42, power: 6, source: 'lhm' }],
    storage: [{ id: 'nvme0', name: 'Samsung SSD 990 PRO', temp: 35, power: 2.5, source: 'lhm' },
      { id: 'sata0', name: 'Seagate BarraCuda', temp: 38, power: 4.5, source: 'smart' }],
  },
});

const test = base.extend({
  hardwarePanel: async ({ page }, use) => {
    const setPc = async pc => page.evaluate(async pc => {
      window.__hardwarePc = pc;
      const { D, emit } = await import('/panel/main.js');
      D.state = { ...D.state, pc };
      emit('state');
    }, pc);
    const load = async (pc, { template = system, widgets = template.widgets, display = {} } = {}) => {
      // 在演示初始化前预置数据，避免延迟返回的默认组件目录覆盖测试数据的布局。
      await page.goto('/panel/style.css');
      await page.evaluate(({ widgets, display }) => {
        localStorage.clear();
        localStorage.setItem('pb-demo-config', JSON.stringify({
          night: { enabled: false }, shortcuts: [], rules: [], display: { idleReturnSec: 0, language: 'en', ...display },
          pages: [{ id: 'test', title: 'Test', enabled: true, widgets: widgets.map((w, i) => ({ ...w, id: `test-${i}` })) }],
        }));
      }, { widgets, display });
      await page.goto('/panel/');
      await expect(page.locator('.grid > .w').first()).toBeVisible();
      await page.waitForFunction(async () => !!(await import('/panel/main.js')).D.state?.pc);
      await page.evaluate(async pc => {
        // 包装演示版的 fetch，而非拦截网络路由，确保后续每次轮询也使用当前测试数据。
        window.__hardwarePc = pc;
        const real = window.fetch;
        window.fetch = async (...args) => {
          const r = await real(...args);
          if (!String(args[0]).includes('/api/state')) return r;
          const state = await r.json();
          state.pc = window.__hardwarePc;
          return new Response(JSON.stringify(state), { headers: { 'Content-Type': 'application/json' } });
        };
        const { D, emit } = await import('/panel/main.js');
        D.state = { ...D.state, pc };
        emit('state');
      }, pc);
    };
    await use({ load, setPc });
  },
});

const cpu = page => page.locator('.w-chart[data-metrics="cpu"] .chips .hw-values');
const gpu = page => page.locator('.w-chart[data-metrics="gpu"] .chips .hw-values');
const ram = page => page.locator('.ram-usage .hw-values');
const devices = (page, kind) => page.locator(`.w-meters .hw-device[data-kind="${kind}"]`);
const stat = (page, metric) => page.locator(`.w-stat[data-metric="${metric}"]`);
const statReading = (page, kind) => stat(page, 'ram').locator(`.t-extra .hw-values[data-kind="${kind}"]`);
const temp = locator => locator.locator('.hw-temp .hw-number');
const watts = locator => locator.locator('.hw-power .hw-number');
const gpuViews = [
  { type: 'chart', x: 0, y: 0, w: 6, h: 3, options: { metrics: ['gpu'] } },
  { type: 'stat', x: 6, y: 0, w: 3, h: 2, options: { metric: 'gpu' } },
  { type: 'gauge', x: 9, y: 0, w: 3, h: 3, options: { metric: 'gpu' } },
  { type: 'gpu', x: 0, y: 3, w: 6, h: 3, options: { adapter: '0' } },
  { type: 'gpu', x: 6, y: 3, w: 6, h: 3, options: { adapter: '1' } },
];

async function expectInside(card, children) {
  const r = await card.boundingBox();
  for (const el of await children.all()) {
    const b = await el.boundingBox();
    expect(b).not.toBeNull();
    expect(b.x).toBeGreaterThanOrEqual(r.x - 1);
    expect(b.x + b.width).toBeLessThanOrEqual(r.x + r.width + 1);
    expect(b.y).toBeGreaterThanOrEqual(r.y - 1);
    expect(b.y + b.height).toBeLessThanOrEqual(r.y + r.height - 1);
  }
}

test('System shares CPU/GPU metadata styling and preserves named physical readings and usage', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware());
  await expect(page.locator('.grid > .w')).toHaveCount(4);
  await expect(temp(cpu(page))).toHaveText('63°C');
  await expect(watts(cpu(page))).toHaveText('95.5 W');
  await expect(cpu(page)).toHaveAttribute('data-heat', 'normal');
  await expect(temp(gpu(page))).toHaveText('57°C');
  await expect(watts(gpu(page))).toHaveText('120 W');
  await expect(gpu(page)).toHaveAttribute('data-kind', 'gpu');
  await expect(gpu(page)).toHaveAttribute('data-heat', 'normal');
  await expect(gpu(page)).toHaveAttribute('aria-label', /GPU.*Temperature: 57°C.*Power: 120 W/);
  await expect(page.locator('.w-chart[data-metrics="gpu"] .s-label svg[data-icon="gpu"]')).toHaveCount(1);
  for (const reading of [cpu(page), gpu(page)]) {
    await expect(reading).toHaveCSS('font-size', '11px');
    await expect(reading).toHaveCSS('gap', '6px');
    await expect(reading.locator('.hw-temp')).toHaveCSS('gap', '2px');
    await expect(reading.locator('.hw-power')).toHaveCSS('gap', '2px');
    await expect(reading.locator('.hw-power')).toHaveCSS('color', 'rgb(238, 242, 246)');
    await expect(reading.locator('svg[data-icon="thermometer"]')).toHaveCount(1);
    await expect(reading.locator('svg[data-icon="zap"]')).toHaveCount(1);
  }
  await expect(page.locator('.ram-usage .hw-usage')).toHaveText('22 / 64 GB');
  await expect(temp(ram(page))).toHaveText('42°C');
  await expect(watts(ram(page))).toHaveText('4–6 W');
  await expect(page.locator('.w-meters > .s-head .s-name')).toHaveText('Up 1 h 0 min');
  await expect(page.locator('.w-meters > .meters > .meter')).toHaveCount(3);
  await expect(page.locator('.w-meters .disks .m-row')).toHaveText(['C:12%', 'D:16%']);
  await expect(page.locator('.w-meters .disks .hw-values')).toHaveCount(0);
  await expect(devices(page, 'ram').locator('.hw-device-name')).toHaveText(['DIMM A2', 'DIMM B2']);
  await expect(devices(page, 'storage').locator('.hw-device-name')).toHaveText(['Samsung SSD 990 PRO', 'Seagate BarraCuda']);
  await expect(devices(page, 'storage').locator('.hw-temp .hw-number')).toHaveText(['35°C', '38°C']);
  await expect(devices(page, 'storage').locator('.hw-power .hw-number')).toHaveText(['2.5 W', '4.5 W']);
  await expect(page.locator('.w-meters')).not.toContainText(/99°C|999 W/);
  for (const kind of ['cpu', 'gpu']) {
    const chart = page.locator(`.w-chart[data-metrics="${kind}"]`);
    const chip = await chart.locator('.hw-values').boundingBox(), load = await chart.locator('.s-main > b').boundingBox();
    expect(chip.x).toBeGreaterThan(load.x + load.width);
    await expectInside(chart, chart.locator('.hw-values, .hw-number'));
  }
});

test('CPU temperature and actual watts are independent, including zero and unavailable power', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware());
  for (const c of [
    { cpuTemp: 0, cpuPower: 0, t: '0°C', w: '0 W' },
    { cpuTemp: 55, cpuPower: null, t: '55°C', w: null },
    { cpuTemp: null, cpuPower: 65.2, t: null, w: '65.2 W' },
    { cpuTemp: null, cpuPower: null, t: '—', w: null },
    { cpuTemp: '55', cpuPower: '65', t: '—', w: null },
    { cpuTemp: Infinity, cpuPower: NaN, t: '—', w: null },
  ]) {
    await hardwarePanel.setPc({ ...hardware(), cpu: 100, cpuTemp: c.cpuTemp, cpuPower: c.cpuPower });
    if (c.t == null) await expect(temp(cpu(page))).toHaveCount(0);
    else await expect(temp(cpu(page))).toHaveText(c.t);
    if (c.w == null) await expect(watts(cpu(page))).toHaveCount(0);
    else await expect(watts(cpu(page))).toHaveText(c.w);
  }
  await expect(cpu(page)).not.toContainText(/NaN|Infinity|100 W|0 W/);
});

test('sensorless Apple GPUs retain memory/model fallback in actual Home, System and gauge metadata', async ({ page, hardwarePanel }) => {
  const pc = hardware();
  pc.gpu = { name: 'Apple M2 Pro', util: 37, temp: null, power: null, powerPct: null, vramUsed: 2, vramTotal: 16 };
  pc.gpus = [pc.gpu];
  for (const view of [
    { options: { template: home }, selector: '.w-stat[data-metric="gpu"]' },
    { options: { template: system }, selector: '.w-chart[data-metrics="gpu"]' },
    { options: { widgets: [{ type: 'gauge', x: 0, y: 0, w: 6, h: 6, options: { metric: 'gpu' } }] }, selector: '.w-gauge[data-metric="gpu"]' },
  ]) {
    await hardwarePanel.load(pc, view.options);
    const card = page.locator(view.selector), reading = card.locator('.hw-values');
    await expect(reading.locator('.hw-fallback')).toHaveText('2 / 16 GB memory');
    await expect(reading.locator('.hw-fallback')).toBeVisible();
    await expect(reading.locator('.hw-fallback')).toHaveCSS('color', 'rgb(138, 148, 163)');
    await expect(reading).toHaveAttribute('aria-label', /2 \/ 16 GB memory/);
    await expect(temp(reading)).toHaveText('—');
    await expect(watts(reading)).toHaveCount(0);
    for (const [sensor, text] of [[{ temp: 0 }, '0°C'], [{ power: 0 }, '0 W'], [{ powerPct: 0 }, '0% of limit']]) {
      await hardwarePanel.setPc({ ...pc, gpu: { ...pc.gpu, ...sensor } });
      await expect(reading).toContainText(text);
      await expect(reading.locator('.hw-fallback')).toHaveCount(0);
    }
    await hardwarePanel.setPc({ ...pc, gpu: { ...pc.gpu, vramUsed: null, vramTotal: null } });
    await expect(reading.locator('.hw-fallback')).toHaveText('Apple M2 Pro');
    await hardwarePanel.setPc({ ...pc, gpu: null });
    await expect(reading.locator('.hw-fallback')).toHaveCount(0);
    await expect(temp(reading)).toHaveText('—');
    await expect(watts(reading)).toHaveCount(0);
    await expect(reading).toHaveAttribute('aria-label', /Temperature: No data/);
    await expect(reading).not.toContainText(/0°C|0 W|GB memory|Apple/);
  }
});

for (const template of [system, home]) {
  test(`${template.id} GPU metadata guards missing/invalid readings, real zero and power-share fallback`, async ({ page, hardwarePanel }) => {
    await hardwarePanel.load(hardware(), { template });
    const reading = template === system ? gpu(page) : stat(page, 'gpu').locator('.t-sub .hw-values');
    for (const c of [
      { temp: 0, power: 0, share: 99, t: '0°C', p: '0 W', unit: 'watt' },
      { temp: 55, power: null, t: '55°C' },
      { temp: null, power: 65.2, t: null, p: '65.2 W', unit: 'watt' },
      { temp: null, power: null, t: '—' },
      { temp: '55', power: '65', t: '—' },
      { temp: true, power: false, t: '—' },
      { temp: [], power: {}, t: '—' },
      { temp: Infinity, power: NaN, t: '—' },
      { temp: -5, power: -1, t: '-5°C' },
      { temp: null, power: null, share: 0, t: null, p: '0% of limit', unit: 'percent' },
      { temp: 54, power: -1, share: 11.7, t: '54°C', p: '12% of limit', unit: 'percent' },
      { temp: null, power: null, share: -1, t: '—' },
      { temp: null, power: null, share: '12', t: '—' },
    ]) {
      const pc = hardware();
      pc.gpu = { ...pc.gpu, util: 100, temp: c.temp, power: c.power, powerPct: c.share };
      pc.temperatures.lhmConnected = false;
      await hardwarePanel.setPc(pc);
      if (c.t == null) await expect(temp(reading)).toHaveCount(0);
      else await expect(temp(reading)).toHaveText(c.t);
      if (c.p == null) {
        await expect(watts(reading)).toHaveCount(0);
        await expect(reading).toHaveAttribute('aria-label', /No power data/);
      } else {
        await expect(watts(reading)).toHaveText(c.p);
        await expect(reading.locator('.hw-power')).toHaveAttribute('data-unit', c.unit);
        await expect(reading.locator('.hw-power')).toHaveCSS('color', 'rgb(238, 242, 246)');
      }
      if (!Number.isFinite(c.temp)) {
        await expect(reading).toHaveAttribute('data-heat', 'unknown');
        await expect(reading).toHaveAttribute('aria-label', /Temperature: No data/);
      }
      await expect(reading).not.toHaveAttribute('aria-label', /LibreHardwareMonitor/);
      await expect(reading).not.toContainText(/NaN|Infinity|true|false|100 W|\[object Object\]/);
    }
    const zero = hardware(); zero.gpu = { ...zero.gpu, temp: 0, power: 0 };
    await hardwarePanel.setPc(zero);
    await page.evaluate(async () => {
      const { applyState } = await import('/panel/main.js');
      applyState(await (await fetch('/api/state')).json());
    });
    await expect(temp(reading)).toHaveText('0°C');
    await expect(watts(reading)).toHaveText('0 W');
    await hardwarePanel.setPc({ ...hardware(), gpu: null });
    await expect(temp(reading)).toHaveText('—');
    await expect(watts(reading)).toHaveCount(0);
    await hardwarePanel.setPc(null);
    await page.evaluate(async () => {
      const { D, emit } = await import('/panel/main.js'); D.state = null; emit('state');
    });
    await expect(temp(reading)).toHaveText('—');
    await expect(reading).toHaveAttribute('data-heat', 'unknown');
    await expect(reading).toHaveAttribute('aria-label', /Loading…/);
    await expect(watts(reading)).toHaveCount(0);
  });
}

test('negative CPU/module/disk watts are unavailable while mixed ranges preserve real zero', async ({ page, hardwarePanel }) => {
  const pc = hardware(); pc.cpuPower = -12;
  pc.temperatures.ram = [{ name: 'Negative DIMM', temp: 40, power: -2 }, { name: 'Zero DIMM', temp: 41, power: 0 },
    { name: 'Actual DIMM', temp: 42, power: 6 }, { power: -5 }];
  pc.temperatures.storage = [{ name: 'Negative disk', temp: 35, power: -1 }, { name: 'Actual disk', temp: 38, power: 4 }, { power: -3 }];
  await hardwarePanel.load(pc);
  await expect(temp(cpu(page))).toHaveText('63°C');
  await expect(watts(cpu(page))).toHaveCount(0);
  await expect(watts(ram(page))).toHaveText('0–6 W');
  await expect(devices(page, 'ram')).toHaveCount(3);
  await expect(devices(page, 'ram').first().locator('.hw-power')).toHaveCount(0);
  await expect(devices(page, 'ram').locator('.hw-power .hw-number')).toHaveText(['0 W', '6 W']);
  await expect(devices(page, 'storage')).toHaveCount(2);
  await expect(devices(page, 'storage').first().locator('.hw-power')).toHaveCount(0);
  await expect(devices(page, 'storage').locator('.hw-power .hw-number')).toHaveText('4 W');
  await hardwarePanel.load(pc, { template: home });
  await expect(stat(page, 'cpu').locator('.hw-power')).toHaveCount(0);
  await expect(watts(statReading(page, 'ram'))).toHaveText('0–6 W');
  await expect(watts(statReading(page, 'storage'))).toHaveText('4 W');
});

for (const [language, unavailable] of [['zh', '暂无功耗数据'], ['zh-Hant', '暫無功耗資料']]) {
  test(`${language} hardware accessibility uses power-consumption and the existing memory label`, async ({ page, hardwarePanel }) => {
    await hardwarePanel.load(hardware(), { display: { language } });
    const memoryLabel = await page.evaluate(() => window.PhoneBoxI18n.t('w.memory'));
    await expect(page.locator('.w-meters > .meters')).toHaveAttribute('aria-label', memoryLabel);
    await expect(cpu(page)).toHaveAttribute('aria-label', /功耗: 95\.5 W/);
    await hardwarePanel.setPc({ ...hardware(), cpuPower: null });
    await expect(cpu(page)).toHaveAttribute('aria-label', new RegExp(unavailable));
    await expect(watts(cpu(page))).toHaveCount(0);
  });
}

test('legacy agents preserve usage and real CPU temperature without guessing RAM/storage sensors', async ({ page, hardwarePanel }) => {
  const pc = hardware();
  delete pc.temperatures; delete pc.cpuPower; delete pc.gpus;
  await hardwarePanel.load(pc);
  await expect(temp(cpu(page))).toHaveText('63°C');
  await expect(watts(cpu(page))).toHaveCount(0);
  await expect(ram(page)).toHaveAttribute('data-heat', 'unknown');
  await expect(ram(page)).toHaveAttribute('title', /No data/);
  await expect(temp(ram(page))).toHaveText('—');
  await expect(devices(page, 'storage')).toHaveCount(0);
  await expect(devices(page, 'ram')).toHaveCount(0);
  await expect(page.locator('.ram-usage .hw-usage')).toHaveText('22 / 64 GB');
  await expect(page.locator('.w-meters .disks > .meter')).toHaveCount(2);
  await expect(page.locator('.w-meters')).not.toContainText('LibreHardwareMonitor');
});

test('loading clears old readings and disconnected/unknown/no-sensor states stay distinct', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware());
  await hardwarePanel.setPc(null);
  await expect(page.locator('.w-meters > .meters')).toHaveText('Loading…');
  await expect(temp(cpu(page))).toHaveText('—');
  await expect(cpu(page)).toHaveAttribute('title', /Loading…/);
  await expect(watts(cpu(page))).toHaveCount(0);
  for (const [temperatures, missing] of [[undefined, 'No data'], [{}, 'No data'], [{ lhmConnected: 'false' }, 'No data'],
    [{ lhmConnected: false }, 'needs LibreHardwareMonitor'], [{ lhmConnected: true, ram: [], storage: [] }, 'No sensor exposed']]) {
    await hardwarePanel.setPc({ ...hardware(), cpuTemp: null, cpuPower: null, temperatures });
    await expect(cpu(page)).toHaveAttribute('title', new RegExp(missing));
    await expect(ram(page)).toHaveAttribute('title', new RegExp(missing));
    await expect(temp(ram(page))).toHaveText('—');
  }
  const offline = hardware(); offline.temperatures.lhmConnected = false;
  await hardwarePanel.setPc(offline);
  await expect(temp(cpu(page))).toHaveText('63°C');
  await expect(watts(cpu(page))).toHaveText('95.5 W');
  await expect(devices(page, 'storage').first().locator('.hw-temp .hw-number')).toHaveText('35°C');
  await expect(devices(page, 'storage').last().locator('.hw-power .hw-number')).toHaveText('4.5 W');
});

test('malformed sensors cannot coerce null, strings, booleans or nonfinite values to zero', async ({ page, hardwarePanel }) => {
  const pc = hardware(), bad = [null, false, 12, 'sensor', [], {}, { name: {}, temp: '12', power: '5' }];
  pc.temperatures = { lhmConnected: true,
    ram: [...bad, { name: 'Zero DIMM', temp: 0, power: 0 }, { name: 'Null DIMM', temp: null, power: null },
      { name: 'Invalid DIMM', temp: true, power: Infinity }],
    storage: [...bad, { name: 'Zero disk', temp: 0, power: 0, source: {} }, { name: 'Power only', temp: null, power: 3.2 },
      { name: 'Invalid disk', temp: NaN, power: '9' }],
  };
  await hardwarePanel.load(pc);
  await expect(temp(ram(page))).toHaveText('0°C');
  await expect(watts(ram(page))).toHaveText('0 W');
  await expect(devices(page, 'ram')).toHaveCount(3);
  await expect(devices(page, 'storage')).toHaveCount(3);
  await expect(devices(page, 'ram').locator('.hw-temp .hw-number')).toHaveText(['0°C', '—', '—']);
  await expect(devices(page, 'storage').locator('.hw-temp .hw-number')).toHaveText(['0°C', '—']);
  await expect(devices(page, 'storage').locator('.hw-power .hw-number')).toHaveText(['0 W', '3.2 W']);
  await expect(devices(page, 'ram').last().locator('.hw-values')).toHaveAttribute('data-heat', 'unknown');
  await expect(devices(page, 'ram').last().locator('.hw-temp')).toHaveCSS('color', 'rgb(138, 148, 163)');
  await expect(page.locator('.w-meters')).not.toContainText(/NaN|Infinity|12°C|9 W|\[object Object\]/);
  await hardwarePanel.setPc({ ...hardware(), temperatures: { lhmConnected: true, ram: {}, storage: 'bad' } });
  await expect(devices(page, 'ram')).toHaveCount(0);
  await expect(devices(page, 'storage')).toHaveCount(0);
  await expect(temp(ram(page))).toHaveText('—');
});

const heatColors = { cool: 'rgb(34, 211, 238)', normal: 'rgb(74, 222, 128)', warm: 'rgb(251, 191, 36)', hot: 'rgb(248, 113, 113)' };
for (const [kind, levels] of [['cpu', [50, 70, 85]], ['gpu', [50, 75, 85]], ['ram', [35, 50, 65]], ['storage', [35, 50, 60]]]) {
  test(`${kind} heat uses every exact boundary and hottest actual reading, with neutral watts`, async ({ page, hardwarePanel }) => {
    await hardwarePanel.load(hardware());
    const target = () => kind === 'cpu' ? cpu(page) : kind === 'gpu' ? gpu(page) : kind === 'ram' ? ram(page) : devices(page, 'storage').first().locator('.hw-values');
    for (const [value, heat] of [[0, 'cool'], [levels[0] - 0.1, 'cool'], [levels[0], 'normal'], [levels[1] - 0.1, 'normal'],
      [levels[1], 'warm'], [levels[2] - 0.1, 'warm'], [levels[2], 'hot'], [levels[2] + 1, 'hot']]) {
      const pc = hardware();
      if (kind === 'cpu') { pc.cpuTemp = value; pc.cpuPower = 5; }
      else if (kind === 'gpu') pc.gpu = { ...pc.gpu, temp: value, power: 5 };
      else pc.temperatures[kind] = [{ name: 'Hottest sensor', temp: value, power: 5 }, ...(kind === 'ram' ? [{ name: 'Cooler sensor', temp: -1, power: 2 }] : [])];
      await hardwarePanel.setPc(pc);
      await expect(target()).toHaveAttribute('data-heat', heat);
      await expect(temp(target())).toHaveText(`${Math.round(value)}°C`);
      await expect(target().locator('.hw-temp')).toHaveCSS('color', heatColors[heat]);
      await expect(target().locator('.hw-power')).toHaveCSS('color', 'rgb(238, 242, 246)');
    }
  });
}

test('GPU charts/stats/gauges retain the primary selector and dedicated cards retain adapter selection', async ({ page, hardwarePanel }) => {
  const errors = []; page.on('pageerror', e => errors.push(String(e)));
  const pc = hardware();
  pc.gpus[0] = { ...pc.gpus[0], util: 18 };
  pc.gpus[1] = { ...pc.gpus[1], util: 67, power: 13 };
  pc.gpu = pc.gpus[1]; // 刻意让主 GPU 不位于数组索引 0。
  await hardwarePanel.load(pc, { widgets: gpuViews });
  const primary = [gpu(page), stat(page, 'gpu').locator('.t-sub .hw-values'), page.locator('.w-gauge .g-sub .hw-values')];
  for (const reading of primary) {
    await expect(temp(reading)).toHaveText('44°C');
    await expect(watts(reading)).toHaveText('13 W');
  }
  await expect(page.locator('.w-chart .s-main > b')).toHaveText('67');
  await expect(stat(page, 'gpu').locator('.t-val > b')).toHaveText('67');
  await expect(page.locator('.w-gauge .g-val')).toHaveText('67%');
  const detail = page.locator('.w-gpu');
  await expect(detail.nth(0).locator('.s-name')).toHaveText('GeForce RTX 4080');
  await expect(detail.nth(1).locator('.s-name')).toHaveText('Radeon Graphics');
  await expect(detail.locator('.gd-temp > .hw-values .hw-number')).toHaveText(['57°C', '44°C']);
  await expect(detail.locator('.gd-rows .hw-power .hw-number')).toHaveText(['120 W', '13 W']);
  pc.gpu = pc.gpus[0];
  await hardwarePanel.setPc(pc);
  for (const reading of primary) {
    await expect(temp(reading)).toHaveText('57°C');
    await expect(watts(reading)).toHaveText('120 W');
  }
  pc.gpus.reverse();
  await hardwarePanel.setPc(pc);
  await expect(detail.locator('.gd-temp > .hw-values .hw-number')).toHaveText(['44°C', '57°C']);
  delete pc.gpus;
  await hardwarePanel.setPc(pc);
  await expect(detail.nth(0).locator('.gd-temp .hw-temp .hw-number')).toHaveText('57°C');
  await expect(detail.nth(1)).toContainText('No second GPU');
  expect(errors).toEqual([]);
});

test('Home, chart, gauge and dedicated GPU readouts share GPU colors without changing temperature-limit tracks', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware(), { widgets: gpuViews });
  const detail = page.locator('.w-gpu').first();
  const primary = [gpu(page), stat(page, 'gpu').locator('.t-sub .hw-values'), page.locator('.w-gauge .g-sub .hw-values')];
  for (const [value, heat] of [[0, 'cool'], [49.9, 'cool'], [50, 'normal'], [74.9, 'normal'], [75, 'warm'], [84.9, 'warm'], [85, 'hot']]) {
    const pc = hardware();
    pc.gpus[0] = { ...pc.gpus[0], temp: value, power: 5, tempMax: 100 };
    pc.gpu = pc.gpus[0];
    await hardwarePanel.setPc(pc);
    for (const reading of [...primary, detail.locator('.gd-temp > .hw-values'), detail.locator('.gd-rows .hw-values').last()]) {
      await expect(reading).toHaveAttribute('data-kind', 'gpu');
      await expect(reading).toHaveAttribute('data-heat', heat);
      await expect(temp(reading)).toHaveText(`${Math.round(value)}°C`);
      await expect(temp(reading)).toHaveCSS('color', heatColors[heat]);
    }
    for (const reading of primary) await expect(watts(reading)).toHaveCSS('color', 'rgb(238, 242, 246)');
    await expect(detail.locator('.hw-power .hw-number')).toHaveCSS('color', 'rgb(238, 242, 246)');
  }
  // 现有温度条仍在 tempMax - 15 时警告，在 tempMax - 5 时变红。
  await expect(detail.locator('.gd-rows .meter').last().locator('.track i')).toHaveCSS('background-color', 'rgb(245, 158, 11)');
  const pc = hardware(); pc.gpus[0] = { ...pc.gpus[0], temp: 95, tempMax: 100 }; pc.gpu = pc.gpus[0];
  await hardwarePanel.setPc(pc);
  await expect(detail.locator('.gd-rows .meter').last().locator('.track i')).toHaveCSS('background-color', 'rgb(239, 68, 68)');
});

test('dedicated GPU sensors share guards and neutral power while preserving other metrics', async ({ page, hardwarePanel }) => {
  const pc = hardware();
  pc.gpus[0] = { ...pc.gpus[0], util: 38, powerLimit: 320, clock: 1800, memClock: 9000, fan: 30, fanRpm: 1200, driver: '596.36' };
  await hardwarePanel.load(pc, { widgets: gpuViews.filter(w => w.type === 'gpu') });
  const card = page.locator('.w-gpu').first(), edge = card.locator('.gd-temp > .hw-values');
  await expect(card.locator('.gd-chips .chip')).toHaveText(['Core 1800 MHz', 'Memory 9000 MHz', 'Fan 30% · 1200 rpm', 'Driver 596.36', 'nvidia-smi']);
  await expect(card).toContainText('VRAM');
  for (const c of [
    { temp: 0, power: 0, t: '0°C', p: '0 W' },
    { temp: null, power: 15.5, t: '—', p: '15.5 W' },
    { temp: null, power: 15.5, share: '50', limit: null, t: '—', p: '15.5 W' },
    { temp: -4, power: -2, t: '-4°C' },
    { temp: '55', power: '120', t: '—' },
    { temp: true, power: false, t: '—' },
    { temp: {}, power: [], t: '—' },
    { temp: Infinity, power: NaN, t: '—' },
    { temp: null, power: null, share: 0, t: '—', p: '0% of limit' },
    { temp: null, power: null, share: -5, t: '—' },
  ]) {
    const next = { ...pc, temperatures: { lhmConnected: false }, gpus: [{ ...pc.gpus[0], temp: c.temp, power: c.power, powerPct: c.share,
      powerLimit: c.limit === null ? null : 320, hotSpot: Infinity, memTemp: '70' }] };
    await hardwarePanel.setPc(next);
    await expect(temp(edge)).toHaveText(c.t);
    if (c.p) await expect(card.locator('.gd-rows .hw-power .hw-number')).toHaveText(c.p);
    else await expect(card.locator('.gd-rows .hw-power')).toHaveCount(0);
    if (c.limit === null) expect(await card.locator('.meter:has(.hw-power) .track i').evaluate(el => el.style.width)).toBe('0%');
    await expect(edge).not.toHaveAttribute('aria-label', /LibreHardwareMonitor/);
    if (!Number.isFinite(c.temp)) await expect(edge).toHaveAttribute('aria-label', /Temperature: No data/);
    await expect(card.locator('.gd-sensor')).toHaveCount(0);
    await expect(card).not.toContainText(/NaN|Infinity|120 W|55°C|70°C|\[object Object\]/);
  }
  await hardwarePanel.setPc(null);
  await expect(card).toContainText('No GPU data');
  await expect(card.locator('.hw-values, .gd-rows .meter, .gd-chips .chip')).toHaveCount(0);
});

test('duplicate physical models retain independent names, ids, sources and actual readings', async ({ page, hardwarePanel }) => {
  const pc = hardware();
  pc.temperatures.storage = [
    { id: 'windows-0', name: 'Samsung SSD 990 PRO', temp: 34, power: 2, source: 'windows-storage' },
    { id: 'lhm-0', name: 'Samsung SSD 990 PRO', temp: 41, power: 3, source: 'lhm' },
  ];
  await hardwarePanel.load(pc);
  await expect(devices(page, 'storage')).toHaveCount(2);
  await expect(devices(page, 'storage').locator('.hw-source')).toHaveText(['Windows · windows-0', 'LibreHardwareMonitor · lhm-0']);
  await expect(devices(page, 'storage').locator('.hw-temp .hw-number')).toHaveText(['34°C', '41°C']);
  await expect(devices(page, 'storage').locator('.hw-power .hw-number')).toHaveText(['2 W', '3 W']);
  await expect(page.locator('.w-meters .disks .hw-values')).toHaveCount(0);
});

test('updates/removals survive later polls without stale CPU, RAM or physical disk readings', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware());
  const pc = hardware(); pc.cpuTemp = 0; pc.cpuPower = 0;
  pc.temperatures.ram = [{ id: 'replacement', temp: 54, power: 8, source: 'lhm' }];
  pc.temperatures.storage = [{ name: 'Replacement SSD', temp: 36, power: 1.5, source: 'windows' }];
  await hardwarePanel.setPc(pc);
  await page.evaluate(async () => {
    const { applyState } = await import('/panel/main.js');
    applyState(await (await fetch('/api/state')).json());
  });
  await expect(temp(cpu(page))).toHaveText('0°C');
  await expect(watts(cpu(page))).toHaveText('0 W');
  await expect(temp(ram(page))).toHaveText('54°C');
  await expect(watts(ram(page))).toHaveText('8 W');
  await expect(devices(page, 'ram')).toHaveCount(1);
  await expect(devices(page, 'storage')).toHaveCount(1);
  await expect(page.locator('.w-meters')).not.toContainText(/DIMM A2|Samsung|Seagate/);
  await hardwarePanel.setPc({ ...hardware(), cpuTemp: null, cpuPower: null, temperatures: { lhmConnected: true, ram: [], storage: [] } });
  await expect(temp(cpu(page))).toHaveText('—');
  await expect(watts(cpu(page))).toHaveCount(0);
  await expect(temp(ram(page))).toHaveText('—');
  await expect(devices(page, 'ram')).toHaveCount(0);
  await expect(devices(page, 'storage')).toHaveCount(0);
});

test('names, ids and sources are escaped text and all new hardware icons are decorative inline SVG', async ({ page, hardwarePanel }) => {
  const attack = '<img src=x onerror="window.__hardwareInjected=1"> & <svg onload="window.__hardwareInjected=1">';
  const pc = hardware(); pc.cpuName = attack; pc.gpu.name = attack;
  pc.temperatures.ram = [{ id: attack, temp: 35, power: 4, source: 'constructor' }];
  pc.temperatures.storage = [{ name: attack, temp: 40, power: 2, source: attack }];
  await hardwarePanel.load(pc);
  await expect(page.locator('.w-chart[data-metrics="cpu"] .s-name')).toHaveText(attack);
  await expect(page.locator('.w-chart[data-metrics="gpu"] .s-name')).toHaveText(attack);
  await expect(page.locator('.w-chart[data-metrics="gpu"] .s-label svg[data-icon="gpu"] path')).not.toHaveCount(0);
  await expect(devices(page, 'ram').locator('.hw-device-name')).toHaveText(attack);
  await expect(devices(page, 'storage').locator('.hw-device-name')).toHaveText(attack);
  await expect(devices(page, 'storage').locator('.hw-source')).toHaveText(attack);
  await expect(devices(page, 'ram').locator('.hw-source')).toHaveText('constructor');
  await expect(page.locator('.w-meters img, .hardware-chart img, .w-meters [onload], .w-meters [onerror]')).toHaveCount(0);
  const icons = page.locator('.hw-values svg, .hw-device-name svg, .hardware-chart .s-label svg');
  expect(await icons.count()).toBeGreaterThan(5);
  for (const svg of await icons.all()) {
    await expect(svg).toHaveAttribute('aria-hidden', 'true');
    await expect(svg.locator('path')).not.toHaveCount(0);
  }
  await expect(cpu(page).locator('svg[data-icon="thermometer"]')).toHaveCount(1);
  await expect(cpu(page).locator('svg[data-icon="zap"]')).toHaveCount(1);
  await expect(devices(page, 'ram').locator('svg[data-icon="memory-stick"]')).toHaveCount(1);
  await expect(devices(page, 'storage').locator('svg[data-icon="hard-drive"]')).toHaveCount(1);
  expect(await page.evaluate(() => window.__hardwareInjected)).toBeUndefined();
});

test('official System/Home geometry is retained and the standalone widget/catalog/icon is removed', async ({ page, hardwarePanel }) => {
  expect(catalog.some(w => w.type === 'temperatures')).toBe(false);
  expect(system.widgets).toEqual([
    { type: 'chart', x: 0, y: 0, w: 6, h: 3, options: { metrics: ['cpu'] } },
    { type: 'chart', x: 6, y: 0, w: 6, h: 3, options: { metrics: ['gpu'] } },
    { type: 'meters', x: 0, y: 3, w: 6, h: 3 },
    { type: 'chart', x: 6, y: 3, w: 6, h: 3, options: { metrics: ['down', 'up'] } },
  ]);
  expect(home.widgets.map(w => [w.type, w.x, w.y, w.w, w.h])).toEqual([
    ['clock', 0, 0, 6, 4], ['media', 6, 0, 6, 4], ['stat', 0, 4, 3, 2], ['stat', 3, 4, 3, 2], ['stat', 6, 4, 3, 2], ['stat', 9, 4, 3, 2],
  ]);
  await hardwarePanel.load(hardware());
  expect(await page.evaluate(async () => {
    const { W } = await import('/panel/registry.js'), { WIDGET_ICONS } = await import('/panel/main.js');
    return Object.hasOwn(W, 'temperatures') || Object.hasOwn(WIDGET_ICONS, 'temperatures');
  })).toBe(false);
  await expect(page.locator('.w-temperatures')).toHaveCount(0);
});

for (const viewport of [{ width: 780, height: 360 }, { width: 360, height: 780 }]) {
  test(`System scrolls named physical devices inside its existing card at ${viewport.width}×${viewport.height}`, async ({ page, hardwarePanel }) => {
    await page.setViewportSize(viewport);
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
    const pc = hardware();
    pc.temperatures.storage = Array.from({ length: 18 }, (_, i) => ({ id: `disk-${i}`, name: `Physical disk ${i}`, temp: 30 + i, power: 2, source: 'smart' }));
    await hardwarePanel.load(pc, { display: { theme: 'amoled', cardFill: 'none' } });
    await expect(page.locator('.grid > .w')).toHaveCount(4);
    if (viewport.width > viewport.height) expect(await page.locator('.grid > .w').evaluateAll(nodes => nodes.map(el => el.style.gridArea))).toEqual([
      '1 / 1 / span 3 / span 6', '1 / 7 / span 3 / span 6', '4 / 1 / span 3 / span 6', '4 / 7 / span 3 / span 6',
    ]);
    const gpuCard = page.locator('.w-chart[data-metrics="gpu"]');
    await expectInside(gpuCard, gpuCard.locator('.hw-values, .hw-number'));
    expect(await gpuCard.locator('.chips').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    const list = page.locator('.w-meters > .meters');
    expect(await list.evaluate(el => el.clientHeight > 0 && el.scrollHeight > el.clientHeight && el.scrollWidth <= el.clientWidth)).toBe(true);
    await list.focus(); await page.keyboard.press('End');
    await expect.poll(() => list.evaluate(el => el.scrollTop)).toBeGreaterThan(0);
    await devices(page, 'storage').last().scrollIntoViewIfNeeded();
    await expectInside(list, devices(page, 'storage').last());
    const surfaces = await page.locator('.hw-values, .hw-values *, .hw-device, .hw-device *, .hw-chip').evaluateAll(nodes => nodes.map(el => {
      const s = getComputedStyle(el); return [s.backgroundColor, s.backgroundImage, s.boxShadow];
    }));
    expect(surfaces.every(([c, image, shadow]) => c === 'rgba(0, 0, 0, 0)' && image === 'none' && shadow === 'none')).toBe(true);
    expect(errors).toEqual([]);
  });

  test(`dedicated GPU temperature/power metadata fits without filled surfaces at ${viewport.width}×${viewport.height}`, async ({ page, hardwarePanel }) => {
    await page.setViewportSize(viewport);
    const pc = hardware();
    pc.gpus[0] = { ...pc.gpus[0], util: 100, temp: 100, hotSpot: 105, memTemp: 88, power: 285.4, powerLimit: 320, tempMax: 110 };
    pc.gpus[1] = { ...pc.gpus[1], util: 25, powerPct: 12 };
    await hardwarePanel.load(pc, { widgets: gpuViews.filter(w => w.type === 'gpu').map(w => ({ ...w, y: 0, h: 6 })),
      display: { theme: 'amoled', cardFill: 'none' } });
    const cards = page.locator('.w-gpu');
    await expect(cards).toHaveCount(2);
    await expect(cards.first().locator('.gd-temp .hw-temp .hw-number')).toHaveText(['100°C', '105°C', '88°C']);
    await expect(cards.first().locator('.gd-rows .hw-power .hw-number')).toHaveText('285.4 W');
    await expect(cards.nth(1).locator('.gd-rows .hw-power .hw-number')).toHaveText('12% of limit');
    for (const card of await cards.all()) {
      await expect(card.locator('.s-label svg[data-icon="gpu"]')).toHaveCount(1);
      await expectInside(card, card.locator('.gd-temp .hw-values, .gd-rows .hw-values, .hw-number'));
      expect(await card.locator('.hw-values').evaluateAll(nodes => nodes.every(el => el.scrollWidth <= el.clientWidth + 1))).toBe(true);
      const surfaces = await card.locator('.hw-values, .hw-values *, .gd-temp, .gd-sensor, .gd-reading').evaluateAll(nodes => nodes.map(el => {
        const s = getComputedStyle(el); return [s.backgroundColor, s.backgroundImage, s.boxShadow];
      }));
      expect(surfaces.every(([c, image, shadow]) => c === 'rgba(0, 0, 0, 0)' && image === 'none' && shadow === 'none')).toBe(true);
      await expect(card.locator('.hw-power')).toHaveCSS('color', 'rgb(238, 242, 246)');
      for (const svg of await card.locator('.hw-values svg, .s-label svg').all()) await expect(svg).toHaveAttribute('aria-hidden', 'true');
    }
  });

  test(`Home keeps official cards and visible CPU/GPU/RAM/storage metadata at ${viewport.width}×${viewport.height}`, async ({ page, hardwarePanel }) => {
    await page.setViewportSize(viewport);
    await hardwarePanel.load(hardware(), { template: home, display: { theme: 'amoled', cardFill: 'none' } });
    await expect(page.locator('.grid > .w')).toHaveCount(6);
    if (viewport.width > viewport.height) expect(await page.locator('.grid > .w').evaluateAll(nodes => nodes.map(el => el.style.gridArea))).toEqual([
      '1 / 1 / span 4 / span 6', '1 / 7 / span 4 / span 6', '5 / 1 / span 2 / span 3', '5 / 4 / span 2 / span 3',
      '5 / 7 / span 2 / span 3', '5 / 10 / span 2 / span 3',
    ]);
    await expect(stat(page, 'cpu').locator('.t-sub .hw-temp .hw-number')).toHaveText('63°C');
    await expect(stat(page, 'cpu').locator('.t-sub .hw-power .hw-number')).toHaveText('95.5 W');
    const gpuReading = stat(page, 'gpu').locator('.t-sub .hw-values');
    await expect(temp(gpuReading)).toHaveText('57°C');
    await expect(watts(gpuReading)).toHaveText('120 W');
    await expect(gpuReading).toHaveAttribute('data-kind', 'gpu');
    await expect(gpuReading).toHaveAttribute('data-heat', 'normal');
    await expect(gpuReading).toHaveCSS('font-size', '11px');
    await expect(gpuReading).toHaveCSS('gap', '6px');
    await expect(stat(page, 'gpu').locator('.t-label svg[data-icon="gpu"]')).toHaveCount(1);
    await expect(gpuReading.locator('.hw-temp')).toHaveCSS('color', heatColors.normal);
    await expect(watts(gpuReading)).toHaveCSS('color', 'rgb(238, 242, 246)');
    await expectInside(stat(page, 'gpu'), stat(page, 'gpu').locator('.t-sub .hw-values, .hw-number'));
    expect(await gpuReading.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    const surfaces = await stat(page, 'gpu').locator('.t-sub, .hw-values, .hw-values *').evaluateAll(nodes => nodes.map(el => {
      const s = getComputedStyle(el); return [s.backgroundColor, s.backgroundImage, s.boxShadow, s.textShadow];
    }));
    expect(surfaces.every(([c, image, shadow, text]) => c === 'rgba(0, 0, 0, 0)' && image === 'none' && shadow === 'none' && text === 'none')).toBe(true);
    await expect(stat(page, 'ram').locator('.t-sub')).toHaveText('22 / 64 GB');
    await expect(temp(statReading(page, 'ram'))).toHaveText('42°C');
    await expect(watts(statReading(page, 'ram'))).toHaveText('4–6 W');
    await expect(temp(statReading(page, 'storage'))).toHaveText('38°C');
    await expect(watts(statReading(page, 'storage'))).toHaveText('2.5–4.5 W');
    await expectInside(stat(page, 'cpu'), stat(page, 'cpu').locator('.t-sub .hw-values'));
    await expectInside(stat(page, 'ram'), stat(page, 'ram').locator('.t-sub, .t-extra .hw-values'));
    const pc = hardware(); pc.cpuTemp = null; pc.cpuPower = 55;
    pc.gpu = { ...pc.gpu, temp: null, power: 135.4 };
    pc.temperatures.ram = [{ name: 'DIMM A2', temp: 50, power: null }, { name: 'DIMM B2', temp: null, power: 6 }];
    pc.temperatures.storage = [{ name: 'Power-only disk', temp: null, power: 4 }];
    await hardwarePanel.setPc(pc);
    await expect(stat(page, 'cpu').locator('.t-sub .hw-temp')).toHaveCount(0);
    await expect(stat(page, 'cpu').locator('.t-sub .hw-power .hw-number')).toHaveText('55 W');
    await expect(temp(gpuReading)).toHaveCount(0);
    await expect(watts(gpuReading)).toHaveText('135.4 W');
    await expectInside(stat(page, 'gpu'), gpuReading.locator('.hw-number'));
    await expect(temp(statReading(page, 'ram'))).toHaveText('50°C');
    await expect(watts(statReading(page, 'ram'))).toHaveText('6 W');
    await expect(temp(statReading(page, 'storage'))).toHaveCount(0);
    await expect(watts(statReading(page, 'storage'))).toHaveText('4 W');
    await expect(stat(page, 'ram').locator('.t-sub')).toHaveText('22 / 64 GB');
    await expectInside(stat(page, 'ram'), stat(page, 'ram').locator('.t-sub, .t-extra .hw-values'));
  });
}

test('a four-stat viewport retains RAM usage and both compact hardware groups', async ({ page, hardwarePanel }) => {
  await hardwarePanel.load(hardware(), { widgets: home.widgets.filter(w => w.type === 'stat').map(w => ({ ...w, y: 0 })) });
  await expect(page.locator('.w-stat')).toHaveCount(4);
  await expect(stat(page, 'ram').locator('.t-sub')).toHaveText('22 / 64 GB');
  await expectInside(stat(page, 'ram'), stat(page, 'ram').locator('.t-extra .hw-values'));
  await expect(statReading(page, 'ram').locator('svg[data-icon="memory-stick"]')).toHaveCount(1);
  await expect(statReading(page, 'storage').locator('svg[data-icon="hard-drive"]')).toHaveCount(1);
  await hardwarePanel.setPc({ ...hardware(), cpuTemp: 0, cpuPower: 0, temperatures: undefined });
  await expect(stat(page, 'cpu').locator('.hw-temp .hw-number')).toHaveText('0°C');
  await expect(stat(page, 'cpu').locator('.hw-power .hw-number')).toHaveText('0 W');
  await expect(temp(statReading(page, 'ram'))).toHaveText('—');
  await expect(temp(statReading(page, 'storage'))).toHaveText('—');
  await expect(stat(page, 'ram').locator('.t-sub')).toHaveText('22 / 64 GB');
});

test('unknown metric fallback and gauges remain valid with shared CPU metadata', async ({ page, hardwarePanel }) => {
  const errors = []; page.on('pageerror', e => errors.push(String(e)));
  await hardwarePanel.load(hardware(), { widgets: [
    { type: 'stat', x: 0, y: 0, w: 3, h: 2, options: { metric: 'constructor' } },
    { type: 'gauge', x: 3, y: 0, w: 3, h: 3, options: { metric: 'constructor' } },
    { type: 'chart', x: 6, y: 0, w: 6, h: 3, options: { metrics: ['constructor'] } },
  ] });
  await expect(page.locator('.w-stat .t-label')).toHaveText('CPU');
  await expect(page.locator('.w-gauge .g-label')).toHaveText('CPU');
  await expect(page.locator('.w-gauge .g-sub')).toHaveText('63°C · 95.5 W');
  await expect(temp(cpu(page))).toHaveText('63°C');
  expect(errors).toEqual([]);
});
