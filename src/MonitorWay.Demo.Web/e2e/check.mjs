// Browser scenarios of the monitor in Way.Shell, against the running demo (its logs come through OpenTelemetry):
//   node check.mjs http://localhost:5080 [chrome|msedge|firefox|webkit]
// Needs the playwright package (npm i --no-save playwright); firefox and webkit need `npx playwright install`.
import { chromium, firefox, webkit } from 'playwright';

const base = process.argv[2].replace(/\/$/, '');
const which = process.argv[3] ?? 'chrome';
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

const rows = (page) => page.getByTestId('log-row');
const lastId = async (page) => Number(await rows(page).last().getAttribute('data-id'));

{
  const { context, page, errors } = await open();
  await page.goto(`${base}/monitor/#/logs`);
  record('opens in the shell on the logs', await visible(page.getByRole('heading', { name: 'Logs' })) && page.url().endsWith('#/logs'), page.url());
  record('title from the Way', (await page.title()) === 'Monitor', await page.title());
  record('logs in the shell menu', await visible(page.getByTestId('way-menu').getByText('Logs')));
  record('entries from OpenTelemetry', await visible(rows(page)));

  // The demo's shop logs a few entries a second; live shows them without a reload.
  const before = await lastId(page);
  await page.waitForTimeout(3000);
  record('live: new entries appear', (await lastId(page)) > before, `${before} -> ${await lastId(page)}`);

  // The module's theme applies right after its first render; wait for it rather than read the style at once.
  const monospace = await page.waitForFunction(() => {
    const row = document.querySelector('[data-testid="log-row"]');
    return row && /IBM Plex Mono|Fira Code|monospace/.test(getComputedStyle(row).fontFamily);
  }, null, { timeout: 5000 }).then(() => true, () => false);
  record('monospace list', monospace);

  // An entry of a request: its details, then every entry of the same request through the trace filter.
  const order = rows(page).filter({ hasText: 'placed' }).last();
  await order.click();
  record('details of an entry', await visible(page.getByText('Properties')));
  const trace = page.getByRole('button', { name: 'Same request' }).first();
  if (await visible(trace, 3000)) {
    await trace.click();
    const search = page.getByPlaceholder('Search messages, categories, exceptions, properties…');
    await page.waitForTimeout(800);
    record('same request: trace filter', /^trace:[0-9a-f]{32}$/.test(await search.inputValue()), await search.inputValue());
  } else {
    record('same request: trace filter', false, 'no trace id on the entry');
  }

  // Search, remembered in the address of the route.
  const search = page.getByPlaceholder('Search messages, categories, exceptions, properties…');
  await search.fill('placed');
  await page.waitForTimeout(1200);
  const texts = await rows(page).allTextContents();
  record('search narrows the list', texts.length > 0 && texts.every((t) => t.includes('placed')), String(texts.length));
  record('search in the address', /#\/logs\?.*q=placed/.test(page.url()), page.url());
  await page.reload();
  record('search survives a reload', (await page.getByPlaceholder('Search messages, categories, exceptions, properties…').inputValue()) === 'placed');

  // A file source of the same logs.
  await page.goto(`${base}/monitor/#/logs`);
  await rows(page).first().waitFor();
  await page.getByRole('button', { name: 'Log source' }).click();
  const file = page.getByRole('presentation').getByText(/shop-.*\.clef/).first();
  if (await visible(file, 3000)) {
    await file.click();
    await page.waitForTimeout(1000);
    record('file source', (await rows(page).count()) > 0 && /src=file-/.test(page.url()), page.url());
  } else {
    record('file source', false, 'no file in the source picker');
  }

  await page.getByRole('button', { name: 'Language' }).click();
  await page.getByRole('menuitem', { name: 'Polski' }).click();
  record('language from the shell', await visible(page.getByRole('heading', { name: 'Logi' })));

  record('no errors', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page, errors } = await open({ viewport: { width: 390, height: 800 } });
  await page.goto(`${base}/monitor/#/logs`);
  await rows(page).first().waitFor();
  const fits = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
  record('phone: fits the screen', fits);
  await page.getByRole('button', { name: 'Open menu' }).click();
  record('phone: menu with the logs', await visible(page.getByTestId('way-menu').getByText('Logs')));
  record('phone: no errors', errors.length === 0, errors.join(' | '));
  await context.close();
}

await browser.close();
const failed = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? ` (${r.detail})` : ''}`);
console.log(`\n${which} ${base}: ${results.length - failed.length}/${results.length} ok`);
process.exit(failed.length ? 1 : 0);
