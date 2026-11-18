using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Shiji.Desktop {
    internal sealed class ApiRequestException : Exception {
        public readonly int Status;
        public ApiRequestException(int status, string message) : base(message) { Status = status; }
    }
    internal static class Program {
        [STAThread]
        public static int Main(string[] args) {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--root") root = Path.GetFullPath(args[++i]);
            string identity;
            using (var sha = SHA256.Create()) identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
            bool created;
            using (var mutex = new Mutex(true, @"Local\Shiji.Pet." + identity, out created)) {
                if (!created) {
                    try { using (var signal = EventWaitHandle.OpenExisting(@"Local\Shiji.Pet.Show." + identity)) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                try {
                    var client = new AssistantClient(root);
                    if (args.Contains("--self-test")) {
                        if (!File.Exists(Path.Combine(root, ".local", "pet-test-mode"))) throw new InvalidOperationException("Self-test requires an isolated test installation.");
                        RunSelfTest(client, root).GetAwaiter().GetResult();
                        return 0;
                    }
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var pet = new PetWindow(client, root, identity);
                    app.DispatcherUnhandledException += (sender, e) => { e.Handled = true; pet.ShowError("操作未完成，请重试。" + e.Exception.Message); };
                    app.Run(pet);
                    return 0;
                } catch (Exception e) {
                    try { Directory.CreateDirectory(Path.Combine(root, ".local")); File.WriteAllText(Path.Combine(root, ".local", "pet-error.log"), DateTimeOffset.Now.ToString("O") + " " + e.GetType().Name + ": " + e.Message, Encoding.UTF8); } catch { }
                    if (!args.Contains("--self-test")) MessageBox.Show("拾记桌宠未能启动：" + e.Message + "\n请先运行 Start.cmd，或查看 .local/pet-error.log。", "拾记", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                } finally { mutex.ReleaseMutex(); }
            }
        }

        private static async Task RunSelfTest(AssistantClient client, string root) {
            var results = new List<string>();
            string noteId = null, reminderId = null, taskId = null;
            try {
                var status = await client.Call("/desktop/status");
                Require(AssistantClient.Text(status, "application") == "shiji", "authenticated native client"); results.Add("authenticated native client");
                var note = await client.Call("/notes", "POST", new { title = "桌宠中文备忘录 📝", content = "第一行\n第二行 <script>保留原文</script>", color = "LAVENDER", pinned = false });
                noteId = AssistantClient.Text(note, "id");
                var notes = await client.List("/notes");
                Require(notes.Any(n => AssistantClient.Text(n, "id") == noteId && AssistantClient.Text(n, "content").Contains("第二行")), "Unicode memo roundtrip"); results.Add("Unicode memo roundtrip");
                string key = Guid.NewGuid().ToString();
                var input = new { title = "桌宠一次性提醒测试", message = "喝水", scheduledAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"), timezone = client.Timezone, requestKey = key };
                var reminder = await client.Call("/reminders/quick", "POST", input);
                reminderId = AssistantClient.Text(reminder, "id"); taskId = AssistantClient.Text(reminder, "taskId");
                Require(AssistantClient.Text(reminder, "state") == "SCHEDULED" && AssistantClient.Text(reminder, "confirmation").Contains("添加成功"), "one-step reminder with actual confirmation"); results.Add("one-step reminder with actual confirmation");
                var retry = await client.Call("/reminders/quick", "POST", input);
                Require(AssistantClient.Text(retry, "id") == reminderId, "safe retry reuses reminder"); results.Add("safe retry reuses reminder");
                var cancelled = await client.Call("/reminders/" + reminderId + "/cancel", "POST", new { });
                Require(AssistantClient.Text(cancelled, "state") == "CANCELLED" && AssistantClient.Text(cancelled, "confirmation").Contains("不会再提醒"), "immediate cancellation confirmation"); results.Add("immediate cancellation confirmation");
                var task = await client.Call("/tasks", "POST", new { title = "桌宠任务测试", description = "", priority = "NORMAL", completed = false });
                string ordinaryTaskId = AssistantClient.Text(task, "id");
                try {
                    var changed = await client.Call("/tasks/" + ordinaryTaskId, "PUT", new { title = "桌宠任务测试", description = "", priority = "NORMAL", completed = true, dueAt = (string)null });
                    Require(AssistantClient.Flag(changed, "completed"), "task completion"); results.Add("task completion");
                } finally { client.Call("/tasks/" + ordinaryTaskId, "DELETE").GetAwaiter().GetResult(); }
                var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                Require(AssistantClient.ResolveLocal(new DateTime(2030, 1, 1, 12, 0, 0), zone).Offset == TimeSpan.FromHours(8), "local time converts with actual offset"); results.Add("local time converts with actual offset");
                try { AssistantClient.ResolveLocal(new DateTime(2030, 11, 3, 1, 30, 0), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); throw new Exception("Ambiguous time was accepted"); } catch (ArgumentException) { results.Add("ambiguous DST time rejected"); }
                File.WriteAllText(Path.Combine(root, ".local", "pet-self-test.json"), new JavaScriptSerializer().Serialize(new { passed = results.Count, checks = results }), new UTF8Encoding(false));
            } finally {
                if (reminderId != null) client.Call("/reminders/" + reminderId + "/cancel", "POST", new { }).GetAwaiter().GetResult();
                if (taskId != null) client.Call("/tasks/" + taskId, "DELETE").GetAwaiter().GetResult();
                if (noteId != null) client.Call("/notes/" + noteId, "DELETE").GetAwaiter().GetResult();
            }
        }
        private static void Require(bool condition, string label) { if (!condition) throw new Exception("Self-test failed: " + label); }
    }

    internal sealed class AssistantClient : IDisposable {
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private readonly string token;
        public readonly string BaseUrl;
        public readonly string Timezone;
        public AssistantClient(string root) {
            var config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, ".local", "connection.json"), Encoding.UTF8));
            Uri url;
            if (!Uri.TryCreate(Text(config, "url"), UriKind.Absolute, out url) || url.Scheme != "http" || url.Host != "127.0.0.1" || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0 || url.UserInfo.Length > 0) throw new InvalidOperationException("仅能连接本机拾记服务。");
            BaseUrl = url.GetLeftPart(UriPartial.Authority);
            token = Text(config, "token");
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{64}$")) throw new InvalidOperationException("本机访问密钥无效。");
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // Java accepts a fixed UTC offset, avoiding Windows/IANA zone-name mismatches.
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            Timezone = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
        public async Task<object> Raw(string path, string method = "GET", object body = null) {
            if (!path.StartsWith("/") || path.Contains(":") || path.Contains("..")) throw new ArgumentException("接口路径无效。");
            using (var request = new HttpRequestMessage(new HttpMethod(method), "/api" + path)) {
                if (body != null) request.Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request)) {
                    string text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) {
                        string detail = "操作失败（" + (int)response.StatusCode + "）";
                        try { detail = Text(json.Deserialize<Dictionary<string, object>>(text), "message"); } catch { }
                        throw new ApiRequestException((int)response.StatusCode, detail);
                    }
                    return text.Length == 0 ? null : json.DeserializeObject(text);
                }
            }
        }
        public async Task<Dictionary<string, object>> Call(string path, string method = "GET", object body = null) { return (await Raw(path, method, body)) as Dictionary<string, object>; }
        public async Task<List<Dictionary<string, object>>> List(string path) { var result = (await Raw(path)) as object[]; return result == null ? new List<Dictionary<string, object>>() : result.Cast<Dictionary<string, object>>().ToList(); }
        public void OpenBrowser() { Process.Start(new ProcessStartInfo(BaseUrl + "/#shiji-token=" + token) { UseShellExecute = true }); }
        public static string Text(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        public static bool Flag(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value is bool && (bool)value; }
        public static DateTimeOffset ResolveLocal(DateTime value, TimeZoneInfo zone) {
            value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(value)) throw new ArgumentException("这个时间因夏令时切换而不存在，请选择其他时间。");
            if (zone.IsAmbiguousTime(value)) throw new ArgumentException("这个时间在夏令时切换时会出现两次，请选择其他时间。");
            return new DateTimeOffset(value, zone.GetUtcOffset(value));
        }
        public static string TimeLabel(string value) { DateTimeOffset time; return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) ? time.ToLocalTime().ToString("MM月dd日 HH:mm:ss") : "未设时间"; }
        public void Dispose() { http.Dispose(); }
    }

    internal static class Theme {
        public static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
        public static readonly Brush Ink = Brush("#343044"), Muted = Brush("#8C859E"), Purple = Brush("#7660C9"), Line = Brush("#EAE5F4");
        public static TextBlock Text(string value, double size = 14, Brush color = null) { return new TextBlock { Text = value, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }; }
        public static Button Button(string value, Action action, bool primary = false) {
            var button = new Button { Content = value, MinHeight = 38, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 0, 8), Background = primary ? Purple : Brush("#F2EDF9"), Foreground = primary ? Brushes.White : Ink, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='12' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.82'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
            button.Click += (sender, e) => action(); return button;
        }
        public static TextBox Input(string id, int maxLength, bool multiline = false) {
            var input = new TextBox { MaxLength = maxLength, FontSize = 14, Foreground = Ink, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 14), MinHeight = multiline ? 130 : 40, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            AutomationProperties.SetAutomationId(input, id); return input;
        }
    }

    internal sealed class PetWindow : Window {
        private readonly AssistantClient client;
        private readonly string local;
        private readonly EventWaitHandle showSignal, stopSignal;
        private readonly DispatcherTimer signals;
        private readonly DispatcherTimer blink;
        private readonly Forms.NotifyIcon tray;
        private readonly Border bubble;
        private readonly FrameworkElement cat;
        private readonly TextBlock bubbleText;
        private PanelWindow panel;
        private Point down, initial;
        private bool dragging, allowClose;
        private double petSize = 96;
        private DateTime bubbleUntil;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public PetWindow(AssistantClient client, string root, string identity) {
            this.client = client; local = Path.Combine(root, ".local");
            Title = "拾记桌宠"; Width = 148; Height = 174; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; FontFamily = new FontFamily("Microsoft YaHei UI");
            var area = SystemParameters.WorkArea; Left = area.Right - Width - 26; Top = area.Bottom - Height - 16;
            try {
                var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(local, "pet.json")));
                petSize = Convert.ToDouble(saved["size"], CultureInfo.InvariantCulture);
                if (petSize != 72 && petSize != 96 && petSize != 120) petSize = 96;
                Left = Convert.ToDouble(saved["left"], CultureInfo.InvariantCulture); Top = Convert.ToDouble(saved["top"], CultureInfo.InvariantCulture);
                if (!double.IsNaN(Left) && !double.IsInfinity(Left)) ClampPosition(); else { Left = area.Right - Width; Top = area.Bottom - Height; }
            } catch { }
            var canvas = new Canvas(); Content = canvas;
            bubbleText = Theme.Text("点我，记下小事", 12, Theme.Purple); bubbleText.Margin = new Thickness(0);
            bubble = new Border { Background = Theme.Brush("#FCFAFF"), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 7, 10, 7), Width = 144, Child = bubbleText, IsHitTestVisible = false };
            Canvas.SetTop(bubble, 0); Canvas.SetLeft(bubble, 2); canvas.Children.Add(bubble);
            cat = (FrameworkElement)XamlReader.Parse(CatXaml); cat.Width = petSize; cat.Height = petSize; cat.Cursor = Cursors.Hand; cat.ToolTip = "点击打开拾记 · 按住拖动 · 右键更多";
            AutomationProperties.SetName(cat, "拾记小猫，点击打开菜单，按住拖动"); AutomationProperties.SetAutomationId(cat, "ShijiPet");
            cat.Focusable = true; cat.KeyDown += (sender, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) OpenPanel("home"); };
            Canvas.SetTop(cat, 50); Canvas.SetLeft(cat, (Width - petSize) / 2); canvas.Children.Add(cat);
            cat.MouseLeftButtonDown += (sender, e) => { down = PointToScreen(e.GetPosition(this)); initial = new Point(Left, Top); dragging = false; cat.CaptureMouse(); e.Handled = true; };
            cat.MouseMove += (sender, e) => {
                if (!cat.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                var position = PointToScreen(e.GetPosition(this)); var matrix = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice;
                var delta = matrix.Transform(position - down);
                if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 5) dragging = true;
                if (dragging) { Left = initial.X + delta.X; Top = initial.Y + delta.Y; }
            };
            cat.MouseLeftButtonUp += (sender, e) => { if (!cat.IsMouseCaptured) return; cat.ReleaseMouseCapture(); ClampPosition(); Save(); if (!dragging) OpenPanel("home"); e.Handled = true; };
            var menu = new ContextMenu();
            AddMenu(menu, "添加提醒", () => OpenPanel("reminder")); AddMenu(menu, "写备忘录", () => OpenPanel("note")); AddMenu(menu, "添加任务", () => OpenPanel("task")); AddMenu(menu, "查看提醒", () => OpenPanel("reminders"));
            menu.Items.Add(new Separator());
            var sizeMenu = new MenuItem { Header = "桌宠大小" };
            foreach (var size in new[] { 72, 96, 120 }) { int chosen = size; var item = new MenuItem { Header = (size == 72 ? "小" : size == 96 ? "中（默认）" : "大") + " · " + size + " px" }; item.Click += (sender, e) => SetSize(chosen); sizeMenu.Items.Add(item); }
            menu.Items.Add(sizeMenu); AddMenu(menu, "打开完整网页", () => client.OpenBrowser()); AddMenu(menu, "收起到托盘（提醒继续）", () => Hide()); AddMenu(menu, "退出拾记（停止提醒）", async () => await ExitAll()); cat.ContextMenu = menu;
            tray = new Forms.NotifyIcon { Text = "拾记 · 点击小猫记录日常", Icon = System.Drawing.SystemIcons.Information, Visible = true };
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示桌宠", null, (sender, e) => Dispatcher.Invoke(new Action(ShowPet)));
            trayMenu.Items.Add("添加提醒", null, (sender, e) => Dispatcher.Invoke(new Action(() => OpenPanel("reminder"))));
            trayMenu.Items.Add("写备忘录", null, (sender, e) => Dispatcher.Invoke(new Action(() => OpenPanel("note"))));
            trayMenu.Items.Add("打开完整网页", null, (sender, e) => client.OpenBrowser());
            trayMenu.Items.Add("退出拾记（停止提醒）", null, (sender, e) => Dispatcher.Invoke(new Action(async () => await ExitAll())));
            tray.ContextMenuStrip = trayMenu; tray.DoubleClick += (sender, e) => Dispatcher.Invoke(new Action(() => OpenPanel("home")));
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shiji.Pet.Show." + identity);
            stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shiji.Pet.Stop." + identity);
            signals = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            signals.Tick += (sender, e) => { if (showSignal.WaitOne(0)) OpenPanel("home"); if (stopSignal.WaitOne(0)) ExitPet(); if (bubbleUntil != default(DateTime) && DateTime.UtcNow > bubbleUntil) { bubble.Visibility = Visibility.Collapsed; bubbleUntil = default(DateTime); } }; signals.Start();
            bubbleUntil = DateTime.UtcNow.AddSeconds(8);
            blink = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            blink.Tick += (sender, e) => { if (!IsVisible) return; var eyes = cat.FindName("Eyes") as FrameworkElement; if (eyes != null) { var animation = new DoubleAnimation(1, 0.12, TimeSpan.FromMilliseconds(95)) { AutoReverse = true }; ((ScaleTransform)eyes.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, animation); } }; blink.Start();
            Closing += (sender, e) => { if (!allowClose) { e.Cancel = true; Hide(); } };
            Closed += (sender, e) => { signals.Stop(); blink.Stop(); tray.Visible = false; tray.Dispose(); showSignal.Dispose(); stopSignal.Dispose(); client.Dispose(); };
            SystemParameters.StaticPropertyChanged += WorkAreaChanged;
            Loaded += (sender, e) => ClampPosition();
        }
        private void WorkAreaChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "WorkArea") ClampPosition(); }
        private void AddMenu(ContextMenu menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (sender, e) => action(); menu.Items.Add(item); }
        private void SetSize(int size) { petSize = size; cat.Width = size; cat.Height = size; Canvas.SetLeft(cat, (Width - size) / 2); Save(); }
        private void ClampPosition() { var area = SystemParameters.WorkArea; if (double.IsNaN(Left) || double.IsInfinity(Left)) Left = area.Right - Width; if (double.IsNaN(Top) || double.IsInfinity(Top)) Top = area.Bottom - Height; Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width)); Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)); }
        private void Save() { try { File.WriteAllText(Path.Combine(local, "pet.json"), json.Serialize(new { left = Left, top = Top, size = petSize }), new UTF8Encoding(false)); } catch { } }
        private void ShowPet() { Show(); ClampPosition(); Activate(); }
        public void OpenPanel(string page) {
            ShowPet();
            if (panel == null) { panel = new PanelWindow(client, this); panel.Closed += (sender, e) => panel = null; }
            var area = SystemParameters.WorkArea;
            panel.Left = Math.Max(area.Left, Math.Min(Left - panel.Width + Width, area.Right - panel.Width));
            panel.Top = Math.Max(area.Top, Math.Min(Top - panel.Height - 8, area.Bottom - panel.Height));
            panel.Show(); panel.Activate(); panel.Navigate(page);
            if (File.Exists(Path.Combine(local, "pet-test-mode"))) {
                var test = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                test.Tick += (sender, e) => {
                    test.Stop();
                    var details = new { petVisible = IsVisible, panelVisible = panel != null && panel.IsVisible, left = Left, top = Top, petSize = petSize, width = ActualWidth, height = ActualHeight };
                    File.WriteAllText(Path.Combine(local, "pet-window-test.json"), json.Serialize(details), new UTF8Encoding(false));
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(this); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(local, "pet-preview.png"))) encoder.Save(file);
                    if (panel != null) panel.SavePreview(Path.Combine(local, "pet-panel-preview.png"));
                }; test.Start();
            }
        }
        public void ShowError(string message) { OpenPanel("home"); panel.ShowMessage(message, true); }
        public void Say(string message) { bubbleText.Text = message.Length > 24 ? message.Substring(0, 24) + "…" : message; bubble.Visibility = Visibility.Visible; bubbleUntil = DateTime.UtcNow.AddSeconds(7); }
        private async Task ExitAll() {
            if (MessageBox.Show("退出后拾记后台将停止，到点不会发送提醒。确定退出？", "退出拾记", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try { await client.Call("/desktop/shutdown", "POST", new { }); ExitPet(); } catch (Exception e) { ShowError("未能停止后台：" + e.Message); }
        }
        private void ExitPet() { Save(); SystemParameters.StaticPropertyChanged -= WorkAreaChanged; if (panel != null) panel.Close(); allowClose = true; Close(); Application.Current.Shutdown(); }
        private const string CatXaml = @"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Stretch='Uniform'>
<Canvas Width='128' Height='128'>
<Ellipse Canvas.Left='20' Canvas.Top='116' Width='93' Height='8' Fill='#257160A0'/>
<Path Data='M 90,104 C 126,109 125,73 109,82 C 102,88 121,96 101,96' Stroke='#9782D5' StrokeThickness='12' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
<Path Data='M 32,72 C 24,91 28,116 43,119 L 88,119 C 105,113 104,89 94,72 Z' Fill='#BCA6EF' Stroke='#8E73C3' StrokeThickness='2'/>
<Ellipse Canvas.Left='43' Canvas.Top='86' Width='40' Height='32' Fill='#E7DCF9'/>
<Path Data='M 23,44 L 21,10 Q 22,3 29,8 L 51,25 L 80,25 L 101,8 Q 108,3 107,13 L 104,47 Z' Fill='#BCA6EF' Stroke='#8E73C3' StrokeThickness='2' StrokeLineJoin='Round'/>
<Path Data='M 28,16 L 31,38 L 45,29 Z M 99,16 L 95,37 L 83,29 Z' Fill='#E9BDD9'/>
<Ellipse Canvas.Left='19' Canvas.Top='26' Width='89' Height='64' Fill='#CDB9F3' Stroke='#8E73C3' StrokeThickness='2'/>
<Path Data='M 53,28 Q 56,39 61,40 M 66,27 Q 67,38 72,39' Stroke='#A58AD7' StrokeThickness='4' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
<Canvas x:Name='Eyes' Width='128' Height='100' RenderTransformOrigin='0.5,0.58'><Canvas.RenderTransform><ScaleTransform ScaleY='1'/></Canvas.RenderTransform><Ellipse Canvas.Left='41' Canvas.Top='50' Width='7' Height='11' Fill='#49365F'/><Ellipse Canvas.Left='80' Canvas.Top='50' Width='7' Height='11' Fill='#49365F'/><Ellipse Canvas.Left='43' Canvas.Top='51' Width='2' Height='3' Fill='White'/><Ellipse Canvas.Left='82' Canvas.Top='51' Width='2' Height='3' Fill='White'/></Canvas>
<Ellipse Canvas.Left='29' Canvas.Top='65' Width='14' Height='7' Fill='#E6A9D1' Opacity='0.7'/><Ellipse Canvas.Left='86' Canvas.Top='65' Width='14' Height='7' Fill='#E6A9D1' Opacity='0.7'/>
<Path Data='M 60,62 Q 64,60 68,62 L 64,66 Z' Fill='#9D639A'/><Path Data='M 64,66 Q 60,73 56,68 M 64,66 Q 68,73 72,68' Stroke='#76517E' StrokeThickness='1.8' Fill='Transparent' StrokeEndLineCap='Round'/>
<Path Data='M 39,82 Q 64,91 91,82 L 88,93 Q 64,101 41,93 Z' Fill='#7862BB'/><Ellipse Canvas.Left='60' Canvas.Top='88' Width='10' Height='12' Fill='#F6DDA0'/><Path Data='M 65,91 L 65,96' Stroke='#C1A05C' StrokeThickness='1.5'/>
<Ellipse Canvas.Left='31' Canvas.Top='109' Width='25' Height='12' Fill='#CDB9F3' Stroke='#8E73C3' StrokeThickness='1.5'/><Ellipse Canvas.Left='74' Canvas.Top='109' Width='25' Height='12' Fill='#CDB9F3' Stroke='#8E73C3' StrokeThickness='1.5'/>
</Canvas></Viewbox>";
    }

    internal sealed class PanelWindow : Window {
        private readonly AssistantClient client;
        private readonly PetWindow pet;
        private readonly StackPanel body;
        private readonly TextBlock heading, message;
        private readonly ScrollViewer scroll;
        private string page = "home", requestKey;
        private object pendingReminder;
        private int generation;
        private bool busy;
        public PanelWindow(AssistantClient client, PetWindow pet) {
            this.client = client; this.pet = pet;
            Title = "拾记 · 桌宠助手"; Width = Math.Min(366, SystemParameters.WorkArea.Width); Height = Math.Min(510, SystemParameters.WorkArea.Height); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = true; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            var frame = new Border { Background = Theme.Brush("#FCFAFF"), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Padding = new Thickness(22) };
            var layout = new DockPanel(); frame.Child = layout; Content = frame;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            var close = Theme.Button("×", () => Close()); close.Width = 34; close.MinHeight = 30; close.Padding = new Thickness(0); close.FontSize = 21; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            var home = Theme.Button("‹", () => Navigate("home")); home.Width = 30; home.MinHeight = 30; home.Padding = new Thickness(0); home.Margin = new Thickness(0, 0, 8, 0); home.FontSize = 22; DockPanel.SetDock(home, Dock.Left); header.Children.Add(home);
            heading = Theme.Text("拾记", 20); heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading);
            header.MouseLeftButtonDown += (sender, e) => { if (e.OriginalSource is TextBlock) DragMove(); };
            message = Theme.Text("", 12); message.Visibility = Visibility.Collapsed; message.Margin = new Thickness(0, 10, 0, 0); DockPanel.SetDock(message, Dock.Bottom); layout.Children.Add(message);
            var foot = Theme.Text("本机保存  ·  一次性 Windows 通知", 11, Theme.Muted); foot.Margin = new Thickness(0, 12, 0, 0); DockPanel.SetDock(foot, Dock.Bottom); layout.Children.Add(foot);
            body = new StackPanel(); scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; layout.Children.Add(scroll);
            KeyDown += (sender, e) => { if (e.Key == Key.Escape) Close(); };
        }
        public void ShowMessage(string value, bool error = false) { message.Text = value; message.Foreground = error ? Theme.Brush("#B64D62") : Theme.Purple; message.Visibility = Visibility.Visible; }
        public void SavePreview(string path) {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
        }
        public async void Navigate(string destination) {
            if (busy) return;
            page = destination; generation++; int version = generation; body.Children.Clear(); scroll.ScrollToTop(); message.Visibility = Visibility.Collapsed; pendingReminder = null; requestKey = Guid.NewGuid().ToString();
            try {
                switch (page) {
                    case "reminder": ReminderForm(); break;
                    case "note": NoteForm(null); break;
                    case "task": TaskForm(); break;
                    case "reminders": await Reminders(version); break;
                    case "notes": await Notes(version); break;
                    case "tasks": await Tasks(version); break;
                    default: await Home(version); break;
                }
            } catch (Exception e) { if (version == generation) ShowMessage("未能连接拾记后台：" + e.Message, true); }
        }
        private async Task Home(int version) {
            heading.Text = "拾记，陪你记住";
            body.Children.Add(Theme.Text("把小事交给我，把时间留给自己。", 13, Theme.Muted));
            body.Children.Add(Theme.Button("◷   添加提醒", () => Navigate("reminder"), true));
            body.Children.Add(Theme.Button("✎   写备忘录", () => Navigate("note")));
            body.Children.Add(Theme.Button("＋   添加任务", () => Navigate("task")));
            var row = new UniformGridCompat(3); row.Add(Theme.Button("提醒", () => Navigate("reminders"))); row.Add(Theme.Button("备忘录", () => Navigate("notes"))); row.Add(Theme.Button("任务", () => Navigate("tasks"))); body.Children.Add(row);
            var statusText = Theme.Text("正在连接后台…", 12, Theme.Muted); statusText.Margin = new Thickness(0, 14, 0, 8); body.Children.Add(statusText);
            body.Children.Add(Theme.Button("打开完整网页  ↗", () => client.OpenBrowser()));
            body.Children.Add(Theme.Text("关闭这个面板后提醒继续运行。可拖动小猫；右键调整大小或收起到托盘。", 12, Theme.Muted));
            var status = await client.Call("/notifications/status");
            if (version != generation) return;
            statusText.Text = AssistantClient.Flag(status, "ready") ? "● 后台已连接，通知通道就绪" : "● 通知通道暂不可用：" + AssistantClient.Text(status, "error");
            var reminders = await client.List("/reminders");
            if (version != generation) return;
            var next = reminders.FirstOrDefault(r => AssistantClient.Text(r, "state") == "SCHEDULED");
            if (next != null) body.Children.Add(Theme.Text("下一条：" + AssistantClient.TimeLabel(AssistantClient.Text(next, "scheduledAt")) + "\n" + AssistantClient.Text(next, "message"), 12, Theme.Purple));
        }
        private void ReminderForm() {
            heading.Text = "添加一次性提醒";
            body.Children.Add(Theme.Text("想让我在什么时候提醒你？", 13, Theme.Muted));
            body.Children.Add(Theme.Text("提醒内容")); var text = Theme.Input("ReminderMessage", 160, true); text.MinHeight = 88; body.Children.Add(text);
            body.Children.Add(Theme.Text("提醒日期与时间（本机时区）"));
            var date = new DatePicker { SelectedDate = DateTime.Now.AddMinutes(3).Date, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5), FontSize = 14 }; AutomationProperties.SetAutomationId(date, "ReminderDate"); body.Children.Add(date);
            var time = Theme.Input("ReminderTime", 8); time.Text = DateTime.Now.AddMinutes(3).ToString("HH:mm:ss"); body.Children.Add(time);
            var presets = new UniformGridCompat(3);
            foreach (int minutes in new[] { 3, 10, 30 }) { int value = minutes; presets.Add(Theme.Button(value + " 分钟后", () => { var when = DateTime.Now.AddMinutes(value); date.SelectedDate = when.Date; time.Text = when.ToString("HH:mm:ss"); })); } body.Children.Add(presets);
            body.Children.Add(Theme.Text("时间格式 14:30 或 14:30:00；确认后显示完整计划时间。", 11, Theme.Muted));
            Button save = null;
            save = Theme.Button("添加提醒", async () => {
                await Work(save, async () => {
                    if (pendingReminder == null) {
                        string content = text.Text.Trim(); if (content.Length == 0) throw new ArgumentException("请填写提醒内容。");
                        DateTime clock;
                        if (!date.SelectedDate.HasValue || !DateTime.TryParseExact(time.Text.Trim(), new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out clock)) throw new ArgumentException("请填写有效日期和时间，例如 14:30。");
                        var instant = AssistantClient.ResolveLocal(date.SelectedDate.Value.Date.Add(clock.TimeOfDay), TimeZoneInfo.Local);
                        if (instant <= DateTimeOffset.Now.AddSeconds(1)) throw new ArgumentException("请选择未来的提醒时间。");
                        var offset = instant.Offset; string timezone = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                        pendingReminder = new { title = content, message = content, scheduledAt = instant.ToString("O"), timezone = timezone, requestKey = requestKey };
                    }
                    // Keep the exact request after a timeout; retrying must not create a second reminder.
                    Dictionary<string, object> result;
                    try { result = await client.Call("/reminders/quick", "POST", pendingReminder); }
                    catch (ApiRequestException e) { if (e.Status >= 400 && e.Status < 500) { pendingReminder = null; requestKey = Guid.NewGuid().ToString(); } throw; }
                    string confirmation = AssistantClient.Text(result, "confirmation");
                    if (AssistantClient.Text(result, "state") != "SCHEDULED") { pendingReminder = null; throw new InvalidOperationException("该请求已处理，当前状态：" + StateLabel(AssistantClient.Text(result, "state"))); }
                    pendingReminder = null; requestKey = Guid.NewGuid().ToString();
                    text.Clear(); ShowMessage(confirmation); pet.Say("提醒已添加，我会准时叫你");
                });
                if (pendingReminder != null) { text.IsEnabled = false; date.IsEnabled = false; time.IsEnabled = false; save.Content = "核对并重试原提醒"; ShowMessage(message.Text + "\n结果待核对，点击重试会复用原请求。", true); }
                else { text.IsEnabled = true; date.IsEnabled = true; time.IsEnabled = true; save.Content = "添加提醒"; }
            }, true); body.Children.Add(save); text.Focus();
        }
        private void NoteForm(Dictionary<string, object> note) {
            heading.Text = note == null ? "写一条备忘录" : "编辑备忘录";
            body.Children.Clear(); body.Children.Add(Theme.Text("标题")); var title = Theme.Input("NoteTitle", 160); body.Children.Add(title);
            body.Children.Add(Theme.Text("内容")); var content = Theme.Input("NoteContent", 20000, true); content.MinHeight = 180; body.Children.Add(content);
            var pinned = new CheckBox { Content = "置顶这条备忘录", Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 16) }; body.Children.Add(pinned);
            if (note != null) { title.Text = AssistantClient.Text(note, "title"); content.Text = AssistantClient.Text(note, "content"); pinned.IsChecked = AssistantClient.Flag(note, "pinned"); }
            Button save = null; save = Theme.Button("保存备忘录", async () => await Work(save, async () => {
                if (title.Text.Trim().Length == 0) throw new ArgumentException("请填写备忘录标题。");
                await client.Call(note == null ? "/notes" : "/notes/" + AssistantClient.Text(note, "id"), note == null ? "POST" : "PUT", new { title = title.Text.Trim(), content = content.Text, color = note == null ? "LAVENDER" : AssistantClient.Text(note, "color"), pinned = pinned.IsChecked == true });
                ShowMessage("备忘录已保存。"); pet.Say("已记下，灵感不会溜走"); if (note == null) { title.Clear(); content.Clear(); }
            }), true); body.Children.Add(save); title.Focus();
        }
        private void TaskForm() {
            heading.Text = "添加任务";
            body.Children.Add(Theme.Text("任务名称")); var title = Theme.Input("TaskTitle", 160); body.Children.Add(title);
            body.Children.Add(Theme.Text("补充说明")); var description = Theme.Input("TaskDescription", 10000, true); body.Children.Add(description);
            var priority = new ComboBox { ItemsSource = new[] { "普通", "优先", "不急" }, SelectedIndex = 0, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 14) }; body.Children.Add(priority);
            body.Children.Add(Theme.Text("任务不会自动创建通知，需要通知时选择“添加提醒”。", 12, Theme.Muted));
            Button save = null; save = Theme.Button("添加任务", async () => await Work(save, async () => {
                if (title.Text.Trim().Length == 0) throw new ArgumentException("请填写任务名称。");
                await client.Call("/tasks", "POST", new { title = title.Text.Trim(), description = description.Text, priority = new[] { "NORMAL", "HIGH", "LOW" }[priority.SelectedIndex], completed = false });
                title.Clear(); description.Clear(); ShowMessage("任务已添加。"); pet.Say("小事已记下，一件件来");
            }), true); body.Children.Add(save); title.Focus();
        }
        private async Task Reminders(int version) {
            heading.Text = "我的提醒"; body.Children.Add(Theme.Text("正在读取…", 13, Theme.Muted));
            var rows = await client.List("/reminders"); if (version != generation) return; body.Children.Clear();
            body.Children.Add(Theme.Button("＋ 添加提醒", () => Navigate("reminder"), true));
            if (rows.Count == 0) body.Children.Add(Theme.Text("还没有提醒，想起什么就告诉我。", 13, Theme.Muted));
            foreach (var row in rows.OrderBy(r => AssistantClient.Text(r, "state") == "SCHEDULED" ? 0 : 1).Take(100)) {
                var card = Card(); string state = AssistantClient.Text(row, "state");
                card.Children.Add(Theme.Text(AssistantClient.Text(row, "message"), 14));
                card.Children.Add(Theme.Text(AssistantClient.TimeLabel(AssistantClient.Text(row, "scheduledAt")) + "  ·  " + StateLabel(state), 11, Theme.Muted));
                if (state == "SCHEDULED" && AssistantClient.Text(row, "channel") == "SYSTEM") {
                    Button cancel = null; cancel = Theme.Button("取消这条提醒", async () => await Work(cancel, async () => {
                        var result = await client.Call("/reminders/" + AssistantClient.Text(row, "id") + "/cancel", "POST", new { });
                        string confirmation = AssistantClient.Text(result, "confirmation");
                        card.Children.Clear(); card.Children.Add(Theme.Text(AssistantClient.Text(row, "message"))); card.Children.Add(Theme.Text(confirmation, 12, Theme.Purple)); ShowMessage(confirmation); pet.Say("提醒已关闭，到点不会再提醒");
                    })); card.Children.Add(cancel);
                }
                if (state == "FIRED" && AssistantClient.Text(row, "channel") == "SYSTEM") {
                    Button received = null; received = Theme.Button("我已收到", async () => await Work(received, async () => { await client.Call("/reminders/" + AssistantClient.Text(row, "id") + "/events", "POST", new { state = "RECEIVED", detail = "用户在桌宠面板确认收到通知" }); received.Content = "已确认收到"; received.Visibility = Visibility.Collapsed; ShowMessage("已记录收到通知。"); })); card.Children.Add(received);
                }
                if (state == "DELIVERY_FAILED") card.Children.Add(Theme.Text(AssistantClient.Text(row, "detail"), 11, Theme.Muted));
            }
        }
        private async Task Notes(int version) {
            heading.Text = "我的备忘录"; var rows = await client.List("/notes"); if (version != generation) return;
            body.Children.Add(Theme.Button("＋ 写备忘录", () => Navigate("note"), true));
            if (rows.Count == 0) body.Children.Add(Theme.Text("这里还空着，记下第一条灵感吧。", 13, Theme.Muted));
            foreach (var row in rows.Take(100)) {
                var card = Card(); card.Children.Add(Theme.Text((AssistantClient.Flag(row, "pinned") ? "★ " : "") + AssistantClient.Text(row, "title")));
                var preview = Theme.Text(AssistantClient.Text(row, "content"), 12, Theme.Muted); preview.MaxHeight = 88; preview.TextTrimming = TextTrimming.CharacterEllipsis; card.Children.Add(preview);
                card.Children.Add(Theme.Button("查看 / 编辑", () => { if (busy) return; generation++; page = "note"; message.Visibility = Visibility.Collapsed; NoteForm(row); }));
            }
        }
        private async Task Tasks(int version) {
            heading.Text = "我的任务"; var rows = await client.List("/tasks"); if (version != generation) return;
            body.Children.Add(Theme.Button("＋ 添加任务", () => Navigate("task"), true));
            if (rows.Count == 0) body.Children.Add(Theme.Text("暂时没有任务，享受一下空闲。", 13, Theme.Muted));
            foreach (var row in rows.OrderBy(r => AssistantClient.Flag(r, "completed")).Take(100)) {
                var card = Card(); bool completed = AssistantClient.Flag(row, "completed"); card.Children.Add(Theme.Text((completed ? "✓ " : "○ ") + AssistantClient.Text(row, "title")));
                Button toggle = null; toggle = Theme.Button(completed ? "恢复任务" : "标记完成", async () => await Work(toggle, async () => {
                    object due; row.TryGetValue("dueAt", out due);
                    var result = await client.Call("/tasks/" + AssistantClient.Text(row, "id"), "PUT", new { title = AssistantClient.Text(row, "title"), description = AssistantClient.Text(row, "description"), priority = AssistantClient.Text(row, "priority"), dueAt = due, completed = !completed });
                    completed = AssistantClient.Flag(result, "completed"); toggle.Content = completed ? "恢复任务" : "标记完成"; ((TextBlock)card.Children[0]).Text = (completed ? "✓ " : "○ ") + AssistantClient.Text(row, "title"); ShowMessage(completed ? "任务已完成。" : "任务已恢复。");
                })); card.Children.Add(toggle);
            }
        }
        private StackPanel Card() { var content = new StackPanel(); body.Children.Add(new Border { Child = content, Background = Brushes.White, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 8) }); return content; }
        private async Task Work(Button source, Func<Task> operation) {
            if (busy) return; busy = true; source.IsEnabled = false; var old = source.Content; source.Content = "处理中…";
            try { await operation(); }
            catch (HttpRequestException) { ShowMessage("后台连接中断，请检查拾记是否仍在运行。", true); }
            catch (TaskCanceledException) { ShowMessage("请求超时，结果待核对。", true); }
            catch (Exception e) { ShowMessage(e.Message, true); }
            finally { busy = false; source.IsEnabled = true; if (Equals(source.Content, "处理中…")) source.Content = old; }
        }
        private static string StateLabel(string value) {
            switch (value) { case "SCHEDULED": return "已安排"; case "CANCELLED": return "已取消"; case "FIRED": return "Windows 已接受通知"; case "RECEIVED": return "已确认收到"; case "DELIVERY_FAILED": return "发送待处理"; case "DISPATCHING": return "正在发送"; case "PENDING": return "待处理"; case "FAILED": return "创建失败"; case "PAUSED": return "已暂停"; default: return value; }
        }
    }
    internal sealed class UniformGridCompat : Grid {
        private int next;
        public UniformGridCompat(int columns) { for (int i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition()); Margin = new Thickness(0, 2, -6, 2); }
        public void Add(Button button) { Grid.SetColumn(button, next++); button.Margin = new Thickness(0, 0, 6, 8); button.Padding = new Thickness(4, 7, 4, 7); button.FontSize = 12; Children.Add(button); }
    }
}
