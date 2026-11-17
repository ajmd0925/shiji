import { request } from "./client.mjs";
import {
  claimReminder,
  bindReminder,
  dispatchReminder,
  syncCheck,
} from "./reminders.mjs";
import { nativeSnapshot } from "./native.mjs";
const [command, id, argument] = process.argv.slice(2);
try {
  let result;
  if (
    ["dispatch", "reminder-fired", "claim", "bind", "record"].includes(command)
  ) {
    if (
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(
        id || "",
      )
    )
      throw new Error("Invalid reminder UUID");
    if (command === "claim") result = await claimReminder(id);
    else if (command === "bind") result = await bindReminder(id, argument);
    else if (command === "record")
      result = await request(
        `/reminders/${id}/events`,
        "POST",
        JSON.parse(argument),
      );
    else result = await dispatchReminder(id);
  } else if (command === "notification-status") result = await request("/notifications/status");
  else if (command === "native-check")
    result = nativeSnapshot({ automationId: id });
  else if (command === "sync-check") result = await syncCheck(id);
  else if (command === "sync-complete")
    result = await request("/sync/complete", "POST", {
      runId: id,
      ...(argument ? { error: argument } : {}),
    });
  else if (command === "sync-config")
    result = await request("/sync/config", "POST", {
      automationId: id,
      enabled: argument !== "disabled",
    });
  else
    throw new Error(
      "Usage: cli.mjs dispatch|claim|bind|record|native-check|sync-check|sync-complete|sync-config <id> [argument]",
    );
  console.log(JSON.stringify(result));
} catch (e) {
  console.error(e.message);
  process.exitCode = 1;
}
