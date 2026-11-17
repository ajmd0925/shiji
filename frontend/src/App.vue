<script setup>
import { computed, onMounted, onUnmounted, reactive, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
  House,
  List,
  Bell,
  Notebook,
  Plus,
  Search,
  ArrowRight,
  Check,
  Close,
  Connection,
  MoreFilled,
  EditPen,
  Delete,
  Refresh,
  Top,
  CircleCheck,
  Clock,
  Setting,
  Moon,
} from "@element-plus/icons-vue";
import { api, send } from "./api";

const page = ref("overview"),
  query = ref(""),
  filter = ref("all");
const tasks = ref([]),
  notes = ref([]),
  reminders = ref([]);
const syncStatus = ref({ enabled: false, recent: false });
const connected = ref(false),
  loading = ref(false),
  saving = ref(false),
  error = ref("");
const connectOpen = ref(false),
  token = ref(""),
  taskOpen = ref(false),
  noteOpen = ref(false),
  reminderOpen = ref(false);
const taskForm = reactive({}),
  noteForm = reactive({}),
  reminderForm = reactive({});
const now = ref(new Date());
let timer;
const nav = [
  { id: "overview", text: "我的一天", icon: House },
  { id: "tasks", text: "全部任务", icon: List },
  { id: "reminders", text: "提醒中心", icon: Bell },
  { id: "notes", text: "备忘录", icon: Notebook },
];
const labels = {
  PENDING: "待 Codex 创建",
  CREATING: "创建结果待回写",
  SCHEDULED: "已安排",
  DISPATCHING: "正在发送",
  PAUSED: "已暂停",
  FIRED: "已执行",
  RECEIVED: "已确认收到",
  FAILED: "创建失败",
  DELIVERY_FAILED: "发送待处理",
  CANCEL_PENDING: "待 Codex 取消",
  CANCELLED: "已取消",
};
const colors = { NORMAL: "普通", HIGH: "优先", LOW: "不急" };
const pending = computed(() => tasks.value.filter((t) => !t.completed));
const done = computed(() => tasks.value.filter((t) => t.completed));
const activeReminders = computed(() =>
  reminders.value.filter((r) =>
    ["PENDING", "CREATING", "SCHEDULED", "PAUSED", "CANCEL_PENDING"].includes(
      r.state,
    ),
  ),
);
const nextReminder = computed(() =>
  reminders.value.find(
    (r) =>
      r.state === "SCHEDULED" &&
      new Date(r.verifiedScheduledAt || r.scheduledAt) > now.value,
  ),
);
const dateLabel = computed(() =>
  now.value.toLocaleDateString("zh-CN", {
    month: "long",
    day: "numeric",
    weekday: "long",
  }),
);
const progress = computed(() =>
  tasks.value.length
    ? Math.round((done.value.length / tasks.value.length) * 100)
    : 0,
);
const searchMatch = (text) =>
  text.toLowerCase().includes(query.value.trim().toLowerCase());
const visibleTasks = computed(() =>
  tasks.value
    .filter(
      (t) =>
        searchMatch(`${t.title} ${t.description}`) &&
        (filter.value === "done"
          ? t.completed
          : filter.value === "pending"
            ? !t.completed
            : true),
    )
    .slice(0, page.value === "overview" ? 6 : undefined),
);
const visibleNotes = computed(() =>
  notes.value.filter((n) => searchMatch(`${n.title} ${n.content}`)),
);
const visibleReminders = computed(() =>
  reminders.value.filter((r) => searchMatch(`${r.title} ${r.message}`)),
);
const shortTime = (value) =>
  value
    ? new Date(value).toLocaleString("zh-CN", {
        month: "numeric",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        hour12: false,
      })
    : "未设时间";
const overdue = (t) => !t.completed && t.dueAt && new Date(t.dueAt) < now.value;
const statusType = (state) =>
  ({
    SCHEDULED: "success",
    PAUSED: "info",
    RECEIVED: "success",
    FAILED: "danger",
    DELIVERY_FAILED: "danger",
    DISPATCHING: "warning",
    CANCEL_PENDING: "warning",
    PENDING: "warning",
    CREATING: "warning",
    CANCELLED: "info",
    FIRED: "primary",
  })[state];
