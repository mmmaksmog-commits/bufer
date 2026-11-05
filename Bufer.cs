// Буфер by mysik — история буфера обмена: текст, ссылки, картинки, файлы. Ctrl+Shift+V — быстрая вставка.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Mysik;
using IOPath = System.IO.Path;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("Буфер")]
[assembly: AssemblyDescription("Буфер by mysik — история буфера обмена")]
[assembly: AssemblyCompany("mysik")]
[assembly: AssemblyProduct("Буфер by mysik")]
[assembly: AssemblyCopyright("by mysik")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Bufer
{
    static class Program
    {
        [STAThread]
        static void Main() { Boot.Run("Буфер", "BuferByMysik_SingleInstance", () => new MainWindow()); }
    }

    static class Native
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312;
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

        // Ctrl+V в активное окно
        public static void Paste()
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }

        public static string OwnerApp()
        {
            try
            {
                var h = GetClipboardOwner();
                if (h == IntPtr.Zero) return null;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                using (var p = Process.GetProcessById((int)pid))
                {
                    string d = null;
                    try { d = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                    return string.IsNullOrWhiteSpace(d) ? p.ProcessName : d.Trim();
                }
            }
            catch { return null; }
        }
    }

    enum Kind { Text, Link, Image, Files }

    class Item
    {
        public Kind Kind;
        public string Text;          // текст, ссылка или пути файлов через \n
        public string ImageFile;     // для картинок
        public string Hash;
        public bool Pinned;
        public DateTime Time;
        public string Source;
        public BitmapSource Thumb;
    }

    // ------------------------------------------------------------------ хранилище

    class Store
    {
        public readonly string Dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bufer by mysik");
        string FilePath { get { return IOPath.Combine(Dir, "history.txt"); } }
        public string ImgDir { get { return IOPath.Combine(Dir, "img"); } }
        public readonly List<Item> Items = new List<Item>();
        public const int Max = 500;

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length < 7) continue;
                    var it = new Item
                    {
                        Kind = (Kind)int.Parse(p[0]), Pinned = p[1] == "1", Time = new DateTime(long.Parse(p[2])),
                        Source = Unb(p[3]), Hash = p[4], Text = Unb(p[5]), ImageFile = Unb(p[6])
                    };
                    if (it.Kind == Kind.Image && (it.ImageFile == null || !File.Exists(IOPath.Combine(ImgDir, it.ImageFile)))) continue;
                    Items.Add(it);
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = Items.Select(i => string.Join("\t", new[]
                {
                    ((int)i.Kind).ToString(), i.Pinned ? "1" : "0", i.Time.Ticks.ToString(), B(i.Source), i.Hash ?? "", B(i.Text), B(i.ImageFile)
                }));
                File.WriteAllLines(FilePath + ".tmp", lines, Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(FilePath + ".tmp", FilePath);
            }
            catch { }
        }

        static string B(string s) { return s == null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        static string Unb(string s) { return string.IsNullOrEmpty(s) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

        public void Trim()
        {
            var extra = Items.Where(i => !i.Pinned).Skip(Max).ToList();
            foreach (var i in extra) Remove(i);
        }

        public void Remove(Item i)
        {
            Items.Remove(i);
            if (i.ImageFile != null && !Items.Any(x => x.ImageFile == i.ImageFile))
                try { File.Delete(IOPath.Combine(ImgDir, i.ImageFile)); } catch { }
        }

        public static string HashOf(byte[] data)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").Substring(0, 16);
        }
    }

    // ------------------------------------------------------------------ быстрая вставка (Ctrl+Shift+V)

    class QuickPanel : Window
    {
        readonly MainWindow main;
        readonly TextBox search;
        readonly StackPanel list = new StackPanel();
        readonly ScrollViewer scroller;
        List<Item> shown = new List<Item>();
        int selected;
        public IntPtr Target;

        public QuickPanel(MainWindow main)
        {
            this.main = main;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 440;
            Height = 480;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            var root = new Border
            {
                Margin = new Thickness(12), CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(20, 22, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(46, 51, 62)), BorderThickness = new Thickness(1), Padding = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.6 }
            };
            var dock = new DockPanel();
            search = new TextBox { Style = main.Res("InputBox") };
            DockPanel.SetDock(search, Dock.Top);
            dock.Children.Add(search);
            var hint = new TextBlock
            {
                Text = "↑↓ выбрать · Enter вставить · 1–9 сразу · Esc закрыть", FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 117, 132)), Margin = new Thickness(4, 8, 4, 0)
            };
            DockPanel.SetDock(hint, Dock.Bottom);
            dock.Children.Add(hint);
            scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Content = list };
            dock.Children.Add(scroller);
            root.Child = dock;
            Content = root;

            search.TextChanged += (s, e) => { selected = 0; Render(); };
            PreviewKeyDown += OnKey;
            Deactivated += (s, e) => Hide();
        }

        public void Open()
        {
            Target = Native.GetForegroundWindow();
            var mouse = WF.Control.MousePosition;
            var src = PresentationSource.FromVisual(main);
            double k = src != null ? src.CompositionTarget.TransformFromDevice.M11 : 1;
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, Math.Min(mouse.X * k - 40, area.Right - Width));
            Top = Math.Max(area.Top, Math.Min(mouse.Y * k - 20, area.Bottom - Height));
            search.Text = "";
            selected = 0;
            Render();
            Show();
            Activate();
            search.Focus();
        }

        void Render()
        {
            list.Children.Clear();
            shown = main.Filtered(search.Text, -1).Take(60).ToList();
            if (shown.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "Ничего не найдено", Foreground = new SolidColorBrush(Color.FromRgb(139, 146, 160)), Margin = new Thickness(8, 14, 8, 14) });
                return;
            }
            if (selected >= shown.Count) selected = shown.Count - 1;
            for (int i = 0; i < shown.Count; i++)
            {
                int idx = i;
                var row = main.Preview(shown[i], true, i < 9 ? (i + 1).ToString() : null);
                var host = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Child = row, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
                host.Background = i == selected ? new SolidColorBrush(Color.FromRgb(38, 42, 51)) : Brushes.Transparent;
                host.MouseLeftButtonUp += (s, e) => Choose(idx);
                host.MouseEnter += (s, e) => { if (selected != idx) { selected = idx; Highlight(); } };
                list.Children.Add(host);
            }
        }

        void Highlight()
        {
            for (int i = 0; i < list.Children.Count; i++)
            {
                var b = list.Children[i] as Border;
                if (b != null) b.Background = i == selected ? new SolidColorBrush(Color.FromRgb(38, 42, 51)) : Brushes.Transparent;
            }
            var cur = list.Children.Count > selected ? list.Children[selected] as FrameworkElement : null;
            if (cur != null) cur.BringIntoView();
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            else if (e.Key == Key.Down) { if (selected < shown.Count - 1) { selected++; Highlight(); } e.Handled = true; }
            else if (e.Key == Key.Up) { if (selected > 0) { selected--; Highlight(); } e.Handled = true; }
            else if (e.Key == Key.Enter) { Choose(selected); e.Handled = true; }
            else if (search.Text.Length == 0 && e.Key >= Key.D1 && e.Key <= Key.D9) { Choose(e.Key - Key.D1); e.Handled = true; }
            else if (search.Text.Length == 0 && e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) { Choose(e.Key - Key.NumPad1); e.Handled = true; }
        }

        async void Choose(int i)
        {
            if (i < 0 || i >= shown.Count) return;
            var item = shown[i];
            Hide();
            if (!main.PutToClipboard(item)) return;
            if (Target != IntPtr.Zero) Native.SetForegroundWindow(Target);
            await Task.Delay(120);
            for (int n = 0; n < 20 && ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0); n++)
                await Task.Delay(25);  // ждём, пока отпустят Shift/Ctrl от сочетания
            Native.Paste();
        }
    }

    // ------------------------------------------------------------------ главное окно

    class MainWindow : ShellWindow
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly Store store = new Store();
        TextBox searchBox;
        TextBlock searchPlaceholder, emptyTitle, emptyText;
        WrapPanel filters;
        StackPanel itemsPanel, emptyState;
        CheckBox swAutostart;
        Button clearBtn;
        System.Windows.Documents.Run countRun;
        QuickPanel quick;
        WF.NotifyIcon tray;
        HwndSource hwnd;
        int filter;              // 0 всё, 1 текст, 2 ссылки, 3 картинки, 4 файлы, 5 закреплённые
        bool ignoreNext, reallyExit, startHidden, suppress, hotkeyOk;
        uint excludeFormat, ignoreFormat;
        DispatcherTimer saveTimer;

        public MainWindow()
        {
            LoadUi("Буфер by mysik", 460, 780);
            startHidden = Environment.GetCommandLineArgs().Any(a => a == "--tray");
            if (startHidden) { Opacity = 0; ShowInTaskbar = false; }

            searchBox = F<TextBox>("SearchBox");
            searchPlaceholder = F<TextBlock>("SearchPlaceholder");
            emptyTitle = F<TextBlock>("EmptyTitle");
            emptyText = F<TextBlock>("EmptyText");
            filters = F<WrapPanel>("Filters");
            itemsPanel = F<StackPanel>("ItemsPanel");
            emptyState = F<StackPanel>("EmptyState");
            swAutostart = F<CheckBox>("SwAutostart");
            clearBtn = F<Button>("ClearBtn");
            countRun = F<System.Windows.Documents.Run>("CountRun");

            searchBox.TextChanged += (s, e) => { searchPlaceholder.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
            clearBtn.Click += async (s, e) =>
            {
                if (!await Ask("Очистить историю?", "Удалятся все записи, кроме закреплённых.", "Очистить", "Отмена")) return;
                foreach (var i in store.Items.Where(x => !x.Pinned).ToList()) store.Remove(i);
                SaveSoon();
                Render();
            };
            swAutostart.Checked += (s, e) => SetAutostart(true);
            swAutostart.Unchecked += (s, e) => SetAutostart(false);

            string[] names = { "Всё", "Текст", "Ссылки", "Картинки", "Файлы", "Закреплённые" };
            var pill = S("Pill");
            for (int i = 0; i < names.Length; i++)
            {
                int f = i;
                var rb = new RadioButton { Style = pill, GroupName = "filter", IsChecked = i == 0, Content = new TextBlock { Text = names[i], FontSize = 12.5 }, MinWidth = 0 };
                rb.Checked += (s, e) => { filter = f; Render(); };
                filters.Children.Add(rb);
            }

            excludeFormat = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            ignoreFormat = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); store.Save(); };
            BuildTray();
        }

        public Style Res(string key) { return S(key); }

        public override void Init()
        {
            store.Load();
            foreach (var i in store.Items.Where(x => x.Kind == Kind.Image)) LoadThumb(i);
            suppress = true;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) swAutostart.IsChecked = k != null && k.GetValue("Bufer by mysik") != null;
            suppress = false;

            hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
            hwnd.AddHook(WndProc);
            Native.AddClipboardFormatListener(hwnd.Handle);
            hotkeyOk = Native.RegisterHotKey(hwnd.Handle, 1, 0x2 | 0x4 | 0x4000, 0x56); // Ctrl+Shift+V, без автоповтора
            quick = new QuickPanel(this);
            Render();
            if (!hotkeyOk) Toast("Ctrl+Shift+V занято другой программой — открывай историю кликом по значку в трее");
            if (startHidden) { Hide(); Opacity = 1; ShowInTaskbar = true; }
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_CLIPBOARDUPDATE)
            {
                Dispatcher.BeginInvoke(new Action(async () => { await Task.Delay(80); Capture(); }));
            }
            else if (msg == Native.WM_HOTKEY && w.ToInt32() == 1)
            {
                quick.Open();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // -------------------------------------------------- приём из буфера

        void Capture()
        {
            if (ignoreNext) { ignoreNext = false; return; }
            try
            {
                // менеджеры паролей помечают пароли этими форматами — такое не сохраняем
                if (Native.IsClipboardFormatAvailable(excludeFormat) || Native.IsClipboardFormatAvailable(ignoreFormat)) return;
                string source = Native.OwnerApp();
                Item item = null;
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                    if (files.Count > 0)
                        item = new Item { Kind = Kind.Files, Text = string.Join("\n", files), Hash = "f" + Store.HashOf(Encoding.UTF8.GetBytes(string.Join("\n", files))) };
                }
                else if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(t)) return;
                    if (t.Length > 200000) t = t.Substring(0, 200000);
                    bool link = Uri.IsWellFormedUriString(t.Trim(), UriKind.Absolute) && (t.Trim().StartsWith("http") || t.Trim().StartsWith("www")) && !t.Trim().Contains(" ");
                    item = new Item { Kind = link ? Kind.Link : Kind.Text, Text = t, Hash = "t" + Store.HashOf(Encoding.UTF8.GetBytes(t)) };
                }
                else if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img == null) return;
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    byte[] png;
                    using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
                    string hash = "i" + Store.HashOf(png);
                    var existing = store.Items.FirstOrDefault(x => x.Hash == hash);
                    if (existing == null)
                    {
                        Directory.CreateDirectory(store.ImgDir);
                        string name = hash + ".png";
                        File.WriteAllBytes(IOPath.Combine(store.ImgDir, name), png);
                        item = new Item { Kind = Kind.Image, ImageFile = name, Hash = hash, Text = img.PixelWidth + " × " + img.PixelHeight };
                        LoadThumb(item);
                    }
                    else item = existing;
                }
                if (item == null) return;

                var same = store.Items.FirstOrDefault(x => x.Hash == item.Hash);
                if (same != null)
                {
                    store.Items.Remove(same);
                    same.Time = DateTime.Now;
                    if (source != null) same.Source = source;
                    item = same;
                }
                else
                {
                    item.Time = DateTime.Now;
                    item.Source = source;
                }
                store.Items.Insert(0, item);
                store.Trim();
                SaveSoon();
                Render();
            }
            catch { }
        }

        void LoadThumb(Item i)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 300;
                bi.UriSource = new Uri(IOPath.Combine(store.ImgDir, i.ImageFile));
                bi.EndInit();
                bi.Freeze();
                i.Thumb = bi;
            }
            catch { }
        }

        public bool PutToClipboard(Item i)
        {
            try
            {
                ignoreNext = false; // своё копирование тоже поднимает элемент наверх — это нормально
                if (i.Kind == Kind.Image)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(IOPath.Combine(store.ImgDir, i.ImageFile));
                    bi.EndInit();
                    Clipboard.SetImage(bi);
                }
                else if (i.Kind == Kind.Files)
                {
                    var sc = new System.Collections.Specialized.StringCollection();
                    sc.AddRange(i.Text.Split('\n').Where(File.Exists).Concat(i.Text.Split('\n').Where(Directory.Exists)).ToArray());
                    if (sc.Count == 0) { Toast("Этих файлов уже нет на месте"); return false; }
                    Clipboard.SetFileDropList(sc);
                }
                else Clipboard.SetText(i.Text);
                return true;
            }
            catch (Exception ex) { Toast("Буфер занят другой программой: " + ex.Message); return false; }
        }

        void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        // -------------------------------------------------- список

        public IEnumerable<Item> Filtered(string query, int f)
        {
            if (f < 0) f = 0;
            IEnumerable<Item> src = store.Items;
            if (f == 1) src = src.Where(i => i.Kind == Kind.Text);
            else if (f == 2) src = src.Where(i => i.Kind == Kind.Link);
            else if (f == 3) src = src.Where(i => i.Kind == Kind.Image);
            else if (f == 4) src = src.Where(i => i.Kind == Kind.Files);
            else if (f == 5) src = src.Where(i => i.Pinned);
            query = (query ?? "").Trim();
            if (query.Length > 0)
                src = src.Where(i => (i.Text ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                     (i.Source ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            return src.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Time);
        }

        void Render()
        {
            itemsPanel.Children.Clear();
            var items = Filtered(searchBox.Text, filter).Take(150).ToList();
            foreach (var i in items) itemsPanel.Children.Add(Card(i));
            bool empty = items.Count == 0;
            emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                bool any = store.Items.Count > 0;
                emptyTitle.Text = any ? "Ничего не найдено" : "Скопируй что-нибудь";
                emptyText.Text = any ? "Попробуй другой запрос или фильтр" : "Всё, что ты копируешь — текст, ссылки, картинки, файлы — будет сохраняться здесь";
            }
            int pinned = store.Items.Count(i => i.Pinned);
            countRun.Text = Plural(store.Items.Count, "запись", "записи", "записей") + (pinned > 0 ? ", закреплено " + pinned : "");
        }

        // Короткое представление записи — и для списка, и для окна быстрой вставки.
        public FrameworkElement Preview(Item i, bool compact, string number)
        {
            var g = Columns(Auto, Star);
            if (number != null)
            {
                var nb = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Br(CCard2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                nb.Child = new TextBlock { Text = number, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Br(CSub), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(nb);
            }
            var body = new StackPanel();
            if (i.Kind == Kind.Image && i.Thumb != null)
            {
                body.Children.Add(new Border
                {
                    Height = compact ? 56 : 110, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Min(compact ? 160 : 360, (compact ? 56 : 110) * i.Thumb.PixelWidth / Math.Max(1.0, i.Thumb.PixelHeight)),
                    Background = new ImageBrush(i.Thumb) { Stretch = Stretch.UniformToFill }
                });
            }
            else
            {
                string text = i.Kind == Kind.Files ? string.Join(", ", i.Text.Split('\n').Select(p => IOPath.GetFileName(p.TrimEnd('\\')))) : i.Text;
                text = text.Replace("\r", "").Trim();
                if (text.Length > 600) text = text.Substring(0, 600);
                var t = new TextBlock
                {
                    Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = compact ? 38 : 58, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Br(i.Kind == Kind.Link ? CGreen : CText), FontSize = 13
                };
                if (i.Kind == Kind.Files) t.FontWeight = FontWeights.SemiBold;
                body.Children.Add(t);
            }
            string kind = i.Kind == Kind.Link ? "ссылка" : i.Kind == Kind.Image ? "картинка " + i.Text : i.Kind == Kind.Files ? Plural(i.Text.Split('\n').Length, "файл", "файла", "файлов") : "текст";
            var meta = Txt((i.Pinned ? "📌 " : "") + kind + " · " + When(i.Time) + (i.Source != null ? " · " + i.Source : ""), 11, CFaint, false);
            meta.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(meta);
            g.Children.Add(At(body, 1));
            return g;
        }

        UIElement Card(Item i)
        {
            var g = Columns(Star, Auto);
            g.Children.Add(Preview(i, false, null));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden, Margin = new Thickness(8, -4, -6, 0) };
            actions.Children.Add(ActionBtn(i.Pinned ? "" : "", i.Pinned ? "Открепить" : "Закрепить", () => { i.Pinned = !i.Pinned; SaveSoon(); Render(); }));
            actions.Children.Add(ActionBtn("", "Удалить", () => { store.Remove(i); SaveSoon(); Render(); }));
            g.Children.Add(At(actions, 1));

            var card = new Border
            {
                Background = Br(CCard), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 8),
                Child = g, Cursor = Cursors.Hand, ToolTip = "Нажми, чтобы скопировать"
            };
            if (i.Pinned) { card.BorderBrush = Br(Hex("#2B5A43")); card.BorderThickness = new Thickness(1); }
            card.MouseEnter += (s, e) => { card.Background = Br(Hex("#21252D")); actions.Visibility = Visibility.Visible; };
            card.MouseLeave += (s, e) => { card.Background = Br(CCard); actions.Visibility = Visibility.Hidden; };
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (e.OriginalSource is TextBlock && ((TextBlock)e.OriginalSource).FontFamily == Icons) return;
                if (PutToClipboard(i)) Toast("Скопировано — вставляй где нужно");
            };
            return card;
        }

        Button ActionBtn(string glyph, string tip, Action a)
        {
            var b = new Button { Style = S("IconBtn"), Content = Glyph(glyph, 12, CSub), ToolTip = tip, Width = 30, Height = 30 };
            b.Click += (s, e) => { e.Handled = true; a(); };
            return b;
        }

        static string When(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "только что";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " мин назад";
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "вчера " + t.ToString("HH:mm");
            return t.ToString("d MMM HH:mm", Ru);
        }

        // -------------------------------------------------- трей

        void BuildTray()
        {
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Открыть историю", null, (s, e) => ShowMain());
            open.Font = new SD.Font(open.Font, SD.FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(new WF.ToolStripMenuItem("Выход", null, (s, e) => { reallyExit = true; Close(); }));
            tray = new WF.NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Буфер by mysik — Ctrl+Shift+V" };
            try { tray.Icon = SD.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { tray.Icon = SD.SystemIcons.Application; }
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(); };
        }

        void ShowMain()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            searchBox.Focus();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            saveTimer.Stop();
            store.Save();
            if (hwnd != null)
            {
                Native.RemoveClipboardFormatListener(hwnd.Handle);
                Native.UnregisterHotKey(hwnd.Handle, 1);
            }
            tray.Visible = false;
            tray.Dispose();
            if (quick != null) quick.Close();
            base.OnClosing(e);
        }

        void SetAutostart(bool on)
        {
            if (suppress) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Bufer by mysik", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                    else k.DeleteValue("Bufer by mysik", false);
                }
                Toast(on ? "Будет запускаться вместе с Windows" : "Автозапуск выключен");
            }
            catch (Exception ex) { Toast("Не получилось: " + ex.Message); }
        }
    }
}
