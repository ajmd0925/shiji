import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

test("页面取消真实原生测试提醒无需二次确认，并展示等待状态", async ({
  page,
}) => {
  const config = JSON.parse(
    readFileSync(
      fileURLToPath(new URL("../../.local/connection.json", import.meta.url)),
      "utf8",
    ),
  );
  await page.addInitScript(
    (token) => sessionStorage.setItem("assistant-token", token),
    config.token,
  );
  await page.goto("/");
  await page.getByRole("button", { name: "提醒中心", exact: true }).click();
  const row = page
    .locator(".reminder-row")
    .filter({ hasText: "自动取消联调测试（临时）" });
  test.skip((await row.count()) === 0, "仅在真实原生联调夹具存在时运行");
  await expect(row.getByText("已安排", { exact: true })).toBeVisible();
  await row.getByRole("button", { name: "取消", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(row.getByText("待 Codex 取消", { exact: true })).toBeVisible();
  await expect(row.getByText(/发送前会复查取消状态/)).toBeVisible();
});