const changePage = (id) => {
  page.value = id;
  query.value = "";
  filter.value = "all";
};

async function load(silent = false) {
  if (!silent) loading.value = true;
  try {
    const data = await Promise.all([
      api("/tasks"),
      api("/notes"),
      api("/reminders"),
      api("/notifications/status"),
    ]);
    [tasks.value, notes.value, reminders.value, syncStatus.value] = data;
    connected.value = true;
    error.value = "";
  } catch (e) {
    error.value = e.message;
    if (e.status === 401) connected.value = false;
  } finally {
    loading.value = false;
  }
}
async function connect() {
  if (!token.value.trim()) return ElMessage.warning("请填写访问密钥");
  sessionStorage.setItem("assistant-token", token.value.trim());
  await load();
  if (connected.value) {
    connectOpen.value = false;
    token.value = "";
    ElMessage.success("已连接你的个人空间");
  }
}
function requireConnection() {
  if (connected.value) return true;
  connectOpen.value = true;
  return false;
}
function openTask(task) {
  if (!requireConnection()) return;
  Object.assign(taskForm, {
    id: task?.id,
    title: task?.title || "",
    description: task?.description || "",
    priority: task?.priority || "NORMAL",
    dueAt: task?.dueAt ? new Date(task.dueAt) : null,
    completed: task?.completed || false,
  });
  taskOpen.value = true;
}
async function saveTask() {
  if (!taskForm.title.trim()) return ElMessage.warning("给任务起个名字吧");
  saving.value = true;
  try {
    const { id, ...data } = taskForm;
    await send(id ? `/tasks/${id}` : "/tasks", data, id ? "PUT" : "POST");
    taskOpen.value = false;
    await load(true);
    ElMessage.success(id ? "任务已更新" : "任务已添加");
  } catch (e) {
    ElMessage.error(e.message);
  } finally {
    saving.value = false;
  }
}
async function toggleTask(task) {
  try {
    await send(
      `/tasks/${task.id}`,
      {
        title: task.title,
        description: task.description,
        priority: task.priority,
        dueAt: task.dueAt,
        completed: !task.completed,
      },
      "PUT",
    );
    await load(true);
  } catch (e) {
    ElMessage.error(e.message);
  }
}
async function deleteTask(task) {
  try {
    await ElMessageBox.confirm(`确定删除“${task.title}”？`, "删除任务", {
      type: "warning",
      confirmButtonText: "删除",
      cancelButtonText: "保留",
    });
    await api(`/tasks/${task.id}`, { method: "DELETE" });
    await load(true);
    ElMessage.success("任务已删除");
  } catch (e) {
    if (e instanceof Error) ElMessage.error(e.message);
  }
}
function openNote(note) {
  if (!requireConnection()) return;
  Object.assign(noteForm, {
    id: note?.id,
    title: note?.title || "",
    content: note?.content || "",
    color: note?.color || "LAVENDER",
    pinned: note?.pinned || false,
  });
  noteOpen.value = true;
}
async function saveNote() {
  if (!noteForm.title.trim()) return ElMessage.warning("请填写备忘录标题");
  saving.value = true;
  try {
    const { id, ...data } = noteForm;
    await send(id ? `/notes/${id}` : "/notes", data, id ? "PUT" : "POST");
    noteOpen.value = false;
    await load(true);
    ElMessage.success("备忘录已保存");
  } catch (e) {
    ElMessage.error(e.message);
  } finally {
    saving.value = false;
  }
}
async function deleteNote(note) {
  try {
    await ElMessageBox.confirm("确定删除这条备忘录？", "删除备忘录", {
      confirmButtonText: "删除",
      cancelButtonText: "保留",
    });
    await api(`/notes/${note.id}`, { method: "DELETE" });
    noteOpen.value = false;
    await load(true);
  } catch (e) {
    if (e instanceof Error) ElMessage.error(e.message);
  }
}
function openReminder(task) {
  if (!requireConnection()) return;
  Object.assign(reminderForm, {
    taskId: task?.id || "",
    message: task?.title || "",
    scheduledAt: new Date(Date.now() + 3 * 60000),
  });
  reminderOpen.value = true;
}
async function saveReminder() {
  if (
    !reminderForm.taskId ||
    !reminderForm.message.trim() ||
    !reminderForm.scheduledAt
  )
    return ElMessage.warning("请填写任务、提醒内容和时间");
  if (new Date(reminderForm.scheduledAt) <= new Date())
    return ElMessage.warning("请选择未来的提醒时间");
  saving.value = true;
  try {
    const result = await send("/reminders", {
      ...reminderForm,
      timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      requestKey: crypto.randomUUID(),
    });
    reminderOpen.value = false;
    await load(true);
    ElMessage.success(
      result.confirmation || "提醒请求已保存，尚未安排通知",
    );
  } catch (e) {
    ElMessage.error(e.message);
  } finally {
    saving.value = false;
  }
}
async function reminderAction(reminder, action) {
  try {
    if (action === "cancel") {
      const result = await send(`/reminders/${reminder.id}/cancel`, {});
      ElMessage.success(
        result.confirmation || (result.state === "CANCELLED"
          ? "提醒已取消"
          : "取消请求已保存，等待 Codex 确认"),
      );
    } else
      await send(`/reminders/${reminder.id}/events`, {
        state: "RECEIVED",
        detail: "用户在管理界面确认收到通知",
      });
    await load(true);
    if (action !== "cancel") ElMessage.success("已记录通知送达");
  } catch (e) {
    if (e instanceof Error) ElMessage.error(e.message);
  }
}
async function copyPrompt() {
  try {
    await navigator.clipboard.writeText(
      "使用拾记个人助手，查看提醒及后台通知服务状态。新提醒由拾记后台发送系统通知，无需创建 Codex 自动化。",
    );
    ElMessage.success("已复制，请粘贴到已安装插件的 Codex 聊天");
  } catch {
    ElMessage.info("请在 Codex 中发送：使用拾记，查看提醒和后台通知服务状态");
  }
}
onMounted(() => {
  // Launcher passes the local secret in the fragment, which is never sent to the server.
  const launch = new URLSearchParams(location.hash.slice(1));
  const launchToken = launch.get("shiji-token");
  if (launchToken && /^[a-f0-9]{64}$/.test(launchToken)) {
    sessionStorage.setItem("assistant-token", launchToken);
    history.replaceState(null, "", location.pathname + location.search);
  }
  if (sessionStorage.getItem("assistant-token")) load();
  timer = setInterval(() => {
    now.value = new Date();
    if (connected.value && !document.hidden) load(true);
  }, 5000);
});
onUnmounted(() => clearInterval(timer));
</script>

