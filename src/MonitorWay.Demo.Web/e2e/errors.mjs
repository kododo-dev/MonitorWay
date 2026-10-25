// Browser scenarios of the errors, against the running demo (its shop throws now and then, in spans and logs):
//   node errors.mjs http://localhost:5080 [chrome|msedge|firefox|webkit] [screenshot directory]
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

const errorRows = (page) => page.getByTestId('error-row');
const counts = async (page) => (await errorRows(page).allInnerTexts()).map((t) => Number(t.split('\n').find((l) => /^[\d,\s]+$/.test(l.trim()))?.replace(/\D/g, '') ?? NaN));

{
  const { context, page, errors } = await open();
  await page.goto(`${base}/monitor/#/logs`);
  const menu = page.getByTestId('way-menu').getByText('Errors');
  record('errors in the shell menu', await visible(menu));
  await menu.click();
  record('opens the errors', await visible(page.getByRole('heading', { name: 'Errors' })) && page.url().endsWith('#/errors'), page.url());
  record('groups of exceptions', await visible(errorRows(page), 60000), `${await errorRows(page).count()} groups`);
  record('trend of the last 24 hours', await visible(errorRows(page).first().locator('svg rect')));
  if (shots) await page.screenshot({ path: `${shots}/09-errors.png` });

  await page.getByRole('button', { name: 'Most frequent' }).click();
  await page.waitForTimeout(800);
  const sorted = await counts(page);
  record('most frequent first', sorted.length > 0 && sorted.every((c, i) => i === 0 || c <= sorted[i - 1]), sorted.join(','));
  record('sort in the address', page.url().includes('sort=count'), page.url());

  const search = page.getByPlaceholder('Search types, messages, stack traces…');
  await search.fill('type:Timeout');
  await page.waitForTimeout(800);
  const types = await errorRows(page).allInnerTexts();
  record('search by type', await visible(errorRows(page), 60000) && types.every((t) => t.startsWith('TimeoutException')), types.map((t) => t.split('\n')[0]).join(','));

  // The group: stack, frames, occurrences reported by both the log and the span.
  await errorRows(page).first().click();
  const detail = page.getByTestId('error-detail');
  record('group: stack trace', await visible(detail.getByText('Stack trace (latest occurrence)')));
  record('group: recognized by its frames', await visible(detail.getByText(/Web\.Shop\.ChargeAsync/)));
  record('group: address of the group', /#\/errors\/[0-9a-f]{16}$/.test(page.url()), page.url());
  const fingerprint = page.url().split('/').pop();
  record('group: occurrences', await visible(page.getByTestId('occurrence-row')));
  // The newest one may have only its log yet: the span comes when its trace is complete, seconds later.
  const occurrence = page.getByTestId('occurrence-row')
    .filter({ has: page.getByText('log', { exact: true }) })
    .filter({ has: page.getByText('span', { exact: true }) }).first();
  record('group: log and span counted once', await visible(occurrence, 20000));
  if (shots) await page.screenshot({ path: `${shots}/10-error.png` });

  // To the trace of an occurrence, and back from the exception on its span.
  await occurrence.getByRole('button', { name: 'Open trace' }).click();
  record('occurrence: its trace', await visible(page.getByTestId('span-row')) && page.url().includes('#/traces/'), page.url());
  await page.getByTestId('span-row').filter({ hasText: 'POST payments' }).first().click();
  await page.getByTestId('span-details').getByRole('button', { name: 'Open error' }).click();
  record('span exception: back to the group', await visible(detail) && page.url().endsWith(`/errors/${fingerprint}`), page.url());

  // From a log entry with the exception.
  await page.goto(`${base}/monitor/#/logs?q=${encodeURIComponent('has:exception exception:Timeout')}`);
  await visible(page.getByTestId('log-row'), 30000);
  await page.getByTestId('log-row').last().click();
  await page.getByRole('button', { name: 'Open error' }).click();
  record('log entry: open error', await visible(detail) && page.url().endsWith(`/errors/${fingerprint}`), page.url());

  await page.goto(`${base}/monitor/#/errors?q=color%3Ared`);
  record('invalid query: reported', await visible(page.getByText(/does not apply to errors/)));
  await page.goto(`${base}/monitor/#/errors/0000000000000000`);
  record('unknown group: reported', await visible(page.getByText(/not in memory/)));
  record('no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page, errors } = await open({ colorScheme: 'dark', viewport: { width: 390, height: 780 } });
  await page.goto(`${base}/monitor/#/errors`);
  record('phone: errors', await visible(errorRows(page)));
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  record('phone: no horizontal scroll', overflow <= 0, `${overflow}px`);
  await errorRows(page).first().click();
  record('phone: group', await visible(page.getByTestId('error-detail')));
  if (shots) await page.screenshot({ path: `${shots}/11-error-phone-dark.png` });
  record('phone: no errors in the console', errors.length === 0, errors.join(' | '));
  await context.close();
}

{
  const { context, page } = await open({ locale: 'pl-PL' });
  await page.goto(`${base}/monitor/#/errors`);
  record('pl: heading', await visible(page.getByRole('heading', { name: 'Błędy' })));
  record('pl: sort', await visible(page.getByRole('button', { name: 'Najczęstsze' })));
  await context.close();
}

await browser.close();
const failed = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? `  (${r.detail})` : ''}`);
console.log(`${results.length - failed.length}/${results.length} passed (${which})`);
process.exit(failed.length ? 1 : 0);
