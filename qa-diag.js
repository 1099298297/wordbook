const { chromium } = require('C:/Users/railgun/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright-core');

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true,
    args: ['--no-first-run', '--disable-gpu'],
  });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];
  page.on('pageerror', (e) => errors.push('PAGEERROR: ' + e.message + '\n' + (e.stack || '')));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('CONSOLE: ' + m.text()); });
  page.on('response', (r) => { if (r.status() >= 400) errors.push('HTTP' + r.status() + ' ' + r.url()); });

  await page.goto('http://127.0.0.1:17812/', { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  console.log('chips done/pending/error/none: ' +
    await page.locator('.chip.ai-done').count() + '/' +
    await page.locator('.chip.ai-pending').count() + '/' +
    await page.locator('.chip.ai-error').count() + '/' +
    await page.locator('.chip.ai-none').count());
  console.log('rows: ' + await page.locator('.word-row').count());
  await page.locator('.word-row').first().click();
  await page.waitForTimeout(400);
  console.log('aiStatus text: ' + (await page.locator('#aiStatus').innerText()).trim());
  console.log('inContext len: ' + (await page.inputValue('#fInContext')).length);
  console.log('explain btn disabled: ' + await page.locator('#btnExplain').isDisabled());
  console.log('sceneNav text: ' + (await page.locator('#sceneIndex').innerText()).trim());
  console.log('== errors ==');
  console.log(errors.join('\n---\n') || 'none');
  await browser.close();
})().catch((e) => { console.error('QA FAIL', e.message); process.exit(1); });
