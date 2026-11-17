import { mkdir, writeFile, readFile, cp } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
const root = fileURLToPath(new URL("../../", import.meta.url));
const plugin = path.join(root, "plugin");
const config = path.join(root, ".local", "connection.json");
try {
  JSON.parse((await readFile(config, "utf8")).replace(/^\uFEFF/, ""));
} catch {
  throw new Error("请先运行 scripts/start-backend.ps1 生成本地连接配置");
}
await writeFile(
  path.join(plugin, ".mcp.json"),
  JSON.stringify(
    {
      mcpServers: {
        shiji: {
          command: process.execPath,
          args: [path.join(plugin, "server.mjs")],
          env: { ASSISTANT_CONFIG: config },
        },
      },
    },
    null,
    2,
  ) + "\n",
);
await mkdir(path.join(root, ".local", "marketplace", ".agents", "plugins"), {
  recursive: true,
});
const packaged = path.join(root, ".local", "marketplace", "plugins", "shiji");
await mkdir(packaged, { recursive: true });
for (const entry of [".codex-plugin", "skills", ".mcp.json"]) {
  await cp(path.join(plugin, entry), path.join(packaged, entry), {
    recursive: true,
  });
}
await writeFile(
  path.join(
    root,
    ".local",
    "marketplace",
    ".agents",
    "plugins",
    "marketplace.json",
  ),
  JSON.stringify(
    {
      name: "shiji-local",
      plugins: [
        {
          name: "shiji",
          source: { source: "local", path: "./plugins/shiji" },
          policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
          category: "Productivity",
        },
      ],
    },
    null,
    2,
  ) + "\n",
);
console.log(
  "已生成 plugin/.mcp.json（不含密钥）及 .local/marketplace/.agents/plugins/marketplace.json。请在 Codex 中安装该本地插件。",
);
