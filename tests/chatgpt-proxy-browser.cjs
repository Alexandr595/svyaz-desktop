const { chromium } = require('../artifacts/qa-runner/node_modules/playwright-core');
const fs = require('node:fs/promises');
(async () => {
  const routes = JSON.parse(await fs.readFile('artifacts/proxy-browser-trials.json', 'utf8'));
  const results = [];
  for (const route of routes) {
    const browser = await chromium.launch({ channel: 'msedge', headless: true, proxy: { server: route.server }, args: ['--disable-quic'] });
    try {
      const page = await browser.newPage();
      let status = null, error = null;
      try { status = (await page.goto('https://chatgpt.com/', { waitUntil: 'domcontentloaded', timeout: 18000 }))?.status(); }
      catch (e) { error = e.message.split('\n')[0]; }
      await page.waitForTimeout(5000);
      const body = await page.locator('body').innerText({ timeout: 3000 }).catch(() => '');
      const title = await page.title();
      const result = { upstream: route.upstream, status, error, title, bodyLength: body.length,
        refused: /unable to load site|access denied|sorry, you have been blocked/i.test(body),
        humanCheck: /verify.*human|checking your browser|security verification|провер.*человек/i.test(body),
        chatVisible: /what can i help|ask anything|чем.*помочь|спросите|log in|sign up/i.test(body) };
      const label = route.upstream.replace(/[^a-z0-9]/gi, '-');
      await fs.writeFile('artifacts/chatgpt-' + label + '.html', await page.content());
      await page.screenshot({ path: 'artifacts/chatgpt-' + label + '.png', timeout: 3000 }).catch(() => {});
      results.push(result); console.log(JSON.stringify(result));
      await fs.writeFile('artifacts/chatgpt-browser-proxies.json', JSON.stringify(results, null, 2));
    } finally { await browser.close(); }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
