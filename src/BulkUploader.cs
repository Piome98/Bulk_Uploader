// Bulk Uploader - LLM 채팅(Copilot, ChatGPT, Claude, Gemini 등)에 파일을 N개씩 나눠 자동 업로드
// 동작: 파일 N개를 클립보드에 올려 Ctrl+V -> 업로드 대기 -> 메시지(".") 붙여넣기 -> Enter -> 응답 대기 -> 반복
// 빌드: build.ps1 (Windows 기본 내장 .NET Framework 4.x 컴파일러 사용, 별도 설치 불필요)

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Bulk Uploader")]
[assembly: System.Reflection.AssemblyDescription("LLM 채팅에 파일을 N개씩 나눠 자동 업로드")]
[assembly: System.Reflection.AssemblyProduct("Bulk Uploader")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace BulkUploader
{
    static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint GA_ROOT = 2;

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO fi);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        public static IntPtr ForegroundRoot()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return h;
            IntPtr r = GetAncestor(h, GA_ROOT);
            return r == IntPtr.Zero ? h : r;
        }

        public static bool IsOwnProcess(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public static string Title(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static void Key(Keys k, bool up)
        {
            byte vk = (byte)k;
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
        }

        public static void Press(Keys modifier, Keys key)
        {
            if (modifier != Keys.None) { Key(modifier, false); Thread.Sleep(30); }
            Key(key, false); Thread.Sleep(30); Key(key, true);
            if (modifier != Keys.None) { Thread.Sleep(30); Key(modifier, true); }
        }

        public static bool ModifiersDown()
        {
            int[] vks = { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, LWin, RWin
            foreach (int v in vks)
                if ((GetAsyncKeyState(v) & 0x8000) != 0) return true;
            return false;
        }

        public static void Flash(IntPtr hwnd)
        {
            var fi = new FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(fi);
            fi.hwnd = hwnd;
            fi.dwFlags = 2 | 12; // FLASHW_TRAY | FLASHW_TIMERNOFG
            FlashWindowEx(ref fi);
        }
    }

    class FileEntry
    {
        public string Path;
        public long Size;
        public string Status = "";
    }

    class Settings
    {
        public int BatchSize = 3;
        public string Message = ".";
        public string FinalMessage = "";
        public int SendKey = 0; // 0 = Enter, 1 = Ctrl+Enter
        public decimal UploadWait = 5;
        public decimal ResponseWait = 20;
        public bool Manual = false;
        public decimal Countdown = 3;
        public bool TopMost = false;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BulkUploader", "settings.ini"); }
        }

        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n"); }
        static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] == 'n' ? '\n' : s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1);
                    decimal d;
                    switch (k)
                    {
                        case "batch": int b; if (int.TryParse(v, out b)) s.BatchSize = b; break;
                        case "msg": s.Message = Unesc(v); break;
                        case "final": s.FinalMessage = Unesc(v); break;
                        case "sendkey": int sk; if (int.TryParse(v, out sk)) s.SendKey = sk; break;
                        case "upload": if (decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d)) s.UploadWait = d; break;
                        case "resp": if (decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d)) s.ResponseWait = d; break;
                        case "countdown": if (decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Countdown = d; break;
                        case "manual": s.Manual = v == "1"; break;
                        case "top": s.TopMost = v == "1"; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var lines = new[]
                {
                    "batch=" + BatchSize,
                    "msg=" + Esc(Message),
                    "final=" + Esc(FinalMessage),
                    "sendkey=" + SendKey,
                    "upload=" + UploadWait.ToString(ci),
                    "resp=" + ResponseWait.ToString(ci),
                    "countdown=" + Countdown.ToString(ci),
                    "manual=" + (Manual ? "1" : "0"),
                    "top=" + (TopMost ? "1" : "0"),
                };
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    public class MainForm : Form
    {
        const int HK_START = 1, HK_STOP = 2;
        const string AppTitle = "Bulk Uploader - LLM 파일 분할 업로드";

        readonly List<FileEntry> files = new List<FileEntry>();
        readonly List<List<int>> batches = new List<List<int>>();
        int curBatch = 0;   // 다음에 보낼 배치
        int curStep = 0;    // 0 = 파일 붙여넣기 차례, 1 = 메시지 전송 차례
        bool running, countingDown, waiting, pauseReq, stopReq, draggingOut;
        TaskCompletionSource<bool> resumeTcs;
        IntPtr target = IntPtr.Zero;
        string savedClipboard;
        bool hotkeysOk;
        Point dragStart;
        bool dragArmed;
        float dpi = 1f;

        ListView lv;
        Label lblEmpty, lblDrag;
        NumericUpDown nudBatch, nudUpload, nudResp, nudCountdown;
        TextBox txtMsg, txtFinal;
        ComboBox cboSend;
        RadioButton rbAuto, rbManual;
        CheckBox chkTop;
        Button btnStart, btnPause, btnStop, btnReset, btnPrev, btnNext;
        FlowLayoutPanel editBar;
        ToolStripStatusLabel lblStatus;
        ToolStripProgressBar prog;

        bool Busy { get { return running || countingDown; } }

        public MainForm(string[] initialPaths)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("맑은 고딕", 9F);
            Text = AppTitle;
            ClientSize = new Size(980, 690);
            MinimumSize = new Size(860, 640);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;

            BuildUi();
            ResumeLayout(false);
            PerformLayout();

            ApplySettings(Settings.Load());
            RefreshList();
            SetStatus(hotkeysOk
                ? "준비됨 - 파일을 목록에 끌어다 놓으세요. (F9: 시작/일시정지/재개, F10: 중지)"
                : "준비됨 - F9/F10 단축키 등록에 실패했습니다(다른 프로그램이 사용 중). 버튼으로 조작하세요.");
            if (initialPaths != null && initialPaths.Length > 0) AddPaths(initialPaths);
        }

        // ───────────────────────── UI 구성 ─────────────────────────

        static Button MkBtn(string text, EventHandler onClick)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 2, 6, 2);
            b.Margin = new Padding(3);
            b.UseVisualStyleBackColor = true;
            b.Click += onClick;
            return b;
        }

        static NumericUpDown MkNud(decimal min, decimal max, int decimals, decimal step)
        {
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals; n.Increment = step;
            n.Width = 70;
            n.TextAlign = HorizontalAlignment.Right;
            n.Anchor = AnchorStyles.Left;
            return n;
        }

        static Label MkLabel(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(3, 5, 3, 5);
            return l;
        }

        static Label MkHint(string text)
        {
            var l = MkLabel(text);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 0, 3, 4);
            return l;
        }

        static TableLayoutPanel MkGrid(int cols)
        {
            var t = new TableLayoutPanel();
            t.ColumnCount = cols;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Dock = DockStyle.Fill;
            for (int i = 0; i < cols; i++)
                t.ColumnStyles.Add(new ColumnStyle(i == cols - 1 ? SizeType.Percent : SizeType.AutoSize, 100));
            return t;
        }

        static GroupBox MkGroup(string title, Control content)
        {
            var g = new GroupBox();
            g.Text = title;
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.Dock = DockStyle.Fill;
            g.Padding = new Padding(6, 4, 6, 4);
            g.Margin = new Padding(0, 0, 0, 8);
            g.Controls.Add(content);
            return g;
        }

        static void AddRow(TableLayoutPanel t, params Control[] cells)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int col = 0;
            foreach (Control c in cells)
            {
                t.Controls.Add(c, col, row);
                col++;
            }
            if (cells.Length < t.ColumnCount)
                t.SetColumnSpan(cells[cells.Length - 1], t.ColumnCount - cells.Length + 1);
        }

        void BuildUi()
        {
            // 상단 편집 버튼
            editBar = new FlowLayoutPanel();
            editBar.Dock = DockStyle.Top;
            editBar.AutoSize = true;
            editBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            editBar.Padding = new Padding(6, 6, 6, 2);
            editBar.Controls.Add(MkBtn("파일 추가...", delegate { AddFilesDialog(); }));
            editBar.Controls.Add(MkBtn("폴더 추가...", delegate { AddFolderDialog(); }));
            editBar.Controls.Add(MkBtn("선택 삭제", delegate { RemoveSelected(); }));
            editBar.Controls.Add(MkBtn("전체 삭제", delegate { ClearAll(); }));
            editBar.Controls.Add(MkBtn("▲ 위로", delegate { MoveSelected(-1); }));
            editBar.Controls.Add(MkBtn("▼ 아래로", delegate { MoveSelected(1); }));
            editBar.Controls.Add(MkBtn("이름순 정렬", delegate { SortByName(); }));
            var btnHelp = MkBtn("사용법", delegate { ShowHelp(); });
            editBar.Controls.Add(btnHelp);

            // 파일 목록
            lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.AllowDrop = true;
            lv.Columns.Add("#", S(42), HorizontalAlignment.Right);
            lv.Columns.Add("배치", S(48), HorizontalAlignment.Center);
            lv.Columns.Add("파일명", S(250));
            lv.Columns.Add("크기", S(75), HorizontalAlignment.Right);
            lv.Columns.Add("상태", S(80), HorizontalAlignment.Center);
            lv.Columns.Add("폴더", S(320));
            lv.DragEnter += OnFileDragEnter;
            lv.DragDrop += OnFileDragDrop;
            lv.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) RemoveSelected();
                if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem it in lv.Items) it.Selected = true;
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(lv, true, null);

            lblEmpty = new Label();
            lblEmpty.Text = "여기에 파일이나 폴더를 끌어다 놓으세요\r\n(여러 개를 한꺼번에 놓아도 됩니다)";
            lblEmpty.Font = new Font("맑은 고딕", 12F);
            lblEmpty.ForeColor = SystemColors.GrayText;
            lblEmpty.BackColor = SystemColors.Window;
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.Dock = DockStyle.Fill;
            lblEmpty.AllowDrop = true;
            lblEmpty.DragEnter += OnFileDragEnter;
            lblEmpty.DragDrop += OnFileDragDrop;
            lblEmpty.DoubleClick += delegate { AddFilesDialog(); };
            lv.Controls.Add(lblEmpty);

            var center = new Panel();
            center.Dock = DockStyle.Fill;
            center.Padding = new Padding(8, 4, 4, 8);
            center.Controls.Add(lv);

            // 오른쪽 설정 패널
            var side = new TableLayoutPanel();
            side.Dock = DockStyle.Right;
            side.Width = 380;
            side.ColumnCount = 1;
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            side.Padding = new Padding(4, 4, 8, 8);
            side.AutoScroll = true;

            // 업로드 설정
            var g1 = MkGrid(3);
            nudBatch = MkNud(1, 50, 0, 1);
            nudBatch.ValueChanged += delegate { if (!Busy) { ResetProgress(); RefreshList(); } };
            AddRow(g1, MkLabel("한 번에 올릴 파일 수"), nudBatch, MkLabel("개"));
            txtMsg = new TextBox(); txtMsg.Dock = DockStyle.Fill;
            AddRow(g1, MkLabel("배치마다 보낼 메시지"), txtMsg);
            txtFinal = new TextBox(); txtFinal.Dock = DockStyle.Fill;
            AddRow(g1, MkLabel("마지막 배치 메시지"), txtFinal);
            AddRow(g1, new Label(), MkHint("비워두면 위 메시지를 사용"));
            cboSend = new ComboBox();
            cboSend.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSend.Items.AddRange(new object[] { "Enter", "Ctrl+Enter" });
            cboSend.Anchor = AnchorStyles.Left;
            cboSend.Width = 100;
            AddRow(g1, MkLabel("전송 키"), cboSend);
            nudUpload = MkNud(0, 300, 1, 0.5m);
            AddRow(g1, MkLabel("붙여넣기 후 업로드 대기"), nudUpload, MkLabel("초"));
            side.Controls.Add(MkGroup("업로드 설정", g1));

            // 다음 배치 진행 방식
            var g2 = MkGrid(3);
            rbAuto = new RadioButton(); rbAuto.Text = "자동: 전송 후"; rbAuto.AutoSize = true; rbAuto.Anchor = AnchorStyles.Left;
            nudResp = MkNud(0, 3600, 0, 5);
            AddRow(g2, rbAuto, nudResp, MkLabel("초 기다림"));
            rbManual = new RadioButton(); rbManual.Text = "수동: 응답이 끝나면 F9 눌러서 다음 배치"; rbManual.AutoSize = true; rbManual.Anchor = AnchorStyles.Left;
            AddRow(g2, rbManual);
            rbAuto.CheckedChanged += delegate { nudResp.Enabled = rbAuto.Checked; };
            side.Controls.Add(MkGroup("다음 배치로 넘어가기", g2));

            // 시작 옵션
            var g3 = MkGrid(3);
            nudCountdown = MkNud(1, 30, 0, 1);
            AddRow(g3, MkLabel("시작 버튼 카운트다운"), nudCountdown, MkLabel("초"));
            chkTop = new CheckBox(); chkTop.Text = "이 창을 항상 위에 표시"; chkTop.AutoSize = true; chkTop.Anchor = AnchorStyles.Left;
            chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
            AddRow(g3, chkTop);
            side.Controls.Add(MkGroup("시작", g3));

            // 실행 버튼
            var runBar = new FlowLayoutPanel();
            runBar.AutoSize = true;
            runBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            runBar.Dock = DockStyle.Fill;
            runBar.Margin = new Padding(0, 0, 0, 6);
            btnStart = MkBtn("▶ 시작 (F9)", delegate { OnStartButton(); });
            btnStart.Font = new Font(Font, FontStyle.Bold);
            btnPause = MkBtn("일시정지", delegate { RequestPause(); });
            btnStop = MkBtn("■ 중지 (F10)", delegate { RequestStop(); });
            btnReset = MkBtn("초기화", delegate { if (!Busy) { ResetProgress(); RefreshList(); SetStatus("진행 상황을 초기화했습니다. 처음 배치부터 보냅니다."); } });
            runBar.Controls.AddRange(new Control[] { btnStart, btnPause, btnStop, btnReset });
            side.Controls.Add(runBar);

            // 수동 드래그 영역
            var dragGrid = MkGrid(1);
            dragGrid.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
            lblDrag = new Label();
            lblDrag.AutoSize = false;
            lblDrag.Height = 56;
            lblDrag.Dock = DockStyle.Fill;
            lblDrag.TextAlign = ContentAlignment.MiddleCenter;
            lblDrag.BackColor = Color.FromArgb(232, 241, 253);
            lblDrag.BorderStyle = BorderStyle.FixedSingle;
            lblDrag.Cursor = Cursors.Hand;
            lblDrag.MouseDown += OnDragOutMouseDown;
            lblDrag.MouseMove += OnDragOutMouseMove;
            lblDrag.MouseUp += delegate { dragArmed = false; };
            AddRow(dragGrid, lblDrag);
            var navBar = new FlowLayoutPanel();
            navBar.AutoSize = true;
            navBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navBar.Anchor = AnchorStyles.Right;
            btnPrev = MkBtn("◀ 이전 배치", delegate { StepBatch(-1); });
            btnNext = MkBtn("다음 배치 ▶", delegate { StepBatch(1); });
            navBar.Controls.Add(btnPrev);
            navBar.Controls.Add(btnNext);
            AddRow(dragGrid, navBar);
            side.Controls.Add(MkGroup("수동 모드 (붙여넣기가 안 되는 사이트용)", dragGrid));

            // 상태 표시줄
            var status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            prog = new ToolStripProgressBar();
            prog.Size = new Size(160, 16);
            status.Items.Add(lblStatus);
            status.Items.Add(prog);

            Controls.Add(center);
            Controls.Add(side);
            Controls.Add(editBar);
            Controls.Add(status);
        }

        int S(int px) { return (int)(px * dpi); }

        void ApplySettings(Settings s)
        {
            nudBatch.Value = Math.Max(nudBatch.Minimum, Math.Min(nudBatch.Maximum, s.BatchSize));
            txtMsg.Text = s.Message;
            txtFinal.Text = s.FinalMessage;
            cboSend.SelectedIndex = s.SendKey == 1 ? 1 : 0;
            nudUpload.Value = Math.Max(nudUpload.Minimum, Math.Min(nudUpload.Maximum, s.UploadWait));
            nudResp.Value = Math.Max(nudResp.Minimum, Math.Min(nudResp.Maximum, s.ResponseWait));
            nudCountdown.Value = Math.Max(nudCountdown.Minimum, Math.Min(nudCountdown.Maximum, s.Countdown));
            rbManual.Checked = s.Manual;
            rbAuto.Checked = !s.Manual;
            nudResp.Enabled = !s.Manual;
            chkTop.Checked = s.TopMost;
        }

        void SaveSettings()
        {
            var s = new Settings();
            s.BatchSize = (int)nudBatch.Value;
            s.Message = txtMsg.Text;
            s.FinalMessage = txtFinal.Text;
            s.SendKey = cboSend.SelectedIndex;
            s.UploadWait = nudUpload.Value;
            s.ResponseWait = nudResp.Value;
            s.Countdown = nudCountdown.Value;
            s.Manual = rbManual.Checked;
            s.TopMost = chkTop.Checked;
            s.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Busy)
            {
                stopReq = true;
                if (resumeTcs != null) resumeTcs.TrySetResult(false);
            }
            SaveSettings();
            base.OnFormClosing(e);
        }

        // ───────────────────────── 단축키 ─────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeysOk = Native.RegisterHotKey(Handle, HK_START, 0, (uint)Keys.F9);
            hotkeysOk &= Native.RegisterHotKey(Handle, HK_STOP, 0, (uint)Keys.F10);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HK_START);
            Native.UnregisterHotKey(Handle, HK_STOP);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_START) OnF9();
                else if (id == HK_STOP) RequestStop();
                return;
            }
            base.WndProc(ref m);
        }

        void OnF9()
        {
            if (countingDown) return;
            if (running && waiting) Resume(true);
            else if (running) RequestPause();
            else StartRun(true);
        }

        // ───────────────────────── 파일 목록 ─────────────────────────

        void OnFileDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !Busy && !draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnFileDragDrop(object sender, DragEventArgs e)
        {
            if (Busy || draggingOut) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) AddPaths(paths);
        }

        void AddFilesDialog()
        {
            if (Busy) return;
            using (var d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = "업로드할 파일 선택";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
            }
        }

        void AddFolderDialog()
        {
            if (Busy) return;
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "폴더 안의 모든 파일(하위 폴더 포함)을 추가합니다.";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
            }
        }

        static bool IsHidden(string path)
        {
            try { return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch { return true; }
        }

        static void Walk(string dir, List<string> output)
        {
            string[] fs, ds;
            try { fs = Directory.GetFiles(dir); ds = Directory.GetDirectories(dir); }
            catch { return; }
            Array.Sort(fs, new Comparison<string>(Native.StrCmpLogicalW));
            Array.Sort(ds, new Comparison<string>(Native.StrCmpLogicalW));
            foreach (string f in fs)
                if (!IsHidden(f)) output.Add(f);
            foreach (string d in ds)
                if (!IsHidden(d)) Walk(d, output);
        }

        void AddPaths(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) Walk(p, found);
                else if (File.Exists(p)) found.Add(p);
            }
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) existing.Add(f.Path);
            int added = 0;
            foreach (string p in found)
            {
                if (!existing.Add(p)) continue;
                long size = 0;
                try { size = new FileInfo(p).Length; } catch { }
                var fe = new FileEntry();
                fe.Path = p;
                fe.Size = size;
                files.Add(fe);
                added++;
            }
            ResetProgress();
            RefreshList();
            SetStatus(added + "개 파일을 추가했습니다." + (found.Count > added ? " (중복 " + (found.Count - added) + "개 제외)" : ""));
        }

        List<int> SelectedIndices()
        {
            var list = new List<int>();
            foreach (int i in lv.SelectedIndices) list.Add(i);
            list.Sort();
            return list;
        }

        void RemoveSelected()
        {
            if (Busy) return;
            var sel = SelectedIndices();
            for (int i = sel.Count - 1; i >= 0; i--) files.RemoveAt(sel[i]);
            ResetProgress();
            RefreshList();
        }

        void ClearAll()
        {
            if (Busy || files.Count == 0) return;
            files.Clear();
            ResetProgress();
            RefreshList();
        }

        void MoveSelected(int dir)
        {
            if (Busy) return;
            var sel = SelectedIndices();
            if (sel.Count == 0) return;
            if (dir < 0 && sel[0] == 0) return;
            if (dir > 0 && sel[sel.Count - 1] == files.Count - 1) return;
            if (dir > 0) sel.Reverse();
            var moved = new List<int>();
            foreach (int i in sel)
            {
                var tmp = files[i];
                files[i] = files[i + dir];
                files[i + dir] = tmp;
                moved.Add(i + dir);
            }
            ResetProgress();
            RefreshList();
            foreach (int i in moved) lv.Items[i].Selected = true;
            lv.EnsureVisible(moved[0]);
        }

        void SortByName()
        {
            if (Busy) return;
            files.Sort(delegate(FileEntry a, FileEntry b) { return Native.StrCmpLogicalW(Path.GetFileName(a.Path), Path.GetFileName(b.Path)); });
            ResetProgress();
            RefreshList();
        }

        void ResetProgress()
        {
            curBatch = 0;
            curStep = 0;
            foreach (var f in files) f.Status = "";
        }

        static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.#") + " MB";
            return (b / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }

        void RefreshList()
        {
            int size = (int)nudBatch.Value;
            batches.Clear();
            for (int i = 0; i < files.Count; i += size)
            {
                var b = new List<int>();
                for (int j = i; j < Math.Min(i + size, files.Count); j++) b.Add(j);
                batches.Add(b);
            }

            lv.BeginUpdate();
            lv.Items.Clear();
            var alt = Color.FromArgb(243, 246, 250);
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                int bi = i / size;
                var it = new ListViewItem((i + 1).ToString());
                it.SubItems.Add((bi + 1).ToString());
                it.SubItems.Add(Path.GetFileName(f.Path));
                it.SubItems.Add(FormatSize(f.Size));
                it.SubItems.Add(f.Status);
                it.SubItems.Add(Path.GetDirectoryName(f.Path));
                it.BackColor = bi % 2 == 1 ? alt : SystemColors.Window;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            lblEmpty.Visible = files.Count == 0;
            UpdateUi();
        }

        void SetFileStatus(List<int> batch, string status)
        {
            foreach (int i in batch)
            {
                files[i].Status = status;
                if (i < lv.Items.Count)
                {
                    lv.Items[i].SubItems[4].Text = status;
                    lv.Items[i].ForeColor = status == "전송 완료" || status == "드래그함" ? SystemColors.GrayText : SystemColors.WindowText;
                }
            }
            if (batch.Count > 0 && batch[0] < lv.Items.Count) lv.EnsureVisible(batch[batch.Count - 1]);
        }

        // ───────────────────────── 상태 / UI 갱신 ─────────────────────────

        void SetStatus(string s) { lblStatus.Text = s; }

        void UpdateUi()
        {
            bool busy = Busy;
            editBar.Enabled = !busy;
            nudBatch.Enabled = !busy;
            btnReset.Enabled = !busy;
            btnStart.Enabled = !countingDown && (!running || waiting);
            btnStart.Text = running && waiting ? "▶ 재개 (F9)" : "▶ 시작 (F9)";
            btnPause.Enabled = running && !waiting && !countingDown;
            btnStop.Enabled = busy;
            btnPrev.Enabled = !busy && curBatch > 0;
            btnNext.Enabled = !busy && curBatch < batches.Count;

            prog.Maximum = Math.Max(1, batches.Count);
            prog.Value = Math.Min(prog.Maximum, curBatch);
            Text = busy && batches.Count > 0 ? "[" + curBatch + "/" + batches.Count + "] " + AppTitle : AppTitle;

            if (batches.Count == 0)
                lblDrag.Text = "보낼 파일이 없습니다";
            else if (curBatch >= batches.Count)
                lblDrag.Text = "모든 배치를 보냈습니다 (" + batches.Count + "개)";
            else
                lblDrag.Text = "배치 " + (curBatch + 1) + " / " + batches.Count + "  ·  파일 " + batches[curBatch].Count + "개\r\n여기를 끌어서 채팅창에 놓으세요";
        }

        void StepBatch(int dir)
        {
            if (Busy) return;
            curBatch = Math.Max(0, Math.Min(batches.Count, curBatch + dir));
            curStep = 0;
            UpdateUi();
        }

        // ───────────────────────── 수동 드래그 ─────────────────────────

        void OnDragOutMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { dragArmed = true; dragStart = e.Location; }
        }

        void OnDragOutMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragArmed || Busy || e.Button != MouseButtons.Left) return;
            Size ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - dragStart.X) < ds.Width && Math.Abs(e.Y - dragStart.Y) < ds.Height) return;
            dragArmed = false;
            if (curBatch >= batches.Count) return;

            var batch = batches[curBatch];
            var paths = new List<string>();
            foreach (int i in batch) if (File.Exists(files[i].Path)) paths.Add(files[i].Path);
            if (paths.Count == 0) return;

            DragDropEffects result;
            draggingOut = true;
            try { result = lblDrag.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy | DragDropEffects.Link); }
            finally { draggingOut = false; }

            if (result != DragDropEffects.None)
            {
                SetFileStatus(batch, "드래그함");
                curBatch++;
                curStep = 0;
                UpdateUi();
                SetStatus("배치 " + curBatch + "을(를) 드래그했습니다. 채팅창에서 메시지를 보낸 뒤 다음 배치를 끌어 놓으세요.");
            }
            else
            {
                SetStatus("드롭이 확인되지 않았습니다. 실제로 첨부됐다면 [다음 배치 ▶]를 누르세요.");
            }
        }

        // ───────────────────────── 자동 업로드 ─────────────────────────

        void ShowHelp()
        {
            MessageBox.Show(this,
                "■ 사용 방법\r\n" +
                "1. 올릴 파일/폴더를 목록에 끌어다 놓거나 [파일 추가]로 고릅니다.\r\n" +
                "    목록 순서대로 설정한 개수(기본 3개)씩 묶여 올라갑니다.\r\n" +
                "2. 브라우저에서 Copilot / ChatGPT / Claude / Gemini 채팅 화면을 엽니다.\r\n" +
                "3. 시작하는 방법 (둘 중 하나)\r\n" +
                "    A) 채팅 입력창을 클릭한 상태에서 F9 → 바로 시작\r\n" +
                "    B) [▶ 시작] 클릭 → 카운트다운 동안 채팅 입력창 클릭\r\n" +
                "4. 프로그램이 [파일 붙여넣기 → 업로드 대기 → 메시지(\".\") 전송 → 응답 대기]를 반복합니다.\r\n\r\n" +
                "■ 단축키 (어느 창에서든 동작)\r\n" +
                "    F9  : 시작 / 일시정지 / 재개 (수동 모드에서는 다음 배치)\r\n" +
                "    F10 : 중지\r\n\r\n" +
                "■ 팁\r\n" +
                "• 응답이 끝나기 전에 다음 파일이 올라가면 '자동' 대기 시간을 늘리거나 '수동' 모드를 쓰세요.\r\n" +
                "• 메시지가 전송되지 않고 입력창에 남아 있으면 '업로드 대기' 시간을 늘리세요.\r\n" +
                "• 진행 중 다른 창으로 전환하면 엉뚱한 곳에 붙여넣지 않도록 자동으로 일시정지됩니다.\r\n" +
                "   채팅 입력창을 다시 클릭하고 F9를 누르면 멈춘 곳부터 이어집니다.\r\n" +
                "• 붙여넣기(Ctrl+V)로 파일이 첨부되지 않는 사이트는 오른쪽 아래 파란 영역을 끌어서\r\n" +
                "   채팅창에 놓으면 현재 배치 파일들이 한 번에 첨부됩니다.\r\n" +
                "• 작업이 끝나면 원래 클립보드의 텍스트를 복원합니다.",
                "사용법", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnStartButton()
        {
            if (running && waiting) Resume(false);
            else if (!Busy) StartRun(false);
        }

        void RequestPause()
        {
            if (!running || waiting) return;
            pauseReq = true;
            SetStatus("일시정지 요청됨 - 현재 단계를 마치고 멈춥니다...");
        }

        void RequestStop()
        {
            if (!Busy) return;
            stopReq = true;
            if (resumeTcs != null) resumeTcs.TrySetResult(false);
            SetStatus("중지하는 중...");
        }

        void Resume(bool viaHotkey)
        {
            if (waiting && resumeTcs != null) resumeTcs.TrySetResult(viaHotkey);
        }

        async void StartRun(bool viaHotkey)
        {
            if (Busy) return;
            if (files.Count == 0)
            {
                SetStatus("먼저 업로드할 파일을 추가하세요.");
                SystemSounds.Beep.Play();
                return;
            }
            if (curBatch >= batches.Count)
            {
                if (MessageBox.Show(this, "모든 배치를 이미 보냈습니다. 처음부터 다시 보낼까요?", "Bulk Uploader",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ResetProgress();
                RefreshList();
            }
            SaveSettings();

            stopReq = false;
            pauseReq = false;
            waiting = false;
            running = true;
            target = IntPtr.Zero;
            SaveClipboard();
            UpdateUi();
            try
            {
                IntPtr fg = Native.ForegroundRoot();
                if (viaHotkey && !Native.IsOwnProcess(fg)) target = fg;
                else await Countdown();
                await RunLoop();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "오류가 발생했습니다:\r\n" + ex.Message, "Bulk Uploader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                running = false;
                waiting = false;
                countingDown = false;
                RestoreClipboard();
                UpdateUi();
            }

            if (curBatch >= batches.Count)
            {
                SetStatus("완료! 파일 " + files.Count + "개를 " + batches.Count + "개 배치로 보냈습니다.");
                SystemSounds.Asterisk.Play();
            }
            else
            {
                SetStatus("중지됨 - " + curBatch + " / " + batches.Count + " 배치 전송. [▶ 시작]을 누르면 이어서 보냅니다.");
            }
            Native.Flash(Handle);
        }

        // 카운트다운 후 그 시점의 활성 창을 대상(채팅창)으로 잡는다
        async Task Countdown()
        {
            countingDown = true;
            UpdateUi();
            try
            {
                int secs = (int)nudCountdown.Value;
                for (int i = secs; i > 0 && !stopReq; i--)
                {
                    SetStatus(i + "초 후 시작합니다 - 지금 브라우저의 채팅 입력창을 클릭하세요!");
                    for (int t = 0; t < 10 && !stopReq; t++) await Task.Delay(100);
                }
                IntPtr fg = Native.ForegroundRoot();
                target = Native.IsOwnProcess(fg) ? IntPtr.Zero : fg;
            }
            finally
            {
                countingDown = false;
                UpdateUi();
            }
        }

        bool TargetOk()
        {
            return target != IntPtr.Zero && Native.ForegroundRoot() == target;
        }

        async Task WaitResume(string message)
        {
            pauseReq = false;
            waiting = true;
            resumeTcs = new TaskCompletionSource<bool>();
            SetStatus(message);
            UpdateUi();
            Native.Flash(Handle);
            bool viaHotkey = await resumeTcs.Task;
            resumeTcs = null;
            waiting = false;
            UpdateUi();
            if (stopReq) return;

            IntPtr fg = Native.ForegroundRoot();
            if (viaHotkey && !Native.IsOwnProcess(fg)) target = fg;
            else await Countdown();
        }

        // pause/stop 요청이 오면 즉시 반환
        async Task Wait(decimal seconds, string label)
        {
            var end = DateTime.Now.AddSeconds((double)seconds);
            while (!stopReq && !pauseReq)
            {
                double left = (end - DateTime.Now).TotalSeconds;
                if (left <= 0) break;
                SetStatus(label + " " + Math.Ceiling(left) + "초  (F9: 일시정지, F10: 중지)");
                await Task.Delay(100);
            }
        }

        async Task WaitModifiersReleased()
        {
            for (int i = 0; i < 60 && Native.ModifiersDown(); i++) await Task.Delay(50);
        }

        async Task RunLoop()
        {
            while (curBatch < batches.Count && !stopReq)
            {
                string where = "배치 " + (curBatch + 1) + "/" + batches.Count;

                if (pauseReq)
                {
                    await WaitResume("일시정지됨 (" + where + ") - 채팅 입력창을 클릭하고 F9를 누르면 이어서 진행합니다.");
                    continue;
                }
                if (target == IntPtr.Zero)
                {
                    await WaitResume("채팅 입력창이 선택되지 않았습니다 - 입력창을 클릭하고 F9를 누르세요.");
                    continue;
                }
                if (!TargetOk())
                {
                    await WaitResume("다른 창으로 전환되어 일시정지됨 (" + where + ") - 채팅 입력창을 다시 클릭하고 F9를 누르세요.");
                    continue;
                }

                var batch = batches[curBatch];
                await WaitModifiersReleased();
                if (!TargetOk()) continue;

                if (curStep == 0)
                {
                    var sc = new StringCollection();
                    var present = new List<int>();
                    foreach (int i in batch)
                    {
                        if (File.Exists(files[i].Path)) { sc.Add(files[i].Path); present.Add(i); }
                        else SetFileStatus(new List<int> { i }, "파일 없음");
                    }
                    if (sc.Count > 0)
                    {
                        var data = new DataObject();
                        data.SetFileDropList(sc);
                        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
                        Clipboard.SetDataObject(data, true, 10, 100);
                        await Task.Delay(150);
                        Native.Press(Keys.ControlKey, Keys.V);
                        SetFileStatus(present, "업로드 중");
                    }
                    curStep = 1;
                    UpdateUi();
                    await Wait(nudUpload.Value, where + " - 파일 " + sc.Count + "개 붙여넣음, 업로드 대기");
                    continue;
                }

                // curStep == 1: 메시지 전송
                bool last = curBatch == batches.Count - 1;
                string msg = last && txtFinal.Text.Length > 0 ? txtFinal.Text : txtMsg.Text;
                if (msg.Length > 0)
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, true, msg);
                    Clipboard.SetDataObject(data, true, 10, 100);
                    await Task.Delay(150);
                    Native.Press(Keys.ControlKey, Keys.V);
                    await Task.Delay(250);
                }
                if (cboSend.SelectedIndex == 1) Native.Press(Keys.ControlKey, Keys.Enter);
                else Native.Press(Keys.None, Keys.Enter);

                var done = new List<int>();
                foreach (int i in batch) if (files[i].Status == "업로드 중") done.Add(i);
                SetFileStatus(done, "전송 완료");
                curBatch++;
                curStep = 0;
                UpdateUi();
                if (curBatch >= batches.Count) break;

                string next = "배치 " + (curBatch + 1) + "/" + batches.Count;
                if (rbManual.Checked)
                    await WaitResume(where + " 전송 완료 - 응답이 끝나면 F9를 눌러 " + next + "을(를) 보냅니다.");
                else
                    await Wait(nudResp.Value, where + " 전송 완료 - 응답 대기 후 " + next + " 시작까지");
            }
        }

        void SaveClipboard()
        {
            savedClipboard = null;
            try { if (Clipboard.ContainsText()) savedClipboard = Clipboard.GetText(); } catch { }
        }

        void RestoreClipboard()
        {
            try
            {
                if (savedClipboard != null) Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, savedClipboard), true, 5, 100);
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var mutex = new Mutex(true, "BulkUploader.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Bulk Uploader가 이미 실행 중입니다.", "Bulk Uploader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm(args));
            }
        }
    }
}
