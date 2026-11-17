import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
const connection = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../.local/connection.json", import.meta.url)),
    "utf8",
  ).replace(/^\uFEFF/, ""),
);
const errors = [];
test.beforeEach(async ({ page }) => {
  errors.length = 0;
  page.on("pageerror", (e) => errors.push(e.message));
  await page.addInitScript((token) => {
    if (!sessionStorage.getItem("test-initialized")) {
      sessionStorage.setItem("assistant-token", token);
      sessionStorage.setItem("test-initialized", "1");
    }
  }, connection.token);
});
test.afterEach(() => expect(errors).toEqual([]));

test("任务、备忘录和提醒请求通过真实后端保存", async ({ page }) => {
  const suffix = Date.now().toString();
  await page.goto("/");
  await expect(page.getByText("服务已连接")).toBeVisible();
  await page
    .getByRole("button", { name: "添加任务", exact: true })
    .first()
    .click();
  const taskDialog = page.getByRole("dialog");
  await taskDialog
    .getByPlaceholder("例如：读完一章书")
    .fill(`自动化测试任务-${suffix}`);
  await taskDialog
    .getByPlaceholder("记下细节，开始时更轻松")
    .fill("测试持久化，不触发真实通知");
  await taskDialog.getByRole("button", { name: "保存任务" }).click();
  await expect(
    page.getByText(`自动化测试任务-${suffix}`, { exact: true }),
  ).toBeVisible();
  await page.reload();
  await expect(
    page.getByText(`自动化测试任务-${suffix}`, { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: /全部任务/ })
    .first()
    .click();
  const row = page
    .locator(".task-row")
    .filter({ hasText: `自动化测试任务-${suffix}` });
  await row.getByRole("button", { name: "添加任务提醒" }).click();
  const reminderDialog = page.getByRole("dialog");
  await reminderDialog.getByRole("button", { name: "保存提醒请求" }).click();
  await expect(page.getByText(/添加成功，将于/)).toBeVisible();
  await page.getByRole("button", { name: "提醒中心", exact: true }).click();
  const reminder = page
    .locator(".reminder-row")
    .filter({ hasText: `自动化测试任务-${suffix}` });
  await expect(reminder.getByText("已安排", {exact:true})).toBeVisible();
  await reminder.getByRole("button", { name: "关闭通知", exact: true }).click();
  await expect(page.getByText("该提醒通知已关闭，到点不会再提醒").first()).toBeVisible();
  await expect(reminder.getByText("已取消", { exact: true })).toBeVisible();
  await page
    .getByRole("button", { name: /全部任务/ })
    .first()
    .click();
  await row.getByRole("button", { name: "完成任务", exact: true }).click();
  await expect(row).toHaveClass(/completed/);
  await row.getByRole("button", { name: "恢复任务" }).click();
  await expect(row).not.toHaveClass(/completed/);
  await row.getByRole("button", { name: "删除任务" }).click();
  await page.getByRole("button", { name: "删除", exact: true }).click();
  await expect(row).toHaveCount(0);
  await page.getByRole("button", { name: "备忘录", exact: true }).click();
  await page.getByRole("button", { name: "写备忘录" }).click();
  const noteDialog = page.getByRole("dialog");
  await noteDialog
    .getByPlaceholder("给这个想法起个名字")
    .fill(`测试备忘录-${suffix}`);
  await noteDialog
    .getByPlaceholder("不用整理，先写下来…")
    .fill("<script>alert(1)</script>\n第二行原文");
  await noteDialog.getByRole("button", { name: "SAGE", exact: true }).click();
  await noteDialog.getByText("置顶这条").click();
  await noteDialog.getByRole("button", { name: "保存备忘录" }).click();
  const note = page
    .locator(".note-card")
    .filter({ hasText: `测试备忘录-${suffix}` });
  await expect(note).toBeVisible();
  await expect(note).toContainText("<script>alert(1)</script>");
  await page.reload();
  await page.getByRole("button", { name: "备忘录", exact: true }).click();
  await expect(note).toBeVisible();
  await note.click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "删除", exact: true })
    .click();
  await page
    .locator(".el-message-box")
    .getByRole("button", { name: "删除", exact: true })
    .click();
  await expect(note).toHaveCount(0);
});

test("搜索、连接校验和手机布局", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByText("服务已连接")).toBeVisible();
  await page
    .getByRole("button", { name: /全部任务/ })
    .first()
    .click();
  await page.getByPlaceholder("搜索任务").fill("不存在的搜索结果-12345");
  await expect(page.getByText("没有找到相关任务")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator("h1")).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  await page.getByRole("button", { name: "备忘录", exact: true }).click();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  await page.screenshot({
    path: "../.local/screenshots/mobile.png",
    fullPage: true,
  });
  await page.setViewportSize({ width: 1440, height: 1050 });
  await page.getByRole("button", { name: "我的一天" }).click();
  await page.screenshot({
    path: "../.local/screenshots/desktop.png",
    fullPage: true,
  });
  await page.evaluate(() => sessionStorage.removeItem("assistant-token"));
  await page.reload();
  await page.getByRole("button", { name: "连接我的空间" }).click();
  await page
    .getByPlaceholder("粘贴 .local/access-token.txt 中的密钥")
    .fill("invalid-token");
  await page.getByRole("button", { name: "连接", exact: true }).click();
  await expect(
    page.getByRole("dialog").getByText("请先连接个人助手，访问密钥无效"),
  ).toBeVisible();
});
