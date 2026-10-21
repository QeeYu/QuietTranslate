using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuietTranslate;

internal sealed partial class SettingsWindow : Window
{
    private readonly Settings draft;
    private readonly Action<Settings> save;
    private readonly List<Action> changes = new(), lookChanges = new();
    private readonly Dictionary<string, string> pendingKeys = new();
    internal bool CredentialChanged(string target) => pendingKeys.ContainsKey(target);
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel[] pages = { new(), new(), new(), new(), new(), new(), new() };
    private readonly Button[] nav = new Button[7];
    private readonly TextBlock title = new(), subtitle = Ui.Hint(""), notice = Ui.Hint(""), shortcutNotice = Ui.Hint("点击快捷键框后录入；Esc 取消。单键推荐 Pause，也可用 Alt+P。" );
    private readonly List<TextBox> shortcutBoxes = new();
    private readonly HashSet<Hotkey> originalHotkeys = new();
    private readonly BubbleView preview;
    private readonly Border buttonPreview = new();
    private readonly TextBlock appearanceHint = Ui.Hint("");
    private readonly ServiceEditor service, ocr;
    private bool night;
    private readonly List<Button> themeButtons = new();
    private IntPtr keyboardHook;
    private Native.HookProc? keyboardProc;
    private bool recordingActive;
    internal event Action<bool>? RecordingChanged;
    private Action? speechSaved;

