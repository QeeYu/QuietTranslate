using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace QuietTranslate;

internal sealed class ServiceEditor : StackPanel
{
    private readonly Settings draft;
    private readonly bool ocr;
    private readonly Dictionary<string, string> pending;
    private readonly ComboBox choice = new(), profiles = new();
    private readonly TextBox endpoint = Field(), model = Field(), appId = Field(), region = Field(), prompt = Multi(), body = Multi(), path = Field(), header = Field(), prefix = Field(), profileName = Field();
    private readonly PasswordBox key = new();
    private readonly TextBlock keyStatus = new() { Name = "ServiceKeyStatus", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Border keyBadge = new() { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, -5, 0, 12) };
    private readonly CheckBox thinking = new() { Content = "使用此平台的关闭思考参数（仅支持的平台生效）" }, word = new() { Content = "孤立单词显示少量常见释义（仅 AI）" }, anonymous = new() { Content = "此服务无需密钥（如本机服务）" }, form = new() { Content = "使用表单编码（默认 POST JSON）" };
    private readonly StackPanel modelArea = new(), idArea = new(), regionArea = new(), aiArea = new(), customArea = new();
    private readonly TextBlock note = Ui.Hint("");
    private int editing;
    private bool loading;
    private string loadedIdentity = "";
    internal Provider Provider => (Provider)editing;
    internal OcrProvider OcrProvider => (OcrProvider)editing;
    internal ServiceSettings Config => ocr ? draft.OcrServices[OcrProvider] : draft.Services[Provider];
    internal ServiceEditor(Settings draft, bool ocr, Dictionary<string, string> pending)
    {
        this.draft = draft; this.ocr = ocr; this.pending = pending;
        editing = ocr ? (int)draft.OcrProvider : (int)draft.Provider;
        choice.ItemsSource = ocr ? Enum.GetValues<OcrProvider>().Select(Settings.OcrName).ToArray() : Enum.GetValues<Provider>().Select(Settings.Name).ToArray();
        Add(this, ocr ? "识别服务" : "翻译服务", choice); Add(this, "完整接口地址或 AI 基础地址", endpoint);
        key.Name = ocr ? "OcrKey" : "ServiceKey"; keyStatus.Name = ocr ? "OcrKeyStatus" : "ServiceKeyStatus";
        endpoint.Name = ocr ? "OcrEndpoint" : "ServiceEndpoint"; choice.Name = ocr ? "OcrChoice" : "ServiceChoice";
        Add(this, "密钥 · 留空保留已保存值", key); keyBadge.Child = keyStatus; Children.Add(keyBadge);
        Add(idArea, "应用 ID / SecretId / 百度 OCR API Key（非密钥部分）", appId); Children.Add(idArea);
        Add(modelArea, "模型名称 · 以服务商控制台为准", model);
        var fetchModels = new Button { Content = "读取可用模型列表", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        var modelList = new ComboBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) }; modelList.SelectionChanged += (_, _) => { if (modelList.SelectedItem is string name) model.Text = name; };
        fetchModels.Click += async (_, _) =>
        {
            fetchModels.IsEnabled = false;
            try
            {
                Commit(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var url = Settings.CompletionEndpoint(Provider.CustomAI, Config.Endpoint).AbsoluteUri; url = url[..^"/chat/completions".Length] + "/models";
                using var client = ApiSupport.Client(); using var request = new HttpRequestMessage(HttpMethod.Get, url); ApiSupport.Authenticate(request, Config, Secret());
                using var doc = await ApiSupport.SendJson(client, request, deadline.Token); string names = ApiSupport.Extract(doc.RootElement, "data[].id");
                if (names.Length == 0) throw new InvalidOperationException("服务没有返回模型列表，请手动填写模型名。");
                modelList.ItemsSource = names.Split('\n'); modelList.Visibility = Visibility.Visible; note.Text = "选择模型后保存；列表包含的模型不一定都适合翻译或图片识别。";
            }
            catch (Exception) { note.Text = "读取模型列表失败或服务不支持此接口，请手动填写模型名称。"; }
            finally { fetchModels.IsEnabled = true; }
        };
        modelArea.Children.Add(fetchModels); modelArea.Children.Add(modelList); Children.Add(modelArea);
        Add(regionArea, "服务区域", region); Children.Add(regionArea);
        Children.Add(anonymous); Children.Add(note);
        var forget = new Button { Content = "删除此地址的密钥", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        forget.Name = ocr ? "ForgetOcrKey" : "ForgetServiceKey";
        forget.Click += (_, _) => { Commit(); pending[Target()] = ""; if (NeedsIdentity) { pending[Target() + "/id"] = ""; Config.AppId = ""; appId.Clear(); } key.Clear(); RefreshCredentialStatus(); note.Text = "保存并应用后删除密钥及相关应用标识。"; }; Children.Add(forget);
        aiArea.Children.Add(thinking); aiArea.Children.Add(word); Add(aiArea, "翻译提示词 · {source_language} / {target_language}", prompt);
        var presets = new WrapPanel();
        foreach (var pair in new[] { ("恢复默认", ServiceSettings.DefaultPrompt), ("Discord 聊天", ServiceSettings.DefaultPrompt + " 保留聊天语气、缩写、表情、用户提及和频道名称，避免过度书面化。"), ("日文游戏", ServiceSettings.DefaultPrompt + " 注意游戏术语、人物称谓与日语省略。无法确定的专有名词保留原文，不添加剧情。") })
        { var p = pair; var button = new Button { Content = p.Item1, Margin = new Thickness(0, 0, 8, 0) }; button.Click += (_, _) => prompt.Text = p.Item2; presets.Children.Add(button); }
        aiArea.Children.Add(presets); Children.Add(new Expander { Header = "AI 提示词与参数", Content = aiArea, IsExpanded = false });
        Add(customArea, "密钥请求头（如 Authorization；仅在模板传密钥时可留空）", header); Add(customArea, "密钥前缀（Bearer 后需一个空格；无需前缀可留空）", prefix); customArea.Children.Add(form);
        Add(customArea, ocr ? "POST 模板 · {image_base64} / {image_data_url} / {api_key}" : "POST 模板 · {text} / {source} / {target} / {api_key}", body);
        Add(customArea, "译文／文字 JSON 路径 · 如 data.text 或 results[].text", path);
        customArea.Children.Add(Ui.Hint("模板是 JSON；占位符只放在字符串值中，程序负责转义。自定义接口支持 POST JSON / 表单及单个密钥请求头，不运行脚本。"));
        Children.Add(new Expander { Header = "自定义接口与鉴权", Content = customArea });
        if (!ocr)
        {
            var saved = new StackPanel(); Add(saved, "命名配置 · 可保存多个自定义服务", profiles); Add(saved, "新配置名称", profileName);
            var saveAs = new Button { Content = "另存为命名配置", HorizontalAlignment = HorizontalAlignment.Left };
            saveAs.Click += (_, _) =>
            {
                try
                {
                    Commit(); string name = profileName.Text.Trim(); if (name.Length == 0) throw new ArgumentException("请填写配置名称。");
                    string oldTarget = Target(); var copy = Clone(Config); copy.CredentialId = Guid.NewGuid().ToString("N");
                    string secret = pending.TryGetValue(oldTarget, out var current) ? current : Credentials.Get(oldTarget);
                    string identity = Config.AppId;
                    draft.SavedServices[name] = new() { Provider = Provider, Settings = copy }; draft.Services[Provider] = Clone(copy);
                    if (secret.Length > 0) pending[Target()] = secret;
                    if (NeedsIdentity) { pending[Target() + "/id"] = identity; draft.Services[Provider].AppId = identity; }
                    RefreshProfiles(); RefreshCredentialStatus(); note.Text = "命名配置已加入草稿；点击保存并应用后生效。";
                }
                catch (Exception ex) { note.Text = ex.Message; }
            };
            saved.Children.Add(saveAs); Children.Add(new Expander { Header = "多个服务配置", Content = saved }); RefreshProfiles();
            profiles.SelectionChanged += (_, _) => { if (loading || profiles.SelectedItem is not string name || !draft.SavedServices.TryGetValue(name, out var p)) return; Commit(); draft.Services[p.Provider] = Clone(p.Settings); loading = true; editing = (int)p.Provider; choice.SelectedIndex = editing; loading = false; Load(); };
        }
        choice.SelectionChanged += (_, _) => { if (loading || choice.SelectedIndex < 0) return; Commit(); editing = choice.SelectedIndex; Load(); };
        key.PasswordChanged += (_, _) => { if (!loading) RefreshCredentialStatus(); };
        endpoint.TextChanged += (_, _) => { if (!loading) RefreshCredentialStatus(); };
        anonymous.Checked += (_, _) => { if (!loading) RefreshCredentialStatus(); }; anonymous.Unchecked += (_, _) => { if (!loading) RefreshCredentialStatus(); };
        loading = true; choice.SelectedIndex = editing; loading = false; Load();
    }
    private void RefreshProfiles() { loading = true; profiles.ItemsSource = draft.SavedServices.Keys.ToArray(); loading = false; }
    internal void Commit()
    {
        var s = Config;
        if (NeedsIdentity && !s.Endpoint.Equals(endpoint.Text.Trim(), StringComparison.OrdinalIgnoreCase) && appId.Text == loadedIdentity) appId.Clear();
        s.Endpoint = endpoint.Text.Trim(); s.Model = model.Text.Trim(); s.AppId = appId.Text.Trim(); s.Region = region.Text.Trim(); s.DisableThinking = thinking.IsChecked == true; s.WordMeanings = word.IsChecked == true; s.AllowAnonymous = anonymous.IsChecked == true;
        s.Prompt = prompt.Text; s.BodyTemplate = body.Text; s.ResponsePath = path.Text.Trim(); s.KeyHeader = header.Text.Trim(); s.KeyPrefix = prefix.Text; s.FormEncoded = form.IsChecked == true;
        if (key.Password.Length > 0) { if (!string.IsNullOrWhiteSpace(key.Password)) pending[Target()] = key.Password.Trim(); key.Clear(); }
        if (NeedsIdentity) pending[Target() + "/id"] = s.AppId;
        if (ocr) draft.OcrProvider = OcrProvider; else draft.Provider = Provider;
        // Update an already named profile as the user edits it, without writing any secret to JSON.
        if (!ocr && s.CredentialId.Length > 0) foreach (var p in draft.SavedServices.Values) if (p.Provider == Provider && p.Settings.CredentialId == s.CredentialId) p.Settings = Clone(s);
        RefreshCredentialStatus();
    }
    internal string Target() => ocr ? OcrService.SecretTarget(OcrProvider, Config) : Credentials.Target(Provider, Config);
    private bool NeedsIdentity => (!ocr && Provider is Provider.Baidu or Provider.Tencent or Provider.Youdao) || (ocr && OcrProvider is OcrProvider.Tencent or OcrProvider.Baidu);
    internal string Secret() => pending.TryGetValue(Target(), out var value) ? value : Credentials.Get(Target());
    internal void RefreshCredentialStatus()
    {
        string text; bool warning = false;
        try
        {
            var current = new ServiceSettings { Endpoint = endpoint.Text.Trim(), CredentialId = Config.CredentialId };
            string target = ocr ? OcrService.SecretTarget(OcrProvider, current) : Credentials.Target(Provider, current);
            if (ocr && OcrProvider == OcrProvider.Windows) text = "本地识别 · 无需密钥";
            else if (!string.IsNullOrWhiteSpace(key.Password)) { text = "已填写新密钥 · 待保存并应用"; warning = true; }
            else if (pending.TryGetValue(target, out var value)) { text = value.Length == 0 ? "密钥待删除 · 保存并应用后生效" : "新密钥待保存 · 请点击保存并应用"; warning = true; }
            else if (Credentials.Exists(target)) text = "✓ 密钥已保存 · 留空即可继续使用";
            else if (anonymous.IsChecked == true) text = "允许无密钥访问 · 请确认服务支持匿名";
            else { text = "未配置密钥 · 请填写后保存并应用"; warning = true; }
        }
        catch (System.ComponentModel.Win32Exception) { text = "无法确认密钥保存状态 · 请重试"; warning = true; }
        keyStatus.Text = text; keyStatus.SetResourceReference(TextBlock.ForegroundProperty, warning ? "UiWarning" : "UiAccent");
        keyBadge.SetResourceReference(Border.BackgroundProperty, warning ? "UiControl" : "UiAccentSoft");
    }
    internal void Validate()
    {
        Commit(); if (ocr && OcrProvider == OcrProvider.Windows) return;
        ApiSupport.Endpoint(Config.Endpoint);
        if (ocr && OcrProvider == OcrProvider.Local && !ApiSupport.Endpoint(Config.Endpoint).IsLoopback) throw new ArgumentException("本机服务只允许 localhost / 127.0.0.1 / ::1 地址。");
        if ((!ocr && Settings.IsAI(Provider)) || (ocr && OcrProvider == OcrProvider.VisionAI)) { if (Config.Model.Length == 0) throw new ArgumentException("请填写模型名称。"); }
        if ((!ocr && Provider == Provider.CustomTranslation) || (ocr && OcrProvider == OcrProvider.Custom)) { using var _ = JsonDocument.Parse(Config.BodyTemplate); if (string.IsNullOrWhiteSpace(Config.ResponsePath)) throw new ArgumentException("请填写返回文字的 JSON 路径。"); }
    }
    private void Load()
    {
        loading = true;
        var s = Config;
        if (NeedsIdentity && s.AppId.Length == 0) s.AppId = pending.TryGetValue(Target() + "/id", out var identity) ? identity : Credentials.Get(Target() + "/id");
        endpoint.Text = s.Endpoint; model.Text = s.Model; appId.Text = s.AppId; region.Text = s.Region; key.Clear(); thinking.IsChecked = s.DisableThinking; word.IsChecked = s.WordMeanings; anonymous.IsChecked = s.AllowAnonymous;
        loadedIdentity = s.AppId;
        prompt.Text = s.Prompt; body.Text = s.BodyTemplate; path.Text = s.ResponsePath; header.Text = s.KeyHeader; prefix.Text = s.KeyPrefix; form.IsChecked = s.FormEncoded;
        bool ai = !ocr && Settings.IsAI(Provider), custom = (!ocr && Provider == Provider.CustomTranslation) || (ocr && OcrProvider == OcrProvider.Custom);
        modelArea.Visibility = ai || (ocr && OcrProvider == OcrProvider.VisionAI) ? Visibility.Visible : Visibility.Collapsed;
        aiArea.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        customArea.Visibility = custom || ai || (ocr && OcrProvider == OcrProvider.VisionAI) ? Visibility.Visible : Visibility.Collapsed;
        idArea.Visibility = (!ocr && Provider is Provider.Baidu or Provider.Tencent or Provider.Youdao) || (ocr && OcrProvider is OcrProvider.Tencent or OcrProvider.Baidu) ? Visibility.Visible : Visibility.Collapsed;
        regionArea.Visibility = (!ocr && Provider is Provider.Microsoft or Provider.Tencent) || (ocr && OcrProvider == OcrProvider.Tencent) ? Visibility.Visible : Visibility.Collapsed;
        endpoint.IsEnabled = key.IsEnabled = anonymous.IsEnabled = !(ocr && OcrProvider == OcrProvider.Windows);
        note.Text = ocr ? OcrProvider switch {
            OcrProvider.Local => "独立本机 HTTP OCR：POST image_base64 + language，返回 text。只连接回环地址；无需云密钥。可使用附带的 Windows OCR 服务，也可按教程接入其他本地识别引擎。",
            OcrProvider.Windows => ScreenshotOcr.ReadinessMessage,
            OcrProvider.OCRSpace => "需要申请免费 Key。官方免费套餐：25,000 次/月，另有每 IP 每日 500 次限制，单图 1 MB；无可用性保证。使用引擎 2 自动识别日英等文字。",
            OcrProvider.Tencent => "需要开通 OCR。通用印刷体识别有每月 1,000 次免费额度，可能与其他接口共享；超额费用和额度以控制台为准。",
            OcrProvider.Baidu => "API Key 填入应用标识，Secret Key 填入密钥。使用高精度接口的 auto_detect 自动识别；免费测试资源须按此接口在控制台领取，不与标准版额度混用。",
            OcrProvider.GoogleVision => "需要开通 Cloud Vision 和项目计费。每月前 1,000 个相关计费单元免费；超额按服务商规则计费。",
            OcrProvider.VisionAI => "仅支持兼容 Chat Completions 且接受图片的模型。识别费用由模型平台收取，不能使用纯文字模型。", _ => "使用你自己的 OCR 服务；图片字段和返回路径均可设置。" } : Provider switch {
                Provider.Qwen => "地址随地域、工作空间而异，请以控制台提供的地址为准。", Provider.Doubao => "模型名或推理接入点 ID 请从火山方舟控制台复制。", Provider.Kimi => "不同模型的思考参数可能不同；本工具不向 Kimi 发送 DeepSeek 专有参数。", _ => "密钥由 Windows 凭据管理器保护。源语言默认自动识别，不保留翻译历史或结果缓存。" };
        loading = false; RefreshCredentialStatus();
    }
    private static ServiceSettings Clone(ServiceSettings s) => JsonSerializer.Deserialize<ServiceSettings>(JsonSerializer.Serialize(s))!;
    private static TextBox Field() => new() { FontSize = 13 };
    private static TextBox Multi() => new() { FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static void Add(StackPanel panel, string label, UIElement element) { panel.Children.Add(Ui.Hint(label)); if (element is FrameworkElement f) f.Margin = new Thickness(0, 6, 0, 12); panel.Children.Add(element); }
}
