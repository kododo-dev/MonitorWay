// Browser scenarios of the traces, against the running demo (its shop records spans through OpenTelemetry):
//   node traces.mjs http://localhost:5080 [chrome|msedge|firefox|webkit] [screenshot directory]
// Needs the playwright package (npm i --no-save playwright); firefox and webkit need `npx playwright install`.
import { chromium, firefox, webkit } from 'playwright';

const base = process.argv[2].replace(/\/$/, '');
const which = process.argv[3] ?? 'chrome';
const shots = process.argv[4];
const browser = which === 'firefox' ? await firefox.launch() : which === 'webkit' ? await webkit.launch() : await chromium.launch({ channel: which });
const results = [];
const record = (name, ok, detail = '') => results.push({ name, ok, detail });
const visible = (l, timeout = 10000) => l.first().waitFor({ timeout }).then(() => true, () => false);

async function open(options = {}) {
  const context = await browser.newContext({ locale: 'en-US', colorScheme: 'light', viewport: { width: 1280, height: 800 }, ...options });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) errors.push(m.text()); });
  page.on('response', (r) => { if (r.status() >= 400 && !r.url().endsWith('/favicon.ico')) errors.push(`${r.status()} ${r.url()}`); });
  return { context, page, errors };
}

const traceRows = (page) => page.getByTestId('trace-row');
const spanRows = (page) => page.getByTestId('span-row');

{
  const { context, page, errors } = await open();
  await page.goto(`${base}/monitor/#/logs`);
  const menu = page.getByTestId('way-menu').getByText('Traces');
  record('traces in the shell menu', await visible(menu));
  await menu.click();
  record('opens the traces', await visible(page.getByRole('heading', { name: 'Traces' })) && page.url().endsWith('#/traces'), page.url());
  record('traces from OpenTelemetry', await visible(traceRows(page)));

  // The shop handles a request or two a second; live adds them without a reload.
  const first = await traceRows(page).first().getAttribute('data-trace-id');
  await page.waitForFunction((id) => document.querySelector('[data-testid="trace-row"]')?.getAttribute('data-trace-id') !== id, first, { timeout: 15000 }).catch(() => {});
  record('live: new traces appear', (await traceRows(page).first().getAttribute('data-trace-id')) !== first);
  if (shots) await page.screenshot({ path: `${shots}/06-traces.png` });

  // Errors only: the token goes to the search and the address.
  await page.getByRole('button', { name: 'Only traces with an error' }).click();
  const search = page.getByPlaceholder('Search spans, services, attributes…');
  record('errors only: token in the search', (await search.inputValue()) === 'status:error', await search.inputValue());
  const hasErrors = await visible(traceRows(page), 60000);
  await page.waitForTimeout(500);
  record('errors only: address remembers it', decodeURIComponent(page.url()).includes('q=status:error'), page.url());
  const errorRows = await traceRows(page).count();
  const withIcon = await page.locator('[data-testid="trace-row"]:not([data-errors="0"])').count();
  record('errors only: every row failed', hasErrors && errorRows > 0 && errorRows === withIcon, `${withIcon}/${errorRows}`);

  // A failed trace: the waterfall, the failed span with its exception, and the trace's logs.
  await traceRows(page).first().click();
  record('trace: waterfall', await visible(spanRows(page)) && (await spanRows(page).count()) >= 2, `${await spanRows(page).count()} spans`);
  record('trace: address of the trace', /#\/traces\/[0-9a-f]{32}$/.test(page.url()), page.url());
  const traceId = page.url().split('/').pop();
  record('trace: logs of the trace', await visible(page.getByTestId('trace-log-row')));
  const failed = spanRows(page).filter({ has: page.locator('text=/POST payments|StockReservations|SELECT connections/') }).last();
  await failed.click();
  const details = page.getByTestId('span-details');
  record('span: details', await visible(details.getByText('Attributes')));
  record('span: status error', await visible(details.getByText(/^Error/)));
  if (shots) await page.screenshot({ path: `${shots}/07-trace.png` });

  // To the logs of the trace and back from a log entry.
  await page.getByRole('button', { name: 'Show in logs' }).click();
  const logSearch = page.getByPlaceholder('Search messages, categories, exceptions, properties…');
  await visible(logSearch);
  record('show in logs: trace filter', (await logSearch.inputValue()) === `trace:${traceId}`, await logSearch.inputValue());
  await visible(page.getByTestId('log-row'));
  await page.getByTestId('log-row').first().click();
  const openTrace = page.getByRole('button', { name: 'Open trace' });
  record('log entry: open trace', await visible(openTrace));
  await openTrace.click();
  record('log entry: back at the trace', await visible(spanRows(page)) && page.url().endsWith(`/traces/${traceId}`), page.url());

  // A query the server cannot use says why.
  await page.getByRole('button', { name: 'All traces' }).click();
  await search.fill('duration:>soon');
  record('invalid query: reported', await visible(page.getByText(/is not a duration/)));

  await page.goto(`${base}/monitor/#/traces/${'0'.repeat(32)}`);
  record('unknown trace: reported', await visible(page.getByText(/not in memory/)));
  record('no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  // Dark theme and a phone: the list fits, the trace opens.
  const { context, page, errors } = await open({ colorScheme: 'dark', viewport: { width: 390, height: 780 } });
  await page.goto(`${base}/monitor/#/traces`);
  record('phone: traces', await visible(traceRows(page)));
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  record('phone: no horizontal scroll', overflow <= 0, `${overflow}px`);
  await traceRows(page).first().click();
  record('phone: waterfall', await visible(spanRows(page)));
  await spanRows(page).first().click();
  record('phone: span details over the waterfall', await visible(page.getByTestId('span-details')));
  if (shots) await page.screenshot({ path: `${shots}/08-trace-phone-dark.png` });
  record('phone: no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  // Polish, as the shell picks it from the browser.
  const { context, page } = await open({ locale: 'pl-PL' });
  await page.goto(`${base}/monitor/#/traces`);
  record('pl: heading', await visible(page.getByRole('heading', { name: 'Ślady' })));
  record('pl: search', await visible(page.getByPlaceholder('Szukaj w spanach, usługach, atrybutach…')));
  await context.close();
}

await browser.close();
const failed = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? `  (${r.detail})` : ''}`);
console.log(`${results.length - failed.length}/${results.length} passed (${which})`);
process.exit(failed.length ? 1 : 0);
