import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { request } from '../plugin/client.mjs';

// An explicit live test: emits one toast, cancels the other, then cleans only its own fixtures.
// Run in the logged-in desktop user's session after backend startup and notification registration.
const tasks = [], rows = [];
let succeeded = false;
try {
  assert.equal((await request('/notifications/status')).ready, true);
  for (const name of ['拾记后台定时测试', '拾记已取消测试（不应出现）']) {
    tasks.push(await request('/tasks', 'POST', { title: name, description: '可删除的系统通知验证记录', priority: 'NORMAL', completed: false }));
  }
  const due = new Date(Date.now() + 15000).toISOString();
  for (const task of tasks) {
    rows.push(await request('/reminders', 'POST', {
      taskId: task.id, message: task.title === '拾记后台定时测试' ? '拾记后台已到点，请确认电脑是否弹出此提醒。' : '该提醒已取消，不应发送。',
      scheduledAt: due, timezone: Intl.DateTimeFormat().resolvedOptions().timeZone, requestKey: crypto.randomUUID(),
    }));
  }
  const cancelled = await request(`/reminders/${rows[1].id}/cancel`, 'POST', {});
  assert.equal(cancelled.state, 'CANCELLED');
  console.log(JSON.stringify({ planned: due, confirmation: rows[0].confirmation, cancelled: cancelled.confirmation }));
  let fired;
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    const all = await request('/reminders');
    fired = all.find(r => r.id === rows[0].id);
    if (fired?.state === 'FIRED' || fired?.state === 'DELIVERY_FAILED') break;
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  assert.equal(fired?.state, 'FIRED', fired?.detail);
  assert.equal(fired.receivedAt, null);
  assert.equal((await request('/reminders')).find(r => r.id === rows[1].id).state, 'CANCELLED');

  const source = await readFile(new URL('../backend/src/main/resources/windows-notifications.ps1', import.meta.url), 'utf8');
  const history = await new Promise((resolve, reject) => {
    const child = spawn(`${process.env.SystemRoot}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`,
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-EncodedCommand', Buffer.from(source, 'utf16le').toString('base64')],
      { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '';
    const timer = setTimeout(() => { child.kill(); reject(new Error('Notification history timed out')); }, 8000);
    child.stdout.setEncoding('utf8').on('data', s => out += s);
    child.on('error', e => { clearTimeout(timer); reject(e); });
    child.on('close', code => { clearTimeout(timer); try { if (code) throw new Error('History bridge failed'); resolve(JSON.parse(out.trim())); } catch (e) { reject(e); } });
    child.stdin.end(JSON.stringify({ command: 'history' }) + '\n');
  });
  assert.equal(history.ok, true, history.error);
  const tag = id => id.replaceAll('-', '').slice(0, 16);
  assert.equal(history.items.some(item => item.tag === tag(rows[1].id)), false, 'Cancelled reminder appeared in Windows history');
  const inHistory = history.items.some(item => item.tag === tag(rows[0].id));
  // A user may dismiss the live toast before this query, so absence is evidence to review, not proof of failure.
  console.log(JSON.stringify({ result: 'PASS', delayMs: Date.parse(fired.firedAt) - Date.parse(due), firedAt: fired.firedAt,
    windowsHistoryContainsSent: inHistory, cancelledNeverSent: true, userReceiptConfirmed: false }));
  succeeded = true;
} finally {
  for (const row of rows) await request(`/reminders/${row.id}/cancel`, 'POST', {}).catch(() => {});
  for (const task of tasks) await request(`/tasks/${task.id}`, 'DELETE').catch(e => console.error('Fixture cleanup:', e.message));
  if (!succeeded) console.error('Live notification test failed. Do not claim confirmed delivery.');
}