    public SettingsWindow(Settings settings, Action<Settings> save, bool? dark = null, Action? toggleSpeech = null)
    {
        draft = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; this.save = save; night = dark ?? Ui.ResolveTheme(draft.Theme);
        foreach (var text in new[] { settings.SelectionHotkey, settings.ScreenshotHotkey, settings.PauseHotkey, settings.ManualHotkey, settings.Speech.Hotkey }) try { originalHotkeys.Add(Hotkey.Parse(text)); } catch { }
        Icon = AppIcons.Image; Title = "轻译 · 设置"; Width = 910; Height = Math.Min(800, SystemParameters.WorkArea.Height - 40); MinWidth = 760; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13; Ui.Apply(this, night); Ui.Bind(this, BackgroundProperty, "Bg"); Ui.Bind(this, ForegroundProperty, "Text");
        notice.Name = "SettingsNotice";
        async Task CopyField(TextBox field)
        {
            if (ClipboardWriter.IsBusy) return;
            var token = lifetime.Token; string snapshot = field.SelectedText; notice.Text = "正在复制…";
            bool copied = await ClipboardWriter.CopyAsync(snapshot);
            if (!token.IsCancellationRequested && notice.Text == "正在复制…") notice.Text = copied ? "已复制" : "未复制：剪贴板被占用或已更新，请重试";
        }
        CommandManager.AddPreviewCanExecuteHandler(this, (_, e) =>
        {
            if ((e.OriginalSource as TextBox ?? Keyboard.FocusedElement as TextBox) is not TextBox field) return;
            if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut) e.CanExecute = field.SelectionLength > 0 && (e.Command == ApplicationCommands.Copy || !field.IsReadOnly);
            else if (e.Command == ApplicationCommands.Paste) e.CanExecute = !field.IsReadOnly && Native.IsClipboardFormatAvailable(13);
            else return;
            e.Handled = true;
        });
        PreviewKeyDown += async (_, e) =>
        {
            if (!recordingActive && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && Keyboard.FocusedElement is TextBox field && field.SelectionLength > 0)
            { e.Handled = true; await CopyField(field); }
        };
        // Intercept before TextBox's class handler performs synchronous OLE copy/flush.
        CommandManager.AddPreviewExecutedHandler(this, async (_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy || (e.OriginalSource as TextBox ?? Keyboard.FocusedElement as TextBox) is not TextBox field || field.SelectionLength == 0) return;
            e.Handled = true;
            await CopyField(field);
        });
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) }); root.ColumnDefinitions.Add(new ColumnDefinition()); Content = root;
        var sidebar = new DockPanel { Margin = new Thickness(16, 26, 16, 20) }; var sideBorder = new Border { Child = sidebar, BorderThickness = new Thickness(0, 0, 1, 0) }; Ui.Bind(sideBorder, Border.BackgroundProperty, "Surface"); Ui.Bind(sideBorder, Border.BorderBrushProperty, "Line"); root.Children.Add(sideBorder);
        var brand = new StackPanel { Margin = new Thickness(8, 0, 0, 28) }; brand.Children.Add(new TextBlock { Text = "轻译", FontSize = 29, FontWeight = FontWeights.SemiBold }); brand.Children.Add(Ui.Hint("随手选词，轻松表达")); DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 16, 0, 4) }; brand.Children.Add(themeRow);
        foreach (var pair in new[] { (ThemeMode.System, "系统"), (ThemeMode.Light, "浅色"), (ThemeMode.Dark, "深色") })
        {
            var mode = pair.Item1; var button = new Button { Content = pair.Item2, Name = "Theme" + mode, FontSize = 11, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), ToolTip = "界面主题 · 预览后保存并应用" };
            themeButtons.Add(button); themeRow.Children.Add(button);
            button.Click += (_, _) => { draft.Theme = mode; night = Ui.ResolveTheme(mode); Ui.RefreshTheme(this, night); preview?.SetTheme(night); UpdatePreview(); UpdateThemeButtons(); notice.Text = "正在预览主题，点击保存并应用后全局生效。"; };
        }
        UpdateThemeButtons();
        var privacy = Ui.Hint("轻量版 · 语音记录仅本次保留\n关闭语音功能即删除"); DockPanel.SetDock(privacy, Dock.Bottom); sidebar.Children.Add(privacy);
        string[] names = { "划词与快捷键", "气泡与按钮", "翻译服务", "截图 OCR", "应用规则", "输入翻译栏", "实时语音字幕" };
        var navigation = new StackPanel(); sidebar.Children.Add(navigation);
        for (int i = 0; i < pages.Length; i++) { int page = i; nav[i] = new Button { Content = names[i], HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Padding = new Thickness(10, 12, 10, 12), Margin = new Thickness(0, 0, 0, 6), Name = i == 0 ? "SelectionTab" : i == 1 ? "AppearanceTab" : i == 2 ? "ServiceTab" : "Page" + i }; nav[i].Click += (_, _) => ShowPage(page); navigation.Children.Add(nav[i]); }
        var right = new DockPanel(); Grid.SetColumn(right, 1); root.Children.Add(right);
        var heading = new StackPanel { Margin = new Thickness(26, 24, 26, 18) }; title.FontSize = 24; title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title); heading.Children.Add(subtitle); DockPanel.SetDock(heading, Dock.Top); right.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(26, 12, 26, 20) }; footer.Children.Add(notice); var apply = new Button { Content = "保存并应用", Name = "SaveSettings", MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Ui.Color("#247A85")), Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(apply); DockPanel.SetDock(footer, Dock.Bottom); right.Children.Add(footer);
        Ui.Bind(apply, Control.BackgroundProperty, "Accent"); Ui.Bind(apply, Control.ForegroundProperty, "OnAccent");
        var content = new StackPanel { Margin = new Thickness(26, 0, 26, 0) }; foreach (var p in pages) content.Children.Add(p); right.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var selection = Card(0, "确认文字选区，再出现按钮"); selection.Children.Add(Ui.Hint("拖选、双击或键盘选词后确认有效文字，鼠标附近出现按钮，并避让常见划词菜单。点击才发送翻译；按钮不抢焦点。无法读取选区的应用，可在应用规则中开启兼容模式。"));
        Toggle(selection, "划词后显示浮动翻译按钮", draft.SelectionButtonEnabled, v => draft.SelectionButtonEnabled = v, "SelectionButtonEnabled");
        var keys = Card(0, "全局快捷键"); Shortcut(keys, "选中文字 → 翻译", draft.SelectionHotkey, v => draft.SelectionHotkey = v, "SelectionShortcut"); Shortcut(keys, "截图 → OCR 翻译", draft.ScreenshotHotkey, v => draft.ScreenshotHotkey = v, "ScreenshotShortcut"); Shortcut(keys, "总暂停 / 恢复", draft.PauseHotkey, v => draft.PauseHotkey = v, "PauseShortcut"); Shortcut(keys, "输入翻译栏：打开 / 关闭", draft.ManualHotkey, v => draft.ManualHotkey = v, "ManualShortcut"); shortcutNotice.Name = "ShortcutNotice"; keys.Children.Add(shortcutNotice);
        Toggle(keys, "总暂停时也禁用输入翻译栏呼出键", draft.PauseManual, v => draft.PauseManual = v);
        Shortcut(keys, "实时语音翻译功能：开 / 关（关闭即删除记录）", draft.Speech.Hotkey, v => draft.Speech.Hotkey = v, "SpeechShortcut");
        var compatibility = Card(0, "取词与消失行为"); Number(compatibility, "复制等待上限（300–2500 毫秒）", draft.CopyWaitMs, 300, 2500, v => draft.CopyWaitMs = v); Number(compatibility, "兼容模式按钮兜底时间（3–60 秒）", draft.CompatibilityTimeout, 3, 60, v => draft.CompatibilityTimeout = v);
        compatibility.Children.Add(Ui.Hint("正常选区没有固定八秒超时；悬停不消失。点击外部、Esc、切换应用、滚动或选区失效时收起。兼容模式的计时会在鼠标靠近时暂停。"));

        var showcase = Card(1, "实时预览 · 按译文长度贴合"); preview = new BubbleView(draft.BubbleOpacity, draft.BubbleFontSize, draft.BubbleTextColor, night, () => { }, () => { }) { Name = "BubblePreview" }; preview.Text = "今晚八点见，资料已发到频道里。\n需要帮助的话，随时告诉我。";
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) }; previewRow.Children.Add(preview); buttonPreview.Margin = new Thickness(12, 0, 0, 0); buttonPreview.VerticalAlignment = VerticalAlignment.Center; previewRow.Children.Add(buttonPreview); showcase.Children.Add(previewRow);
        var examples = new WrapPanel();
        foreach (var pair in new[] { ("单词", "你好"), ("短句", "今晚八点见，资料已发到频道里。"), ("段落", "今晚八点见，资料已发到频道里。需要帮助的话，随时告诉我。\n这段示例用来查看较长译文的换行与留白，文字只在本机预览，不发送给翻译服务。") })
        { var p = pair; var example = new Button { Content = p.Item1, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8) }; example.Click += (_, _) => { preview.Text = p.Item2; UpdatePreview(); }; examples.Children.Add(example); }
        showcase.Children.Add(examples);
        var bubble = Card(1, "配色与阅读");
        Toggle(bubble, "智能配色 · 按底色协调文字和图标，改善低对比配色", draft.BubbleSmartColors, v => draft.BubbleSmartColors = v, "BubbleSmartColors", look: true);
        Slider(bubble, "背景透明度", (1 - draft.BubbleOpacity) * 100, 0, 100, v => draft.BubbleOpacity = 1 - v / 100, "BubbleTransparency", true);
        Slider(bubble, "译文字号", draft.BubbleFontSize, 12, 22, v => draft.BubbleFontSize = v, "BubbleFontSize", true);
        Slider(bubble, "滚轮速度（% · 较小更细腻）", draft.WheelScrollSpeed * 100, 25, 150, v => draft.WheelScrollSpeed = v / 100, "WheelScrollSpeed", true);
        var foregroundHex = Color(bubble, "文字颜色 · 留空自动匹配底色", draft.BubbleTextColor, v => draft.BubbleTextColor = v, "BubbleColorHex");
        var backgroundHex = Color(bubble, "背景颜色 · 留空跟随明暗主题", draft.BubbleBackgroundColor, v => draft.BubbleBackgroundColor = v, "BubbleBackgroundHex");
        var borderHex = Color(bubble, "边框颜色 · 留空自动协调", draft.BubbleBorderColor, v => draft.BubbleBorderColor = v, "BubbleBorderHex");
        var palettes = new WrapPanel();
        foreach (var pair in new[] { ("跟随主题", ""), ("纸白", "#FAFBFD"), ("墨黑", "#20222F"), ("雾青", "#E8F3F1") })
        { var p = pair; var preset = new Button { Content = p.Item1, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) }; preset.Click += (_, _) => { foregroundHex.Text = ""; backgroundHex.Text = p.Item2; borderHex.Text = ""; }; palettes.Children.Add(preset); }
        bubble.Children.Add(palettes); bubble.Children.Add(appearanceHint);
        Toggle(bubble, "显示边框", draft.BubbleBorderVisible, v => draft.BubbleBorderVisible = v, look: true);
        Toggle(bubble, "边框透明度跟随背景", draft.BubbleBorderOpacity < 0, v => { if (v) draft.BubbleBorderOpacity = -1; else if (draft.BubbleBorderOpacity < 0) draft.BubbleBorderOpacity = draft.BubbleOpacity; }, look: true);
        Slider(bubble, "独立边框透明度（取消跟随后生效）", draft.BubbleBorderOpacity < 0 ? (1 - draft.BubbleOpacity) * 100 : (1 - draft.BubbleBorderOpacity) * 100, 0, 100, v => { if (draft.BubbleBorderOpacity >= 0) draft.BubbleBorderOpacity = 1 - v / 100; }, "BorderTransparency", true);
        var buttonLook = Card(1, "划词按钮 · 独立定制"); Text(buttonLook, "按钮文字（1–2 个字符）", draft.ButtonLabel, v => { if (v.Length is < 1 or > 2) throw new ArgumentException("按钮文字需为 1–2 个字符。"); draft.ButtonLabel = v; }, "ButtonLabel", true);
        Color(buttonLook, "按钮底色", draft.ButtonBackground, v => draft.ButtonBackground = v.Length == 0 ? "#247A85" : v, "ButtonBackgroundHex"); Color(buttonLook, "按钮文字颜色", draft.ButtonForeground, v => draft.ButtonForeground = v.Length == 0 ? "#FFFFFF" : v, "ButtonForegroundHex"); Color(buttonLook, "按钮边框颜色", draft.ButtonBorderColor, v => draft.ButtonBorderColor = v.Length == 0 ? "#247A85" : v, "ButtonBorderColorHex");
        Slider(buttonLook, "按钮透明度", (1 - draft.ButtonOpacity) * 100, 0, 95, v => draft.ButtonOpacity = 1 - v / 100, "ButtonTransparency", true); Slider(buttonLook, "按钮大小", draft.ButtonSize, 28, 56, v => draft.ButtonSize = v, "ButtonSize", true); Slider(buttonLook, "圆角", draft.ButtonRadius, 0, 28, v => draft.ButtonRadius = v, "ButtonRadius", true);
        Toggle(buttonLook, "显示按钮边框", draft.ButtonBorderVisible, v => draft.ButtonBorderVisible = v, look: true); Toggle(buttonLook, "轻微阴影", draft.ButtonShadow, v => draft.ButtonShadow = v, look: true);
        Choice(buttonLook, "按钮位置", new[] { "选区右侧", "选区右上", "鼠标附近" }, draft.ButtonPosition, v => draft.ButtonPosition = v); Number(buttonLook, "水平间距（0–80）", draft.ButtonOffsetX, 0, 80, v => draft.ButtonOffsetX = v); Number(buttonLook, "垂直偏移（-80–80）", draft.ButtonOffsetY, -80, 80, v => draft.ButtonOffsetY = v);

        service = new ServiceEditor(draft, false, pendingKeys); Card(2, "普通翻译与 AI").Children.Add(service);
        var test = new Button { Content = "测试当前翻译服务", HorizontalAlignment = HorizontalAlignment.Left, Name = "TestTranslation" }; pages[2].Children.Add(test);
        test.Click += async (_, _) => { test.IsEnabled = false; try { service.Validate(); notice.Text = "正在发送一条测试翻译…"; using var api = new TranslationService(); await api.TranslateAsync(service.Provider, service.Config, service.Secret(), "Hello, world!", _ => { }, lifetime.Token); notice.Text = "翻译成功。保存并应用即可使用。"; } catch (OperationCanceledException) { } catch (Exception ex) { notice.Text = ex.Message; } finally { test.IsEnabled = true; } };
        var ocrIntro = Card(3, "自动识别截图文字"); ocrIntro.Children.Add(Ui.Hint("云端方案无需安装识别模型；只上传框选区域。图片及识别结果不在本机落盘，服务商的数据处理规则由对应平台决定。OCR 与翻译分别计费，不自动更换服务或购买套餐。"));
        LanguageChoice(ocrIntro, "OCR 语种提示 · 默认自动，服务商可能仅将手动语种作为提示", draft.OcrLanguage, true, v => draft.OcrLanguage = v);
        ocr = new ServiceEditor(draft, true, pendingKeys); Card(3, "OCR 服务").Children.Add(ocr);
        var links = new WrapPanel(); foreach (var link in new[] { ("OCR.space 免费 Key", "https://ocr.space/ocrapi"), ("腾讯免费额度", "https://cloud.tencent.com/document/product/866/35945"), ("百度免费测试", "https://bce.baidu.com/product/ocr/"), ("Google 定价", "https://cloud.google.com/vision/pricing") }) { var item = link; var b = new Button { Content = item.Item1, Margin = new Thickness(0, 0, 8, 8) }; b.Click += (_, _) => Process.Start(new ProcessStartInfo(item.Item2) { UseShellExecute = true }); links.Children.Add(b); } pages[3].Children.Add(links);
        var ocrTest = new Button { Content = "用示例图测试 OCR", HorizontalAlignment = HorizontalAlignment.Left }; pages[3].Children.Add(ocrTest);
        ocrTest.Click += async (_, _) => { ocrTest.IsEnabled = false; try { ocr.Validate(); notice.Text = "正在识别本程序生成的 Hello World 示例图…"; using var image = new System.Drawing.Bitmap(480, 90); using (var g = System.Drawing.Graphics.FromImage(image)) using (var font = new System.Drawing.Font("Arial", 24)) { g.Clear(System.Drawing.Color.White); g.DrawString("Hello World", font, System.Drawing.Brushes.Black, 8, 12); } using var api = new OcrService(); var result = await api.RecognizeAsync(ocr.OcrProvider, ocr.Config, ocr.OcrProvider == OcrProvider.Windows ? "" : ocr.Secret(), image, lifetime.Token); notice.Text = "识别成功：" + result; } catch (OperationCanceledException) { } catch (Exception ex) { notice.Text = ex.Message; } finally { ocrTest.IsEnabled = true; } };

        BuildRules();
        var manual = Card(5, "停止输入后自动翻译"); Number(manual, "停顿时间（300–2000 毫秒）", draft.AutoTranslateDelay, 300, 2000, v => draft.AutoTranslateDelay = v);
        LanguageChoice(manual, "默认源语言", draft.ManualSource, true, v => draft.ManualSource = v); LanguageChoice(manual, "默认目标语言", draft.ManualTarget, false, v => draft.ManualTarget = v); Toggle(manual, "输入翻译栏始终置顶", draft.ManualTopmost, v => draft.ManualTopmost = v);
        manual.Children.Add(Ui.Hint("中文／日文输入法确认文字后才计时。Ctrl+Enter 可立即翻译，Enter 换行。使用同一快捷键打开／关闭输入栏（默认 Alt+T，可自定义），Esc 关闭；输入法组词时先由输入法处理 Esc。点击外部不关闭；关闭会清空原文和译文。修改输入会使旧结果过期，不保存历史。"));
        var word = Card(5, "孤立单词与语种歧义"); word.Children.Add(Ui.Hint("源语言可手动指定，应用规则可预设源语种。AI 单词模式最多提供三个常见释义；普通翻译保持原服务结果，不自动改用 AI 或增加费用。气泡右上角的更多菜单可以将原文带入翻译栏编辑。"));
        BuildSpeech(toggleSpeech); UpdatePreview(); ShowPage(0);
        apply.Click += (_, _) =>
        {
            try
            {
                foreach (var change in changes) change(); foreach (var change in lookChanges) change(); service.Validate(); ocr.Validate(); if (!ValidateHotkeys()) return;
                var value = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(draft))!;
                Credentials.SaveChanges(pendingKeys, () => save(value)); pendingKeys.Clear(); service.RefreshCredentialStatus(); ocr.RefreshCredentialStatus(); speechSaved?.Invoke();
                originalHotkeys.Clear(); foreach (var box in shortcutBoxes) originalHotkeys.Add(Hotkey.Parse(box.Text));
                notice.Text = "已保存并立即生效。设置可以保持打开，切到其他应用即可翻译。";
            }
            catch (Exception ex) { notice.Text = ex.Message; }
        };
        Closed += (_, _) => { StopRecording(); lifetime.Cancel(); keyCleanup(); lifetime.Dispose(); };
        void keyCleanup() { pendingKeys.Clear(); notice.Text = ""; Content = null; }
    }
    private void BuildRules()
    {
        var panel = Card(4, "选择应用，设置前台行为"); panel.Children.Add(Ui.Hint("正常使用、仅快捷键、自动暂停、兼容选区。自动暂停只影响该应用在前台时；手动总暂停必须手动恢复。用进程名匹配，不修改目标应用。"));
        var process = new TextBox { Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(process);
        var pick = new Button { Content = "选窗口：点击后 3 秒内切到目标应用", HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(pick);
        pick.Click += async (_, _) => { pick.IsEnabled = false; WindowState = WindowState.Minimized; try { await Task.Delay(3000, lifetime.Token); Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint id); using var p = Process.GetProcessById((int)id); if (id != Environment.ProcessId) process.Text = p.ProcessName; } catch (OperationCanceledException) { return; } catch { notice.Text = "无法读取目标应用，请手动填写程序名。"; } finally { if (!lifetime.IsCancellationRequested) { WindowState = WindowState.Normal; Activate(); pick.IsEnabled = true; } } };
        var modes = new ComboBox { ItemsSource = new[] { "正常使用", "仅快捷键", "自动暂停", "兼容选区（可能误弹按钮）" }, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(modes);
        var langs = new ComboBox { ItemsSource = ApiSupport.Languages.Select(ApiSupport.LanguageName).ToArray(), SelectedIndex = 0 }; panel.Children.Add(Ui.Hint("此应用的源语言（默认自动；纯汉字日文可指定日语）")); panel.Children.Add(langs);
        var list = new ListBox { MinHeight = 100, MaxHeight = 220, Margin = new Thickness(0, 12, 0, 12) }; Ui.Bind(list, Control.BackgroundProperty, "Input"); Ui.Bind(list, Control.ForegroundProperty, "Text"); panel.Children.Add(list);
        void Refresh() { list.ItemsSource = draft.AppRules.Select(r => r.ProcessName + " · " + modes.Items[r.Mode.GetHashCode()] + " · " + ApiSupport.LanguageName(r.SourceLanguage)).ToArray(); }
        list.SelectionChanged += (_, _) => { int i = list.SelectedIndex; if (i >= 0 && i < draft.AppRules.Count) { var r = draft.AppRules[i]; process.Text = r.ProcessName; modes.SelectedIndex = (int)r.Mode; langs.SelectedIndex = Array.IndexOf(ApiSupport.Languages, r.SourceLanguage); } };
        var actions = new WrapPanel(); var add = new Button { Content = "加入 / 更新规则", Margin = new Thickness(0, 0, 8, 0) }; add.Click += (_, _) => { string name = System.IO.Path.GetFileNameWithoutExtension(process.Text.Trim()); if (name.Length == 0) { notice.Text = "请先选择应用。"; return; } draft.AppRules.RemoveAll(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)); draft.AppRules.Add(new() { ProcessName = name, Mode = (AppMode)modes.SelectedIndex, SourceLanguage = ApiSupport.Languages[Math.Max(0, langs.SelectedIndex)] }); Refresh(); notice.Text = "规则已加入草稿，保存并应用后生效。"; }; actions.Children.Add(add);
        var remove = new Button { Content = "删除选中规则" }; remove.Click += (_, _) => { int i = list.SelectedIndex; if (i >= 0 && i < draft.AppRules.Count) draft.AppRules.RemoveAt(i); Refresh(); }; actions.Children.Add(remove); panel.Children.Add(actions); Refresh();
    }
    private void UpdateThemeButtons()
    {
        for (int i = 0; i < themeButtons.Count; i++)
        {
            Ui.Bind(themeButtons[i], Control.BackgroundProperty, (int)draft.Theme == i ? "AccentSoft" : "Surface");
            Ui.Bind(themeButtons[i], Control.ForegroundProperty, (int)draft.Theme == i ? "Accent" : "Muted");
        }
    }
    internal void ShowPage(int page)
    {
        string[] names = { "划词与快捷键", "让气泡和按钮更合你意", "连接你的翻译服务", "识别图片里的文字", "为不同应用设置规则", "输入，停顿，即可翻译", "把电脑声音变成字幕" };
        for (int i = 0; i < pages.Length; i++) { pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed; Ui.Bind(nav[i], Button.BackgroundProperty, i == page ? "AccentSoft" : "Surface"); }
        title.Text = names[page]; subtitle.Text = page == 1 ? "背景、边框、文字独立设置，透明度所见即所得。" : "保存立即生效，无需关闭设置或转入后台。";
    }
    private StackPanel Card(int page, string name) { var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) }); pages[page].Children.Add(Ui.Card(panel)); return panel; }
    private void Text(StackPanel panel, string label, string value, Action<string> set, string name = "", bool look = false)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 12), Name = name }; panel.Children.Add(Ui.Hint(label)); panel.Children.Add(box); (look ? lookChanges : changes).Add(() => set(box.Text.Trim())); if (look) box.TextChanged += (_, _) => UpdatePreview();
    }
    private void Number(StackPanel panel, string label, int value, int min, int max, Action<int> set) => Text(panel, label, value.ToString(), text => { if (!int.TryParse(text, out int n) || n < min || n > max) throw new ArgumentException(label + "超出范围。"); set(n); });
    private void Toggle(StackPanel panel, string label, bool value, Action<bool> set, string name = "", bool look = false)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Name = name }; panel.Children.Add(box); (look ? lookChanges : changes).Add(() => set(box.IsChecked == true)); if (look) { box.Checked += (_, _) => UpdatePreview(); box.Unchecked += (_, _) => UpdatePreview(); }
    }
    private void Slider(StackPanel panel, string label, double value, double min, double max, Action<double> set, string name, bool look)
    {
        var hint = Ui.Hint(label + " · " + Math.Round(value)); var slider = new Slider { Value = value, Minimum = min, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 12), Name = name }; panel.Children.Add(hint); panel.Children.Add(slider); (look ? lookChanges : changes).Add(() => set(slider.Value)); slider.ValueChanged += (_, _) => { hint.Text = label + " · " + Math.Round(slider.Value); if (look) UpdatePreview(); };
    }
    private TextBox Color(StackPanel panel, string label, string value, Action<string> set, string name)
    {
        panel.Children.Add(Ui.Hint(label)); var row = new DockPanel { Margin = new Thickness(0, 6, 0, 12) }; var box = new TextBox { Text = value, Name = name, MaxLength = 7, MinWidth = 110 }; var choose = new Button { Content = "选颜色", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(choose, Dock.Right); row.Children.Add(choose); row.Children.Add(box); panel.Children.Add(row); lookChanges.Add(() => set(Settings.NormalizeTextColor(box.Text))); box.TextChanged += (_, _) => UpdatePreview();
        choose.Click += (_, _) => { using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true }; try { if (box.Text.Length > 0) dialog.Color = System.Drawing.ColorTranslator.FromHtml(box.Text); } catch { } if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) box.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"; };
        return box;
    }
    private void UpdatePreview()
    {
        if (preview == null) return;
        try { foreach (var change in lookChanges) change(); preview.ApplyAppearance(draft); preview.Width = preview.FittingWidth(350); preview.SetHeightLimit(190); appearanceHint.Text = preview.ColorAdjusted ? "已根据底色调整文字颜色，保持清晰；关闭智能配色可保留手选原色。" : "自动颜色会匹配底色；高透明度的实际可读性仍受底图影响。"; var face = SelectionButtonWindow.CreateFace(draft); buttonPreview.Background = face.Background; buttonPreview.BorderBrush = face.BorderBrush; buttonPreview.BorderThickness = face.BorderThickness; buttonPreview.CornerRadius = face.CornerRadius; buttonPreview.Effect = face.Effect; buttonPreview.Width = buttonPreview.Height = draft.ButtonSize; buttonPreview.Child = new TextBlock { Text = draft.ButtonLabel, Foreground = new SolidColorBrush(SelectionButtonWindow.TextColor(draft)), FontSize = draft.ButtonSize * .4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; } catch (ArgumentException) { appearanceHint.Text = "颜色格式为 #RRGGBB，当前预览保留上次有效配色。"; }
    }
    private void Choice(StackPanel panel, string label, string[] values, int selected, Action<int> set) { var box = new ComboBox { ItemsSource = values, SelectedIndex = Math.Clamp(selected, 0, values.Length - 1), Margin = new Thickness(0, 6, 0, 12) }; panel.Children.Add(Ui.Hint(label)); panel.Children.Add(box); changes.Add(() => set(box.SelectedIndex)); }
    private void LanguageChoice(StackPanel panel, string label, string selected, bool auto, Action<string> set) { var langs = ApiSupport.Languages.Where(l => auto || l != "auto").ToArray(); Choice(panel, label, langs.Select(ApiSupport.LanguageName).ToArray(), Math.Max(0, Array.IndexOf(langs, selected)), i => set(langs[i])); }
    private void Shortcut(StackPanel panel, string label, string value, Action<string> set, string name) { var box = new TextBox { Text = value, Name = name, IsReadOnly = true, Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 12) }; panel.Children.Add(Ui.Hint(label)); panel.Children.Add(box); shortcutBoxes.Add(box); changes.Add(() => set(box.Text)); AttachRecorder(box); }
    private void AttachRecorder(TextBox box)
    {
        string previous = box.Text;
        void Record(uint vk, uint mods)
        {
            if (!box.IsKeyboardFocused) return;
            if (vk == 0x1B) { box.Text = previous; Keyboard.ClearFocus(); return; }
            string text = Hotkey.Format(mods, vk); try { Hotkey.Parse(text); } catch { shortcutNotice.Text = "使用组合键或单独的 Pause / F1–F11。"; return; }
            box.Text = previous = text; Keyboard.ClearFocus(); ValidateHotkeys();
        }
        box.GotKeyboardFocus += (_, _) =>
        {
            StopRecording(); recordingActive = true; RecordingChanged?.Invoke(true); previous = box.Text; box.Text = "请按快捷键…";
            keyboardProc = (int code, IntPtr message, IntPtr data) => { if (code >= 0 && message.ToInt32() is 0x100 or 0x104) { uint vk = (uint)Marshal.ReadInt32(data); if (vk is not (0x09 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)) { uint mods = (Native.Down(0x11) ? 2u : 0) | (Native.Down(0x12) ? 1u : 0) | (Native.Down(0x10) ? 4u : 0) | (Native.Down(0x5B) || Native.Down(0x5C) ? 8u : 0); Dispatcher.BeginInvoke(new Action(() => Record(vk, mods))); return new IntPtr(1); } } return Native.CallNextHookEx(keyboardHook, code, message, data); };
            keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
        };
        box.LostKeyboardFocus += (_, _) => { StopRecording(); if (box.Text == "请按快捷键…") box.Text = previous; };
        box.PreviewKeyDown += (_, e) => { var key = e.Key == Key.System ? e.SystemKey : e.Key; if (key == Key.Tab) return; e.Handled = true; if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return; uint mods = (uint)Keyboard.Modifiers; uint mapped = ((mods & 2) != 0 ? 2u : 0) | ((mods & 1) != 0 ? 1u : 0) | ((mods & 4) != 0 ? 4u : 0) | ((mods & 8) != 0 ? 8u : 0); Record((uint)KeyInterop.VirtualKeyFromKey(key), mapped); };
    }
    private void StopRecording() { if (keyboardHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; } keyboardProc = null; if (recordingActive) { recordingActive = false; RecordingChanged?.Invoke(false); } }
    private bool ValidateHotkeys()
    {
        try { var keys = shortcutBoxes.Select(b => Hotkey.Parse(b.Text)).ToArray(); if (keys.Distinct().Count() != keys.Length) throw new ArgumentException("快捷键不能重复。"); foreach (var k in keys) if (!originalHotkeys.Contains(k) && !Hotkeys.Available(k)) throw new ArgumentException(Hotkey.Format(k.Modifiers, k.Key) + " 已被其他程序占用。"); shortcutNotice.Text = "快捷键格式有效，保存时应用并检查冲突。"; return true; }
        catch (Exception ex) { shortcutNotice.Text = ex.Message; ShowPage(0); return false; }
    }
}
