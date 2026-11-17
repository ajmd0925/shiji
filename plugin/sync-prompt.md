历史兼容提示词：旧版 Codex 自动同步现已停用。新 SYSTEM 提醒由后台执行，不使用此流程；下文保留为旧实现参考，不构成启用授权。

这是用户已授权的拾记页面自动同步，约每分钟执行一次，只处理拾记自己的原生提醒。同步 automationId 由本轮 heartbeat 记录提供。
这是持续同步服务，空队列表示本轮无工作，不代表监控结束；不要因此删除本自动化。用户明确要求停止自动同步时再暂停或删除。

优先用 reminder_sync_check(automationId) 获取队列和 runId。工具未加载时用 exec：
node plugin/cli.mjs sync-check '<本轮automationId>'
此入口只管理项目业务及只读核验原生结果；原生修改走宿主 automation_update。不要读取账号凭据或修改全局权限。

acquired=false 直接 DONT_NOTIFY。空队列调用 reminder_sync_complete 或 CLI sync-complete <runId> 后 DONT_NOTIFY。常规同步不发送聊天进度。

按顺序处理：
1. cancellations：native 是只读核验。仅操作本业务 UUID 匹配的真实记录。唯一匹配时用宿主删除实际 ID，成功才 reminder_record(CANCELLED)。已绑定 ID 经核验确认不存在也可记录 CANCELLED 并说明。无 ID 且查不到结果、多匹配或核验不可用时保留待取消并写本轮 error，不凭空生成 ID。
2. reconcile：只恢复唯一已存在的真实结果，不再创建。ACTIVE 原生计划 nextRunAt 与 scheduledAt 相差小于一秒，才用 reminder_bind(id,automationId) 补写。CLI 为 bind <UUID> <真实ID>。错误计划先暂停再 record PAUSED；未知结果保持待核对。
3. creations：用 reminder_claim 或 CLI claim <UUID> 领取。成功后用返回的 prompt、schedule.rrule、原 title，创建本聊天 heartbeat，notificationPolicy=null。收到真实 ID 后立即 reminder_bind 或 CLI bind <UUID> <ID>，核验实际时间才算成功。规则明确被拒且没有创建时可调整重试一次，但不能挪动原日期时间；未知结果不重建。核验不符先暂停实际 ID，再 record PAUSED。bind 返回 CANCEL_PENDING 则立即取消实际原生记录并回写。确定未创建才记录 FAILED。
4. completed：FIRED/RECEIVED 的原生记录如仍 ACTIVE，用宿主 update 暂停，保留名称、提示词、规则、目标聊天及通知偏好，不删除记录。业务状态保持原值。完整原配置可只读对应 automation.toml。

本轮完成后必须 sync-complete <runId> [真实错误摘要] 或 reminder_sync_complete。CLI 状态补写为 record <UUID> '<JSON>'，字段 state、automationId、detail；不要 shell 拼接用户正文。
空队列、正常取消、创建、例行暂停均 DONT_NOTIFY，结果显示在页面。需要用户介入才 NOTIFY。用户已授权这些请求，不逐条额外确认；宿主权限确实拦截时保留队列并准确说明。不要通过其他聊天发消息。
