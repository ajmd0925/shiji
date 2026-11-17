# 自动同步流程

历史兼容文档：自动同步已暂停。新 SYSTEM 提醒不使用此流程，不得仅凭本文启动 heartbeat。仅用户明确要求处理旧 CODEX 记录时参考。

使用宿主原生工具执行创建、暂停和删除；CLI/MCP 仅管理业务和只读核验原生结果，不修改 Codex SQLite/TOML。

1. reminder_sync_check(当前同步 automationId)。工具未加载时运行 `node <项目>/plugin/cli.mjs sync-check <automationId>`。acquired=false 说明已有轮次，直接静默。保存 runId。
2. 优先处理 cancellations。只操作本业务 UUID 核验匹配的原生记录；缺少 ID 时通过业务 UUID 查找。多匹配或无法核验则保留并记 error。宿主删除确认后 reminder_record(CANCELLED)。已绑定 ID 确认不存在可记录 CANCELLED 并说明。无 ID 且查不到创建结果时不声称已取消，等待创建结果核对。
3. reconcile 若唯一匹配的 ACTIVE 原生计划有效，实际时间与请求相差小于一秒，用 reminder_bind 补写。未知结果不重建。错误计划先暂停，保留真实 ID 并记录 PAUSED。
4. creations 用 reminder_claim 的 schedule、prompt 创建当前聊天 heartbeat，notificationPolicy 正常。再 bind；绑定返回 CANCEL_PENDING 则立即取消。核验失败则暂停并记录 PAUSED。明确被拒且未创建时可调整仍在未来的规则重试一次，不改变原定时间；未知结果不重建。
5. completed 的 FIRED/RECEIVED 原生记录如仍 ACTIVE，用宿主暂停，保留原配置，不删除记录或改业务状态。
6. reminder_sync_complete(runId,error?)，CLI 为 `sync-complete <runId> [error]`。失败如实记录，后续轮次重试待取消；不将失败或未知结果写为成功。

CLI 的 `record <UUID> <JSON>` 补写状态；`bind <UUID> <automationId>` 带原生时间核验；`native-check <automationId>` 只读核对状态与实际计划。

页面约 5 秒刷新。取消请求保存后阻止下一次 dispatch 领取；若取消晚于 dispatch 已领取的发送，仍可能收到。后端离线时静默，恢复后需核对错过的执行。不能声称秒级或离线送达保证。
