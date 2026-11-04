const { chromium } = require('../artifacts/qa-runner/node_modules/playwright-core');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    for (const host of ['svyaz-proxy-probe.invalid', 'svyaz-control-probe.invalid']) {
      try { await page.goto('https://' + host, { timeout: 7000 }); }
      catch (error) { process.stdout.write(host + ': ' + error.message.split('\n')[0] + '\n'); }
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
