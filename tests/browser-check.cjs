const { chromium } = require('../artifacts/qa-runner/node_modules/playwright-core');
const fs = require('node:fs/promises');
const path = require('node:path');

(async () => {
  const root = path.resolve(__dirname, '..');
  const { Proxy: server } = JSON.parse(await fs.readFile(path.join(root, 'artifacts/active-proxy.json'), 'utf8'));
  const browser = await chromium.launch({ channel: 'msedge', headless: true, proxy: { server }, args: ['--disable-quic'] });
  const report = [];
  try {
    for (const [name, url] of [
      ['chatgpt', 'https://chatgpt.com/'],
      ['telegram', 'https://web.telegram.org/a/'],
      ['discord', 'https://discord.com/app'],
      ['youtube', 'https://www.youtube.com/watch?v=jNQXAC9IVRw&autoplay=1&mute=1']
    ]) {
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      const page = await context.newPage();
      const failed = [];
      page.on('requestfailed', request => failed.push({ host: new URL(request.url()).host, reason: request.failure()?.errorText }));
      const responses = [];
      page.on('response', response => responses.push({ host: new URL(response.url()).host, status: response.status() }));
      const item = { name, url, status: null, navigationError: null };
      try { item.status = (await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 20000 }))?.status(); }
      catch (error) { item.navigationError = error.message.split('\n')[0]; }
      try { await page.waitForTimeout(6000); } catch {}
      item.title = await page.title().catch(() => '');
      const body = await page.locator('body').innerText({ timeout: 3000 }).catch(() => '');
      item.bodyLength = body.length;
      item.browserError = /ERR_PROXY_CONNECTION_FAILED|ERR_TUNNEL_CONNECTION_FAILED|ERR_CONNECTION_TIMED_OUT|ERR_CONNECTION_CLOSED/.test(body);
      item.accessCheck = /verify you are human|checking your browser|security verification|провер.*человек|проверка безопасности/i.test(body);
      item.loginVisible = /log in|login|sign in|войти|вход|phone number|номер телефона/i.test(body);
      item.responseHosts = [...new Set(responses.filter(r => r.status < 400).map(r => r.host))];
      item.failed = failed.slice(0, 20);
      if (name === 'youtube') {
        item.video = await page.locator('video').first().evaluate(async video => {
          video.muted = true;
          let error = null;
          try { await video.play(); } catch (e) { error = e.name; }
          return { readyState: video.readyState, currentTime: video.currentTime, paused: video.paused, error };
        }, { timeout: 3000 }).catch(error => ({ error: error.message.split('\n')[0] }));
        await page.waitForTimeout(3000);
        item.videoAfter = await page.locator('video').first().evaluate(video => ({ readyState: video.readyState, currentTime: video.currentTime, paused: video.paused })).catch(() => null);
      }
      await fs.writeFile(path.join(root, `artifacts/rendered-${name}.html`), await page.content().catch(() => ''), 'utf8');
      await page.screenshot({ path: path.join(root, `artifacts/rendered-${name}.png`), timeout: 3000 }).catch(() => {});
      report.push(item);
      await fs.writeFile(path.join(root, 'artifacts/browser-check.json'), JSON.stringify(report, null, 2));
      process.stdout.write(JSON.stringify(item) + '\n');
      await context.close();
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
