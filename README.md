# 拾记 · GPT-Pet

拾记 GPT-Pet（gpt-pet）是一款个人任务、备忘录和一次性提醒助手，带可拖动、会眨眼和挥手的 Windows 龙娘桌宠。Vue 3 + Element Plus、Spring Boot + REST、MySQL、Maven，附 Codex MCP 插件与技能，使用 MIT 许可证。与作品集项目分开维护。

搜索关键词：`gpt`、`pet`、`gpt-pet`、`desktop-pet`、`拾记`。桌宠与提醒本地运行，不需要 GPT API。

## 提醒如何工作

在页面或 Codex 聊天添加提醒，后端检查通知通道并保存计划，返回具体年月日、时分秒和时区。后台每 500 毫秒检查到期记录，通过 Windows 自带通知接口发送 **拾记系统通知**。取消直接写入数据库并阻止后续发送，不使用模型定期查看聊天，不需要逐条批准。

这是本机 Windows 通知，不是 ChatGPT 云端 Tasks 或 Codex 原生自动化，也不会推送到手机或跨账号同步。现有页面的样式和布局保留。

- 任务支持编辑、完成/恢复、优先级、截止时间、搜索与删除；截止时间不会自动创建提醒。
- 备忘录支持颜色、置顶、正文、搜索和编辑。
- 提醒只执行一次；发送前与取消使用同一数据库锁。取消先完成则不发送，发送已开始则可能留下此前通知，确认文案会区分。
- `FIRED` 只证明 Windows 接受请求，不能证明用户看到；用户确认后才是 `RECEIVED`。
- 服务中断或结果未知不盲目重发。恢复时只补发五分钟内的到期提醒，更早的记录显示待处理。

## 获取源码

```powershell
git clone https://github.com/ajmd0925/shiji-gpt-pet.git
cd shiji-gpt-pet
```

## 安装与启动

