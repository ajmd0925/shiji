Windows x64 便携版：下载 `shiji-版本-windows-x64.zip`，完整解压后双击 `Start.cmd`。

- 自带 Java 21 与编译后的页面，无需安装 Java、Node、Maven、MySQL 或 Codex。
- 首次生成个人空间并注册本机 Windows 通知；`Check.cmd` 检查环境，`Stop.cmd` 正常停止。
- 默认 H2 本地文件数据库，支持手动配置 MySQL；两者数据独立。
- 保留原有页面与任务、备忘录、一次性提醒功能。

请先阅读包内 README.txt 和 docs/distribution.md。提醒需要电脑唤醒、保持登录和后台运行，Windows 允许通知横幅；不推送手机。

升级前先停止并备份整个旧目录，将旧版完整 `.local` 复制到新包目录。不要将个人数据、密钥提交到公开仓库。

自动化检查不替代另一台全新 Windows 电脑上的人工通知验收；实际验证范围见源码 docs/test-report.md。
