// Browser scenarios of a second service: the demo's warehouse sends its telemetry over OTLP, and every view can be
// narrowed to one service:
//   node services.mjs http://localhost:5080 [chrome|msedge|firefox|webkit] [screenshot directory]
// Needs the playwright package (npm i --no-save playwright); firefox and webkit need `npx playwright install`.
// Start it a minute after the demo, so the warehouse has sent its first metrics and failed once or twice.
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

/** Chooses a service in the picker of the page. */
async function choose(page, service) {
  await page.getByTestId('service-picker').click();
  await page.getByRole('option', { name: service, exact: true }).click();
}

{
  const { context, page, errors } = await open();

  // Traces: the warehouse's spans are in the shop's traces.
  await page.goto(`${base}/monitor/#/traces`);
  record('traces: service picker', await visible(page.getByTestId('service-picker')));
  await choose(page, 'warehouse');
  const search = page.getByPlaceholder('Search spans, services, attributes…');
  record('traces: picker sets the search', (await search.inputValue()) === 'service:warehouse', await search.inputValue());
  await page.waitForTimeout(800);
  const rows = await page.getByTestId('trace-row').allInnerTexts();
  record('traces: every one passes the warehouse', rows.length > 0 && rows.every((r) => r.includes('warehouse')), `${rows.length} traces`);
  await page.getByTestId('trace-row').filter({ hasText: 'POST /shop/orders' }).first().click();
  const span = page.getByTestId('span-row').filter({ hasText: 'POST /reservations' });
  record('trace: spans of both services', await visible(span) && await visible(page.getByTestId('span-row').filter({ hasText: 'POST /shop/orders' })));
  await span.first().click();
  record('trace: the warehouse span is the warehouse\'s', await visible(page.getByTestId('span-details').getByText('warehouse', { exact: true })));
  if (shots) await page.screenshot({ path: `${shots}/15-distributed-trace.png` });

  // Logs: the warehouse's entries, sent over OTLP.
  await page.goto(`${base}/monitor/#/logs`);
  await visible(page.getByTestId('log-row'));
  await choose(page, 'warehouse');
  const logSearch = page.getByPlaceholder('Search messages, categories, exceptions, properties…');
  record('logs: picker sets the search', (await logSearch.inputValue()) === 'service:warehouse', await logSearch.inputValue());
  await page.waitForTimeout(1200);
  const entries = await page.getByTestId('log-row').allInnerTexts();
  record('logs: only the warehouse', entries.length > 0 && entries.every((e) => e.includes('Warehouse.') || e.includes('PickService')), `${entries.length} entries`);

  // Errors: the warehouse's exceptions.
  await page.goto(`${base}/monitor/#/errors`);
  await choose(page, 'warehouse');
  // The search waits a moment for typing to end before it asks the server.
  await page.waitForTimeout(1500);
  const groups = page.getByTestId('error-row');
  record('errors: the warehouse\'s', await visible(groups, 90000) && (await groups.allInnerTexts()).every((g) => g.includes('warehouse')),
    (await groups.allInnerTexts()).map((g) => g.split('\n')[0]).join(','));

  // Metrics: the warehouse's own instruments, sent as running totals.
  await page.goto(`${base}/monitor/#/metrics`);
  await page.getByPlaceholder('Find an instrument…').fill('warehouse.reservations');
  await page.getByTestId('metric-item').first().click();
  await choose(page, 'warehouse');
  record('metrics: service in the address', page.url().includes('svc=warehouse'), page.url());
  record('metrics: the warehouse\'s counter', await visible(page.getByTestId('metric-chart').locator('canvas')) && await visible(page.getByTestId('metric-group')));

  // Overview: one service's routes.
  await page.goto(`${base}/monitor/#/overview`);
  await choose(page, 'shop');
  await page.waitForTimeout(1000);
  record('overview: service in the address', page.url().includes('svc=shop'), page.url());
  record('overview: the shop\'s routes', await visible(page.getByTestId('route-row').filter({ hasText: '/shop/orders' })));
  record('no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page, errors } = await open({ colorScheme: 'dark', viewport: { width: 390, height: 800 } });
  await page.goto(`${base}/monitor/#/traces?q=service%3Awarehouse`);
  record('phone: traces of a service', await visible(page.getByTestId('trace-row')));
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  record('phone: no horizontal scroll', overflow <= 0, `${overflow}px`);
  record('phone: no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page } = await open({ locale: 'pl-PL' });
  await page.goto(`${base}/monitor/#/traces`);
  await page.getByTestId('service-picker').click();
  record('pl: all services', await visible(page.getByRole('option', { name: 'Wszystkie usługi' })));
  await context.close();
}

await browser.close();
const failed = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? `  (${r.detail})` : ''}`);
console.log(`${results.length - failed.length}/${results.length} passed (${which})`);
process.exit(failed.length ? 1 : 0);
