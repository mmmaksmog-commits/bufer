// Общая основа программ by mysik: окно со своей рамкой, диалоги, уведомления, запуск процессов, форматирование.
// C# 5 / .NET Framework 4.x — собирается встроенным в Windows компилятором.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mysik
{
    static class Boot
    {
        // Запуск приложения: один экземпляр, TLS 1.2, обработка ошибок.
        public static void Run(string appName, string mutexName, Func<ShellWindow> make)
        {
            bool created;
            using (var mutex = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show(appName + " уже открыт.", appName + " by mysik", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | (SecurityProtocolType)192;
                ServicePointManager.DefaultConnectionLimit = 32;
                System.Windows.Forms.Application.EnableVisualStyles();

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show("Ошибка: " + e.Exception.Message, appName + " by mysik", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                app.Startup += (s, e) =>
                {
                    ShellWindow w;
                    try { w = make(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Не удалось открыть окно:\n" + ex, appName + " by mysik", MessageBoxButton.OK, MessageBoxImage.Error);
                        app.Shutdown();
                        return;
                    }
                    app.MainWindow = w;
                    w.Show();
                    w.Init();
                };
                app.Run();
            }
        }
    }

    abstract class ShellWindow : Window
    {
        public static readonly Color CGreen = Hex("#2FB36E");
        public static readonly Color CRed = Hex("#F0525F");
        public static readonly Color CAmber = Hex("#F2B33D");
        public static readonly Color CIdle = Hex("#4A505C");
        public static readonly Color CSub = Hex("#8B92A0");
        public static readonly Color CFaint = Hex("#6E7584");
        public static readonly Color CText = Hex("#E8EAEE");
        public static readonly Color CCard = Hex("#1C1F26");
        public static readonly Color CCard2 = Hex("#262A33");
        public static readonly Color CLine = Hex("#2A2E37");
        public static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        protected FrameworkElement Root;
        protected FontFamily Icons;

        FrameworkElement overlay;
        TextBlock dlgTitle, dlgText, snackText, logText;
        Button dlgYes, dlgNo;
        Border snack;
        TextBox logBox;
        TaskCompletionSource<bool> dlgTcs;
        int snackVersion, signatureClicks;

        public abstract void Init();

        // Загружает разметку окна и вставляет в неё общую тему.
        protected void LoadUi(string title, double width, double height)
        {
            var asm = Assembly.GetExecutingAssembly();
            string theme = ReadResource(asm, "Theme.xaml");
            string page = ReadResource(asm, "MainWindow.xaml");
            int a = theme.IndexOf('>', theme.IndexOf("<ResourceDictionary", StringComparison.Ordinal)) + 1;
            int b = theme.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal);
            page = page.Replace("<!--THEME-->", theme.Substring(a, b - a));
            Root = (FrameworkElement)XamlReader.Parse(page);

            Title = title;
            Width = width;
            Height = height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Content = Root;
            Icons = (FontFamily)Root.FindResource("Icons");

            var titleBar = F<FrameworkElement>("TitleBar");
            titleBar.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
            F<Button>("MinBtn").Click += (s, e) => WindowState = WindowState.Minimized;
            F<Button>("CloseBtn").Click += (s, e) => Close();

            overlay = F<FrameworkElement>("Overlay");
            dlgTitle = F<TextBlock>("DlgTitle");
            dlgText = F<TextBlock>("DlgText");
            dlgYes = F<Button>("DlgYes");
            dlgNo = F<Button>("DlgNo");
            snack = F<Border>("Snack");
            snackText = F<TextBlock>("SnackText");
            logBox = Root.FindName("LogBox") as TextBox;

            dlgYes.Click += (s, e) => CloseDialog(true);
            dlgNo.Click += (s, e) => CloseDialog(false);
            overlay.MouseLeftButtonDown += (s, e) => { if (ReferenceEquals(e.OriginalSource, overlay)) CloseDialog(false); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && overlay.Visibility == Visibility.Visible) CloseDialog(false); };

            var sig = Root.FindName("Signature") as System.Windows.Documents.Run;
            if (sig != null)
                sig.MouseLeftButtonDown += (s, e) =>
                {
                    signatureClicks++;
                    Toast(signatureClicks % 5 == 0 ? "Ты нашёл пасхалку. Привет от mysik!" : title);
                };
        }

        static string ReadResource(Assembly asm, string name)
        {
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("Нет встроенного файла " + name + " — пересобери программу через build.bat");
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        protected T F<T>(string name) where T : class
        {
            var o = Root.FindName(name) as T;
            if (o == null) throw new Exception("В разметке нет элемента " + name);
            return o;
        }

        protected Style S(string key) { return (Style)Root.FindResource(key); }

        // Вкладки: показывает одну страницу из набора.
        protected void ShowOnly(FrameworkElement page, params FrameworkElement[] all)
        {
            foreach (var p in all) p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        protected bool DialogOpen { get { return overlay.Visibility == Visibility.Visible; } }

        protected Task<bool> Ask(string title, string text, string yes, string no)
        {
            if (dlgTcs != null) { var old = dlgTcs; dlgTcs = null; old.TrySetResult(false); }
            if (!IsVisible) Show();
            dlgTitle.Text = title;
            dlgText.Text = text ?? "";
            dlgText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            dlgYes.Content = yes;
            dlgNo.Visibility = no != null ? Visibility.Visible : Visibility.Collapsed;
            if (no != null) dlgNo.Content = no;
            Grid.SetColumn(dlgYes, no == null ? 0 : 2);
            Grid.SetColumnSpan(dlgYes, no == null ? 3 : 1);

            overlay.Visibility = Visibility.Visible;
            overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            dlgTcs = new TaskCompletionSource<bool>();
            return dlgTcs.Task;
        }

        void CloseDialog(bool result)
        {
            overlay.Visibility = Visibility.Collapsed;
            var t = dlgTcs;
            dlgTcs = null;
            if (t != null) t.TrySetResult(result);
        }

        protected async void Toast(string msg)
        {
            int my = ++snackVersion;
            snackText.Text = msg;
            snack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            await Task.Delay(3200);
            if (my == snackVersion)
                snack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        }

        protected void Log(string msg)
        {
            if (logBox == null) return;
            if (logBox.Text.Length > 60000) logBox.Text = logBox.Text.Substring(30000);
            logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
            logBox.ScrollToEnd();
        }

        protected void UI(Action a)
        {
            try { Dispatcher.BeginInvoke(a); } catch { }
        }

        // -------------------------------------------------- построение элементов из кода

        protected TextBlock Txt(string text, double size, Color color, bool bold)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, Foreground = Br(color), TextTrimming = TextTrimming.CharacterEllipsis };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        protected TextBlock Glyph(string glyph, double size, Color color)
        {
            return new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = Br(color), VerticalAlignment = VerticalAlignment.Center };
        }

        protected static Grid Columns(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        protected static GridLength Auto { get { return GridLength.Auto; } }
        protected static GridLength Star { get { return new GridLength(1, GridUnitType.Star); } }

        protected static T At<T>(T el, int col) where T : UIElement { Grid.SetColumn(el, col); return el; }

        // -------------------------------------------------- форматирование

        public static Color Hex(string s) { return (Color)ColorConverter.ConvertFromString(s); }

        public static SolidColorBrush Br(Color c) { return new SolidColorBrush(c); }

        public static string FmtBytes(double b)
        {
            if (b < 0) b = 0;
            if (b < 1024) return ((int)b) + " Б";
            if (b < 1024 * 1024) return (b / 1024).ToString("0", Ru) + " КБ";
            if (b < 1024.0 * 1024 * 1024) return (b / 1024 / 1024).ToString(b < 100 * 1024 * 1024 ? "0.0" : "0", Ru) + " МБ";
            return (b / 1024 / 1024 / 1024).ToString("0.00", Ru) + " ГБ";
        }

        public static string FmtTime(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(sec)));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int a = Math.Abs(n) % 100, b = a % 10;
            string w = (a > 10 && a < 20) ? many : b == 1 ? one : (b >= 2 && b <= 4) ? few : many;
            return n + " " + w;
        }

        public static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, Inv, out d) ? d : -1;
        }
    }

    // ------------------------------------------------------------------ запуск программ

    class Runner
    {
        public Process P;
        public bool Killed;

        [DllImport("kernel32.dll")]
        static extern uint GetOEMCP();

        // utf8 = true для yt-dlp/ffmpeg, false — для системных утилит (netsh, powercfg), они пишут в OEM-кодировке.
        public Task<int> Run(string exe, string args, Action<string> onLine, bool utf8, string workDir)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Encoding enc = Encoding.UTF8;
            if (!utf8) { try { enc = Encoding.GetEncoding((int)GetOEMCP()); } catch { } }
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (workDir != null) psi.WorkingDirectory = workDir;

            var p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            P = p;

            return Task.Run(() =>
            {
                p.WaitForExit();
                int code = -1;
                try { code = p.ExitCode; } catch { }
                return code;
            });
        }

        // Удобный вызов: запустить и собрать весь вывод.
        public static async Task<KeyValuePair<int, string>> Capture(string exe, string args, bool utf8)
        {
            var sb = new StringBuilder();
            int code;
            try
            {
                code = await new Runner().Run(exe, args, l => { lock (sb) sb.AppendLine(l); }, utf8, null);
            }
            catch (Exception ex) { code = -1; sb.AppendLine(ex.Message); }
            return new KeyValuePair<int, string>(code, sb.ToString());
        }

        public static Task<KeyValuePair<int, string>> PowerShell(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; " + script));
            return Capture("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, true);
        }

        public void Kill()
        {
            Killed = true;
            var p = P;
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                var k = new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F");
                k.UseShellExecute = false;
                k.CreateNoWindow = true;
                using (var kp = Process.Start(k)) kp.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // Кавычки для командной строки Windows.
        public static string Q(string s)
        {
            if (s == null) s = "";
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(c); }
                bs = 0;
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
