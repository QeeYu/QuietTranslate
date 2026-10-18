# 本机 OCR：内置识别与可选服务

本机服务是独立可选组件，不放进轻量版主程序，也不会由主程序自动启动。不使用它时，仍可直接使用云端 OCR 或内置 Windows OCR。

## 文件夹和教程是否需要保留

- 选择云端 OCR 或主程序内置的「Windows OCR」：不需要 `local-ocr` 文件夹，也不需要启动独立服务。
- 选择「本机 OCR 服务」：需要保留运行该服务所需的 exe、dll 和配置文件；可放在任意独立文件夹，不要求放进轻译目录。教程 md 文件可以删除。
- 开发和重新构建独立服务：源码中的 `local-ocr` 目录及其引用的 `OCR/WindowsOcr.cs`、`Assets` 图标应保留。教程只在源码和在线文档中提供，主程序运行包不附带它。

## 运行可选服务

1. Windows 10 2004 / Windows 11 x64，安装 .NET 8 Desktop Runtime x64（主程序与服务共用运行环境）。
2. 当前发布页不提供独立 OCR 运行包。需要 HTTP 服务时，按文末「源码构建」生成 `artifacts/local-ocr`，运行其中的 `QuietTranslate.LocalOcr.exe`。保留此控制台窗口，按 Ctrl+C 退出。
3. 浏览器打开 `http://127.0.0.1:17321/health`，应看到 `ready` 和已安装的识别语言。
4. 轻译 → 截图 OCR → 选择“本机 OCR 服务（独立接口）”，地址保留 `http://127.0.0.1:17321/ocr`，勾选“此服务无需密钥”。保存并应用，再点测试 OCR 或使用截图快捷键。
5. 修改端口：在服务目录的终端运行 `./QuietTranslate.LocalOcr.exe 17322`，同时修改轻译中的地址。端口冲突时不需要关闭防火墙。

## 自动识别语言与组件

源码中的示例服务使用 Windows 自带 OCR，不含大模型，不需要 Python 或独立模型下载。日语、英语识别需要对应的 Windows OCR 语言组件。Windows 设置 → 时间和语言 → 语言和区域 → 添加英语 / 日语 → 语言选项，安装对应的光学字符识别组件；安装后重启服务。

Microsoft 说明：OCR 可用语种取决于设备已安装的语言包：[AvailableRecognizerLanguages](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.availablerecognizerlanguages)。只安装显示语言或键盘不一定等于安装 OCR，最终以 `/health` 的 languages 为准。

选择“自动识别”会在已安装的日英识别器间尝试，假名/汉字作为日语线索。Windows OCR 无置信度，纯汉字、短词、混合语言、竖排漫画不能保证准确；需要更好识别可使用云端或按下方协议接自己的本地 OCR 引擎。截图 OCR 只返回原文，后续翻译仍由轻译当前的翻译服务完成；翻译选用云端时，识别原文仍会发送给该翻译服务。

## 独立接口协议：接入其他本地 OCR 引擎

“本机 OCR 服务”入口只接受回环地址（127.0.0.1、localhost、::1）。不要求一定使用源码中的示例服务：你的 RapidOCR、PaddleOCR 或其他本地服务只要实现以下 HTTP 协议即可。示例服务本身只监听 `127.0.0.1`。

请求 `POST /ocr`，Content-Type: application/json：

```json
{"image_base64":"不带 data: 前缀的 PNG Base64","language":"auto"}
```

`language` 可为 auto / en / ja / zh-Hans 等。成功 HTTP 200：

```json
{"text":"识别原文\n下一行"}
```

失败使用非 2xx 状态与 `{"error":"错误说明"}`。主程序固定读取 `text`。如果你的现成服务协议不同，选“自定义 OCR 接口”，配置 `{image_base64}` 或 `{image_data_url}`、JSON 返回路径及鉴权即可，不必修改主程序。

可选健康检查 `GET /health`，示例 `{"ready":true,"languages":"en-US、ja-JP"}`。示例服务串行处理、请求 7 MB、解码图片最多 1600 万像素、文字最多 8000 字，不记录图片/原文/请求日志；不接受带 Origin 的网页请求且不启用 CORS。主程序上传 PNG 上限 5 MB，识别截止时间 35 秒。服务退出即释放图片，不保存任何识别历史。

需要本地访问令牌时，在启动服务的 PowerShell 中设置：

```powershell
$env:QUIETTRANSLATE_OCR_TOKEN = '自行生成的长随机字符串'
./QuietTranslate.LocalOcr.exe
```

然后轻译中取消“无需密钥”，填入同一个令牌并保存（由 Windows 凭据管理器存储）。请求头为 `Authorization: Bearer 令牌`。令牌不要写入源码、教程或分享截图。

## 无需轻译的接口测试

在服务运行时执行，替换为自己的测试图片：

```powershell
$bytes = [IO.File]::ReadAllBytes('D:\test.png')
$body = @{ image_base64 = [Convert]::ToBase64String($bytes); language = 'auto' } | ConvertTo-Json
Invoke-RestMethod 'http://127.0.0.1:17321/ocr' -Method Post -ContentType 'application/json' -Body $body
```

如设置了令牌，给上面的命令增加 `-Headers @{ Authorization = "Bearer $env:QUIETTRANSLATE_OCR_TOKEN" }`。失败优先看 `/health`、端口、图片大小和语言组件。不要开启全网监听或关闭系统安全功能。

## 源码构建

安装 .NET 8 SDK x64。源码包保持目录结构（服务引用共享的 OCR/WindowsOcr.cs 和 Assets 图标），在源码根目录执行：

```powershell
dotnet build local-ocr/QuietTranslate.LocalOcr.csproj -c Release
dotnet publish local-ocr/QuietTranslate.LocalOcr.csproj -c Release -r win-x64 --self-contained false -o artifacts/local-ocr
```

首次构建需要从 NuGet 获取 Windows SDK .NET 引用包；无 Python、无额外 OCR NuGet 库。只需要本地识别的普通用户可直接选择主程序内置的 Windows OCR，无需构建或启动 HTTP 服务。
