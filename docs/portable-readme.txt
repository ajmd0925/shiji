拾记 · Windows 便携版

1. 将整个 ZIP 解压到可写文件夹（不要在压缩包内运行），双击 Start.cmd。
2. 首次会注册拾记通知并显示龙娘桌宠；以后也用 Start.cmd。
   右键桌宠选择定时提醒、备忘录、任务表或完整网页；按住拖动。托盘菜单可调大小或收起。
   默认 144 逻辑像素，可选 72/96/120/144/160；支持收起到托盘，双击托盘可恢复。
   OpenBrowser.cmd 可打开原有的完整网页。
3. Check.cmd 检查环境。Stop.cmd 正常停止后台并保留数据。

已包含 Java 21 运行时和编译好的页面，不需要 Java、Node、Maven、MySQL 或 Codex。
默认使用 H2 本地文件数据库。MySQL 模式和可选 Codex 插件见 docs/distribution.md。
桌宠使用 Windows .NET Framework 4.8，Check.cmd 可检查；缺少时可用 OpenBrowser.cmd。
Start.cmd 不会自动下载软件、不申请管理员权限、不添加开机启动。

收起桌宠、关闭面板/网页和命令窗口后后台仍运行。托盘菜单“退出拾记（停止提醒）”会停止后台。电脑需要开机、唤醒并保持登录。
Windows「设置 → 系统 → 通知」中允许拾记通知及横幅，并检查请勿打扰。

所有个人数据、密钥、日志在当前目录的 .local 文件夹。
含中文/emoji 的路径会自动使用当前用户 AppData/Shiji 中的程序缓存，数据仍保存在原目录。
升级：先 Stop.cmd，备份旧目录；新版本解压到新目录后，将完整 .local 复制过去再启动。
不要同时启动两个使用同一份数据的副本。H2 与 MySQL 数据不会自动互相迁移。
不要将 .local 上传 GitHub 或发给他人。删除程序前请先停止后台并备份数据。

这是本机 Windows 通知，不能推送到手机，也不是 ChatGPT/Codex 云端任务。
源码、许可证与问题反馈：https://github.com/ajmd0925/shiji
