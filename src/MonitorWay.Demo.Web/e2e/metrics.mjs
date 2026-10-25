// Browser scenarios of the overview and the metrics, against the running demo (its shop is called over HTTP, so ASP.NET
// Core reports its requests):
//   node metrics.mjs http://localhost:5080 [chrome|msedge|firefox|webkit] [screenshot directory]
// Needs the playwright package (npm i --no-save playwright); firefox and webkit need `npx playwright install`.
// The metrics are collected every 10 seconds: start it at least half a minute after the demo.
import { chromium, firefox, webkit } from 'playwright';

const base = process.argv[2].replace(/\/$/, '');
const which = process.argv[3] ?? 'chrome';
const shots = process.argv[4];
const browser = which === 'firefox' ? await firefox.launch() : which === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: which });
const results = [];
const record = (name, ok, detail = '') => results.push({ name, ok, detail });
const visible = (l, timeout = 10000) => l.first().waitFor({ timeout }).then(() => true, () => false);

async function open(options = {}) {
  const context = await browser.newContext({ locale: 'en-US', colorScheme: 'light', viewport: { width: 1280, height: 900 }, ...options });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) errors.push(m.text()); });
  page.on('response', (r) => { if (r.status() >= 400 && !r.url().endsWith('/favicon.ico')) errors.push(`${r.status()} ${r.url()}`); });
  return { context, page, errors };
}

/** Drags across the middle of a chart, as a person selecting a moment. */
async function drag(page, chart) {
  const box = await chart.locator('.u-over').boundingBox();
  await page.mouse.move(box.x + box.width * 0.3, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.6, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
}

{
  const { context, page, errors } = await open();
  await page.goto(`${base}/monitor/`);
  record('opens on the overview', await visible(page.getByRole('heading', { name: 'Overview' })) && page.url().endsWith('#/overview'), page.url());
  record('overview first in the menu', (await page.getByTestId('way-menu').getByRole('button').first().innerText()).includes('Overview'));
  record('five tiles', await visible(page.getByTestId('tile')) && (await page.getByTestId('tile').count()) === 5);
  const requests = page.getByTestId('tile').first();
  const tile = (await requests.innerText()).replace(/\s+/g, ' ');
  record('requests counted', /^requests [1-9][\d,]* [\d.]+ per second$/i.test(tile), tile);
  record('charts drawn', await visible(page.getByTestId('requests-chart').locator('canvas')) && await visible(page.getByTestId('latency-chart').locator('canvas')));
  const route = page.getByTestId('route-row').filter({ hasText: '/shop/orders' });
  record('routes with the shop', await visible(route));
  record('recent errors', await visible(page.getByTestId('overview-error'), 60000));
  if (shots) await page.screenshot({ path: `${shots}/12-overview.png`, fullPage: true });

  await page.getByRole('button', { name: '6h' }).click();
  record('range in the address', page.url().includes('t=6h'), page.url());

  // A route leads to its traces in the range.
  await route.first().click();
  const search = page.getByPlaceholder('Search spans, services, attributes…');
  record('route: its traces', await visible(page.getByTestId('trace-row'), 15000) && (await search.inputValue()) === 'service:shop http.route:/shop/orders', await search.inputValue());
  const names = await page.getByTestId('trace-row').allInnerTexts();
  record('route: only that route', names.length > 0 && names.every((n) => n.includes('POST /shop/orders')), `${names.length} traces`);

  // A moment dragged on a chart leads to the traces of that moment.
  await page.goto(`${base}/monitor/#/overview?t=15m`);
  const chart = page.getByTestId('requests-chart');
  await visible(chart.locator('canvas'));
  await drag(page, chart);
  record('drag: traces of the moment', await visible(page.getByRole('heading', { name: 'Traces' })) && page.url().includes('from=') && page.url().includes('to='), page.url());
  record('drag: the range is shown', await visible(page.locator('.MuiChip-root').filter({ hasText: ' to ' })));

  await page.goto(`${base}/monitor/#/overview`);
  await page.getByTestId('overview-error').first().click();
  record('recent error: its group', await visible(page.getByTestId('error-detail')));
  record('no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page, errors } = await open();
  await page.goto(`${base}/monitor/#/overview`);
  await page.getByTestId('way-menu').getByText('Metrics').click();
  const items = page.getByTestId('metric-item');
  record('metrics: instruments', await visible(items) && (await items.count()) > 10, `${await items.count()} instruments`);
  await page.getByPlaceholder('Find an instrument…').fill('request.duration');
  const duration = items.filter({ hasText: 'http.server.request.duration' });
  record('metrics: search', (await items.count()) === 1 && await visible(duration), `${await items.count()}`);
  await duration.click();
  const chart = page.getByTestId('metric-chart');
  record('histogram: percentiles by default', await visible(chart.locator('canvas')) && await visible(page.getByTestId('metric-group')) &&
    (await page.getByTestId('metric-group').allInnerTexts()).some((t) => t.includes('p95')));
  record('histogram: address of the instrument', decodeURIComponent(page.url()).includes('m=Microsoft.AspNetCore.Hosting/http.server.request.duration'), page.url());

  await page.getByTestId('group-select').click();
  await page.getByRole('option', { name: /^http\.route/ }).click();
  await page.waitForTimeout(800);
  const groups = await page.getByTestId('metric-group').allInnerTexts();
  record('group by route', groups.some((g) => g.includes('/shop/orders')) && decodeURIComponent(page.url()).includes('g=http.route'), groups.join(' | ').replace(/\t/g, ' '));
  if (shots) await page.screenshot({ path: `${shots}/13-metrics.png` });

  await page.getByPlaceholder('Find an instrument…').fill('shop.orders');
  await items.filter({ hasText: 'shop.orders' }).first().click();
  await page.getByTestId('group-select').click();
  await page.getByRole('option', { name: /^result/ }).click();
  await page.waitForTimeout(800);
  const results = await page.getByTestId('metric-group').allInnerTexts();
  record('counter by result', results.some((r) => r.includes('placed')), results.join(' | ').replace(/\t/g, ' '));
  record('metrics: no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page, errors } = await open({ colorScheme: 'dark', viewport: { width: 390, height: 800 } });
  await page.goto(`${base}/monitor/#/overview`);
  record('phone: overview', await visible(page.getByTestId('requests-chart').locator('canvas')));
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  record('phone: no horizontal scroll', overflow <= 0, `${overflow}px`);
  if (shots) await page.screenshot({ path: `${shots}/14-overview-phone-dark.png`, fullPage: true });
  await page.goto(`${base}/monitor/#/metrics`);
  await page.getByTestId('metric-item').filter({ hasText: 'shop.orders' }).first().click();
  record('phone: metric chart', await visible(page.getByTestId('metric-chart').locator('canvas')));
  record('phone: no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page } = await open({ locale: 'pl-PL' });
  await page.goto(`${base}/monitor/`);
  record('pl: overview', await visible(page.getByRole('heading', { name: 'Przegląd' })));
  record('pl: tiles', await visible(page.getByText('Błędy serwera')));
  await context.close();
}

await browser.close();
const failed = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? `  (${r.detail})` : ''}`);
console.log(`${results.length - failed.length}/${results.length} passed (${which})`);
process.exit(failed.length ? 1 : 0);
