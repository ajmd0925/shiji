// Read-only verification of this desktop's scheduler. All changes use the host tool.
import { DatabaseSync } from "node:sqlite";
import { homedir } from "node:os";
import path from "node:path";

export function nativeSnapshot({ automationId, reminderId, database } = {}) {
  const file =
    database ||
    path.join(
      process.env.CODEX_HOME || path.join(homedir(), ".codex"),
      "sqlite",
      "codex.db",
    );
  let db;
  try {
    db = new DatabaseSync(file, { readOnly: true });
    const columns =
      "id,name,status,rrule,next_run_at,last_run_at,kind,target_thread_id";
    const rows =
      automationId && reminderId
        ? db
            .prepare(
              `SELECT ${columns} FROM automations WHERE id=? AND instr(prompt,?)>0`,
            )
            .all(automationId, reminderId)
        : automationId
          ? db
              .prepare(`SELECT ${columns} FROM automations WHERE id=?`)
              .all(automationId)
          : reminderId
            ? db
                .prepare(
                  `SELECT ${columns} FROM automations WHERE kind='heartbeat' AND instr(prompt,?)>0`,
                )
                .all(reminderId)
            : [];
    return {
      available: true,
      automations: rows.map(({ next_run_at, last_run_at, ...r }) => ({
        ...r,
        nextRunAt:
          next_run_at == null ? null : new Date(next_run_at).toISOString(),
        lastRunAt:
          last_run_at == null ? null : new Date(last_run_at).toISOString(),
      })),
    };
  } catch {
    return {
      available: false,
      automations: [],
      error: "无法只读核验本机 Codex 调度记录；不要宣称已核对时间或已取消",
    };
  } finally {
    db?.close();
  }
}

export function scheduleFor(
  iso,
  timezone = Intl.DateTimeFormat().resolvedOptions().timeZone,
) {
  const date = new Date(iso);
  if (!Number.isFinite(+date) || +date <= Date.now())
    throw new Error("提醒时间已过，请重新设置");
  const fields = Object.fromEntries(
    new Intl.DateTimeFormat("en-GB", {
      timeZone: timezone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hourCycle: "h23",
    })
      .formatToParts(date)
      .filter((p) => p.type !== "literal")
      .map((p) => [p.type, p.value]),
  );
  // This desktop rejects UTC UNTIL for local rules. COUNT alone is insufficient:
  // dispatch is single-use and the sync heartbeat pauses completed native records.
  return {
    rrule: `RRULE:FREQ=DAILY;BYHOUR=${+fields.hour};BYMINUTE=${+fields.minute};BYSECOND=${+fields.second};COUNT=1`,
    expectedAt: date.toISOString(),
    timezone,
    instruction:
      "创建后必须通过 native-check 比对实际 nextRunAt 与 expectedAt；不一致则先暂停，不能报告已安排。",
  };
}
