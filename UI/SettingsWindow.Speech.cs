using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuietTranslate;

internal sealed partial class SettingsWindow
{
    private Action<SpeechOptions>? syncSpeechAppearance;
    internal void SyncSpeechAppearance(SpeechOptions options) => syncSpeechAppearance?.Invoke(options);
    internal void ReportSpeechShortcutConflict() { notice.Text = "实时语音快捷键被其他程序占用；原有快捷键仍可使用。可更换语音快捷键，或从托盘开关功能。"; }
    private void BuildSpeech(Action? toggle)
    {
        var options = draft.Speech;
        string? stagedSpeechTarget = null;
        var appearanceBaseline = options.Copy();
        var connection = Card(6, "Gemini Live Translate");
        connection.Children.Add(Ui.Hint("默认关闭，保存设置不会采集声音。只显示字幕，不播放译音；付费接口仍可能计算生成的音频费用。源语言自动识别。"));
        connection.Children.Add(Ui.Hint("仅使用Live直译；分段、草稿显示及保留在本地处理，不调用第二个文字翻译服务。"));
        Text(connection, "完整实时接口地址（Google Live 协议，可自定义网关）", options.Endpoint, v => options.Endpoint = v, "SpeechEndpoint");
        Text(connection, "实时翻译模型", options.Model, v => options.Model = v, "SpeechModel");
        var auth = new ComboBox { ItemsSource = new[] { "Google API Key（运行时查询参数）", "x-goog-api-key 请求头", "Authorization: Bearer 请求头" }, SelectedIndex = options.Authentication, Margin = new Thickness(0, 5, 0, 10), Name = "SpeechAuthentication" };
        connection.Children.Add(Ui.Hint("鉴权方式")); connection.Children.Add(auth);
        var key = new PasswordBox { Name = "SpeechApiKey", Margin = new Thickness(0, 5, 0, 8), MaxLength = 1200 };
        connection.Children.Add(Ui.Hint("API Key（留空保留已保存密钥）")); connection.Children.Add(key);
        var presence = Ui.Hint(""); presence.Name = "SpeechKeyStatus"; connection.Children.Add(presence);
        var delete = new CheckBox { Name = "SpeechDeleteKey", Content = "保存时删除此语音接口的密钥", Margin = new Thickness(0, 5, 0, 10) }; connection.Children.Add(delete);
        var endpoint = (TextBox)connection.Children.OfType<TextBox>().First();
        void RefreshPresence()
        {
            try { var check = options.Copy(); check.Endpoint = endpoint.Text; presence.Text = delete.IsChecked == true ? "待删除 · 保存并应用后删除此接口密钥" : key.Password.Length > 0 ? "待保存 · 新密钥尚未应用" : Credentials.Exists(check.SecretTarget) ? "✓ 密钥已保存 · Windows 凭据管理（输入框留空属正常）" : "尚未保存此接口的密钥"; }
            catch { presence.Text = "请先填写有效的完整实时接口地址"; }
        }
        key.PasswordChanged += (_, _) => RefreshPresence(); endpoint.TextChanged += (_, _) => RefreshPresence(); RefreshPresence();
        delete.Checked += (_, _) => RefreshPresence(); delete.Unchecked += (_, _) => RefreshPresence();
        var source = Card(6, "选择要翻译的电脑声音");
        var mode = new ComboBox { ItemsSource = new[] { "整个播放设备", "指定应用（需系统支持）" }, SelectedIndex = options.CaptureProcess ? 1 : 0, Margin = new Thickness(0, 6, 0, 10), Name = "SpeechSourceMode" };
        var sources = new ComboBox { DisplayMemberPath = nameof(AudioChoice.Label), Margin = new Thickness(0, 0, 0, 10), Name = "SpeechSource" };
        source.Children.Add(mode); source.Children.Add(sources);
        var refresh = new Button { Content = "刷新音源", HorizontalAlignment = HorizontalAlignment.Left }; source.Children.Add(refresh);
        source.Children.Add(Ui.Hint("应用列表来自正在使用音频的程序，请先让目标应用播放声音。设备模式会包含该设备的其他声音；指定应用退出后停止采集，不自动改抓整个电脑。"));
        int revision = 0;
        async Task Refresh()
        {
            int version = ++revision; bool process = mode.SelectedIndex == 1; refresh.IsEnabled = false;
            try
            {
                if (process && !AudioCapture.SupportsProcess) { sources.ItemsSource = null; notice.Text = "当前系统不支持按应用采集，请选择播放设备。"; return; }
                var choices = await Task.Run(() => AudioCapture.Choices(process));
                if (version != revision || lifetime.IsCancellationRequested) return;
                sources.ItemsSource = choices;
                sources.SelectedItem = process ? choices.FirstOrDefault(c => c.ProcessId == options.ProcessId && c.Started == options.ProcessStarted) : choices.FirstOrDefault(c => c.Id == options.DeviceId);
                if (!process && sources.SelectedItem == null) sources.SelectedIndex = 0;
            }
            catch { if (!lifetime.IsCancellationRequested) notice.Text = "无法读取播放音源，请稍后刷新。"; }
            finally { if (!lifetime.IsCancellationRequested) refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await Refresh(); mode.SelectionChanged += async (_, _) => await Refresh();
        Loaded += async (_, _) => await Refresh();
        var look = Card(6, "字幕与本次记录");
        var target = new ComboBox { ItemsSource = SpeechOptions.Languages.Select(SpeechOptions.LanguageName).ToArray(), SelectedIndex = Array.IndexOf(SpeechOptions.Languages, options.Target), Name = "SpeechTarget", Margin = new Thickness(0, 5, 0, 10) };
        look.Children.Add(Ui.Hint("目标语言（运行时也可从字幕栏切换）")); look.Children.Add(target);
        Toggle(look, "同时显示同期原文（仅改变显示，不切换接口）", options.Bilingual, v => options.Bilingual = v, "SpeechBilingual");
        Toggle(look, "原文先行预览：淡斜原文 → 淡斜译文草稿 → 正体译文", options.PreviewOriginal, v => options.PreviewOriginal = v, "SpeechPreviewOriginal");
        Toggle(look, "原音已经是目标语言时也输出字幕", options.EchoTarget, v => options.EchoTarget = v);
        look.Children.Add(Ui.Hint("字幕字体（本机字体；缺失字形自动回退）"));
        var family = new ComboBox { Name = "SpeechFontFamily", IsTextSearchEnabled = true, ItemsSource = Fonts.SystemFontFamilies.Select(font => font.Source).Append(options.FontFamily).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray(), SelectedItem = options.FontFamily, MaxDropDownHeight = 260, Margin = new Thickness(0, 5, 0, 10) };
        look.Children.Add(family);
        look.Children.Add(Ui.Hint("字幕粗细"));
        var weights = new[] { 300, 400, 500, 600, 700 };
        var weight = new ComboBox { Name = "SpeechFontWeight", ItemsSource = new[] { "纤细", "常规（推荐）", "中等", "半粗", "粗体" }, SelectedIndex = Array.IndexOf(weights, options.FontWeight), Margin = new Thickness(0, 5, 0, 10) };
        look.Children.Add(weight);
        Number(look, "字幕字号（16–40）", (int)options.FontSize, 16, 40, v => options.FontSize = v);
        var fontSize = look.Children.OfType<TextBox>().Last(); fontSize.Name = "SpeechFontSize";
        var fontPreview = new TextBlock { Name = "SpeechFontPreview", Text = "字幕预览 · 你好，世界\nHello · こんにちは", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 12) };
        look.Children.Add(fontPreview);
        void PreviewFont()
        {
            string name = (family.SelectedItem as string ?? family.Text).Trim();
            if (name.Length == 0 || name.Length > 160 || name.Any(c => char.IsControl(c) || "\\/:#@".Contains(c))) return;
            fontPreview.FontFamily = new FontFamily(name + ", Microsoft YaHei UI, Segoe UI");
            fontPreview.FontWeight = FontWeight.FromOpenTypeWeight(weights[Math.Clamp(weight.SelectedIndex, 0, weights.Length - 1)]);
            if (int.TryParse(fontSize.Text, out int size) && size is >= 16 and <= 40) fontPreview.FontSize = size;
        }
        family.SelectionChanged += (_, _) => PreviewFont();
        weight.SelectionChanged += (_, _) => PreviewFont(); fontSize.TextChanged += (_, _) => PreviewFont(); PreviewFont();
        changes.Add(() => { options.FontFamily = (family.SelectedItem as string ?? family.Text).Trim(); options.FontWeight = weights[Math.Clamp(weight.SelectedIndex, 0, weights.Length - 1)]; });
        Text(look, "背景不透明度（0–100%，0 隐藏背景和边框）", ((int)(options.Opacity * 100)).ToString(), text => { if (!int.TryParse(text, out int n) || n is < 0 or > 100) throw new ArgumentException("背景不透明度须为 0–100%。"); options.Opacity = n / 100.0; }, "SpeechOpacity");
        Number(look, "收段后保留秒数（2–15，多行一起计时）", (int)options.LineLifetime, 2, 15, v => options.LineLifetime = v);
        Text(look, "背景颜色 #RRGGBB", options.Background, v => options.Background = Settings.NormalizeTextColor(v), "SpeechBackground");
        Text(look, "文字颜色 #RRGGBB（自动调整可读性）", options.Foreground, v => options.Foreground = Settings.NormalizeTextColor(v), "SpeechForeground");
        Toggle(look, "电影字幕模式：隐藏顶部工具栏和底部状态（双击恢复）", options.ControlsHidden, v => options.ControlsHidden = v, "SpeechControlsHidden");
        syncSpeechAppearance = updated =>
        {
            options.CopyAppearanceFrom(updated);
            // Synchronize only untouched controls; moving the overlay must not discard an edited font/color.
            if ((family.SelectedItem as string ?? family.Text) == appearanceBaseline.FontFamily) family.SelectedItem = updated.FontFamily;
            if (weight.SelectedIndex == Array.IndexOf(weights, appearanceBaseline.FontWeight)) weight.SelectedIndex = Array.IndexOf(weights, updated.FontWeight);
            if (fontSize.Text == ((int)appearanceBaseline.FontSize).ToString()) fontSize.Text = ((int)updated.FontSize).ToString();
            foreach (var box in look.Children.OfType<TextBox>())
            {
                if (box.Name == "SpeechOpacity" && box.Text == Math.Round(appearanceBaseline.Opacity * 100).ToString()) box.Text = Math.Round(updated.Opacity * 100).ToString();
                else if (box.Name == "SpeechBackground" && box.Text == appearanceBaseline.Background) box.Text = updated.Background;
                else if (box.Name == "SpeechForeground" && box.Text == appearanceBaseline.Foreground) box.Text = updated.Foreground;
            }
            var hidden = look.Children.OfType<CheckBox>().First(c => c.Name == "SpeechControlsHidden");
            if (hidden.IsChecked == appearanceBaseline.ControlsHidden) hidden.IsChecked = updated.ControlsHidden;
            appearanceBaseline = updated.Copy(); PreviewFont();
        };
        look.Children.Add(Ui.Hint("字体、字号和粗细保存后直接应用，不重连。仅使用Live直译，无第二个文字接口。已用于当前译文的原文尾巴不另起一行，新原文段仍可先行预览。译文约0.65秒内转正体，真正收段后才计时；最多三行。原译独立记录，不保证逐句配对。关闭功能、总暂停及退出清空记录。"));
        var toggleButton = new Button { Content = "实时语音翻译功能：开启 / 关闭", IsEnabled = toggle != null, HorizontalAlignment = HorizontalAlignment.Left, Name = "ToggleSpeech" };
        toggleButton.Click += (_, _) => toggle?.Invoke(); pages[6].Children.Add(toggleButton);
        changes.Add(() =>
        {
            options.Authentication = auth.SelectedIndex; options.Target = SpeechOptions.Languages[Math.Max(0, target.SelectedIndex)]; options.CaptureProcess = mode.SelectedIndex == 1;
            if (sources.SelectedItem is AudioChoice choice)
            {
                if (options.CaptureProcess) { options.ProcessId = choice.ProcessId; options.ProcessStarted = choice.Started; options.ProcessName = choice.ProcessName; }
                else options.DeviceId = choice.Id;
            }
            options.Validate();
            if (options.CaptureProcess && options.ProcessId <= 0) throw new ArgumentException("请先播放目标应用的声音，并在实时语音页选择应用。");
            if (stagedSpeechTarget != null) pendingKeys.Remove(stagedSpeechTarget);
            stagedSpeechTarget = options.SecretTarget;
            if (delete.IsChecked == true) pendingKeys[options.SecretTarget] = "";
            else if (key.Password.Length > 0) pendingKeys[options.SecretTarget] = key.Password;
        });
        speechSaved = () => { appearanceBaseline = options.Copy(); key.Clear(); delete.IsChecked = false; RefreshPresence(); };
    }
}
