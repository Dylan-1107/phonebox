// OLED burn-in protection (Settings › Display): pixel shift and idle dim, against the web demo with a mocked clock.
const { test, expect } = require('@playwright/test');

async function show(page, display) {
  await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate((display) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, ...display },
      pages: [
        { id: 'one', title: 'One', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page one' } }] },
        { id: 'two', title: 'Two', widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page two' } }] },
      ],
    }));
  }, display);
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
  // Until the stored config arrives the panel runs on its defaults, whose night screen covers 00:30-07:30. Between
  // those hours (on the CI's UTC clock too) a tap would only wake that screen; the next tick clears it.
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('burn-in fixture configuration survives delayed default initialization', async ({ page }) => {
  const { gatedLayout } = require('./layout-ordering');
  expect(await gatedLayout(page, p => show(p, {}))).toEqual(['one', 'two']);
});

const shift = (page) => page.evaluate(() => {
  const s = getComputedStyle(document.documentElement);
  return `${s.getPropertyValue('--shift-x').trim()} ${s.getPropertyValue('--shift-y').trim()}`;
});

test('pixel shift is off unless it is turned on', async ({ page }) => {
  await show(page, {});
  await page.clock.fastForward(130_000);
  await expect(page.locator('body')).not.toHaveClass(/shifted/);
  expect(await page.evaluate(() => getComputedStyle(document.body).transform)).toBe('none');
});

test('pixel shift moves the panel to its next position every 2 minutes', async ({ page }) => {
  await show(page, { pixelShift: true });
  await expect(page.locator('body')).toHaveClass(/shifted/);
  const first = await shift(page);
  await page.clock.fastForward(121_000);
  await expect.poll(() => shift(page)).not.toBe(first);
  // never more than 2 px from its place
  const [x, y] = (await shift(page)).split(' ').map((v) => Math.abs(parseFloat(v)));
  expect(Math.max(x, y)).toBeLessThanOrEqual(2);
});

test('idle dim covers the panel, and the touch that wakes it presses nothing', async ({ page }) => {
  await show(page, { idleDimMin: 1 });
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);

  // a tap on the dots bar's overview button only brightens the panel ...
  const all = await page.locator('#dots .page-all').boundingBox();
  const tap = () => page.mouse.click(all.x + all.width / 2, all.y + all.height / 2);
  await tap();
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
  await page.clock.fastForward(1_000);
  await expect(page.locator('#overview')).toBeHidden();

  // ... and the next one works as usual
  await tap();
  await expect(page.locator('#overview')).toBeVisible();
});

test('a notification brightens a dimmed panel', async ({ page }) => {
  await show(page, { idleDimMin: 1 });
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);
  await page.evaluate(() => fetch('/api/notify', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: 'Build', text: 'done' }) }));
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
});

test('a page switch waiting behind the open overview does not keep the panel from dimming', async ({ page }) => {
  await show(page, { idleDimMin: 1, switchCountdownSec: 0 });
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#overview')).toBeVisible();
  await page.evaluate(() => fetch('/api/notify', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: 'Build', text: 'done', page: 'two' }) }));
  await expect(page.locator('#navChip')).toHaveClass(/on/); // waits until the overview is closed
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);
});

test('idle black mode blanks the panel and pauses the ocean until the wake tap', async ({ page }) => {
  await show(page, { idleDimMin: 1, idleBlack: true, background: 'sharks' });
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);
  await expect(page.locator('body')).toHaveClass(/idle-black/);
  await expect(page.locator('#ocean')).toHaveCSS('display', 'none');
  await page.waitForTimeout(1600);
  expect(await page.evaluate(() => getComputedStyle(document.body, '::after').opacity)).toBe('1');
  const all = await page.locator('#dots .page-all').boundingBox();
  await page.mouse.click(all.x + all.width / 2, all.y + all.height / 2);
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
  await expect(page.locator('#overview')).toBeHidden();
});

test('the ocean saver overrides render cost and restores the saved custom profile', async ({ page }) => {
  await show(page, {});
  const profiles = await page.evaluate(async () => {
    const { oceanRenderSettings } = await import('/panel/main.js');
    return [
      oceanRenderSettings({ oceanPowerSaver: true, oceanFps: 60, oceanDetail: 'high', oceanFish: 400 }),
      oceanRenderSettings({ oceanPowerSaver: true, oceanFps: 60, oceanDetail: 'high', oceanFish: 400 }, false),
      oceanRenderSettings({ oceanPowerSaver: false, oceanFps: 45, oceanDetail: 'balanced', oceanFish: 210 }),
    ];
  });
  expect(profiles).toEqual([
    { fps: 24, detail: 'battery', fish: 80 },
    { fps: 60, detail: 'high', fish: 400 },
    { fps: 45, detail: 'balanced', fish: 210 },
  ]);
});

test('power settings have translated labels in every supported language', async ({ page }) => {
  await page.goto('/panel/');
  const missing = await page.evaluate(() => {
    const { LANGUAGES, STRINGS } = window.PhoneBoxI18n;
    const keys = ['s.display.oceanPowerSaver', 's.oled.idleBlack'];
    return LANGUAGES.flatMap(({ code }) => keys
      .filter((key) => !STRINGS[code]?.[key]?.trim())
      .map((key) => `${code}:${key}`));
  });
  expect(missing).toEqual([]);
});
