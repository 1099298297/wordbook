using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Wordbook;

/// <summary>在独立 WPF 线程上运行记录弹窗，窗口可反复开关。</summary>
public class CaptureHost
{
    private Thread _thread;
    private Dispatcher _dispatcher;
    private System.Windows.Application _app;
    private CaptureWindow _window;
    private readonly ManualResetEventSlim _ready = new(false);

    public void Toggle(Config cfg, IntPtr prevHwnd)
    {
        Diag.Log("Toggle called");
        EnsureThread();
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_window != null && _window.IsVisible)
                {
                    Diag.Log("Toggle: closing existing window");
                    _window.Close();
                    _window = null;
                    return;
                }
                Diag.Log("Toggle: creating window");
                _window = new CaptureWindow(cfg, prevHwnd);
                _window.Closed += (_, _) => { Diag.Log("Window closed"); _window = null; };
                _window.Show();
                _window.Activate();
                Diag.Log("Toggle: window shown");
            });
        }
        catch (Exception ex)
        {
            Diag.Log("Toggle error: " + ex);
            throw;
        }
    }

    public void Stop()
    {
        if (_thread == null || !_thread.IsAlive) return;
        try
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            _thread.Join(1500);
        }
        catch (Exception ex) { Diag.Log("Stop error: " + ex.Message); }
    }

    private void EnsureThread()
    {
        if (_thread != null && _thread.IsAlive) return;
        _ready.Reset();
        _thread = new Thread(() =>
        {
            try
            {
                _app = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
                _dispatcher = Dispatcher.CurrentDispatcher;
                _ready.Set();
                Diag.Log("WPF dispatcher running");
                _app.Run();
            }
            catch (Exception ex)
            {
                Diag.Log("WPF thread error: " + ex);
                try { _ready.Set(); } catch { /* 忽略 */ }
            }
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait(2000);
    }
}

public class CaptureWindow : Window
{
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(91, 91, 240));
    private static readonly Brush AccentSoftBrush = new SolidColorBrush(Color.FromRgb(233, 233, 253));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(210, 70, 70));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(110, 116, 132));

    private readonly Config _cfg;
    private readonly IntPtr _prevHwnd;
    private readonly TextBox _input;
    private readonly TextBlock _hint;
    private readonly ScrollViewer _sentenceScroll;
    private readonly WrapPanel _sentencePanel;
    private readonly HashSet<string> _selectedWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _status;
    private readonly Button _saveBtn;
    private readonly DispatcherTimer _parseTimer;
    private bool _isSentenceMode;

    public CaptureWindow(Config cfg, IntPtr prevHwnd)
    {
        _cfg = cfg;
        _prevHwnd = prevHwnd;
        Width = 520;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(252, 253, 255)),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 228, 240)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26, ShadowDepth = 5, Opacity = 0.25, Color = Color.FromRgb(20, 25, 50),
            },
        };
        var root = new StackPanel();
        border.Child = root;
        Content = border;

        root.Children.Add(new TextBlock
        {
            Text = "记录生词",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 40, 55)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(new TextBlock
        {
            Text = "粘贴或输入单词 / 句子，句子可点击选词",
            Foreground = MutedBrush,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _input = new TextBox
        {
            FontSize = 15,
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 46,
            MaxHeight = 100,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(244, 246, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 238)),
            BorderThickness = new Thickness(1),
        };
        root.Children.Add(_input);

        _hint = new TextBlock
        {
            Text = "点击句子中的单词即可选中（可多选，再点一次取消）：",
            Foreground = MutedBrush,
            FontSize = 12.5,
            Margin = new Thickness(0, 10, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_hint);

        _sentencePanel = new WrapPanel();
        _sentenceScroll = new ScrollViewer
        {
            Content = _sentencePanel,
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 2),
        };
        root.Children.Add(_sentenceScroll);

        _status = new TextBlock
        {
            Foreground = ErrorBrush,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_status);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancelBtn = MakeButton("取消 (Esc)", false);
        cancelBtn.Click += (_, _) => Close();
        _saveBtn = MakeButton("保存", true);
        _saveBtn.Click += async (_, _) => await SaveAsync();
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(_saveBtn);
        root.Children.Add(btnRow);

        _input.Text = CleanClipboard();
        _input.TextChanged += (_, _) => ScheduleParse();
        _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _parseTimer.Tick += (_, _) => { _parseTimer.Stop(); ParseInput(); };

        PreviewKeyDown += OnPreviewKey;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); ParseInput(); };
        SourceInitialized += (_, _) =>
            Dispatcher.BeginInvoke(new Action(PositionNearCursor), DispatcherPriority.Loaded);
        Closed += (_, _) => RestoreFocus();
    }

    private static string CleanClipboard()
    {
        try
        {
            var t = System.Windows.Clipboard.GetText()?.Trim();
            return string.IsNullOrWhiteSpace(t) ? "" : t;
        }
        catch { return ""; }
    }

    private void ScheduleParse()
    {
        _parseTimer.Stop();
        _parseTimer.Start();
    }

    private void ParseInput()
    {
        var text = _input.Text.Trim();
        _isSentenceMode = LooksLikeSentence(text);
        _sentenceScroll.Visibility = _isSentenceMode ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = _isSentenceMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_isSentenceMode)
        {
            _selectedWords.Clear();
            _sentencePanel.Children.Clear();
            _status.Visibility = Visibility.Collapsed;
            UpdateSaveLabel();
            return;
        }

        // 重新渲染句子：每个英文单词是可点击的按钮，标点与空格原样保留
        var stillSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _selectedWords)
            if (text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) stillSelected.Add(w);
        _selectedWords.Clear();
        foreach (var w in stillSelected) _selectedWords.Add(w);

        _sentencePanel.Children.Clear();
        foreach (var part in SplitTokens(text))
        {
            if (IsWordToken(part))
            {
                var word = part;
                var btn = new Button
                {
                    Content = word,
                    FontSize = 14.5,
                    FontWeight = FontWeights.SemiBold,
                    Padding = new Thickness(4, 1, 4, 2),
                    Margin = new Thickness(0, 2, 2, 2),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(45, 51, 75)),
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                var isOn = _selectedWords.Contains(word);
                ApplyWordStyle(btn, isOn);
                btn.Click += (_, _) =>
                {
                    if (_selectedWords.Contains(word)) _selectedWords.Remove(word);
                    else _selectedWords.Add(word);
                    ApplyWordStyle(btn, _selectedWords.Contains(word));
                    _status.Visibility = Visibility.Collapsed;
                    UpdateSaveLabel();
                };
                _sentencePanel.Children.Add(btn);
            }
            else
            {
                var sep = part;
                _sentencePanel.Children.Add(new TextBlock
                {
                    Text = sep,
                    FontSize = 15,
                    Margin = new Thickness(0, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 86, 110)),
                });
            }
        }
        _status.Visibility = Visibility.Collapsed;
        UpdateSaveLabel();
    }

    private static void ApplyWordStyle(Button btn, bool on)
    {
        if (on)
        {
            btn.Background = AccentBrush;
            btn.Foreground = Brushes.White;
            btn.BorderThickness = new Thickness(0);
        }
        else
        {
            btn.Background = AccentSoftBrush;
            btn.Foreground = new SolidColorBrush(Color.FromRgb(45, 51, 75));
        }
    }

    private static List<string> SplitTokens(string text)
    {
        var tokens = new List<string>();
        var last = 0;
        foreach (Match m in Regex.Matches(text, @"[A-Za-z]+(?:[''-][A-Za-z]+)*"))
        {
            if (m.Index > last) tokens.Add(text[last..m.Index]);
            tokens.Add(m.Value);
            last = m.Index + m.Length;
        }
        if (last < text.Length) tokens.Add(text[last..]);
        return tokens;
    }

    private static bool IsWordToken(string s)
    {
        return Regex.IsMatch(s, @"^[A-Za-z]+(?:[''-][A-Za-z]+)*$");
    }

    private void UpdateSaveLabel()
    {
        if (_isSentenceMode)
        {
            var n = _selectedWords.Count;
            _saveBtn.Content = n > 0 ? $"保存 {n} 个词" : "保存";
        }
        else _saveBtn.Content = "保存";
    }

    private static Button MakeButton(string text, bool primary)
    {
        return new Button
        {
            Content = text,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 108,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = primary ? AccentBrush : new SolidColorBrush(Color.FromRgb(240, 242, 250)),
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(60, 66, 90)),
            BorderThickness = new Thickness(0),
        };
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter && !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SaveAsync();
        }
    }

    private void ShowStatus(string msg, bool error = true)
    {
        _status.Text = msg;
        _status.Foreground = error ? ErrorBrush : new SolidColorBrush(Color.FromRgb(20, 150, 110));
        _status.Visibility = Visibility.Visible;
    }

    private async Task SaveAsync()
    {
        if (!_saveBtn.IsEnabled) return;
        var text = _input.Text.Trim();
        var items = new List<(string Word, string Sentence)>();
        if (_isSentenceMode)
        {
            foreach (var w in _selectedWords)
                items.Add((w, text));
        }
        else if (IsWordLike(text))
        {
            items.Add((text, ""));
        }

        if (items.Count == 0)
        {
            ShowStatus(_isSentenceMode
                ? "请先点击句子中的单词（可多选），再点保存"
                : "请输入一个英文单词，或粘贴一句英文句子");
            return;
        }

        _saveBtn.IsEnabled = false;
        try
        {
            var arr = new JsonArray();
            foreach (var (w, s) in items)
                arr.Add(new JsonObject { ["word"] = w, ["sentence"] = s });
            var payload = new JsonObject { ["items"] = arr };
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            var res = await http.PostAsync(_cfg.HomeUrl + "/api/words/batch", content);
            if (res.IsSuccessStatusCode)
            {
                ShowStatus($"已保存 {items.Count} 个词，正在逐个结合原句讲解…", false);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
                timer.Tick += (_, _) => { timer.Stop(); Close(); };
                timer.Start();
            }
            else
            {
                _saveBtn.IsEnabled = true;
                ShowStatus("保存失败：" + (int)res.StatusCode + "，请确认程序已启动");
            }
        }
        catch (Exception ex)
        {
            _saveBtn.IsEnabled = true;
            ShowStatus("保存失败：" + ex.Message);
        }
    }

    private void PositionNearCursor()
    {
        GetCursorPos(out var pt);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var x = pt.X / scale - Width / 2;
        var y = pt.Y / scale + 14;
        var wa = SystemParameters.WorkArea;
        x = Math.Max(wa.Left + 8, Math.Min(x, wa.Right - Width - 8));
        y = Math.Max(wa.Top + 8, Math.Min(y, wa.Bottom - Height - 8));
        Left = x;
        Top = y;
    }

    private void RestoreFocus()
    {
        if (_prevHwnd == IntPtr.Zero) return;
        try { SetForegroundWindow(_prevHwnd); } catch { /* 忽略 */ }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.OriginalSource is DependencyObject dep && IsInteractive(dep)) return;
        try { DragMove(); }
        catch (Exception ex) { Diag.Log("DragMove error: " + ex.Message); }
    }

    private static bool IsInteractive(DependencyObject node)
    {
        while (node != null)
        {
            if (node is Button or TextBox or ScrollBar or Thumb or ComboBox) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
        }
        return false;
    }

    private static bool LooksLikeSentence(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Regex.Matches(s, @"[A-Za-z]+").Count >= 2 && s.Contains(' ');
    }

    private static bool IsWordLike(string s)
    {
        return !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), @"^[A-Za-z][A-Za-z''-]*$");
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
