// The page bar (Settings › Display › Page navigation): the dots shown as buttons with each page's icon and name,
// always (with its own strip of the screen, at the bottom or down a side) or while the dots are being used.
const { test, expect } = require('@playwright/test');

const PAGES = ['Home', 'Music', 'Stats', 'Games', 'Agenda'].map((title, i) => ({ id: 'p' + i, title, icon: '',
  widgets: [{ id: 'n' + i, type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: title } }] }));

async function show(page, display, clock = false, pages = PAGES) {
  await page.setViewportSize({ width: 800, height: 400 });
  if (clock) await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate(({ pages, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], pages,
      display: { idleReturnSec: 0, minDwellSec: 0, switchCountdownSec: 0, ...display } }));
  }, { pages, display });
  await page.goto('/panel/');
  await expect(page.locator('#dots .dot')).toHaveCount(pages.length);
  await expect(page.locator('body')).not.toHaveClass(/night/); // the default night hours apply until the stored settings do
}
const activeDot = (page) => page.locator('#dots i').evaluateAll((d) => d.findIndex((x) => x.classList.contains('on')));
const pagerBox = (page) => page.evaluate(() => { const p = document.getElementById('pager'); return { w: p.clientWidth, h: p.clientHeight, left: p.getBoundingClientRect().left, scroll: p.scrollLeft }; });

test('always shown at the bottom: names to tap, and the pages keep clear of the bar', async ({ page }) => {
  await show(page, { pageBar: 'bar' });
  await expect(page.locator('#dots')).toHaveClass(/\bpagebar\b/);
  await expect(page.locator('#dots .dot-nm').nth(3)).toBeVisible();
  await expect(page.locator('#dots .dot-nm').nth(3)).toHaveText('Games');
  const box = await pagerBox(page);
  expect(box.h).toBe(400 - 52);
  const bar = await page.locator('#dots').boundingBox();
  expect(Math.round(bar.y)).toBeGreaterThanOrEqual(box.h - 1);
  await page.locator('#dots .dot').nth(3).click();
  await expect.poll(() => activeDot(page)).toBe(3);
  await expect(page.locator('#dots .dot').nth(3)).toHaveClass(/\bon\b/);
});

test('always shown on the left: the pages are narrower and still turn one whole page at a time', async ({ page }) => {
  await show(page, { pageBar: 'bar', pageBarSide: 'left' });
  await expect(page.locator('#dots')).toHaveClass(/\bpb-vertical\b/);
  const box = await pagerBox(page);
  expect(box.w).toBe(800 - 76);
  expect(box.left).toBe(76);
  await page.locator('#dots .dot').nth(2).click();
  await expect.poll(() => activeDot(page)).toBe(2);
  await expect.poll(async () => (await pagerBox(page)).scroll).toBe(2 * (800 - 76));
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => activeDot(page)).toBe(3);
});

test('shown when the dots are touched: the first touch opens the bar, it closes again by itself', async ({ page }) => {
  await show(page, { pageBar: 'auto' }, true);
  await expect(page.locator('#dots')).not.toHaveClass(/\bpagebar\b/);
  expect((await pagerBox(page)).h).toBe(400);              // nothing is taken from the pages
  await page.locator('#dots .dot').nth(3).click();        // opens the bar, goes nowhere
  await expect(page.locator('#dots')).toHaveClass(/\bpagebar\b/);
  expect(await activeDot(page)).toBe(0);
  await page.locator('#dots .dot').nth(3).click();        // now the button opens its page
  await expect.poll(() => activeDot(page)).toBe(3);
  for (let i = 0; i < 6; i++) await page.clock.fastForward(1000);
  await expect(page.locator('#dots')).not.toHaveClass(/\bpagebar\b/);
});

test('the touch that opens the bar presses neither the overview nor the pause button', async ({ page }) => {
  await show(page, { pageBar: 'auto' });
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#dots')).toHaveClass(/pagebar/);
  await expect(page.locator('#overview')).toBeHidden();
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#overview')).toBeVisible();
});

test('a side bar shown by a touch stays centred with the controls at the top', async ({ page }) => {
  await show(page, { pageBar: 'auto', pageBarSide: 'left', controlsTop: true });
  await page.locator('#dots .dot').first().click();
  await expect(page.locator('#dots')).toHaveClass(/pb-vertical/);
  const bar = await page.locator('#dots').boundingBox();
  expect(bar.y).toBeGreaterThanOrEqual(0);
  expect(Math.abs(bar.y + bar.height / 2 - 200)).toBeLessThan(2);
});

