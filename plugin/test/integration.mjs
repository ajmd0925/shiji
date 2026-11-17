import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
const client = new Client({ name: "shiji-integration", version: "1.0.0" });
await client.connect(
  new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../server.mjs", import.meta.url))],
    env: process.env,
    stderr: "inherit",
  }),
);
async function call(name, args = {}) {
  const result = await client.callTool({ name, arguments: args });
  if (result.isError) throw new Error(result.content[0].text);
  return JSON.parse(result.content[0].text);
}
let task, note, reminder;
try {
  task = await call("task_add", { title: "MCP 集成测试（无真实通知）" });
  note = await call("note_save", {
    title: "MCP 备忘录测试",
    content: "接口往返验证",
  });
  reminder = await call("reminder_prepare", {
    taskId: task.id,
    message: "后台安排与立即取消测试，不触发到期通知",
    scheduledAt: new Date(Date.now() + 3600000).toISOString(),
    timezone: "Asia/Shanghai",
    requestKey: crypto.randomUUID(),
  });
  assert.equal(reminder.state, "SCHEDULED");
  assert.equal(reminder.channel, "SYSTEM");
  assert.match(reminder.confirmation, /添加成功/);
  assert.equal((await call("notification_status")).ready, true);
  assert.equal(
    (await call("reminders_list")).find((r) => r.id === reminder.id).receivedAt,
    null,
  );
  const cancelled = await call("reminder_cancel", { id: reminder.id });
  assert.equal(cancelled.state, "CANCELLED");
  assert.match(cancelled.confirmation, /到点不会再提醒/);
  console.log(
    "PASS: MCP → Spring Boot → database system schedule/cancel. No actual notification was emitted.",
  );
} finally {
  if (reminder) await call("reminder_cancel", { id: reminder.id }).catch(e => console.error("Cleanup reminder:", e.message));
  if (task)
    await call("task_delete", { id: task.id }).catch((e) =>
      console.error("Cleanup task:", e.message),
    );
  if (note)
    await call("note_delete", { id: note.id }).catch((e) =>
      console.error("Cleanup note:", e.message),
    );
  await client.close();
}
