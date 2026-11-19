using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Shiji.Desktop {
    // All assets are preprocessed at build time. No image work or network calls during playback.
    internal sealed class PetAnimation {
        private readonly BitmapSource[] frames;
        private readonly Action<BitmapSource> display;
        private readonly Func<bool> canAnimate;
        private readonly DispatcherTimer timer;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Random random = new Random();
        private readonly int fps, blinkStart, blinkCount, waveStart, waveCount;
        private int action; // 0: quiet idle, 1: blink, 2: wave
        public int Frame { get; private set; }
        public int Updates { get; private set; }
        public int FrameCount { get { return frames.Length; } }
        public int Action { get { return action; } }
        public bool IsRunning { get { return timer.IsEnabled; } }
        public bool IsQuiet { get { return action == 0 && timer.Interval.TotalSeconds >= 3.8; } }
        public double ElapsedMilliseconds { get { return clock.Elapsed.TotalMilliseconds; } }

        public PetAnimation(Action<BitmapSource> display, Func<bool> canAnimate) {
            this.display = display; this.canAnimate = canAnimate;
            var assembly = typeof(PetAnimation).Assembly;
            Dictionary<string, object> info;
            using (var stream = assembly.GetManifestResourceStream("Shiji.Desktop.Motion.json")) {
                if (stream == null) throw new InvalidOperationException("桌宠动画描述缺失，请重新下载完整程序。");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            int cell = Convert.ToInt32(info["cell"]), columns = Convert.ToInt32(info["columns"]), count = Convert.ToInt32(info["frames"]);
            fps = Convert.ToInt32(info["fps"]); blinkStart = Convert.ToInt32(info["blinkStart"]); blinkCount = Convert.ToInt32(info["blinkCount"]);
            waveStart = Convert.ToInt32(info["waveStart"]); waveCount = Convert.ToInt32(info["waveCount"]);
            var atlas = new BitmapImage();
            using (var stream = assembly.GetManifestResourceStream("Shiji.Desktop.Motion.png")) {
                if (stream == null) throw new InvalidOperationException("桌宠动画皮肤缺失，请重新下载完整程序。");
                atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad; atlas.StreamSource = stream; atlas.EndInit();
            }
            atlas.Freeze();
            if (cell < 48 || columns < 1 || count < 3 || fps < 1 || fps > 60 || blinkStart < 1 || blinkCount < 2 || waveStart < blinkStart + blinkCount || waveCount < 2 || waveStart + waveCount > count || columns * cell != atlas.PixelWidth || ((count + columns - 1) / columns) * cell != atlas.PixelHeight)
                throw new InvalidOperationException("桌宠动画资源不完整，请重新下载完整程序。");
            frames = new BitmapSource[count];
            for (int i = 0; i < count; i++) {
                var frame = new CroppedBitmap(atlas, new Int32Rect(i % columns * cell, i / columns * cell, cell, cell));
                frame.Freeze(); frames[i] = frame;
            }
            timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Tick += Tick;
            Frame = -1; ShowFrame(0);
        }

        public void Start() { if (!timer.IsEnabled && canAnimate()) ScheduleBlink(); }
        public void Stop() { timer.Stop(); clock.Reset(); action = 0; ShowFrame(0); }
        public void Wave() {
            if (!canAnimate() || action == 2) return;
            action = 2; clock.Restart(); ShowFrame(waveStart);
            timer.Interval = TimeSpan.FromMilliseconds(16); timer.Start();
        }
        private void ScheduleBlink() {
            action = 0; clock.Reset(); ShowFrame(0);
            // No high-frequency ticks during the quiet part of idle.
            timer.Interval = TimeSpan.FromMilliseconds(3800 + random.Next(1800)); timer.Start();
        }
        internal void BeginBlink() {
            if (!canAnimate()) return;
            action = 1; clock.Restart(); ShowFrame(blinkStart);
            timer.Interval = TimeSpan.FromMilliseconds(16); timer.Start();
        }
        private void Tick(object sender, EventArgs e) {
            if (!canAnimate()) { Stop(); return; }
            if (action == 0) BeginBlink();
            int count = action == 2 ? waveCount : blinkCount;
            int first = action == 2 ? waveStart : blinkStart;
            // Select by elapsed time, so an occupied UI thread never queues stale poses.
            int frame = (int)(clock.Elapsed.TotalSeconds * fps);
            if (frame >= count) { ScheduleBlink(); return; }
            ShowFrame(first + frame);
        }
        private void ShowFrame(int index) {
            if (Frame == index) return;
            Frame = index; Updates++; display(frames[index]);
        }
    }
}
