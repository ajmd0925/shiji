import test from "node:test";
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { nativeSnapshot, scheduleFor } from "../native.mjs";

test("计划按客户端时区转换跨日时间，并返回必须核验的日期", () => {
  const r = scheduleFor("2030-01-01T16:05:06Z", "Asia/Shanghai");
  assert.match(r.rrule, /BYHOUR=0;BYMINUTE=5;BYSECOND=6;COUNT=1$/);
  assert.equal(r.expectedAt, "2030-01-01T16:05:06.000Z");
  assert.throws(() => scheduleFor("2000-01-01T00:00:00Z"), /已过/);
});
test("原生核验只读且要求业务 UUID 匹配，不能将复用 ID 绑定到错误提醒", () => {
  const folder = mkdtempSync(path.join(tmpdir(), "shiji-native-"));
  const database = path.join(folder, "fixture.db");
  try {
    const db = new DatabaseSync(database);
    db.exec(
      "CREATE TABLE automations(id TEXT,name TEXT,status TEXT,rrule TEXT,next_run_at INTEGER,last_run_at INTEGER,kind TEXT,target_thread_id TEXT,prompt TEXT)",
    );
    db.prepare("INSERT INTO automations VALUES(?,?,?,?,?,?,?,?,?)").run(
      "native",
      "fixture",
      "ACTIVE",
      "DAILY",
      1893456000000,
      null,
      "heartbeat",
      "thread",
      "uuid-expected",
    );
    db.close();
    const r = nativeSnapshot({
      automationId: "native",
      reminderId: "uuid-expected",
      database,
    });
    assert.equal(r.available, true);
    assert.equal(r.automations[0].nextRunAt, "2030-01-01T00:00:00.000Z");
    assert.deepEqual(
      nativeSnapshot({ automationId: "native", reminderId: "wrong", database })
        .automations,
      [],
    );
    assert.equal(
      nativeSnapshot({ database: path.join(folder, "missing.db") }).available,
      false,
    );
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});