<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="#" @click.prevent="changePage('overview')"
        ><span class="brand-mark"
          ><el-icon><Check /></el-icon></span
        ><span>拾记<small>PERSONAL SPACE</small></span></a
      >
      <p class="nav-caption">留一点空间，给重要的事</p>
      <nav aria-label="功能导航">
        <button
          v-for="item in nav"
          :key="item.id"
          :aria-label="item.text"
          :class="['nav-item', { active: page === item.id }]"
          @click="changePage(item.id)"
        >
          <el-icon><component :is="item.icon" /></el-icon
          ><span>{{ item.text }}</span
          ><span
            v-if="item.id === 'tasks' && pending.length"
            class="nav-count"
            >{{ pending.length }}</span
          >
        </button>
      </nav>
      <div class="sidebar-bottom">
        <div class="integration-mini">
          <span class="integration-logo">✳</span
          ><strong>和 Codex 一起记</strong>
          <p>把一句话，变成一件完成的事。</p>
          <button @click="changePage('reminders')">
            了解提醒流程 <el-icon><ArrowRight /></el-icon>
          </button>
        </div>
        <button class="profile" @click="connectOpen = true">
          <span class="avatar">我</span
          ><span
            >我的个人空间<small>{{
              connected ? "已连接 · 本地私有" : "等待连接"
            }}</small></span
          ><el-icon><Setting /></el-icon>
        </button>
      </div>
    </aside>
    <div class="main-area">
      <header class="topbar">
        <span class="breadcrumb"
          >个人空间 <span>/</span>
          {{ nav.find((n) => n.id === page)?.text }}</span
        >
        <div class="topbar-right">
          <span :class="['connection-state', { on: connected }]"
            ><i />{{ connected ? "服务已连接" : "尚未连接" }}</span
          ><button
            class="icon-button"
            aria-label="刷新数据"
            :disabled="!connected || loading"
            @click="load()"
          >
            <el-icon :class="{ 'is-loading': loading }"
              ><Refresh
            /></el-icon></button
          ><button
            class="avatar small"
            aria-label="连接设置"
            @click="connectOpen = true"
          >
            我
          </button>
        </div>
      </header>
      <main v-loading="loading" class="content">
        <el-alert
          v-if="error"
          :title="error"
          type="error"
          show-icon
          :closable="false"
          class="error-banner"
        />
        <div class="page-heading">
          <div>
            <p class="eyebrow">
              {{
                page === "overview" ? dateLabel : "MAKE ROOM FOR WHAT MATTERS"
              }}
            </p>
            <h1>
              {{
                page === "overview"
                  ? "把今天，过得从容一点。"
                  : page === "tasks"
                    ? "小小行动，慢慢完成。"
                    : page === "notes"
                      ? "灵感和琐事，都有着落。"
                      : "让重要的事，准时出现。"
              }}
            </h1>
            <p class="subtitle">
              {{
                page === "overview"
                  ? "记下要做的事，也给自己留一点空白。"
                  : page === "tasks"
                    ? "整理待办，专注下一件值得做的事。"
                    : page === "notes"
                      ? "不用记住所有事，写下来就好。"
                      : "拾记后台静默安排，提醒状态清清楚楚。"
              }}
            </p>
          </div>
          <el-button
            type="primary"
            size="large"
            :icon="Plus"
            @click="
              page === 'notes'
                ? openNote()
                : page === 'reminders'
                  ? openReminder()
                  : openTask()
            "
            >{{
              page === "notes"
                ? "写备忘录"
                : page === "reminders"
                  ? "添加提醒"
                  : "添加任务"
            }}</el-button
          >
        </div>
        <div v-if="!connected" class="connect-banner">
          <div>
            <el-icon><Connection /></el-icon
            ><span
              ><strong>欢迎来到你的个人空间</strong
              ><small>连接本地服务，开始记录任务和灵感。</small></span
            >
          </div>
          <el-button @click="connectOpen = true"
            >连接我的空间 <el-icon><ArrowRight /></el-icon
          ></el-button>
        </div>

        <template v-if="page === 'overview'">
          <section class="summary-grid" aria-label="今日概览">
            <article class="summary-card">
              <span class="stat-icon violet"
                ><el-icon><List /></el-icon
              ></span>
              <p>待办任务</p>
              <div>
                <strong>{{ pending.length }}</strong
                ><span>件值得完成的小事</span>
              </div>
            </article>
            <article class="summary-card">
              <span class="stat-icon green"
                ><el-icon><CircleCheck /></el-icon
              ></span>
              <p>已完成</p>
              <div>
                <strong>{{ done.length }}</strong
                ><span>每一步，都算数</span>
              </div>
            </article>
            <article class="summary-card">
              <span class="stat-icon orange"
                ><el-icon><Bell /></el-icon
              ></span>
              <p>待处理提醒</p>
              <div>
                <strong>{{ activeReminders.length }}</strong
                ><span>{{
                  nextReminder
                    ? `下次 ${shortTime(nextReminder.scheduledAt)}`
                    : "把重要的事交给提醒"
                }}</span>
              </div>
            </article>
          </section>
          <div class="overview-grid">
            <section class="panel tasks-panel">
              <div class="section-heading">
                <h2>
                  今天，想做些什么 <span>{{ pending.length }}</span>
                </h2>
                <button class="text-button" @click="changePage('tasks')">
                  全部任务 <el-icon><ArrowRight /></el-icon>
                </button>
              </div>
              <div class="filter-tabs">
                <button
                  :class="{ selected: filter === 'all' }"
                  @click="filter = 'all'"
                >
                  全部</button
                ><button
                  :class="{ selected: filter === 'pending' }"
                  @click="filter = 'pending'"
                >
                  待完成</button
                ><button
                  :class="{ selected: filter === 'done' }"
                  @click="filter = 'done'"
                >
                  已完成
                </button>
              </div>
              <div v-if="!visibleTasks.length" class="empty-state">
                <div class="empty-art">
                  <el-icon><List /></el-icon><span>✦</span>
                </div>
                <h3>
                  {{
                    filter === "done"
                      ? "每一次完成，都值得记录"
                      : "给今天一个小小的开始"
                  }}
                </h3>
                <p>
                  {{
                    filter === "done"
                      ? "完成的任务会出现在这里"
                      : "添加第一件待办，让脑海轻一点"
                  }}
                </p>
                <el-button text type="primary" :icon="Plus" @click="openTask()"
                  >添加任务</el-button
                >
              </div>
              <div
                v-for="task in visibleTasks"
                :key="task.id"
                :class="['task-row', { completed: task.completed }]"
              >
                <button
                  class="task-check"
                  :aria-label="task.completed ? '恢复任务' : '完成任务'"
                  @click="toggleTask(task)"
                >
                  <el-icon v-if="task.completed"><Check /></el-icon>
                </button>
                <div class="task-copy" @click="openTask(task)">
                  <strong>{{ task.title }}</strong>
                  <p v-if="task.description">{{ task.description }}</p>
                  <div class="task-meta">
                    <span :class="['priority', task.priority.toLowerCase()]"
                      ><i />{{ colors[task.priority] }}</span
                    ><span v-if="task.dueAt" :class="{ overdue: overdue(task) }"
                      ><el-icon><Clock /></el-icon
                      >{{ shortTime(task.dueAt) }}</span
                    >
                  </div>
                </div>
                <button
                  class="icon-button"
                  aria-label="编辑任务"
                  @click="openTask(task)"
                >
                  <el-icon><EditPen /></el-icon>
                </button>
              </div>
              <button class="add-inline" @click="openTask()">
                <el-icon><Plus /></el-icon> 添加一件小事
              </button>
            </section>
            <aside class="right-rail">
              <article class="focus-card">
                <div>
                  <span>ONE THING AT A TIME</span><span class="sun">✳</span>
                </div>
                <h2>不必一次完成所有。<br />先做好眼前这一件。</h2>
                <p>今天的进度</p>
                <div class="progress-line">
                  <span :style="{ width: progress + '%' }" />
                </div>
                <footer>
                  <span>已完成 {{ done.length }} / {{ tasks.length }} 件</span
                  ><strong>{{ progress }}%</strong>
                </footer>
              </article>
              <section class="panel note-preview">
                <div class="section-heading">
                  <h2>随手记</h2>
                  <button
                    class="icon-button"
                    aria-label="新增备忘录"
                    @click="openNote()"
                  >
                    <el-icon><Plus /></el-icon>
                  </button>
                </div>
                <template v-if="notes.length"
                  ><button
                    v-for="note in notes.slice(0, 2)"
                    :key="note.id"
                    :class="['mini-note', note.color.toLowerCase()]"
                    @click="openNote(note)"
                  >
                    <strong>{{ note.title }}</strong>
                    <p>{{ note.content }}</p>
                    <small>{{ shortTime(note.updatedAt) }}</small>
                  </button></template
                >
                <div v-else class="note-empty">
                  <span>“</span>
                  <p>突然想到的点子，<br />别让它悄悄溜走。</p>
                  <button class="text-button" @click="openNote()">
                    写下第一条备忘录 <el-icon><ArrowRight /></el-icon>
                  </button>
                </div>
              </section>
            </aside>
          </div>
        </template>

        <section v-if="page === 'tasks'" class="panel full-panel">
          <div class="list-toolbar">
            <div class="filter-tabs">
              <button
                v-for="f in [
                  { id: 'all', text: '全部任务' },
                  { id: 'pending', text: '待完成' },
                  { id: 'done', text: '已完成' },
                ]"
                :key="f.id"
                :class="{ selected: filter === f.id }"
                @click="filter = f.id"
              >
                {{ f.text }}
              </button>
            </div>
            <el-input
              v-model="query"
              placeholder="搜索任务"
              :prefix-icon="Search"
              clearable
              class="search-input"
            />
          </div>
          <div v-if="!visibleTasks.length" class="empty-state">
            <div class="empty-art">
              <el-icon><List /></el-icon>
            </div>
            <h3>{{ query ? "没有找到相关任务" : "这里还有很多可能" }}</h3>
            <p>
              {{ query ? "换个关键词试试" : "从一件小事开始，把想法变成行动" }}
            </p>
            <el-button type="primary" plain :icon="Plus" @click="openTask()"
              >添加任务</el-button
            >
          </div>
          <div
            v-for="task in visibleTasks"
            :key="task.id"
            :class="['task-row', { completed: task.completed }]"
          >
            <button
              class="task-check"
              :aria-label="task.completed ? '恢复任务' : '完成任务'"
              @click="toggleTask(task)"
            >
              <el-icon v-if="task.completed"><Check /></el-icon>
            </button>
            <div class="task-copy">
              <strong @click="openTask(task)">{{ task.title }}</strong>
              <p v-if="task.description">{{ task.description }}</p>
              <div class="task-meta">
                <span :class="['priority', task.priority.toLowerCase()]"
                  ><i />{{ colors[task.priority] }}</span
                ><span v-if="task.dueAt" :class="{ overdue: overdue(task) }">{{
                  shortTime(task.dueAt)
                }}</span>
              </div>
            </div>
            <div class="row-actions">
              <el-button
                v-if="!task.completed"
                text
                :icon="Bell"
                aria-label="添加任务提醒"
                @click="openReminder(task)"
              /><el-button
                text
                :icon="EditPen"
                aria-label="编辑任务"
                @click="openTask(task)"
              /><el-button
                text
                :icon="Delete"
                aria-label="删除任务"
                @click="deleteTask(task)"
              />
            </div>
          </div>
        </section>

        <template v-if="page === 'notes'"
          ><div class="notes-toolbar">
            <span>{{ notes.length }} 条记录，安放每一个想法</span
            ><el-input
              v-model="query"
              placeholder="搜索备忘录"
              :prefix-icon="Search"
              clearable
              class="search-input"
            />
          </div>
          <div class="notes-grid">
            <button
              v-for="note in visibleNotes"
              :key="note.id"
              :class="['note-card', note.color.toLowerCase()]"
              @click="openNote(note)"
            >
              <div>
                <span>{{ shortTime(note.updatedAt) }}</span
                ><el-icon v-if="note.pinned"><Top /></el-icon>
              </div>
              <h2>{{ note.title }}</h2>
              <p>{{ note.content || "还没有正文，点击继续写…" }}</p>
              <footer>
                点击编辑 <el-icon><EditPen /></el-icon>
              </footer></button
            ><button class="note-card new-note" @click="openNote()">
              <el-icon><Plus /></el-icon><strong>记下此刻的想法</strong
              ><span>一个灵感，一件琐事，一段心情</span>
            </button>
          </div>
          <p v-if="query && !visibleNotes.length" class="search-empty">
            没有找到相关备忘录
          </p></template
        >

        <template v-if="page === 'reminders'"
          ><section class="reminder-explainer">
            <span class="integration-logo">✳</span>
            <div>
              <h2>让拾记帮你准时想起</h2>
              <p>
                保存并确认时间 → 拾记后台静默等待 → 到点发送电脑系统通知 →
                由你确认收到。
              </p>
              <small>{{
                syncStatus.ready
                  ? "后台通知通道可用，无需聊天轮询。请保持后端运行和电脑唤醒；横幅显示取决于 Windows 通知设置。"
                  : "后台通知通道尚不可用，请检查服务及 Windows 通知设置。"
              }}</small>
              <small v-if="syncStatus.lastScanAt"
                >最近检查：{{ shortTime(syncStatus.lastScanAt) }}</small
              >
              <small v-if="syncStatus.error" role="alert"
                >通知待处理：{{ syncStatus.error }}</small
              >
            </div>
            <el-button @click="copyPrompt"
              >复制查看指令 <el-icon><Connection /></el-icon
            ></el-button>
          </section>
          <section class="panel full-panel">
            <div class="list-toolbar">
              <h2>
                提醒记录 <span class="muted">{{ reminders.length }}</span>
              </h2>
              <el-input
                v-model="query"
                placeholder="搜索提醒"
                :prefix-icon="Search"
                clearable
                class="search-input"
              />
            </div>
            <div v-if="!visibleReminders.length" class="empty-state">
              <div class="empty-art">
                <el-icon><Bell /></el-icon>
              </div>
              <h3>重要的事，值得被记住</h3>
              <p>为任务添加一次性提醒，留出专注的空间</p>
              <el-button type="primary" plain @click="openReminder()"
                >添加提醒</el-button
              >
            </div>
            <article
              v-for="reminder in visibleReminders"
              :key="reminder.id"
              class="reminder-row"
            >
              <div class="reminder-symbol">
                <el-icon><Bell /></el-icon>
              </div>
              <div class="reminder-copy">
                <strong>{{ reminder.title }}</strong>
                <p>{{ reminder.message }}</p>
                <small
                  >{{ shortTime(reminder.scheduledAt) }} ·
                  {{ reminder.timezone }}</small
                >
                <p v-if="reminder.state === 'PENDING'" class="state-help">
                  {{
                    new Date(reminder.scheduledAt) < now
                      ? "请求时间已过，请取消并重新设置"
                      : "旧版 Codex 请求，尚未创建原生提醒"
                  }}
                </p>
                <p
                  v-if="reminder.state === 'CANCEL_PENDING'"
                  class="state-help"
                >
                  取消请求已保存，等待 Codex 确认；发送前会复查取消状态
                </p>
                <p v-if="reminder.state === 'PAUSED'" class="state-help">
                  原生提醒已暂停，记录保留
                </p>
                <small v-if="reminder.verifiedScheduledAt"
                  >原生计划已核验：{{
                    shortTime(reminder.verifiedScheduledAt)
                  }}</small
                >
                <p
                  v-if="
                    reminder.state === 'SCHEDULED' &&
                    new Date(reminder.scheduledAt) < now
                  "
                  class="state-help"
                >
                  计划时间已过，尚未确认发送，请检查后台服务
                </p>
                <p
                  v-if="
                    reminder.detail &&
                    ['FAILED', 'PAUSED', 'DELIVERY_FAILED', 'FIRED', 'CANCELLED'].includes(reminder.state)
                  "
                  class="state-help"
                >
                  {{ reminder.detail }}
                </p>
              </div>
              <div class="reminder-actions">
                <el-tag :type="statusType(reminder.state)" effect="light">{{
                  labels[reminder.state]
                }}</el-tag>
                <div>
                  <el-button
                    v-if="reminder.state === 'FIRED' || (reminder.channel !== 'SYSTEM' && reminder.state === 'SCHEDULED')"
                    text
                    type="primary"
                    @click="reminderAction(reminder, 'received')"
                    >我已收到</el-button
                  ><el-button
                    v-if="
                      !['CANCELLED', 'CANCEL_PENDING'].includes(reminder.state)
                    "
                    text
                    @click="reminderAction(reminder, 'cancel')"
                    >{{
                      reminder.channel === 'SYSTEM' ? '关闭通知' : ["FIRED", "RECEIVED"].includes(reminder.state)
                        ? "申请删除原生记录"
                        : "取消"
                    }}</el-button
                  >
                </div>
              </div>
            </article>
          </section></template
        >
        <footer class="page-footer">
          <span>拾记 · 给生活一点秩序</span
          ><span
            ><el-icon><Moon /></el-icon> 不慌不忙，慢慢来</span
          >
        </footer>
      </main>
    </div>
    <el-dialog
      v-model="connectOpen"
      title="连接个人空间"
      width="440px"
      :close-on-click-modal="false"
      ><p class="dialog-intro">
        启动后端服务后，输入启动脚本生成的访问密钥。密钥仅保存在当前浏览器会话中。
      </p>
      <el-form label-position="top" @submit.prevent="connect"
        ><el-form-item label="访问密钥"
          ><el-input
            v-model="token"
            type="password"
            show-password
            placeholder="粘贴 .local/access-token.txt 中的密钥"
            autocomplete="off"
            @keyup.enter="connect" /></el-form-item></el-form
      ><el-alert
        v-if="error"
        :title="error"
        type="error"
        :closable="false"
      /><template #footer
        ><el-button @click="connectOpen = false">稍后</el-button
        ><el-button type="primary" :loading="loading" @click="connect"
          >连接</el-button
        ></template
      ></el-dialog
    >
    <el-dialog
      v-model="taskOpen"
      :title="taskForm.id ? '编辑任务' : '添加一件小事'"
      width="500px"
      ><el-form label-position="top" @submit.prevent="saveTask"
        ><el-form-item label="任务名称" required
          ><el-input
            v-model="taskForm.title"
            maxlength="160"
            placeholder="例如：读完一章书" /></el-form-item
        ><el-form-item label="补充说明"
          ><el-input
            v-model="taskForm.description"
            type="textarea"
            :rows="3"
            maxlength="10000"
            placeholder="记下细节，开始时更轻松"
        /></el-form-item>
        <div class="form-columns">
          <el-form-item label="优先级"
            ><el-select v-model="taskForm.priority"
              ><el-option
                v-for="(text, value) in colors"
                :key="value"
                :value="value"
                :label="text" /></el-select></el-form-item
          ><el-form-item label="截止时间"
            ><el-date-picker
              v-model="taskForm.dueAt"
              type="datetime"
              placeholder="可选"
              format="MM-DD HH:mm"
          /></el-form-item>
        </div>
        <p class="form-hint">
          截止时间用于整理任务；通知请在提醒中心单独添加。
        </p></el-form
      ><template #footer
        ><el-button @click="taskOpen = false">取消</el-button
        ><el-button type="primary" :loading="saving" @click="saveTask"
          >保存任务</el-button
        ></template
      ></el-dialog
    >
    <el-dialog
      v-model="noteOpen"
      :title="noteForm.id ? '编辑备忘录' : '写一条备忘录'"
      width="560px"
      ><el-form label-position="top"
        ><el-form-item label="标题" required
          ><el-input
            v-model="noteForm.title"
            maxlength="160"
            placeholder="给这个想法起个名字" /></el-form-item
        ><el-form-item label="内容"
          ><el-input
            v-model="noteForm.content"
            type="textarea"
            :rows="8"
            maxlength="20000"
            placeholder="不用整理，先写下来…"
        /></el-form-item>
        <div class="note-options">
          <div class="color-picker">
            <button
              type="button"
              v-for="color in ['LAVENDER', 'SAGE', 'PEACH', 'BLUE']"
              :key="color"
              :class="[
                color.toLowerCase(),
                { picked: noteForm.color === color },
              ]"
              :aria-label="color"
              @click="noteForm.color = color"
            >
              <el-icon v-if="noteForm.color === color"><Check /></el-icon>
            </button>
          </div>
          <el-checkbox v-model="noteForm.pinned">置顶这条</el-checkbox>
        </div></el-form
      ><template #footer
        ><el-button
          v-if="noteForm.id"
          type="danger"
          text
          :icon="Delete"
          class="delete-note"
          @click="deleteNote(noteForm)"
          >删除</el-button
        ><el-button @click="noteOpen = false">取消</el-button
        ><el-button type="primary" :loading="saving" @click="saveNote"
          >保存备忘录</el-button
        ></template
      ></el-dialog
    >
    <el-dialog v-model="reminderOpen" title="添加一次性提醒" width="500px"
      ><el-alert
        :title="
          syncStatus.ready
            ? '保存成功后由拾记后台发送电脑系统通知'
            : '通知通道尚不可用，保存时会检查并返回原因'
        "
        type="info"
        :closable="false"
        show-icon
      /><el-form label-position="top" class="reminder-form"
        ><el-form-item label="关联任务" required
          ><el-select
            v-model="reminderForm.taskId"
            placeholder="选择一个待办任务"
            no-data-text="请先添加待办任务"
            ><el-option
              v-for="task in pending"
              :key="task.id"
              :value="task.id"
              :label="task.title" /></el-select></el-form-item
        ><el-form-item label="提醒内容" required
          ><el-input
            v-model="reminderForm.message"
            type="textarea"
            :rows="3"
            maxlength="2000"
            placeholder="到点时，想提醒自己什么？" /></el-form-item
        ><el-form-item label="提醒时间" required
          ><el-date-picker
            v-model="reminderForm.scheduledAt"
            type="datetime"
            placeholder="选择提醒时间"
            format="YYYY-MM-DD HH:mm:ss"
        /></el-form-item>
        <p class="form-hint">
          按浏览器本地时区保存。提醒只执行一次，收到后由你决定是否删除记录。
        </p></el-form
      ><template #footer
        ><el-button @click="reminderOpen = false">取消</el-button
        ><el-button type="primary" :loading="saving" @click="saveReminder"
          >保存提醒请求</el-button
        ></template
      ></el-dialog
    >
  </div>
</template>
