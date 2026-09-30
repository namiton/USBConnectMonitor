// Copyright (c) 2026 @Namiton
// SPDX-License-Identifier: MIT
//
// USB Monitor — USB デバイスの接続/切断をリアルタイム表示する監視ツール
// デザイン: Linear のデザインシステム（ダークサーフェス + インディゴアクセント）に準拠
// ビルド: build.ps1（.NET Framework 4.x 同梱の csc.exe を使用。C# 5 構文のみ）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace UsbMonitor
{
    // ------------------------------------------------------------------ デザイントークン（Linear DESIGN.md）
    static class Theme
    {
        public static readonly Color BgLevel0 = Hex("#08090A");
        public static readonly Color BgLevel1 = Hex("#0F1011");
        public static readonly Color BgLevel2 = Hex("#141516");
        public static readonly Color BgSecondary = Hex("#1C1C1F");
        public static readonly Color TextPrimary = Hex("#F7F8F8");
        public static readonly Color TextSecondary = Hex("#D0D6E0");
        public static readonly Color TextTertiary = Hex("#8A8F98");
        public static readonly Color TextQuaternary = Hex("#62666D");
        public static readonly Color BorderPrimary = Hex("#23252A");
        public static readonly Color BorderSecondary = Hex("#34343A");
        public static readonly Color Brand = Hex("#5E6AD2");
        public static readonly Color AccentHover = Hex("#828FFF");
        public static readonly Color Green = Hex("#27A644");
        public static readonly Color Yellow = Hex("#F0BF00");
        public static readonly Color Red = Hex("#EB5757");

        // rgba(255,255,255,a) 系
        public static readonly Color White03 = Color.FromArgb(8, 255, 255, 255);
        public static readonly Color White05 = Color.FromArgb(13, 255, 255, 255);
        public static readonly Color White07 = Color.FromArgb(18, 255, 255, 255);
        public static readonly Color White08 = Color.FromArgb(20, 255, 255, 255);
        public static readonly Color White15 = Color.FromArgb(38, 255, 255, 255);
        public static readonly Color White25 = Color.FromArgb(64, 255, 255, 255);
        public static readonly Color BrandTint = Color.FromArgb(46, 94, 106, 210);     // rgba(94,106,210,0.18)
        public static readonly Color BrandBorder = Color.FromArgb(102, 130, 143, 255); // rgba(130,143,255,0.4)

        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static Font Sans(float px) { return new Font("Segoe UI", px * Scale, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font SansSemibold(float px) { return new Font("Segoe UI Semibold", px * Scale, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Mono(float px) { return new Font("Consolas", px * Scale, FontStyle.Regular, GraphicsUnit.Pixel); }

        public static Color Tint(Color c, int alpha) { return Color.FromArgb(alpha, c.R, c.G, c.B); }

        static Color Hex(string h)
        {
            return Color.FromArgb(Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, float radius, Color fill, Color border)
        {
            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            using (var path = Round(rf, radius))
            {
                if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.A > 0) using (var pen = new Pen(border, 1)) g.DrawPath(pen, path);
            }
        }

        const TextFormatFlags BaseFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, BaseFlags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        public static void TextCenter(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, BaseFlags | TextFormatFlags.HorizontalCenter);
        }
        public static void TextRight(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            TextRenderer.DrawText(g, s, f, r, c, BaseFlags | TextFormatFlags.Right);
        }
        public static int Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }

        // ステータスチップ（badge パターンを状態色で着色）
        public static int Chip(Graphics g, string text, Font f, Color color, int x, int centerY, int minWidth)
        {
            int h = S(20);
            int w = Math.Max(minWidth, Measure(text, f) + S(16));
            var r = new Rectangle(x, centerY - h / 2, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillRound(g, r, h / 2f, Tint(color, 38), Tint(color, 102));
            TextCenter(g, text, f, color, r);
            return w;
        }

        public static void Dot(Graphics g, Color c, int cx, int cy, int size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - size / 2f, cy - size / 2f, size, size);
        }
    }

    // ------------------------------------------------------------------ Windows API
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int len, string filter, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out int devInst, string id, int flags);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out uint type, byte[] buf, ref int size, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int CR_SUCCESS = 0;
        const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
        const int CM_LOCATE_DEVNODE_PHANTOM = 0x1;
        const int DN_HAS_PROBLEM = 0x400;

        static DEVPROPKEY KeyDeviceDesc = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        static DEVPROPKEY KeyFriendlyName = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static DEVPROPKEY KeyLocationInfo = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 15);
        static DEVPROPKEY KeyBusReportedDesc = new DEVPROPKEY("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);

        // 現在接続中（devnode が存在する）の USB デバイス ID 一覧
        public static List<string> GetPresentUsbIds()
        {
            var list = new List<string>();
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            int len;
            if (CM_Get_Device_ID_List_SizeW(out len, "USB", flags) != CR_SUCCESS || len <= 0) return list;
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != CR_SUCCESS) return list;
            foreach (var s in new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)) list.Add(s);
            return list;
        }

        // 問題コード（0 = 正常, -1 = devnode なし）
        public static int GetProblem(string id)
        {
            int dev;
            if (CM_Locate_DevNodeW(out dev, id, 0) != CR_SUCCESS) return -1;
            int status, problem;
            if (CM_Get_DevNode_Status(out status, out problem, dev, 0) != CR_SUCCESS) return -1;
            return (status & DN_HAS_PROBLEM) != 0 ? problem : 0;
        }

        public static void GetNames(string id, out string name, out string location)
        {
            name = null; location = null;
            int dev;
            if (CM_Locate_DevNodeW(out dev, id, 0) != CR_SUCCESS && CM_Locate_DevNodeW(out dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return;
            name = GetString(dev, KeyBusReportedDesc);
            // VID_0000（認識失敗）はデバイス説明に失敗理由が入っている
            if (string.IsNullOrEmpty(name) || id.StartsWith(@"USB\VID_0000", StringComparison.OrdinalIgnoreCase))
                name = GetString(dev, KeyFriendlyName) ?? GetString(dev, KeyDeviceDesc) ?? name;
            location = GetString(dev, KeyLocationInfo);
        }

        static string GetString(int dev, DEVPROPKEY key)
        {
            uint type; int size = 0;
            CM_Get_DevNode_PropertyW(dev, ref key, out type, null, ref size, 0);
            if (size <= 0) return null;
            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(dev, ref key, out type, buf, ref size, 0) != CR_SUCCESS) return null;
            var s = Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0');
            return s.Length == 0 ? null : s;
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = 0x000A0908;                            // #08090A (COLORREF=BGR)
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);     // DWMWA_CAPTION_COLOR（Win11）
            int border = 0x002A2523;                             // #23252A
            DwmSetWindowAttribute(hwnd, 34, ref border, 4);      // DWMWA_BORDER_COLOR（Win11）
        }
    }

    // ------------------------------------------------------------------ データ
    class Device
    {
        public string Id;
        public string Name;
        public string Location;
        public bool Present;
        public int Problem;
        public int SessionCount;
        public DateTime? LastDisconnect;
        public readonly List<DateTime> Recent = new List<DateTime>();   // 直近24時間の切断時刻
        public bool EnumFailure { get { return Id.StartsWith(@"USB\VID_0000", StringComparison.OrdinalIgnoreCase); } }
    }

    enum EventKind { Disconnect, Connect, Error, Info, Warn }

    class MonitorEvent
    {
        public DateTime Time;
        public EventKind Kind;
        public string DeviceId;
        public string Name;
        public string Detail;
    }

    // ------------------------------------------------------------------ ピルボタン（Linear secondary button）
    class PillButton : Control
    {
        bool hover, pressed;
        public PillButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Sans(14);
            Cursor = Cursors.Hand;
            Height = Theme.S(40);
            FitWidth();
        }
        public void FitWidth() { Width = Theme.Measure(Text, Font) + Theme.S(36); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BgLevel0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = pressed ? Theme.White05 : hover ? Theme.White07 : Theme.White03;
            var border = Focused && ShowFocusCues ? Theme.Brand : Theme.White08;
            Theme.FillRound(g, ClientRectangle, Height / 2f, fill, border);
            Theme.TextCenter(g, Text, Font, Theme.TextPrimary, ClientRectangle);
        }
    }

    // ------------------------------------------------------------------ パネル付きスクロールリスト
    class RowList : Control
    {
        public string Title = "";
        public Func<string> Subtitle = () => "";
        public Func<string> Filter = () => null;      // 絞り込み中ならラベル文字列
        public string EmptyText = "";
        public int RowHeight;
        public Func<int> Count = () => 0;
        public Action<Graphics, Rectangle, int, bool> DrawRow;
        public Action<int> RowClick;
        public Action FilterClear;

        readonly Font titleFont = Theme.SansSemibold(13);
        readonly Font subFont = Theme.Sans(12);
        readonly int headerH = Theme.S(44);
        int scroll, hover = -1;
        bool dragging; int dragY, dragScroll;
        Rectangle filterRect;

        public RowList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        Rectangle Content { get { return new Rectangle(1, headerH + 1, Width - 2, Height - headerH - Theme.S(8)); } }
        int MaxScroll { get { return Math.Max(0, Count() * RowHeight - Content.Height); } }

        // 先頭に行が増えたとき、スクロール中の閲覧位置を保つ
        public void InsertedTop(int n) { if (scroll > 0) scroll += n * RowHeight; Clamp(); Invalidate(); }
        public void ResetScroll() { scroll = 0; Invalidate(); }
        void Clamp() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }

        Rectangle Thumb()
        {
            var c = Content;
            int total = Count() * RowHeight;
            if (total <= c.Height) return Rectangle.Empty;
            int h = Math.Max(Theme.S(24), c.Height * c.Height / total);
            int y = c.Top + (int)((long)(c.Height - h) * scroll / Math.Max(1, MaxScroll));
            return new Rectangle(c.Right - Theme.S(8), y, Theme.S(4), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BgLevel0);
            Clamp();
            // panel: bg-level-1 / border-primary / radius 16
            Theme.FillRound(g, ClientRectangle, Theme.S(16), Theme.BgLevel1, Theme.BorderPrimary);

            int pad = Theme.S(16);
            int tw = Theme.Measure(Title, titleFont);
            Theme.Text(g, Title, titleFont, Theme.TextSecondary, new Rectangle(pad, 0, tw + 2, headerH));
            int x = pad + tw + Theme.S(8);
            string sub = Subtitle();
            if (!string.IsNullOrEmpty(sub))
            {
                int sw = Theme.Measure(sub, subFont);
                Theme.Text(g, sub, subFont, Theme.TextQuaternary, new Rectangle(x, 0, sw + 2, headerH));
                x += sw + Theme.S(12);
            }
            string filter = Filter();
            filterRect = Rectangle.Empty;
            if (filter != null)
            {
                // badge-brand + 解除用の ×
                string label = filter + "   ×";
                int w = Math.Min(Theme.Measure(label, subFont) + Theme.S(20), Width - x - pad);
                filterRect = new Rectangle(x, headerH / 2 - Theme.S(12), w, Theme.S(24));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, filterRect, filterRect.Height / 2f, Theme.BrandTint, Theme.BrandBorder);
                Theme.Text(g, label, subFont, Color.White, Rectangle.Inflate(filterRect, -Theme.S(10), 0));
            }
            using (var pen = new Pen(Theme.BorderPrimary)) g.DrawLine(pen, 1, headerH, Width - 2, headerH);

            var c = Content;
            int n = Count();
            if (n == 0)
            {
                Theme.TextCenter(g, EmptyText, subFont, Theme.TextQuaternary, c);
                return;
            }
            g.SetClip(c);
            int first = scroll / RowHeight;
            for (int i = first; i < n; i++)
            {
                int y = c.Top + i * RowHeight - scroll;
                if (y > c.Bottom) break;
                DrawRow(g, new Rectangle(c.Left, y, c.Width, RowHeight), i, i == hover);
            }
            var th = Thumb();
            if (!th.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, th, th.Width / 2f, dragging ? Theme.White25 : Theme.White15, Color.Transparent);
            }
            g.ResetClip();
        }

        int RowAt(Point p)
        {
            var c = Content;
            if (!c.Contains(p)) return -1;
            int i = (p.Y - c.Top + scroll) / RowHeight;
            return i < Count() ? i : -1;
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120 * RowHeight * 3;
            Clamp(); hover = RowAt(e.Location); Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var th = Thumb();
            if (!th.IsEmpty && Rectangle.Inflate(th, Theme.S(4), 0).Contains(e.Location))
            {
                dragging = true; dragY = e.Y; dragScroll = scroll; Capture = true; Invalidate(); return;
            }
            if (!filterRect.IsEmpty && filterRect.Contains(e.Location)) { if (FilterClear != null) FilterClear(); return; }
            int i = RowAt(e.Location);
            if (i >= 0 && RowClick != null) RowClick(i);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                var c = Content; var th = Thumb();
                int track = c.Height - th.Height;
                if (track > 0) scroll = dragScroll + (int)((long)(e.Y - dragY) * MaxScroll / track);
                Clamp(); Invalidate(); return;
            }
            Cursor = (!filterRect.IsEmpty && filterRect.Contains(e.Location)) || (RowClick != null && RowAt(e.Location) >= 0) ? Cursors.Hand : Cursors.Default;
            int h = RowAt(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Invalidate(); }
        }
    }

    // ------------------------------------------------------------------ メイン画面
    class MainForm : Form
    {
        const int MaxEvents = 5000;
        static readonly Regex LocRegex = new Regex(@"Port_#(\d+)\.Hub_#(\d+)");
        static readonly Regex VidPidRegex = new Regex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);

        readonly Dictionary<string, Device> devices = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        readonly List<MonitorEvent> events = new List<MonitorEvent>();   // 新しい順
        readonly HashSet<long> seenRecords = new HashSet<long>();
        List<Device> sortedDevices = new List<Device>();
        List<MonitorEvent> visibleEvents = new List<MonitorEvent>();
        string selectedId;
        bool firstScan = true, paused, watcherActive;
        DateTime startTime = DateTime.Now;
        int sessionTotal;
        MonitorEvent lastDisconnect;
        EventLogWatcher watcher;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly string logDir;

        readonly RowList deviceList = new RowList();
        readonly RowList eventList = new RowList();
        readonly PillButton pauseButton = new PillButton("一時停止");
        readonly PillButton clearButton = new PillButton("クリア");
        readonly PillButton logButton = new PillButton("ログフォルダ");

        readonly Font titleFont = Theme.SansSemibold(24);
        readonly Font labelFont = Theme.Sans(12);
        readonly Font statFont = Theme.SansSemibold(24);
        readonly Font badgeFont = Theme.Sans(12);
        readonly Font nameFont = Theme.Sans(14);
        readonly Font rowFont = Theme.Sans(13);
        readonly Font monoFont = Theme.Mono(12);
        readonly Font chipFont = Theme.Sans(12);

        public MainForm()
        {
            Text = "USB Monitor";
            BackColor = Theme.BgLevel0;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(1080), Theme.S(680));
            MinimumSize = new Size(Theme.S(760), Theme.S(480));
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            logDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "reports");

            deviceList.Title = "デバイス";
            deviceList.Subtitle = () => string.Format("{0} 台接続中", devices.Values.Count(d => d.Present));
            deviceList.EmptyText = "USB デバイスを読み込み中…";
            deviceList.RowHeight = Theme.S(56);
            deviceList.Count = () => sortedDevices.Count;
            deviceList.DrawRow = DrawDeviceRow;
            deviceList.RowClick = i => { var id = sortedDevices[i].Id; SelectDevice(selectedId == id ? null : id); };

            eventList.Title = "イベント";
            eventList.Subtitle = () => string.Format("{0} 件", visibleEvents.Count);
            eventList.Filter = () => selectedId == null ? null : DeviceName(selectedId);
            eventList.FilterClear = () => SelectDevice(null);
            eventList.EmptyText = "まだイベントはありません";
            eventList.RowHeight = Theme.S(32);
            eventList.Count = () => visibleEvents.Count;
            eventList.DrawRow = DrawEventRow;

            pauseButton.Click += (s, e) => TogglePause();
            clearButton.Click += (s, e) => ClearSession();
            logButton.Click += (s, e) => { Directory.CreateDirectory(logDir); Process.Start("explorer.exe", "\"" + logDir + "\""); };

            Controls.AddRange(new Control[] { deviceList, eventList, pauseButton, clearButton, logButton });

            timer.Interval = 1000;
            timer.Tick += (s, e) => Scan();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { Native.DarkTitleBar(Handle); } catch { } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AddEvent(EventKind.Info, null, "監視を開始しました", null);
            StartWatcher();
            Scan();
            timer.Start();
            ThreadPool.QueueUserWorkItem(_ => LoadRecentHistory());
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (watcher != null) { watcher.Enabled = false; watcher.Dispose(); }
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C && selectedId != null) { Clipboard.SetText(selectedId); e.Handled = true; }
            if (e.KeyCode == Keys.Escape && selectedId != null) { SelectDevice(null); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // ---------------------------------------------------------- レイアウト
        int Pad { get { return Theme.S(24); } }
        int HeaderH { get { return Theme.S(88); } }
        int StatsH { get { return Theme.S(76); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Pad, gap = Theme.S(12);
            int bx = ClientSize.Width - pad;
            int by = Theme.S(24);
            foreach (var b in new[] { logButton, clearButton, pauseButton })
            {
                bx -= b.Width;
                b.Location = new Point(bx, by);
                bx -= Theme.S(8);
            }
            int top = HeaderH + StatsH + Theme.S(16);
            int h = ClientSize.Height - top - pad;
            int w = ClientSize.Width - pad * 2 - gap;
            int lw = (int)(w * 0.46);
            deviceList.SetBounds(pad, top, lw, h);
            eventList.SetBounds(pad + lw + gap, top, w - lw, h);
            Invalidate();
        }

        // ---------------------------------------------------------- ヘッダーと統計カード
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BgLevel0);
            int pad = Pad;
            int titleY = Theme.S(24), titleH = Theme.S(40);
            string title = "USB Monitor";
            int tw = Theme.Measure(title, titleFont);
            Theme.Text(g, title, titleFont, Theme.TextPrimary, new Rectangle(pad, titleY, tw + 4, titleH));

            // 状態バッジ
            string state = paused ? "一時停止中" : "監視中";
            int bw = Theme.Measure(state, badgeFont) + Theme.S(36);
            var badge = new Rectangle(pad + tw + Theme.S(16), titleY + (titleH - Theme.S(24)) / 2, bw, Theme.S(24));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (paused) Theme.FillRound(g, badge, badge.Height / 2f, Theme.White05, Theme.White08);
            else Theme.FillRound(g, badge, badge.Height / 2f, Theme.BrandTint, Theme.BrandBorder);
            Theme.Dot(g, paused ? Theme.TextQuaternary : Theme.Green, badge.X + Theme.S(14), badge.Y + badge.Height / 2, Theme.S(6));
            Theme.Text(g, state, badgeFont, paused ? Theme.TextTertiary : Color.White, new Rectangle(badge.X + Theme.S(24), badge.Y, bw - Theme.S(28), badge.Height));

            if (!watcherActive)
            {
                string warn = "イベントログを購読できないため、1 秒未満の切断は取りこぼす可能性があります";
                Theme.Text(g, warn, labelFont, Theme.Yellow, new Rectangle(badge.Right + Theme.S(12), titleY, Math.Max(0, pauseButton.Left - badge.Right - Theme.S(24)), titleH));
            }

            // 統計カード 3 枚
            int gap = Theme.S(12);
            int cw = (ClientSize.Width - pad * 2 - gap * 2) / 3;
            int cy = HeaderH;
            DrawStat(g, new Rectangle(pad, cy, cw, StatsH), "接続中の USB デバイス", devices.Values.Count(d => d.Present).ToString(), Theme.TextPrimary);
            DrawStat(g, new Rectangle(pad + cw + gap, cy, cw, StatsH), string.Format("切断回数（{0:HH:mm} から）", startTime), sessionTotal.ToString(), sessionTotal > 0 ? Theme.Red : Theme.TextPrimary);
            string lastLabel = "最終切断" + (lastDisconnect != null ? " · " + lastDisconnect.Name : "");
            string lastValue = lastDisconnect != null ? lastDisconnect.Time.ToString("HH:mm:ss") : "—";
            DrawStat(g, new Rectangle(pad + (cw + gap) * 2, cy, ClientSize.Width - pad * 2 - (cw + gap) * 2, StatsH), lastLabel, lastValue, Theme.TextPrimary);
        }

        void DrawStat(Graphics g, Rectangle r, string label, string value, Color valueColor)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, r, Theme.S(12), Theme.BgLevel1, Theme.BorderPrimary);
            int p = Theme.S(16);
            Theme.Text(g, label, labelFont, Theme.TextTertiary, new Rectangle(r.X + p, r.Y + Theme.S(12), r.Width - p * 2, Theme.S(18)));
            Theme.Text(g, value, statFont, valueColor, new Rectangle(r.X + p, r.Y + Theme.S(32), r.Width - p * 2, Theme.S(32)));
        }

        // ---------------------------------------------------------- 行の描画
        void DrawDeviceRow(Graphics g, Rectangle r, int index, bool hover)
        {
            var d = sortedDevices[index];
            int pad = Theme.S(16);
            var inner = new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(3), r.Width - Theme.S(12), r.Height - Theme.S(6));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (d.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) Theme.FillRound(g, inner, Theme.S(8), Theme.BrandTint, Theme.BrandBorder);
            else if (hover) Theme.FillRound(g, inner, Theme.S(8), Theme.White05, Color.Transparent);

            Color dot; string state;
            if (d.EnumFailure) { dot = Theme.Yellow; state = "認識失敗"; }
            else if (!d.Present) { dot = Theme.Red; state = "未接続"; }
            else if (d.Problem != 0) { dot = Theme.Yellow; state = "エラー " + d.Problem; }
            else { dot = Theme.Green; state = null; }
            int line1 = r.Y + Theme.S(19), line2 = r.Y + Theme.S(38);
            Theme.Dot(g, dot, r.X + pad + Theme.S(4), line1, Theme.S(8));

            // 右側: 今回の切断回数チップ / 24h 回数
            int right = r.Right - pad;
            int chipW = 0;
            if (d.SessionCount > 0)
            {
                string chip = "切断 " + d.SessionCount;
                chipW = Theme.Measure(chip, chipFont) + Theme.S(16);
                Theme.Chip(g, chip, chipFont, Theme.Red, right - chipW, line1, chipW);
            }
            string recent = d.Recent.Count > 0 ? "24h " + d.Recent.Count : "";
            int recentW = recent.Length > 0 ? Theme.Measure(recent, monoFont) : 0;
            if (recentW > 0) Theme.TextRight(g, recent, monoFont, Theme.TextTertiary, new Rectangle(right - recentW - 2, line2 - Theme.S(9), recentW + 2, Theme.S(18)));

            int tx = r.X + pad + Theme.S(20);
            Theme.Text(g, d.Name ?? d.Id, nameFont, d.Present ? Theme.TextPrimary : Theme.TextTertiary, new Rectangle(tx, line1 - Theme.S(10), right - chipW - Theme.S(12) - tx, Theme.S(20)));

            var parts = new List<string>();
            if (state != null) parts.Add(state);
            var m = d.Location != null ? LocRegex.Match(d.Location) : Match.Empty;
            if (m.Success) parts.Add(string.Format("ポート {0} / ハブ {1}", int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            var vp = VidPidRegex.Match(d.Id);
            parts.Add(vp.Success ? vp.Value.ToUpperInvariant() : d.Id);
            Theme.Text(g, string.Join("  ·  ", parts), monoFont, state != null ? dot : Theme.TextQuaternary, new Rectangle(tx, line2 - Theme.S(9), right - recentW - Theme.S(12) - tx, Theme.S(18)));

            if (index < sortedDevices.Count - 1)
                using (var pen = new Pen(Theme.BorderPrimary)) g.DrawLine(pen, r.X + pad, r.Bottom - 1, r.Right - pad, r.Bottom - 1);
        }

        void DrawEventRow(Graphics g, Rectangle r, int index, bool hover)
        {
            var ev = visibleEvents[index];
            int pad = Theme.S(16);
            if (hover)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(2), r.Width - Theme.S(12), r.Height - Theme.S(4)), Theme.S(6), Theme.White05, Color.Transparent);
            }
            int cy = r.Y + r.Height / 2;
            string time = ev.Time.ToString(ev.Time.Date == DateTime.Today ? "HH:mm:ss" : "MM/dd HH:mm");
            Theme.Text(g, time, monoFont, Theme.TextTertiary, new Rectangle(r.X + pad, r.Y, Theme.S(80), r.Height));

            string tag; Color color;
            switch (ev.Kind)
            {
                case EventKind.Disconnect: tag = "切断"; color = Theme.Red; break;
                case EventKind.Connect: tag = "接続"; color = Theme.Green; break;
                case EventKind.Error: tag = "異常"; color = Theme.Yellow; break;
                case EventKind.Warn: tag = "注意"; color = Theme.Yellow; break;
                default: tag = "情報"; color = Theme.TextTertiary; break;
            }
            int x = r.X + pad + Theme.S(76);
            int chipW = Theme.S(48);
            Theme.Chip(g, tag, chipFont, color, x, cy, chipW);
            x += chipW + Theme.S(12);

            int avail = r.Right - pad - x;
            int nameW = Math.Min(Theme.Measure(ev.Name, rowFont) + 2, avail);
            Theme.Text(g, ev.Name, rowFont, ev.Kind == EventKind.Info ? Theme.TextTertiary : Theme.TextSecondary, new Rectangle(x, r.Y, nameW, r.Height));
            if (!string.IsNullOrEmpty(ev.Detail) && avail - nameW > Theme.S(40))
                Theme.Text(g, ev.Detail, rowFont, Theme.TextQuaternary, new Rectangle(x + nameW + Theme.S(10), r.Y, avail - nameW - Theme.S(10), r.Height));
        }

        // ---------------------------------------------------------- 検出
        static bool IsTarget(string id)
        {
            return id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
                && id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0;
        }

        Device GetDevice(string id)
        {
            Device d;
            if (devices.TryGetValue(id, out d)) return d;
            d = new Device { Id = id };
            string name, loc;
            Native.GetNames(id, out name, out loc);
            d.Name = name ?? id; d.Location = loc;
            devices[id] = d;
            return d;
        }

        string DeviceName(string id) { Device d; return devices.TryGetValue(id, out d) ? d.Name : id; }

        // 切断はイベントログ(1010/1011)から拾う。1 秒未満の瞬断も記録されるため
        void StartWatcher()
        {
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-Kernel-PnP/Device Management", PathType.LogName, "*[System[(EventID=1010 or EventID=1011)]]");
                watcher = new EventLogWatcher(q);
                watcher.EventRecordWritten += (s, e) =>
                {
                    var rec = e.EventRecord;
                    if (rec == null || rec.Properties.Count == 0) return;
                    string id = rec.Properties[0].Value as string;
                    DateTime t = rec.TimeCreated ?? DateTime.Now;
                    int eid = rec.Id;
                    long recId = rec.RecordId ?? -1;
                    if (id == null || !IsTarget(id) || !IsHandleCreated) return;
                    BeginInvoke(new Action(() => OnDisconnect(id, t, eid == 1011 ? "デバイスが障害を報告" : "バス上から消えた", recId)));
                };
                watcher.Enabled = true;
                watcherActive = true;
            }
            catch (Exception ex)
            {
                watcherActive = false;
                AddEvent(EventKind.Warn, null, "イベントログを購読できません", ex.Message);
            }
        }

        void LoadRecentHistory()
        {
            var found = new List<Tuple<string, DateTime, long>>();
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-Kernel-PnP/Device Management", PathType.LogName,
                    "*[System[(EventID=1010 or EventID=1011) and TimeCreated[timediff(@SystemTime) <= 86400000]]]");
                using (var reader = new EventLogReader(q))
                {
                    EventRecord rec;
                    while ((rec = reader.ReadEvent()) != null)
                    {
                        using (rec)
                        {
                            if (rec.Properties.Count == 0) continue;
                            var id = rec.Properties[0].Value as string;
                            if (id != null && IsTarget(id)) found.Add(Tuple.Create(id, rec.TimeCreated ?? DateTime.Now, rec.RecordId ?? -1));
                        }
                    }
                }
            }
            catch { return; }
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                foreach (var f in found)
                {
                    if (f.Item3 >= 0 && !seenRecords.Add(f.Item3)) continue;
                    var d = GetDevice(f.Item1);
                    d.Recent.Add(f.Item2);
                }
                RefreshViews();
            }));
        }

        void OnDisconnect(string id, DateTime time, string reason, long recordId)
        {
            if (paused) return;
            if (recordId >= 0 && !seenRecords.Add(recordId)) return;
            var d = GetDevice(id);
            d.Present = false;
            d.SessionCount++;
            d.LastDisconnect = time;
            d.Recent.Add(time);
            sessionTotal++;
            lastDisconnect = AddEvent(EventKind.Disconnect, id, d.Name, reason, time);
            RefreshViews();
        }

        void Scan()
        {
            if (paused) return;
            var present = new HashSet<string>(Native.GetPresentUsbIds().Where(IsTarget), StringComparer.OrdinalIgnoreCase);
            foreach (var id in present)
            {
                Device known;
                bool isNew = !devices.TryGetValue(id, out known);
                var d = GetDevice(id);
                int problem = Native.GetProblem(id);
                if (!d.Present && !firstScan)
                {
                    string detail = null;
                    if (d.LastDisconnect.HasValue && (DateTime.Now - d.LastDisconnect.Value).TotalMinutes < 10)
                    {
                        double sec = (DateTime.Now - d.LastDisconnect.Value).TotalSeconds;
                        detail = sec < 1.5 ? "1 秒以内に復帰" : string.Format("約 {0:0} 秒で復帰", sec);
                    }
                    else if (isNew) detail = "新しく接続";
                    if (d.EnumFailure) detail = "認識に失敗（ケーブル・給電・接点を確認）";
                    AddEvent(d.EnumFailure ? EventKind.Error : EventKind.Connect, id, d.Name, detail);
                }
                // 45 = 未接続。切断はイベントログ側で表示する
                if (!firstScan && problem > 0 && problem != 45 && problem != d.Problem)
                    AddEvent(EventKind.Error, id, d.Name, "問題コード " + problem);
                d.Present = true;
                d.Problem = problem > 0 ? problem : 0;
            }
            foreach (var d in devices.Values)
            {
                if (!d.Present || present.Contains(d.Id)) continue;
                d.Present = false;
                // イベントログを購読できないときだけ、一覧の差分で切断を記録する
                if (!watcherActive && !firstScan)
                {
                    d.SessionCount++; sessionTotal++;
                    d.LastDisconnect = DateTime.Now; d.Recent.Add(DateTime.Now);
                    lastDisconnect = AddEvent(EventKind.Disconnect, d.Id, d.Name, "一覧から消えた");
                }
            }
            var cutoff = DateTime.Now.AddHours(-24);
            foreach (var d in devices.Values) d.Recent.RemoveAll(t => t < cutoff);
            firstScan = false;
            RefreshViews();
        }

        MonitorEvent AddEvent(EventKind kind, string id, string name, string detail, DateTime? time = null)
        {
            var ev = new MonitorEvent { Time = time ?? DateTime.Now, Kind = kind, DeviceId = id, Name = name, Detail = detail };
            events.Insert(0, ev);
            if (events.Count > MaxEvents) events.RemoveAt(events.Count - 1);
            if (selectedId == null || selectedId.Equals(id, StringComparison.OrdinalIgnoreCase)) eventList.InsertedTop(1);
            WriteLog(ev);
            return ev;
        }

        void WriteLog(MonitorEvent ev)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                string kind = ev.Kind == EventKind.Disconnect ? "切断" : ev.Kind == EventKind.Connect ? "接続" : ev.Kind == EventKind.Error ? "異常" : ev.Kind == EventKind.Warn ? "注意" : "情報";
                string line = string.Join("\t", ev.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), kind, ev.Name, ev.DeviceId ?? "", ev.Detail ?? "");
                File.AppendAllText(Path.Combine(logDir, "usbmonitor_" + ev.Time.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        void RefreshViews()
        {
            // 切断の多いデバイスを上に出す
            sortedDevices = devices.Values
                .Where(d => d.Present || d.SessionCount > 0 || d.Recent.Count > 0)
                .OrderByDescending(d => d.SessionCount)
                .ThenByDescending(d => d.Recent.Count)
                .ThenByDescending(d => d.Present)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            visibleEvents = selectedId == null ? new List<MonitorEvent>(events)
                : events.Where(ev => selectedId.Equals(ev.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            deviceList.Invalidate();
            eventList.Invalidate();
            Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderH + StatsH));
        }

        void SelectDevice(string id)
        {
            selectedId = id;
            eventList.ResetScroll();
            RefreshViews();
        }

        void TogglePause()
        {
            paused = !paused;
            pauseButton.Text = paused ? "再開" : "一時停止";
            if (!paused)
            {
                firstScan = true;   // 停止中の変化は記録しない
                AddEvent(EventKind.Info, null, "監視を再開しました", null);
                Scan();
            }
            else AddEvent(EventKind.Info, null, "監視を一時停止しました", null);
            PerformLayout();
            RefreshViews();
        }

        void ClearSession()
        {
            events.Clear();
            foreach (var d in devices.Values) { d.SessionCount = 0; d.LastDisconnect = null; }
            sessionTotal = 0;
            lastDisconnect = null;
            startTime = DateTime.Now;
            eventList.ResetScroll();
            AddEvent(EventKind.Info, null, "表示をクリアしました", null);
            RefreshViews();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
            Application.Run(new MainForm());
        }
    }
}
