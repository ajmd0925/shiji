import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";

test("MCP 握手、schema、鉴权、业务请求映射和错误回传", async () => {
  const calls = [];
  const stub = http.createServer(async (req, res) => {
    let raw = "";
    for await (const chunk of req) raw += chunk;
    calls.push({
      path: req.url,
      method: req.method,
      token: req.headers.authorization,
      body: raw ? JSON.parse(raw) : null,
    });
    res.setHeader("Content-Type", "application/json");
    if (req.url === "/api/tasks")
      return res.end(
        JSON.stringify(
          req.method === "GET" ? [] : { id: "task-test", ...JSON.parse(raw) },
        ),
      );
    if (req.url.endsWith("/claim"))
      return res.end(
        JSON.stringify({
          id: "11111111-1111-4111-8111-111111111111",
          state: "CREATING",
          scheduledAt: new Date(Date.now() + 3600000).toISOString(),
          message: "测试提醒",
        }),
      );
    res.statusCode = 409;
    res.end(JSON.stringify({ message: "测试冲突，请勿重复创建" }));
  });
  await new Promise((resolve) => stub.listen(0, "127.0.0.1", resolve));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../server.mjs", import.meta.url))],
    env: {
      ...process.env,
      ASSISTANT_API_URL: `http://127.0.0.1:${stub.address().port}`,
      ASSISTANT_API_TOKEN: "test-token-abcdefghijklmnopqrstuvwxyz",
    },
    stderr: "pipe",
  });
  const client = new Client({ name: "shiji-test", version: "1.0.0" });
  try {
    await client.connect(transport);
    const tools = await client.listTools();
    assert.ok(tools.tools.some((t) => t.name === "reminder_claim"));
    assert.ok(tools.tools.some((t) => t.name === "reminder_record"));
    assert.equal(
      tools.tools.find((t) => t.name === "tasks_list").annotations.readOnlyHint,
      true,
    );
    assert.equal(
      tools.tools.find((t) => t.name === "task_add").annotations.readOnlyHint,
      false,
    );
    const added = await client.callTool({
      name: "task_add",
      arguments: { title: "喝水" },
    });
    assert.equal(JSON.parse(added.content[0].text).title, "喝水");
    assert.equal(
      calls[0].token,
      "Bearer test-token-abcdefghijklmnopqrstuvwxyz",
    );
    assert.equal(calls[0].body.priority, "NORMAL");
    const claimed = await client.callTool({
      name: "reminder_claim",
      arguments: { id: "11111111-1111-4111-8111-111111111111" },
    });
    assert.equal(
      JSON.parse(claimed.content[0].text).nativeCreationRequired,
      true,
    );
    const failed = await client.callTool({
      name: "reminder_record",
      arguments: {
        id: "11111111-1111-4111-8111-111111111111",
        state: "SCHEDULED",
        automationId: "test",
      },
    });
    assert.equal(failed.isError, true);
    assert.match(failed.content[0].text, /冲突/);
    const before = calls.length;
    const invalid = await client.callTool({
      name: "reminder_claim",
      arguments: { id: "../../bad" },
    });
    assert.equal(invalid.isError, true);
    assert.equal(calls.length, before);
  } finally {
    await client.close();
    await new Promise((resolve) => stub.close(resolve));
  }
});
