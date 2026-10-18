# 项目结构与维护

主程序继续使用一个 WPF 项目，不引入额外 UI、依赖注入或测试框架。

| 目录 | 职责 |
|---|---|
| `App.cs` | 启动、单实例、托盘与窗口协调、划词／截图翻译流程、退出清理 |
| `UI/` | 设置、输入栏、气泡、主题、定位与滚动 |
| `Translation/` | 翻译协议、HTTP 辅助、流式解析、错误分类、共享请求调度 |
| `OCR/` | 云端识别与 Windows OCR 客户端 |
| `Platform/` | Windows 快捷键、选区、剪贴板、托盘与图标 |
| `Configuration/` | 配置数据、配置文件读写与 Windows 凭据管理 |
| `tests/` | 独立的 Windows 桌面测试程序、模拟接口、截图与演示工具 |
| `local-ocr/` | 独立可选的本机 OCR 服务，不参与主程序编译 |
| `tools/` | 回归运行与发布隐私检查 |

## 翻译请求

划词、输入栏和设置测试都经过 `TranslationService`。多个服务实例共用 `TranslationRequests`：同一接口地址及密钥的请求串行，不同接口或密钥可独立执行。客户端不知道多个密钥是否属于同一平台账户，所以不能代替服务端的账户级限流。

等待和网络请求共同受 30 秒期限约束；排队可以取消，取消后不发送过期文字。遇到限流／繁忙，下一次主动请求遵守 `Retry-After`；缺少该提示时分别等待 2／3 秒。不自动重发失败请求，不重复发送已经输出部分译文的请求。

调度器仅短暂保留接口和密钥组合的哈希、信号量及等待时间，不缓存原文、译文或密钥。空闲等待状态上限为 64 个；大量不同服务同时活跃时，活跃状态随实际任务数量增长，任务结束后清理。

`ServiceErrors` 集中分类错误。HTTP 错误响应最多读取 16 KiB，额外解析最多等待 2 秒；显示应用内预设说明、HTTP 状态及已知业务码，不显示服务端消息或未知业务码。普通 JSON 和流式错误共用分类；未知错误保留安全的通用提示，不猜测账户是否欠费。

## 配置与凭据

原有设置 JSON 结构、文件位置、枚举取值及凭据名称规则保持兼容。`Models` 定义数据，`SettingsStore` 负责配置加载和临时文件替换，`Credentials` 负责 Windows 凭据管理。已有调用通过 `Settings.Load/Save` 进入读写层。

## 构建与验证

```powershell
dotnet build QuietTranslate.csproj -c Release
dotnet build tests/QuietTranslate.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools/run-checks.ps1 -SkipBuild
```

测试宿主直接编译相同的生产源文件，并加上原有 `partial App` 桌面测试夹具，以保留真实 WPF 线程和窗口生命周期测试。正式项目排除整个 `tests/`；测试入口和夹具不随正式程序发布。测试宿主不是用户运行版本，测试需在普通 Windows 桌面执行。

现有兼容 AI 共用同一协议实现；增加服务商时先复用，不为每个平台建立重复框架。只有协议差异明显增长时，再拆出对应协议实现。文件移动本身不代表运行速度提升。
