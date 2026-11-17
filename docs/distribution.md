# Windows 下载与首次使用

GitHub 仓库的 Releases 中下载 `shiji-版本-windows-x64.zip`；GitHub 自动生成的 Source code ZIP 是源码，不能直接启动。

将完整 ZIP 解压到可写目录，双击 `Start.cmd`。便携版自带 Eclipse Temurin Java 21 运行时、Spring Boot 应用和编译后的 Vue 页面，无需安装开发工具、MySQL 或 Codex。首次注册通知、生成本机访问密钥，打开并自动连接页面。原页面设计不变。

页面地址默认 `http://127.0.0.1:17861`。浏览器和命令窗口关闭后后台继续工作；双击 `Stop.cmd` 正常停止；`Check.cmd` 显示环境诊断。第一次注册会发出安装通知；是否出现横幅仍取决于 Windows「通知」和「请勿打扰」设置。

不自动下载软件、不要求管理员权限、不添加 Windows 服务或开机启动。`ExecutionPolicy Bypass` 仅用于启动脚本的当前 PowerShell 进程，不修改系统策略；组织策略仍可能禁止执行。

## 数据与升级

默认使用 H2 本地文件数据库，存储在本目录 `.local/assistant.mv.db`；所有密钥、连接配置、日志也在 `.local/`。这是便携版减少安装步骤的选择；源码仍支持 MySQL，两个模式的数据独立，不自动迁移。

升级前先运行 `Stop.cmd`，备份整个旧目录。新包解压到另一目录，将旧版本完整 `.local` 复制到新目录，再用新目录的 `Start.cmd`。不要同时运行两个数据副本。保留旧备份直至确认数据完整；不要覆盖新版 `runtime/` 和 `app/`。打包文件不包含任何开发者数据或访问密钥。

便携版的数据库与此前从 `backend/` 启动的 H2 路径不同。已有开发环境的数据需先停止旧后台、备份后将旧的 `backend/.local/assistant*` 文件复制到便携版 `.local/`，不能在数据库打开时复制。

## 常见问题

- 端口占用：脚本不会结束其他应用。先 `Stop.cmd`，再执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-desktop.ps1 -Port 17862`，后续记住该端口。
- 未打开页面：看 `.local/desktop.log`、`.local/desktop-error.log`，运行 `Check.cmd`。不要公开发送日志中的个人任务内容或密钥。
- 没有通知横幅：保持 Windows 已登录且唤醒，允许拾记通知横幅，并检查请勿打扰。停止后台、关机或休眠时不会实时提醒；恢复仅补发五分钟内的到期记录。
- 首次注册失败：执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\register-notifications.ps1 -BridgePath .\scripts\windows-notifications.ps1 -AppUrl http://127.0.0.1:17861/`，检查 Windows 通知设置后重试。
- 路径含中文和空格：便携版默认 H2 支持。若 Java 在英文 Windows 中无法从中文路径加载自身 DLL，先尝试 NTFS 短路径，再将包内运行时复制到当前用户 `%LOCALAPPDATA%/Shiji/runtimes/` 缓存（不下载软件，数据仍在安装目录）。若用户目录也无法提供兼容路径，诊断会提示使用纯英文字母路径。放在个人可写文件夹，不要放入 ZIP 内、Program Files 或只读同步位置。
- 系统要求：Windows 10/11 x64，Windows PowerShell 5.1。未提供 macOS、Linux、ARM64 安装包。

## MySQL 与 Codex 可选组件

MySQL 使用者仍按源码 README 运行 `scripts/start-mysql.ps1`（该开发脚本需要 PowerShell 7 和官方 MySQL ZIP）。便携版不会隐式下载、安装或停止 MySQL。也可手动提供 `.local/mysql-connection.json`：

```json
{"user":"assistant","password":"自己的数据库密码","port":3307}
```

先建立本机 MySQL `personal_assistant` 数据库并赋予该用户此库的建表、改表和读写权限。保持 MySQL 已启动，执行 `scripts/start-desktop.ps1 -Profile mysql`；后续双击启动会记住选择。`Stop.cmd` 只停止拾记应用，MySQL 由其原启动方式管理。切回本地模式用 `-Profile local`，原 MySQL 数据保留。

页面使用不依赖 Codex。聊天管理功能仍是可选的源码插件，需要额外安装 Node.js 和 Codex，按源码 README 安装插件。在插件配置中将 `ASSISTANT_CONFIG` 指向便携版 `.local/connection.json`（或用 `ASSISTANT_API_URL` 和本机密钥），不要把路径指向其他人的目录或在公开仓库中写入密钥。

## 维护者构建

需要 Java 21 JDK、Maven 3.9+、Node.js 22.12+（推荐 24），以及网络或已准备的依赖缓存。构建会运行前后端构建与后端测试：

```powershell
./scripts/package-windows.ps1 -JavaHome 'C:/path/to/jdk-21' -Maven 'C:/path/to/mvn.cmd' -DownloadRuntime
```

`-DownloadRuntime` 是显式下载许可，调用 Adoptium 官方 API 获取 Java 21 Windows x64 JRE，按供应商 SHA256 校验。离线可用 `-RuntimeArchive 'C:/downloads/official-jre.zip'`，维护者须自行验证其官方来源与校验值。运行时原有 LICENSE、NOTICE 与 legal 目录随包保留，构建清单记录来源和摘要；包旁 `.sha256` 校验整个 ZIP。

输出在 `release/`，不会纳入 Git。构建仅从固定文件清单复制，禁止携带 `.local`、数据库、令牌、开发者插件绝对路径或 node_modules。

GitHub Actions 对每次推送/PR 在独立 Windows runner 构建、解压后清空开发环境变量并测试启动、API 鉴权、数据持久化、重复启动、端口冲突和停止，再上传测试产物。推送 `v*` 标签时，全部验证通过才创建 GitHub Release 及 ZIP、SHA256 下载附件。

该自动测试不等于真实新电脑的完整验收：runner 带有开发工具、没有普通用户桌面交互。另一台全新 Windows 机器仍需人工检查解压、双击、浏览器自动连接、通知注册/横幅、取消提醒与睡眠恢复。版本的实际验证范围见 `docs/test-report.md`，未完成的人工项不得宣称已通过。