普通使用者可从仓库 [Releases 下载页](https://github.com/ajmd0925/shiji-gpt-pet/releases) 下载 `shiji-版本-windows-x64.zip`，完整解压后双击 `Start.cmd`。便携版自带 Java 和编译后的页面，首次自动连接、注册本机通知并显示桌宠，右键桌宠打开白紫色四项小菜单，可选择定时提醒、备忘录、任务表或完整网页，无需安装开发工具或 MySQL。桌宠默认 144 个逻辑像素，可选 72/96/120/144/160 五档，支持拖动和收起到托盘，待机自动眨眼，点击挥手，托盘“轻动作”可关闭动画；`OpenBrowser.cmd` 打开保留原样的完整网页。`Check.cmd` 检查环境，`Stop.cmd` 正常停止。默认使用 H2 本地文件数据库，MySQL 为可选配置；两种模式的数据独立。详情见 [下载版使用说明](docs/distribution.md)。源码 ZIP 不能直接双击运行。

以下为源码开发方式：

需要 Windows 10/11、Windows PowerShell 5.1（系统自带）、PowerShell 7（运行项目脚本）、.NET Framework 4.8（桌宠）、Java 21、Maven 3.9+、Node.js 22.12+，推荐 Node 24。其他系统可以开发前后端，但当前通知适配器仅支持 Windows，不能宣称通知已安排。

```powershell
cd frontend
npm ci
cd ../plugin
npm ci
cd ..
```

首次为当前用户注册拾记通知：

```powershell
./scripts/register-notifications.ps1
```

该脚本只创建拾记自己的开始菜单快捷方式和 HKCU AppUserModelId，发送一条安装确认通知。不修改系统通知权限，不安装第三方通知模块。请在 Windows「设置 → 系统 → 通知」允许拾记横幅，检查请勿打扰及自动规则。通知进入通知中心与弹出横幅是不同结果。

使用官方 MySQL Windows ZIP 启动独立数据库：

```powershell
./scripts/start-mysql.ps1 -Archive 'D:/path/mysql-8.4.13-winx64.zip'
./scripts/start-backend.ps1 -Profile mysql
```

脚本可传 `-JavaHome` 和 `-Maven` 指定工具路径；不要求采用开发者本机目录。数据库监听 `127.0.0.1:3307`，随机账号、数据和配置在被 Git 忽略的 `.local/`。中文路径的便携 MySQL 依赖 NTFS 短路径；不支持时请使用 ASCII 项目路径并重新生成配置。

启动前端：

```powershell
./scripts/start-frontend.ps1
```

打开 `http://127.0.0.1:5173`，用 `.local/access-token.txt` 连接个人空间。密钥只保存在浏览器当前 sessionStorage。后端默认 `http://127.0.0.1:8080`，除健康检查外均需 Bearer Token。

也可运行隐藏的后台进程（先启动一次后端生成连接配置）：

```powershell
./scripts/restart-backend.ps1 -Profile mysql
```

它只重启本项目的 Java 进程，打包并启动隐藏的后台和通知桥接进程，不安装 Windows 服务或开机任务。后端在**当前已登录用户会话**运行；不能改用没有桌面的 Session 0 服务。数据库与后端需要持续运行，电脑需要唤醒。关闭网页和 Codex 聊天不影响已保存计划；关闭后端、休眠或关机期间不会实时通知。后端日志位于 `.local/backend.log`，状态接口为 `/api/notifications/status`。

开发预览可使用 `start-backend.ps1 -Profile local`，H2 文件数据与 MySQL 不互通。`compose.yml` 可提供 MySQL；复制 `.env.example` 并设置密码后启动，另向后端传入 `DB_PASSWORD`、`DB_USER`、`DB_URL`。通知桥接进程应运行在 Windows 用户桌面，当前不支持在 Docker/Linux 中发送本机 Windows 通知。

## 从旧版本升级

先备份本地数据库，再更新源码并重启后端。启动时保留已有记录，为提醒补充通道字段，历史记录默认 `CODEX`；将旧 MySQL `state` 枚举升级为 VARCHAR，支持新增状态。升级是幂等的，后台必须等升级成功才启动；数据库账号需要项目现有的建表/改表权限。

旧 Codex 提醒不会自动迁移或补发。旧的“拾记自动同步”heartbeat 已暂停，保持暂停。新提醒使用 `SYSTEM`；旧记录仍通过宿主工具手动管理。`plugin/native.mjs` 仅是旧版只读兼容实现，新流程不依赖 Codex 私有数据库或非公开接口。

## Codex 插件

```powershell
cd plugin
node scripts/configure.mjs
cd ..
codex plugin marketplace add ./.local/marketplace
codex plugin add shiji@shiji-local
```

本地配置生成器写入本机 Node/项目绝对路径，不包含密钥。移动项目后重新配置。安装或更新后重新加载插件工具；旧会话可能仍显示旧 schema，请在新会话使用更新后的技能。

示例：

> 使用拾记，三分钟后提醒我喝水，仅一次。

> 使用拾记，取消喝水提醒。

> 使用拾记，记一条备忘录：周末整理读书笔记。

SYSTEM 提醒返回 `SCHEDULED` 和 `confirmation` 后即已安排，不再创建额外 Codex 自动化。取消返回 `CANCELLED` 和实际确认文案。`notification_status` 可检查通知通道；CLI 备用入口为 `node plugin/cli.mjs notification-status`。

## 验证

```powershell
./scripts/test-backend.ps1 -Database mysql
./scripts/test-backend.ps1 -Database local
cd plugin
npm test
node test/integration.mjs
cd ../frontend
npm run build
npm run test:e2e
cd ..
```

MySQL 测试只使用 `personal_assistant_test` 并重建该库中的测试表，不能指向业务库。后端测试使用模拟通知，不实际弹出。浏览器测试需要前后端已启动，默认用本机 Edge。

可选真实通知测试（会发送一条通知）：

```powershell
node scripts/test-system-notifications.mjs
```

它安排 15 秒后提醒、立即取消另一条，核验发送状态、延迟和 Windows 通知历史，清理自身测试记录；操作系统接受不等于用户看到横幅。实测与限制见 [测试报告](docs/test-report.md)。

便携版构建与安装验证：

```powershell
./scripts/package-windows.ps1 -DownloadRuntime
./scripts/test-portable.ps1 -Archive './release/shiji-0.3.0-windows-x64.zip' -BrowserChecks -PetChecks
```

构建需要设置 Java 21 的 `JAVA_HOME` 并让 Maven、Node 可用。显式下载并校验官方运行时；输出 ZIP 与 SHA256。GitHub Actions 在 Windows runner 上构建和验证，`v*` 标签验证通过后发布 Release。隔离安装测试与真实全新电脑的人工通知验收分别记录，不将 runner 测试视为全部兼容性验证。

## 开源与数据

源码为 `backend/`、`frontend/`、`plugin/`、`scripts/` 和 `docs/`。`.local/`、`.env`、生成的 `plugin/.mcp.json`、依赖、数据库、日志及截图均被忽略，不上传 GitHub。仓库不携带用户数据、访问令牌、账号凭据或 MySQL ZIP。

当前面向单人本机使用，后端只绑定本机。尚未加入多人账号、互联网部署及完整迁移版本框架；公开部署需另行设计鉴权、HTTPS 和正式数据库迁移。源码仓库：[拾记 GitHub 仓库](https://github.com/ajmd0925/shiji-gpt-pet)。
