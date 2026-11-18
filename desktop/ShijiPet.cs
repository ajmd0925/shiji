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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                    if (args.Contains("--ui-test")) {
                        if (!File.Exists(Path.Combine(root, ".local", "pet-test-mode"))) throw new InvalidOperationException("UI test requires an isolated installation.");
                        pet.Loaded += async (sender, e) => await pet.RunUiTest();
                    }
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
                var notificationStatus = await client.Call("/notifications/status");
                if (AssistantClient.Flag(notificationStatus, "ready")) {
                var reminder = await client.Call("/reminders/quick", "POST", input);
                reminderId = AssistantClient.Text(reminder, "id"); taskId = AssistantClient.Text(reminder, "taskId");
                Require(AssistantClient.Text(reminder, "state") == "SCHEDULED" && AssistantClient.Text(reminder, "confirmation").Contains("添加成功"), "one-step reminder with actual confirmation"); results.Add("one-step reminder with actual confirmation");
                var retry = await client.Call("/reminders/quick", "POST", input);
                Require(AssistantClient.Text(retry, "id") == reminderId, "safe retry reuses reminder"); results.Add("safe retry reuses reminder");
                var cancelled = await client.Call("/reminders/" + reminderId + "/cancel", "POST", new { });
                Require(AssistantClient.Text(cancelled, "state") == "CANCELLED" && AssistantClient.Text(cancelled, "confirmation").Contains("不会再提醒"), "immediate cancellation confirmation"); results.Add("immediate cancellation confirmation");
                } else {
                    results.Add("notification channel unavailable on this host (not claimed as delivery verification)");
                    var before = await client.List("/tasks");
                    try { await client.Call("/reminders/quick", "POST", input); throw new Exception("Unavailable notification channel incorrectly accepted reminder"); }
                    catch (ApiRequestException e) { Require(e.Status == 503, "unavailable channel returns explicit failure"); results.Add("unavailable channel returns explicit failure"); }
                    Require((await client.List("/tasks")).Count == before.Count, "failed reminder leaves no orphan task"); results.Add("failed reminder leaves no orphan task");
                }
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
            var button = new Button { Content = value, MinHeight = 32, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 8), Background = primary ? Purple : Brush("#F2EDF9"), Foreground = primary ? Brushes.White : Ink, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='12' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.82'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
            button.Click += (sender, e) => action(); return button;
        }
        public static TextBox Input(string id, int maxLength, bool multiline = false) {
            var input = new TextBox { MaxLength = maxLength, FontSize = 14, Foreground = Ink, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10), MinHeight = multiline ? 100 : 34, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            AutomationProperties.SetAutomationId(input, id); return input;
        }
    }

    internal sealed class PetWindow : Window {
        private readonly AssistantClient client;
        private readonly string local;
        private readonly EventWaitHandle showSignal, stopSignal;
        private readonly DispatcherTimer signals;
        private readonly Forms.NotifyIcon tray;
        private readonly Border bubble;
        private readonly FrameworkElement cat;
        private readonly TextBlock bubbleText;
        private Image skinImage;
        private BitmapSource originalSkin;
        private readonly Dictionary<int, BitmapSource> crispSkinCache = new Dictionary<int, BitmapSource>();
        private FrameworkElement skinLighting;
        private readonly System.Windows.Shapes.Ellipse groundShadow;
        private readonly Border shadowReaction;
        private readonly ScaleTransform shadowBreath = new ScaleTransform(1, 1);
        private readonly ScaleTransform shadowJump = new ScaleTransform(1, 1);
        private PanelWindow panel;
        private readonly ContextMenu menu;
        private readonly ContextMenu trayMenu;
        private readonly System.Drawing.Icon trayIcon;
        private MenuItem trayShow, trayHide, trayMotion, trayExit;
        private readonly Dictionary<int, Button> sizeButtons = new Dictionary<int, Button>();
        private Point down, initial;
        private bool dragging, allowClose;
        private const int DefaultPetSize = 144;
        private double petSize = DefaultPetSize;
        private bool motionEnabled = true;
        private readonly ScaleTransform breath = new ScaleTransform(1, 1);
        private readonly TranslateTransform idleOffset = new TranslateTransform();
        private readonly TranslateTransform reactionOffset = new TranslateTransform();
        private readonly RotateTransform tilt = new RotateTransform();
        private DateTime bubbleUntil;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public PetWindow(AssistantClient client, string root, string identity) {
            this.client = client; local = Path.Combine(root, ".local");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Title = "拾记桌宠"; Width = 148; Height = 174; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; FontFamily = new FontFamily("Microsoft YaHei UI");
            var area = SystemParameters.WorkArea; Left = area.Right - Width - 26; Top = area.Bottom - Height - 16;
            try {
                var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(local, "pet.json")));
                petSize = ReadPetSize(saved);
                object savedMotion; if (saved.TryGetValue("motion", out savedMotion) && savedMotion is bool) motionEnabled = (bool)savedMotion;
                Left = Convert.ToDouble(saved["left"], CultureInfo.InvariantCulture); Top = Convert.ToDouble(saved["top"], CultureInfo.InvariantCulture);
                if (!double.IsNaN(Left) && !double.IsInfinity(Left)) ClampPosition(); else { Left = area.Right - Width; Top = area.Bottom - Height; }
            } catch { }
            var canvas = new Canvas(); Content = canvas;
            bubbleText = Theme.Text("右键我，记下小事", 12, Theme.Purple); bubbleText.Margin = new Thickness(0);
            bubble = new Border { Background = Theme.Brush("#FCFAFF"), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 7, 10, 7), Width = 144, Child = bubbleText, IsHitTestVisible = false };
            Canvas.SetTop(bubble, 0); Canvas.SetLeft(bubble, 2); canvas.Children.Add(bubble);
            groundShadow = new System.Windows.Shapes.Ellipse {
                IsHitTestVisible = false, Opacity = 0.8,
                Fill = new RadialGradientBrush {
                    GradientStops = new GradientStopCollection {
                        new GradientStop(Color.FromArgb(105, 75, 54, 106), 0),
                        new GradientStop(Color.FromArgb(42, 97, 73, 128), 0.52),
                        new GradientStop(Color.FromArgb(0, 110, 86, 141), 1)
                    }
                }, RenderTransform = shadowBreath, RenderTransformOrigin = new Point(0.5, 0.5)
            };
            shadowReaction = new Border { Child = groundShadow, IsHitTestVisible = false,
                RenderTransform = shadowJump, RenderTransformOrigin = new Point(0.5, 0.5) };
            canvas.Children.Add(shadowReaction);
            cat = CreatePetVisual();
            var transforms = new TransformGroup(); transforms.Children.Add(breath); transforms.Children.Add(tilt); transforms.Children.Add(idleOffset); transforms.Children.Add(reactionOffset);
            cat.RenderTransform = transforms; cat.RenderTransformOrigin = new Point(0.5, 0.9);
            cat.MouseEnter += (sender, e) => { if (CanAnimate()) tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(tilt.Angle, -1.5, TimeSpan.FromMilliseconds(180))); };
            cat.MouseLeave += (sender, e) => tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(tilt.Angle, 0, TimeSpan.FromMilliseconds(180)));
            cat.Width = petSize; cat.Height = petSize; cat.Cursor = Cursors.Hand; cat.ToolTip = "右键打开拾记菜单 · 按住拖动";
            AutomationProperties.SetName(cat, "拾记龙娘，右键打开菜单，按住拖动"); AutomationProperties.SetAutomationId(cat, "ShijiPet");
            cat.Focusable = true; cat.KeyDown += (sender, e) => { if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Apps) { OpenMenu(); e.Handled = true; } };
            Canvas.SetTop(cat, 50); Canvas.SetLeft(cat, (Width - petSize) / 2); canvas.Children.Add(cat); ApplySize();
            cat.MouseLeftButtonDown += (sender, e) => { down = PointToScreen(e.GetPosition(this)); initial = new Point(Left, Top); dragging = false; StopMotion(); cat.CaptureMouse(); e.Handled = true; };
            cat.MouseMove += (sender, e) => {
                if (!cat.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                var position = PointToScreen(e.GetPosition(this)); var matrix = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice;
                var delta = matrix.Transform(position - down);
                if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 5) dragging = true;
                if (dragging) { Left = initial.X + delta.X; Top = initial.Y + delta.Y; }
            };
            cat.MouseLeftButtonUp += (sender, e) => { if (!cat.IsMouseCaptured) return; cat.ReleaseMouseCapture(); ClampPosition(); Save(); StartMotion(); if (!dragging) { Bounce(); Say("右键我，可以提醒、记事和看任务"); } e.Handled = true; };
            menu = CreateRoundedMenu(204);
            AddMenu("◷", "定时提醒", () => OpenPanel("reminder"));
            AddMenu("✎", "备忘录", () => OpenPanel("notes"));
            AddMenu("☷", "任务表", () => OpenPanel("tasks"));
            AddMenu("↗", "打开完整网页", () => client.OpenBrowser());
            cat.ContextMenu = menu;
            menu.Opened += (sender, e) => { StopMotion(); RecordVisual(); }; menu.Closed += (sender, e) => StartMotion();
            using (var stream = typeof(PetWindow).Assembly.GetManifestResourceStream("Shiji.Desktop.Icon.ico")) {
                if (stream == null) throw new InvalidOperationException("拾记图标缺失，请重新下载完整程序。");
                using (var icon = new System.Drawing.Icon(stream)) trayIcon = (System.Drawing.Icon)icon.Clone();
            }
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            tray = new Forms.NotifyIcon { Text = "拾记 · 点击打开菜单", Icon = trayIcon, Visible = true };
            trayMenu = CreateRoundedMenu(236);
            var heading = new MenuItem { Header = "拾记 · 个人助手", IsEnabled = false, FontSize = 12, Foreground = Theme.Muted };
            trayMenu.Items.Add(heading);
            trayShow = AddTrayItem("☀", "显示桌宠", ShowPet);
            trayHide = AddTrayItem("☾", "隐藏桌宠（提醒继续）", HidePet);
            AddTraySeparator();
            AddTrayItem("◷", "定时提醒", () => OpenPanel("reminder"));
            AddTrayItem("✎", "备忘录", () => OpenPanel("notes"));
            AddTrayItem("☷", "任务表", () => OpenPanel("tasks"));
            AddTrayItem("↗", "打开完整网页", () => client.OpenBrowser());
            AddTraySeparator();
            trayMenu.Items.Add(new MenuItem { Header = "桌宠大小", IsEnabled = false, FontSize = 12, Foreground = Theme.Muted });
            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (int size in new[] { 72, 96, 120, 144, 160 }) {
                int chosen = size;
                var button = Theme.Button(size.ToString(), () => { SetSize(chosen); RefreshTrayMenu(); trayMenu.IsOpen = false; });
                button.Width = 37; button.MinHeight = 30; button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 4, 0); button.FontSize = 12;
                button.ToolTip = size == 144 ? "标准（默认） · 144 px" : size + " px";
                sizeButtons[size] = button; sizeRow.Children.Add(button);
            }
            trayMenu.Items.Add(new MenuItem { Header = sizeRow, StaysOpenOnClick = true, Padding = new Thickness(7, 3, 3, 6) });
            trayMotion = AddTrayItem("≈", "", () => { motionEnabled = !motionEnabled; Save(); if (motionEnabled) StartMotion(); else StopMotion(); });
            AddTraySeparator();
            trayExit = AddTrayItem("⏻", "退出拾记（停止提醒）", async () => await ExitAll());
            trayExit.Foreground = Theme.Brush("#A75073");
            trayMenu.Opened += (sender, e) => { StopMotion(); RefreshTrayMenu(); };
            trayMenu.Closed += (sender, e) => StartMotion();
            tray.MouseUp += (sender, e) => { if (e.Button == Forms.MouseButtons.Left || e.Button == Forms.MouseButtons.Right) Dispatcher.Invoke(new Action(OpenTrayMenu)); };
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shiji.Pet.Show." + identity);
            stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shiji.Pet.Stop." + identity);
            signals = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            signals.Tick += (sender, e) => { if (showSignal.WaitOne(0)) OpenMenu(); if (stopSignal.WaitOne(0)) ExitPet(); if (bubbleUntil != default(DateTime) && DateTime.UtcNow > bubbleUntil) { bubble.Visibility = Visibility.Collapsed; bubbleUntil = default(DateTime); } }; signals.Start();
            bubbleUntil = DateTime.UtcNow.AddSeconds(8);
            Closing += (sender, e) => { if (!allowClose) { e.Cancel = true; HidePet(); } };
            Closed += (sender, e) => { signals.Stop(); StopMotion(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); showSignal.Dispose(); stopSignal.Dispose(); client.Dispose(); };
            SystemParameters.StaticPropertyChanged += WorkAreaChanged;
            Loaded += (sender, e) => { UpdateSkinRaster(); ClampPosition(); StartMotion(); };
            IsVisibleChanged += (sender, e) => { if (IsVisible) StartMotion(); else StopMotion(); };
        }
        private static ContextMenu CreateRoundedMenu(int width) {
            return (ContextMenu)XamlReader.Parse(@"<ContextMenu xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='" + width + @"' Padding='6' Background='#FCFAFF' Foreground='#343044' BorderBrush='#EAE5F4' BorderThickness='1' FontFamily='Microsoft YaHei UI' FontSize='13' HasDropShadow='False'>
<ContextMenu.Template><ControlTemplate TargetType='ContextMenu'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='14' Padding='{TemplateBinding Padding}'><StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/></Border></ControlTemplate></ContextMenu.Template>
<ContextMenu.Resources><Style TargetType='MenuItem'><Setter Property='Padding' Value='10,9'/><Setter Property='Margin' Value='0,1'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'><Border x:Name='Row' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='9' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter ContentSource='Header'/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#EDE5FA'/><Setter Property='Foreground' Value='#7660C9'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style></ContextMenu.Resources></ContextMenu>");
        }
        private static StackPanel TrayLabel(string glyph, string label) {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Theme.Text(glyph, 16, Theme.Purple); icon.Width = 27; icon.Margin = new Thickness(0); row.Children.Add(icon);
            var text = Theme.Text(label, 13); text.Margin = new Thickness(0); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
            return row;
        }
        private MenuItem AddTrayItem(string glyph, string label, Action action) {
            var item = new MenuItem { Header = TrayLabel(glyph, label) };
            AutomationProperties.SetName(item, label);
            item.Click += (sender, e) => { trayMenu.IsOpen = false; action(); };
            trayMenu.Items.Add(item); return item;
        }
        private void AddTraySeparator() {
            trayMenu.Items.Add(new Separator { Margin = new Thickness(9, 3, 9, 3), Background = Theme.Line, Height = 1 });
        }
        private void RefreshTrayMenu() {
            trayShow.IsEnabled = !IsVisible; trayHide.IsEnabled = IsVisible;
            trayShow.Opacity = trayShow.IsEnabled ? 1 : 0.45; trayHide.Opacity = trayHide.IsEnabled ? 1 : 0.45;
            string label = "轻动作 · " + (motionEnabled ? "已开启" : "已关闭");
            trayMotion.Header = TrayLabel("≈", label); AutomationProperties.SetName(trayMotion, label);
            foreach (var pair in sizeButtons) {
                pair.Value.Background = pair.Key == (int)petSize ? Theme.Purple : Theme.Brush("#F2EDF9");
                pair.Value.Foreground = pair.Key == (int)petSize ? Brushes.White : Theme.Purple;
            }
        }
        private void OpenTrayMenu() {
            menu.IsOpen = false; RefreshTrayMenu();
            trayMenu.PlacementTarget = null; trayMenu.Placement = PlacementMode.MousePoint; trayMenu.IsOpen = true;
        }
        private void HidePet() {
            menu.IsOpen = false; trayMenu.IsOpen = false;
            if (panel != null) panel.Close();
            Hide();
        }
        private void WorkAreaChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "WorkArea") ClampPosition(); }
        private void AddMenu(string icon, string label, Action action) {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = Theme.Text(icon, 17, Theme.Purple); glyph.Width = 28; glyph.Margin = new Thickness(0); row.Children.Add(glyph);
            var text = Theme.Text(label, 13); text.Margin = new Thickness(0); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
            var item = new MenuItem { Header = row }; AutomationProperties.SetName(item, label); item.Click += (sender, e) => action(); menu.Items.Add(item);
        }
        public void OpenMenu() { ShowPet(); menu.PlacementTarget = cat; menu.Placement = PlacementMode.Top; menu.IsOpen = true; }
        private void RecordVisual() {
            if (!File.Exists(Path.Combine(local, "pet-test-mode"))) return;
            var test = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            test.Tick += (sender, e) => {
                test.Stop();
                var details = new { petVisible = IsVisible, menuVisible = menu.IsOpen, menuItems = menu.Items.Count, menuWidth = menu.ActualWidth, menuHeight = menu.ActualHeight, panelVisible = panel != null && panel.IsVisible, panelWidth = panel == null ? 0 : panel.ActualWidth, panelHeight = panel == null ? 0 : panel.ActualHeight, left = Left, top = Top, petSize = petSize, width = ActualWidth, height = ActualHeight };
                File.WriteAllText(Path.Combine(local, "pet-window-test.json"), json.Serialize(details), new UTF8Encoding(false));
                SaveVisual(this, Path.Combine(local, "pet-preview.png"));
                if (menu.IsOpen) SaveVisual(menu, Path.Combine(local, "pet-menu-preview.png"));
                if (panel != null && panel.IsVisible) panel.SavePreview(Path.Combine(local, "pet-panel-preview.png"));
            }; test.Start();
        }
        private static void SaveVisual(FrameworkElement view, string path) {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
        private void ApplySize() {
            Width = Math.Max(148, petSize + 24); Height = 50 + petSize + 20;
            cat.Width = petSize; cat.Height = petSize; Canvas.SetLeft(cat, (Width - petSize) / 2);
            Canvas.SetLeft(bubble, (Width - bubble.Width) / 2);
            shadowReaction.Width = petSize * 0.64; shadowReaction.Height = petSize * 0.105;
            Canvas.SetLeft(shadowReaction, (Width - shadowReaction.Width) / 2 + petSize * 0.015);
            Canvas.SetTop(shadowReaction, 50 + petSize * 0.94);
            UpdateSkinRaster();
        }
        private static int ReadPetSize(IDictionary<string, object> saved) {
            object value;
            if (!saved.TryGetValue("size", out value)) return DefaultPetSize;
            double size = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            // Upgrade the old standard once; keep an explicitly chosen 120 afterwards.
            if (size == 120 && !saved.ContainsKey("sizeVersion")) return DefaultPetSize;
            return size == 72 || size == 96 || size == 120 || size == 144 || size == 160 ? (int)size : DefaultPetSize;
        }
        private void SetSize(int size) { petSize = size; ApplySize(); ClampPosition(); Save(); }
        private bool CanAnimate() { return motionEnabled && SystemParameters.ClientAreaAnimation && IsVisible && !cat.IsMouseCaptured && (menu == null || !menu.IsOpen) && (trayMenu == null || !trayMenu.IsOpen); }
        private void StartMotion() {
            if (!CanAnimate()) return;
            breath.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.012, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            idleOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -2, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            shadowBreath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.94, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            groundShadow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.8, 0.7, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        private void StopMotion() {
            shadowBreath.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            shadowJump.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            shadowJump.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            groundShadow.BeginAnimation(UIElement.OpacityProperty, null);
            shadowReaction.BeginAnimation(UIElement.OpacityProperty, null);
            breath.BeginAnimation(ScaleTransform.ScaleYProperty, null); idleOffset.BeginAnimation(TranslateTransform.YProperty, null);
            reactionOffset.BeginAnimation(TranslateTransform.YProperty, null); tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        }
        private void Bounce() {
            if (!CanAnimate()) return;
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(460), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(-8, KeyTime.FromPercent(0.4), new KeySpline(0.2, 0.7, 0.4, 1)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.5, 0, 0.8, 0.5)));
            reactionOffset.BeginAnimation(TranslateTransform.YProperty, animation);
            shadowJump.BeginAnimation(ScaleTransform.ScaleXProperty, ShadowBounce(1, 0.76));
            shadowJump.BeginAnimation(ScaleTransform.ScaleYProperty, ShadowBounce(1, 0.82));
            shadowReaction.BeginAnimation(UIElement.OpacityProperty, ShadowBounce(1, 0.58));
        }
        private static DoubleAnimationUsingKeyFrames ShadowBounce(double resting, double peak) {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(460), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(resting, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(peak, KeyTime.FromPercent(0.4), new KeySpline(0.2, 0.7, 0.4, 1)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(resting, KeyTime.FromPercent(1), new KeySpline(0.5, 0, 0.8, 0.5)));
            return animation;
        }
        private void ClampPosition() { var area = SystemParameters.WorkArea; if (double.IsNaN(Left) || double.IsInfinity(Left)) Left = area.Right - Width; if (double.IsNaN(Top) || double.IsInfinity(Top)) Top = area.Bottom - Height; Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width)); Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)); }
        private void Save() { try { File.WriteAllText(Path.Combine(local, "pet.json"), json.Serialize(new { left = Left, top = Top, size = petSize, sizeVersion = 1, motion = motionEnabled }), new UTF8Encoding(false)); } catch { } }
        private void ShowPet() { Show(); ClampPosition(); Activate(); }
        public void OpenPanel(string page) {
            menu.IsOpen = false; ShowPet();
            if (panel == null) { panel = new PanelWindow(client, this); panel.Closed += (sender, e) => panel = null; }
            var area = SystemParameters.WorkArea;
            panel.Left = Math.Max(area.Left, Math.Min(Left - panel.Width + Width, area.Right - panel.Width));
            panel.Top = Math.Max(area.Top, Math.Min(Top - panel.Height - 8, area.Bottom - panel.Height));
            panel.Show(); panel.Activate(); panel.Navigate(page);
            RecordVisual();
        }
        public async Task RunUiTest() {
            var checks = new List<string>();
            try {
                if (ReadPetSize(new Dictionary<string, object> { { "size", 120 } }) != DefaultPetSize ||
                    ReadPetSize(new Dictionary<string, object> { { "size", 120 }, { "sizeVersion", 1 } }) != 120 ||
                    ReadPetSize(new Dictionary<string, object> { { "size", 96 } }) != 96 ||
                    ReadPetSize(new Dictionary<string, object> { { "size", 160 } }) != 160 ||
                    ReadPetSize(new Dictionary<string, object> { { "size", 999 } }) != DefaultPetSize)
                    throw new Exception("Size upgrade or saved preference failed");
                var originalSize = petSize;
                SetSize(160); UpdateLayout();
                if (Width < 184 || Height < 230 || cat.ActualWidth > Width) throw new Exception("Large skin is clipped");
                SaveVisual(this, Path.Combine(local, "pet-large-preview.png"));
                SetSize((int)originalSize); UpdateLayout();
                StartMotion(); await Task.Delay(500);
                if (CanAnimate() && (!breath.HasAnimatedProperties || !idleOffset.HasAnimatedProperties)) throw new Exception("Idle motion did not start");
                var motionFrames = new List<object>();
                for (int frame = 0; frame < 10; frame++) {
                    SaveVisual(this, Path.Combine(local, "pet-motion-" + frame.ToString("D2") + ".png"));
                    motionFrames.Add(new { frame = frame, scaleY = breath.ScaleY, offsetY = idleOffset.Y, shadowWidth = shadowBreath.ScaleX, shadowOpacity = groundShadow.Opacity });
                    await Task.Delay(160);
                }
                File.WriteAllText(Path.Combine(local, "pet-motion-test.json"), json.Serialize(new { systemAnimations = SystemParameters.ClientAreaAnimation, samples = motionFrames }), new UTF8Encoding(false));
                Bounce(); await Task.Delay(120);
                if (CanAnimate() && (!reactionOffset.HasAnimatedProperties || !shadowJump.HasAnimatedProperties || shadowJump.ScaleX >= 1 || shadowReaction.Opacity >= 1)) throw new Exception("Click reaction did not start");
                SaveVisual(this, Path.Combine(local, "pet-bounce-preview.png"));
                motionEnabled = false; StopMotion();
                if (breath.HasAnimatedProperties || idleOffset.HasAnimatedProperties || reactionOffset.HasAnimatedProperties || shadowBreath.HasAnimatedProperties || shadowJump.HasAnimatedProperties || groundShadow.HasAnimatedProperties || shadowReaction.HasAnimatedProperties) throw new Exception("Motion toggle failed");
                foreach (int pixels in new[] { 72, 96, 120, 150, 160, 180, 200, 240, 320 }) {
                    var crisp = CrispSkin(pixels);
                    if (crisp.PixelWidth != pixels || crisp.PixelHeight != pixels || !crisp.IsFrozen) throw new Exception("Crisp raster size failed");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crisp));
                    using (var file = File.Create(Path.Combine(local, "pet-crisp-" + pixels + ".png"))) encoder.Save(file);
                }
                // Static depth remains visible with animations disabled, at every supported size.
                foreach (int size in new[] { 72, 96, 120, 144, 160, 180 }) {
                    SetSize(size); UpdateLayout();
                    if (Canvas.GetTop(shadowReaction) + shadowReaction.ActualHeight > Height || shadowReaction.IsHitTestVisible || groundShadow.IsHitTestVisible) throw new Exception("Ground shadow layout or pointer handling failed");
                    skinLighting.Visibility = Visibility.Collapsed; skinImage.Effect = null; shadowReaction.Visibility = Visibility.Collapsed;
                    SaveVisual(this, Path.Combine(local, "pet-depth-before-" + size + ".png"));
                    skinLighting.Visibility = Visibility.Visible; skinImage.Effect = null; shadowReaction.Visibility = Visibility.Visible;
                    SaveVisual(this, Path.Combine(local, "pet-depth-after-" + size + ".png"));
                }
                SetSize((int)originalSize); UpdateLayout();
                motionEnabled = true; StartMotion();
                OpenMenu(); await Task.Delay(900);
                if (breath.HasAnimatedProperties || idleOffset.HasAnimatedProperties) throw new Exception("Motion did not pause for menu");
                if (skinImage == null || skinImage.Source == null || !menu.IsOpen || menu.Items.Count != 4 || menu.ActualWidth > 210) throw new Exception("Compact menu failed");
                checks.Add("dragon skin, final-size sharp rasters, five presets and size upgrade, light and ground shadow, synchronized idle and bounce, stop toggle and four-option menu pass");
                string[] destinations = { "reminder", "notes", "tasks" };
                for (int i = 0; i < destinations.Length; i++) {
                    ((MenuItem)menu.Items[i]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Task.Delay(1000);
                    if (menu.IsOpen || panel == null || !panel.IsVisible || panel.CurrentPage != destinations[i] || panel.ActualWidth > 305 || panel.ActualHeight > 443) throw new Exception("Menu route failed: " + destinations[i]);
                    panel.SavePreview(Path.Combine(local, "pet-" + destinations[i] + "-preview.png"));
                    checks.Add(destinations[i] + " menu route opens compact native window");
                    panel.Close(); OpenMenu(); await Task.Delay(200);
                }
                // Exercise form navigation used by add/list/back controls.
                foreach (string destination in new[] { "reminders", "note", "task" }) {
                    OpenPanel(destination); await Task.Delay(700);
                    if (panel.CurrentPage != destination || !panel.IsVisible) throw new Exception("Form failed: " + destination);
                    panel.SavePreview(Path.Combine(local, "pet-" + destination + "-preview.png"));
                    checks.Add(destination + " native form renders");
                }
                panel.Close(); OpenMenu(); await Task.Delay(200); menu.IsOpen = false;
                if (menu.IsOpen || panel != null) throw new Exception("Dismissal failed");
                checks.Add("menu and operation window dismiss without stopping backend");
                OpenTrayMenu(); await Task.Delay(250);
                if (!trayMenu.IsOpen || trayMenu.ActualWidth > 240 || trayIcon == null || Icon == null || sizeButtons.Count != 5 || !trayHide.IsEnabled) throw new Exception("Tray menu or icon failed");
                SaveVisual(trayMenu, Path.Combine(local, "pet-tray-menu-preview.png"));
                foreach (int selectedSize in new[] { 72, 96, 120, 144, 160, 160 }) {
                    if (!trayMenu.IsOpen) { OpenTrayMenu(); await Task.Delay(100); }
                    sizeButtons[selectedSize].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                    if (petSize != selectedSize || trayMenu.IsOpen) throw new Exception("Tray size selection did not apply and dismiss: " + selectedSize);
                }
                SetSize((int)originalSize); OpenTrayMenu(); await Task.Delay(150);
                trayHide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(150);
                if (IsVisible || trayMenu.IsOpen) throw new Exception("Hide tray action failed");
                if (AssistantClient.Text(await client.Call("/desktop/status"), "application") != "shiji") throw new Exception("Hide stopped the background service");
                OpenTrayMenu(); await Task.Delay(200);
                if (!trayMenu.IsOpen || !trayShow.IsEnabled || trayHide.IsEnabled) throw new Exception("Hidden tray menu failed");
                trayShow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(150);
                if (!IsVisible) throw new Exception("Show tray action failed");
                checks.Add("rounded tray menu, custom icon, five size choices dismiss menu including repeated selection, and hide/show preserve backend");
                File.WriteAllText(Path.Combine(local, "pet-ui-test.json"), json.Serialize(new { passed = checks.Count, checks = checks }), new UTF8Encoding(false));
            } catch (Exception e) { File.WriteAllText(Path.Combine(local, "pet-ui-error.log"), e.ToString()); }
            finally { ExitPet(); }
        }
        public void ShowError(string message) { OpenPanel("reminders"); panel.ShowMessage(message, true); }
        public void Say(string message) { bubbleText.Text = message.Length > 24 ? message.Substring(0, 24) + "…" : message; bubble.Visibility = Visibility.Visible; bubbleUntil = DateTime.UtcNow.AddSeconds(7); }
        private async Task ExitAll() {
            if (MessageBox.Show("退出后拾记后台将停止，到点不会发送提醒。确定退出？", "退出拾记", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try { await client.Call("/desktop/shutdown", "POST", new { }); ExitPet(); } catch (Exception e) { ShowError("未能停止后台：" + e.Message); }
        }
        private void ExitPet() { trayMenu.IsOpen = false; menu.IsOpen = false; Save(); SystemParameters.StaticPropertyChanged -= WorkAreaChanged; if (panel != null) panel.Close(); allowClose = true; Close(); Application.Current.Shutdown(); }
        private void UpdateSkinRaster() {
            if (originalSkin == null || skinImage == null) return;
            var source = PresentationSource.FromVisual(this);
            var dpiScale = source == null || source.CompositionTarget == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            skinImage.Source = CrispSkin((int)Math.Round(petSize * dpiScale));
        }
        private BitmapSource crispAtlas;
        private BitmapSource CrispSkin(int pixels) {
            pixels = Math.Max(48, Math.Min(1024, pixels));
            BitmapSource cached; if (crispSkinCache.TryGetValue(pixels, out cached)) return cached;
            if (crispAtlas == null) {
                var atlas = new BitmapImage();
                using (var stream = typeof(PetWindow).Assembly.GetManifestResourceStream("Shiji.Desktop.DragonGirl.Crisp.png")) {
                    if (stream == null) throw new InvalidOperationException("清晰皮肤资源缺失，请重新下载完整程序。");
                    atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad; atlas.StreamSource = stream; atlas.EndInit();
                }
                atlas.Freeze(); crispAtlas = atlas;
            }
            int[] sizes = { 72,90,96,108,120,126,144,150,160,162,168,180,192,200,210,216,240,252,270,280,288,300,320,336,360,384,400,420,480,560,640 };
            int chosen = sizes.FirstOrDefault(size => size >= pixels); if (chosen == 0) chosen = sizes[sizes.Length - 1];
            int x = 0, y = 0, row = 0;
            foreach (int size in sizes) {
                if (x + size > crispAtlas.PixelWidth) { x = 0; y += row; row = 0; }
                if (size == chosen) break;
                x += size; row = Math.Max(row, size);
            }
            BitmapSource result = new CroppedBitmap(crispAtlas, new Int32Rect(x, y, chosen, chosen)); result.Freeze();
            if (chosen != pixels) { result = new TransformedBitmap(result, new ScaleTransform((double)pixels / chosen, (double)pixels / chosen)); result.Freeze(); }
            crispSkinCache[pixels] = result; return result;
        }
        private static System.Windows.Media.Effects.DropShadowEffect CreateContactEffect() {
            return new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(76, 51, 102), BlurRadius = 4, ShadowDepth = 1.5, Direction = 300, Opacity = 0.19, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality };
        }
        private FrameworkElement CreatePetVisual() {
            var bitmap = new BitmapImage();
            using (var stream = typeof(PetWindow).Assembly.GetManifestResourceStream("Shiji.Desktop.DragonGirl.png")) {
                if (stream == null) throw new InvalidOperationException("桌宠皮肤缺失，请重新下载完整程序。");
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit();
            }
            bitmap.Freeze(); originalSkin = bitmap;
            var visual = new Image { Source = bitmap, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            skinImage = visual;
            var lighting = new System.Windows.Shapes.Rectangle {
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0.9, 1),
                    GradientStops = new GradientStopCollection {
                        new GradientStop(Color.FromArgb(10, 255, 250, 244), 0),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.34),
                        new GradientStop(Color.FromArgb(7, 107, 76, 148), 0.62),
                        new GradientStop(Color.FromArgb(14, 80, 54, 116), 1)
                    }
                },
                OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform }
            };
            skinLighting = lighting;
            var layers = new Grid { Background = Brushes.Transparent };
            // Keep the crisp foreground out of the shadow effect's offscreen surface.
            var silhouette = new Image { Source = bitmap, Stretch = Stretch.Uniform, IsHitTestVisible = false, Effect = CreateContactEffect() };
            layers.Children.Add(silhouette); layers.Children.Add(visual); layers.Children.Add(lighting);
            return layers;
        }
    }

    internal sealed class PanelWindow : Window {
        private readonly AssistantClient client;
        private readonly PetWindow pet;
        private readonly StackPanel body;
        private readonly TextBlock heading, message;
        private readonly ScrollViewer scroll;
        private string page = "reminder", requestKey;
        private object pendingReminder;
        private int generation;
        private bool busy;
        public PanelWindow(AssistantClient client, PetWindow pet) {
            this.client = client; this.pet = pet;
            Title = "拾记 · 桌宠助手"; Width = Math.Min(304, SystemParameters.WorkArea.Width); Height = Math.Min(442, SystemParameters.WorkArea.Height); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = true; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            var frame = new Border { Background = Theme.Brush("#FCFAFF"), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(16) };
            var layout = new DockPanel(); frame.Child = layout; Content = frame;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            var close = Theme.Button("×", () => Close()); close.Width = 34; close.MinHeight = 30; close.Padding = new Thickness(0); close.FontSize = 21; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            var home = Theme.Button("‹", Back); home.Width = 30; home.MinHeight = 30; home.Padding = new Thickness(0); home.Margin = new Thickness(0, 0, 8, 0); home.FontSize = 22; DockPanel.SetDock(home, Dock.Left); header.Children.Add(home);
            heading = Theme.Text("拾记", 17); heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading);
            header.MouseLeftButtonDown += (sender, e) => { if (e.OriginalSource is TextBlock) DragMove(); };
            message = Theme.Text("", 12); message.Visibility = Visibility.Collapsed; message.Margin = new Thickness(0, 10, 0, 0); DockPanel.SetDock(message, Dock.Bottom); layout.Children.Add(message);
            var foot = Theme.Text("拾记  ·  本机保存", 11, Theme.Muted); foot.Margin = new Thickness(0, 12, 0, 0); DockPanel.SetDock(foot, Dock.Bottom); layout.Children.Add(foot);
            body = new StackPanel(); scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; layout.Children.Add(scroll);
            KeyDown += (sender, e) => { if (e.Key == Key.Escape) Close(); };
        }
        public string CurrentPage { get { return page; } }
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
                    default: ReminderForm(); break;
                }
            } catch (Exception e) { if (version == generation) ShowMessage("未能连接拾记后台：" + e.Message, true); }
        }
        private void Back() {
            if (busy) return;
            if (page == "reminder") Navigate("reminders");
            else if (page == "note") Navigate("notes");
            else if (page == "task") Navigate("tasks");
            else { Close(); pet.OpenMenu(); }
        }
        private void ReminderForm() {
            heading.Text = "定时提醒";
            body.Children.Add(Theme.Button("查看 / 取消已有提醒", () => Navigate("reminders")));
            body.Children.Add(Theme.Text("提醒内容")); var text = Theme.Input("ReminderMessage", 160, true); text.MinHeight = 58; body.Children.Add(text);
            body.Children.Add(Theme.Text("提醒日期与时间（本机时区）"));
            var date = new DatePicker { SelectedDate = DateTime.Now.AddMinutes(3).Date, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5), FontSize = 14 }; AutomationProperties.SetAutomationId(date, "ReminderDate"); body.Children.Add(date);
            var time = Theme.Input("ReminderTime", 8); time.Text = DateTime.Now.AddMinutes(3).ToString("HH:mm:ss"); body.Children.Add(time);
            var presets = new UniformGridCompat(3);
            foreach (int minutes in new[] { 3, 10, 30 }) { int value = minutes; presets.Add(Theme.Button(value + " 分钟后", () => { var when = DateTime.Now.AddMinutes(value); date.SelectedDate = when.Date; time.Text = when.ToString("HH:mm:ss"); })); } body.Children.Add(presets);
            time.ToolTip = "时间格式：14:30 或 14:30:00";
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
            body.Children.Add(Theme.Text("内容")); var content = Theme.Input("NoteContent", 20000, true); content.MinHeight = 118; body.Children.Add(content);
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
            heading.Text = "任务表"; var rows = await client.List("/tasks"); if (version != generation) return;
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
