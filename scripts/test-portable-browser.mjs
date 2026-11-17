import { chromium } from '../frontend/node_modules/@playwright/test/index.mjs';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const install = process.argv[2];
const connection = JSON.parse(readFileSync(path.join(install, '.local/connection.json'), 'utf8'));
const browser = await chromium.launch({channel:'msedge', headless:true});
try {
  const page = await browser.newPage({locale:'zh-CN'});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${connection.url}/#shiji-token=${connection.token}`);
  await page.getByText('服务已连接', {exact:true}).waitFor();
  assert.equal(new URL(page.url()).hash, '', 'secret must leave address bar');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('assistant-token')), connection.token);
  await page.reload();
  await page.getByText('服务已连接', {exact:true}).waitFor();
  assert.deepEqual(errors, []);
  await page.screenshot({path:path.join(install,'.local/portable-browser.png'), fullPage:true});
  console.log('PASS: compiled page auto-connect, fragment removal, reload and browser errors');
} finally { await browser.close(); }
