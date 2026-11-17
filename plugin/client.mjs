import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

export function configuration() {
  const path =
    process.env.ASSISTANT_CONFIG ||
    fileURLToPath(new URL("../.local/connection.json", import.meta.url));
  let file = {};
  try {
    file = JSON.parse(readFileSync(path, "utf8").replace(/^\uFEFF/, ""));
  } catch {
    /* Environment-only configuration is supported. */
  }
  const url =
    process.env.ASSISTANT_API_URL || file.url || "http://127.0.0.1:8080";
  const token = process.env.ASSISTANT_API_TOKEN || file.token;
  const parsed = new URL(url);
  if (
    !["http:", "https:"].includes(parsed.protocol) ||
    parsed.username ||
    parsed.password
  )
    throw new Error("Invalid assistant API URL");
  if (!token || token.length < 24)
    throw new Error(
      "个人助手未配置，请先运行 scripts/start-backend.ps1 和 plugin/scripts/configure.mjs",
    );
  return { url: url.replace(/\/$/, ""), token };
}
export async function request(path, method = "GET", body, timeoutMs = 10000) {
  const { url, token } = configuration();
  const response = await fetch(`${url}/api${path}`, {
    method,
    redirect: "error",
    signal: AbortSignal.timeout(timeoutMs),
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) {
    const result = await response.json().catch(() => ({}));
    throw new Error(result.message || `个人助手接口失败 (${response.status})`);
  }
  return response.status === 204 ? { deleted: true } : response.json();
}
