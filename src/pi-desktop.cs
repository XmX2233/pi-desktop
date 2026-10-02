// Pi 桌面 — pi-web 原生桌面壳（WinForms + WebView2）
// 启动 pi-web 服务 → 桌面窗口加载 http://127.0.0.1:30141 → 关窗停服务
// DPI 感知在 app.manifest（PerMonitorV2），避免高分辨率下窗口模糊
// 编译：见 build.cmd（csc /target:winexe /win32icon /win32manifest）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PiDesktop
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PiDesktopForm());
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(PiDesktopForm.AppDataDir);
                    File.WriteAllText(Path.Combine(PiDesktopForm.AppDataDir, "crash.log"), ex.ToString());
                }
                catch { }
                throw;
            }
        }
    }

    class PiDesktopForm : Form
    {
        const string URL = "http://127.0.0.1:30141";
        static readonly string ForkDir = @"D:\.Projects\projects\pi-web-fork";
        internal static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiDesktop");
        static readonly string UserDataDir = Path.Combine(AppDataDir, "UserData");
        static readonly string LogFile = Path.Combine(AppDataDir, "pi-desktop.log");
        static readonly string[] NodeCandidates = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pi-node", "current", "node.exe"),
            "node.exe",
        };

        WebView2 _webView;
        CoreWebView2Environment _env;
        readonly List<Form> _children = new List<Form>();
        SettingsWindow _settingsWindow;
        Process _server;
        bool _serverByUs;   // true if this instance started the server (so we must stop it on close)
        bool _closing;
        bool _navigated;

        // 深色标题栏（DWMWA_USE_IMMERSIVE_DARK_MODE）：让原生标题栏融入应用的深色界面，不再出现白杠
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryEnableDarkTitleBar(Handle);
        }

        internal static void TryEnableDarkTitleBar(IntPtr handle)
        {
            try
            {
                const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                int value = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            }
            catch { /* 老系统不支持则保持默认 */ }
        }

        public PiDesktopForm()
        {
            Text = "Pi 桌面";
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            Load += async (s, e) => await InitAsync();
            FormClosing += (s, e) => OnClosing();
        }

        static bool IsServerUp()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(URL);
                req.Timeout = 1500;
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        static string ResolveNode()
        {
            foreach (var p in NodeCandidates)
                if (File.Exists(p)) return p;
            return "node.exe";
        }

        // WebView2 运行时目录：这台机器上自动发现会挂起，必须显式指定。
        // 探测两个安装位置（x64/x86），取最高版本号目录；找不到则回退 null（自动发现）。
        static string ResolveRuntimeDir()
        {
            var roots = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application"),
            };
            Version best = null; string bestDir = null;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name == "SetupMetrics") continue;
                    Version v;
                    if (Version.TryParse(name, out v) && (best == null || v > best) && File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                    {
                        best = v; bestDir = dir;
                    }
                }
            }
            return bestDir;
        }

        void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); }
            catch { }
        }

        void StartServer()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var psi = new ProcessStartInfo
                {
                    FileName = ResolveNode(),
                    Arguments = "bin/pi-web.js --no-open",
                    WorkingDirectory = ForkDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _server = Process.Start(psi);
                _server.OutputDataReceived += (s, e) => Log(e.Data);
                _server.ErrorDataReceived += (s, e) => Log(e.Data);
                _server.BeginOutputReadLine();
                _server.BeginErrorReadLine();
                Log("== Pi 桌面启动，服务 PID " + _server.Id + " ==");
            }
            catch (Exception ex)
            {
                Log("服务启动失败：" + ex);
                MessageBox.Show(this, "Pi 服务启动失败：" + ex.Message, "Pi 桌面");
            }
        }

        void StopServer()
        {
            if (_server == null || _server.HasExited) return;
            try
            {
                // 结束整棵进程树（pi-web.js -> next start）
                Process.Start(new ProcessStartInfo("taskkill", "/PID " + _server.Id + " /T /F")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                }).WaitForExit(5000);
            }
            catch { }
        }

        async Task InitAsync()
        {
            try
            {
                _env = await CoreWebView2Environment.CreateAsync(ResolveRuntimeDir(), UserDataDir, null);
                await _webView.EnsureCoreWebView2Async(_env);
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                // 标记桌面壳环境：pi-web 检测到后，点「常规/模型/技能」直接 window.open 弹独立设置窗口
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__PI_DESKTOP__ = true;");
                // 页面里 window.open（设置面板「移出到桌面」）→ 弹出独立系统窗口，可拖到桌面任意位置
                _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
                {
                    args.Handled = true;
                    try
                    {
                        // 复用同一个设置窗口：已开着就导航到新板块，不重复开
                        if (_settingsWindow == null || _settingsWindow.IsDisposed)
                        {
                            _settingsWindow = new SettingsWindow(_env, args.Uri);
                            _settingsWindow.FormClosed += (s2, e2) => _settingsWindow = null;
                            _children.Add(_settingsWindow);
                            _settingsWindow.FormClosed += (s2, e2) => _children.Remove(_settingsWindow);
                            _settingsWindow.Show();
                        }
                        else
                        {
                            _settingsWindow.NavigateTo(args.Uri);
                            _settingsWindow.Activate();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("弹出设置窗口失败：" + ex);
                        MessageBox.Show(this, "弹出设置窗口失败：" + ex.Message, "Pi 桌面");
                    }
                };
                _webView.CoreWebView2.NavigateToString(
                    "<body style='background:#1e2a4a;color:#9fb0d0;font-family:Microsoft YaHei;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;font-size:18px'>Pi 桌面 · 正在启动服务…</body>");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Pi 桌面 - WebView2 初始化失败");
                return;
            }

            if (IsServerUp())
            {
                // 已有健康实例，直接复用
                _serverByUs = false;
                Navigate();
                return;
            }

            _serverByUs = true;
            StartServer();
            await WaitReadyThenNavigate();
        }

        async Task WaitReadyThenNavigate()
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!_closing && DateTime.UtcNow < deadline)
            {
                if (_server != null && _server.HasExited)
                {
                    Log("服务提前退出");
                    MessageBox.Show(this, "Pi 服务启动失败，详见日志：" + LogFile, "Pi 桌面");
                    return;
                }
                if (IsServerUp())
                {
                    Navigate();
                    return;
                }
                await Task.Delay(1000);
            }
            if (!_closing)
            {
                Log("服务启动超时");
                MessageBox.Show(this, "Pi 服务启动超时，详见日志：" + LogFile, "Pi 桌面");
            }
        }

        void Navigate()
        {
            if (_navigated || _closing) return;
            _navigated = true;
            try { _webView.CoreWebView2.Navigate(URL); }
            catch { }
        }

        void OnClosing()
        {
            _closing = true;
            // 关闭所有移出的设置窗口
            foreach (var child in new List<Form>(_children))
            {
                try { child.Close(); } catch { }
            }
            if (_serverByUs) StopServer();
        }
    }

    // 移出到桌面的设置窗口：普通顶层窗口，可拖到桌面任意位置；与主窗口共用 WebView2 环境（同 profile）
    class SettingsWindow : Form
    {
        WebView2 _wv;

        public SettingsWindow(CoreWebView2Environment env, string uri)
        {
            Text = "Pi 设置";
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_wv);
            FormClosed += (s, e) =>
            {
                try { _wv.Dispose(); } catch { }
            };

            Load += async (s, e) =>
            {
                try
                {
                    await _wv.EnsureCoreWebView2Async(env);
                    _wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    // 设置面板点「×」时页面会尝试 window.close()（弹窗环境可关）；
                    // 壳里 window.close 无 opener 会被浏览器拦截，页面 fallback 导航到首页 → 这里直接关闭窗口
                    _wv.CoreWebView2.NavigationStarting += (s2, e2) =>
                    {
                        try
                        {
                            var u = new Uri(e2.Uri);
                            if (u.PathAndQuery == "/" || u.PathAndQuery == "")
                            {
                                BeginInvoke(new Action(() => Close()));
                            }
                        }
                        catch { }
                    };
                    _wv.CoreWebView2.Navigate(uri);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Pi 设置");
                }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PiDesktopForm.TryEnableDarkTitleBar(Handle);
        }

        public void NavigateTo(string uri)
        {
            try
            {
                if (_wv != null && _wv.CoreWebView2 != null) _wv.CoreWebView2.Navigate(uri);
            }
            catch { }
        }
    }
}