// 使用真实面板传给 MainActivity 的几何信息，而非模拟的 HTML 浏览器或 Dock 叠层。
const BROWSER_PAGES = [
  { id: 'browser', title: 'Browser', kind: 'web', url: 'https://example.test/', widgets: [] },
  PAGES[0],
  { id: 'browser2', title: 'Browser 2', kind: 'web', url: 'https://example.test/', widgets: [] },
];
async function showNativeBrowser(page, display, clock = false) {
  await page.addInitScript(() => {
    window.__webCalls = [];
    window.Android = { webPage: (key, action, url, pageKey, x, y, w, h) => {
      window.__webCalls.push({ action, url, pageKey, rect: [x, y, w, h] });
    } };
  });
  await show(page, display, clock, BROWSER_PAGES);
  // 演示版 publicConfig 早于网页功能，会移除 kind/url；在此补上真实代理的页面字段。
  await page.evaluate(async (pages) => {
    const m = await import('/panel/main.js');
    m.cfg.pages = pages;
    m.buildLayout(); m.goPage(0, true); m.syncWebPage();
  }, BROWSER_PAGES);
  await expect.poll(() => page.evaluate(() => window.__webCalls.some((c) => c.action === 'show'))).toBe(true);
}
async function expectNativeGeometry(page) {
  await expect.poll(() => page.evaluate(() => {
    const calls = window.__webCalls;
    const show = calls.filter((c) => c.action === 'show').at(-1);
    const dock = calls.filter((c) => c.action === 'dock').at(-1);
    const body = document.querySelectorAll('.bx-body')[0].getBoundingClientRect();
    const dots = document.getElementById('dots'), s = getComputedStyle(dots), d = dots.getBoundingClientRect();
    const x = Math.max(0, d.left), y = Math.max(0, d.top);
    const w = Math.min(innerWidth, d.right) - x, h = Math.min(innerHeight, d.bottom) - y;
    const dr = !dots.hidden && s.visibility === 'visible' && +s.opacity > 0 && s.pointerEvents !== 'none' && w > 0 && h > 0 ? [x, y, w, h] : [0, 0, 0, 0];
    return { fullBody: JSON.stringify(show?.rect) === JSON.stringify([body.left, body.top, body.width, body.height]),
      realDock: JSON.stringify(dock?.rect) === JSON.stringify(dr) };
  })).toEqual({ fullBody: true, realDock: true });
}

test('native browser fills the body; auto Dock open/close updates the cutout without resizing the site', async ({ page }) => {
  await showNativeBrowser(page, { pageBar: 'auto' }, true);
  await expectNativeGeometry(page);
  const body = await page.locator('.bx-body').first().boundingBox();
  const closed = await page.locator('#dots').boundingBox();
  await page.locator('#dots .dot').first().click();
  await expect(page.locator('#dots')).toHaveClass(/pagebar/);
  await expectNativeGeometry(page);
  expect(await page.locator('.bx-body').first().boundingBox()).toEqual(body);
  expect((await page.locator('#dots').boundingBox()).height).toBeGreaterThan(closed.height);
  for (let i = 0; i < 6; i++) await page.clock.fastForward(1000);
  await expect(page.locator('#dots')).not.toHaveClass(/pagebar/);
  await expectNativeGeometry(page);
  expect(await page.locator('.bx-body').first().boundingBox()).toEqual(body);
});

for (const display of [
  { pageBar: 'dots' }, { pageBar: 'auto', pageBarSide: 'left' },
  { pageBar: 'auto', pageBarSide: 'right', controlsTop: true }, { pageBar: 'bar' },
]) test(`native browser uses the actual Dock rect: ${JSON.stringify(display)}`, async ({ page }) => {
  await showNativeBrowser(page, display);
  if (display.pageBar === 'auto') await page.locator('#dots .dot').first().click();
  await expectNativeGeometry(page);
  await page.setViewportSize({ width: 460, height: 720 });
  await expectNativeGeometry(page);
  await page.evaluate(() => { document.getElementById('dots').hidden = true; });
  await expectNativeGeometry(page);
  await page.evaluate(() => { document.getElementById('dots').hidden = false; });
  await expectNativeGeometry(page);
});

test('native browser geometry dedupes, follows page movement, hides, and distinguishes pages sharing a URL', async ({ page }) => {
  await showNativeBrowser(page, { pageBar: 'dots' });
  await expectNativeGeometry(page);
  const stable = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.syncWebPage();
    const n = window.__webCalls.length;
    m.syncWebPage(); m.syncWebPage();
    return window.__webCalls.length === n;
  });
  expect(stable).toBe(true);
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.pager.style.scrollSnapType = 'none';
    m.pager.scrollLeft = 80;
    m.syncWebPage();
  });
  await expectNativeGeometry(page);
  expect((await page.evaluate(() => window.__webCalls.filter((c) => c.action === 'show').at(-1))).rect[0]).toBeLessThan(0);
  await page.evaluate(async () => (await import('/panel/main.js')).goPage(1, true));
  await expect.poll(() => page.evaluate(() => window.__webCalls.at(-1)?.action)).toBe('hide');
  await page.evaluate(async () => (await import('/panel/main.js')).goPage(2, true));
  await expect.poll(() => page.evaluate(() => window.__webCalls.filter((c) => c.action === 'show').at(-1)?.pageKey)).toBe('browser2');
  await page.evaluate(async () => (await import('/panel/main.js')).goPage(0, true));
  await expectNativeGeometry(page);
  await page.evaluate(() => { document.querySelector('.bx-body').style.display = 'none'; });
  await expect.poll(() => page.evaluate(() => window.__webCalls.at(-1)?.action)).toBe('hide');
});
