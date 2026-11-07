# ToolHub

Windows 10 1803+ / Windows 11 x64 桌面工具，支持音频输出、显示器亮度、定时关机、局域网互传、设备电量和格式转换。原生 WPF。

<img src="Assets/ToolHub.png" width="80" height="80" alt="ToolHub" />

## 下载

| 平台 | 下载 | 要求 |
| --- | --- | --- |
| Windows | [ToolHub 0.7.0 便携版](https://github.com/dzx241241666-lang/ToolHub/releases/download/v0.7.0/ToolHub-0.7.0-win-x64.zip) | Windows 10 / 11，64 位；解压后运行 `ToolHub.exe`，无需安装 .NET |
| Android | [ToolHub Send 0.5.0 APK](https://github.com/dzx241241666-lang/ToolHub/releases/download/v0.7.0/ToolHub-Send-0.5.0-Android.apk) | Android 10 及以上；局域网互传，可覆盖安装此前同签名版本 |

[完整发布页与校验文件](https://github.com/dzx241241666-lang/ToolHub/releases/tag/v0.7.0)。Windows 包已包含音视频转换组件，`tools` 文件夹与 EXE 一起保留。Office 文档转换需要本机安装 Microsoft Office 或 LibreOffice；设备电量与显示器亮度取决于硬件支持。

## 源码

仓库包含 Windows 桌面端和独立的安卓文件互传端。项目源码采用 [MIT 许可证](LICENSE)，第三方组件与互操作参考资料的许可证和声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。构建产物、个人设置、运行日志和签名密钥不包含在源码仓库内。

开发与问题反馈见 [CONTRIBUTING.md](CONTRIBUTING.md)。

0.6.6 更名为 ToolHub，采用红底白色锤形 T 图标。沿用原设置目录、传输身份、协议和定时关机计划；已保存的接收目录保持原路径。安卓端为 ToolHub Send，可覆盖安装旧版。

## 运行

`artifacts/ToolHub/ToolHub.exe`。关闭窗口后保留托盘图标；双击图标显示窗口，右键菜单中退出。重复打开只显示现有窗口。

## 音频

| 快捷键 | 功能 |
| --- | --- |
| Alt+1 … Alt+6 | 输出设备预设，首次启动按在线设备分配，最多六个 |
| Alt+← / Alt+→ | 在勾选的在线设备中循环 |
| Alt+↑ / Alt+↓ | 音量 ±，默认步长 2% |

前台模式：预设和循环作用于当前前台应用及其子进程；音量快捷键调节该应用的音频会话。操作工具窗口时沿用上一个前台应用。

全部模式：设置系统三种默认输出角色，并切换现有音频会话的应用；音量快捷键调节系统默认输出的主音量。新打开且跟随默认输出的应用使用新设备，其他未运行应用已有的单独输出偏好仍由 Windows 保留。

界面底部滑块和静音始终控制当前显示的输出设备。每行勾选项决定设备是否参与循环。预设保留离线设备，设备恢复后继续有效。

音频页直接单击预设、设备循环或音量旁显示的快捷键即可录入新组合键。录入期间暂停全局快捷键，保存或失焦后恢复；Esc 取消。重复组合键或系统占用会保留原键并在状态栏显示原因。音量步长也位于音频页。

总设置的“栏目显示”支持各功能的显示开关。短按切换显示，按住约 0.45 秒后上下拖动调整顺序；松开保存，Esc 取消拖动。排序同步到左侧导航、托盘菜单和顶部连续序号，隐藏栏目保留位置，重启后保持。设置入口固定在最后；隐藏栏目不终止已经启用的后台功能或关机计划。

应用需要已建立音频会话才能调节应用音量。旧式 WaveOut 播放流、独占音频、ASIO、固定硬件输出或部分游戏可能需要在应用内重新打开播放设备。应用级路由使用 Windows 内部音频策略接口，兼容性取决于系统版本。设置后仍有音频在旧设备播放时，状态栏显示尚未迁移的播放流数。

## 定时关机

左侧“定时关机”支持时、分、秒倒计时，以及年、月、日、时、分滚轮。滚轮支持鼠标滚动、上下拖动、方向键；单击数字可直接输入，Enter 或失焦提交，Esc 取消输入。月份和闰年自动调整日期上限。“指定时间”只显示仍可选择的日期和时间，最早为下一分钟；跨午夜、跨月和跨年时自动推进。未来日期保留全天范围，当前日期按所选小时限制分钟，手动输入采用相同范围。提供 15 分钟、30 分钟、1 小时、2 小时预设；界面实时显示剩余时长和关机日期时间。

“启用定时”将计划写入当前用户的 Windows 计划任务。关闭窗口、退出或重新打开程序后计划仍有效。页面和托盘菜单均可取消本工具的计划。该计划只在当前用户仍登录时执行，不唤醒休眠设备，不补执行错过的时间。默认使用普通关机；勾选“强制关机”后到点强制关闭应用，未保存内容可能丢失。强制选项随计划保存，计划生效期间锁定；修改需先取消原计划。

## 显示器亮度

每台屏幕独立提供亮度滑块、±5% 微调以及 25% / 50% / 75% / 100% 预设。内置屏幕通过 Windows WMI 调节，外接屏通过 DDC/CI 调节硬件亮度。页面打开时同步读取当前值；拖动时合并连续请求，硬件通信在后台执行。显示器接入或移除后自动重新检测，也可手动刷新。

外接屏需支持并开启 DDC/CI，部分扩展坞、转换器或 HDR 模式可能不提供该接口。不可调节的屏幕保留在列表中并显示原因，不使用软件遮罩。亮度直接写入屏幕，不在程序启动时自动改写。

## 局域网互传

0.7.0 / Android 0.5.0 新增文本弹窗、剪贴板和文件夹发送。选择内容后单击设备直接发送；接收文本可查看与复制，文件夹保留层级、空目录和空文件。同名文件夹自动另存，取消或失败清理本次临时目录。Windows 剪贴板支持文字、位图、文件和文件夹；安卓支持文字及带访问权限的文件、图片 URI。首次在安卓接收文件夹时选择一个文件夹保存位置，此后会记住；普通文件仍在 Downloads / ToolHub Send。两端更新可获得完整体验，旧端仍会收到可打开的文本文件或 ZIP。

电脑端进入“局域网互传”页即自动启动服务，右上角只显示状态；窗口关闭到托盘后仍可接收。先选择或拖入多个文件，再单击附近设备卡片即发送。没有已选文件时，单击设备会打开文件选择器，选择后直接发送到该设备；发送成功后清空本次选择，失败时保留。接收前逐批确认，可拒绝；“接收后复制到剪贴板”将校验完成的文件作为文件列表复制，随后可在资源管理器中粘贴。接收目录默认是用户下载目录下的 `ToolHub`。

安卓安装包：`artifacts/ToolHub-Send-Android.apk`，支持 Android 10 及以上，可覆盖安装旧版。手机端仅包含局域网互传，使用“发送 / 接收”底部导航；发送页先选择文件或照片、视频，再单击附近设备直接发送。文件元数据只在选择时后台读取，进度刷新不重建文件列表。接收请求直接弹出确认，两个页面均显示当前传输进度并可取消。支持系统分享入口、接收记录中的打开、分享和复制。文件保存在 `Downloads/ToolHub Send`。通知提供接收、拒绝和停止接收；安卓系统可能限制长期后台运行。手机剪贴板使用文件 URI，能否粘贴取决于目标应用。

0.4.1 的安卓身份使用独立 ECDSA P-256 密钥，允许 TLS 提供者所需的原始摘要签名；启动时实际进行一次本机 HTTPS 握手检查。升级后自动发现会更新证书指纹，避免列表保留旧指纹。握手错误与离线、网络错误分别显示。

两端处于同一局域网时自动发现，可通过设备地址手动连接。电脑首次开启时 Windows 可能询问局域网访问权限；热点隔离、访客 Wi-Fi、防火墙或 VPN 可能阻止设备互相访问。无需账号、云端或第三方传输应用。此模块是独立实现，与 LocalSend 应用和协议不互通。

传输采用 HTTPS、设备证书指纹核对、接收确认和逐文件随机凭据，并校验 SHA-256。设备发现本身没有可信身份认证，接收时应核对设备名称和地址。每批最多 512 个文件、合计 32 GB；同名文件另存，不覆盖已有文件。取消或校验失败时删除本次未完成文件，已经完成的文件保留。暂不包含目录递归、断点续传或跨公网传输。

## 设备电量

“设备电量”页读取 Windows 已知的蓝牙设备及其驱动报告的电量，按蓝牙地址合并设备服务记录。右键设备卡片可修改名称、选择耳机、鼠标、键盘、手柄、手机 / 平板、音箱和通用预设图标，以及上传自己的图标。自定义图标支持 PNG、JPG、BMP、GIF、ICO，上限 8 MB，转换为工具内独立保存的 PNG，原图片不修改。名称和图标仅在本工具内生效，不改系统设备名称。

右键菜单的“隐藏设备”会立即移除该卡片并保存选择，刷新和重启后继续隐藏。右上角“已隐藏”菜单可逐台恢复，包括当前没有连接的设备。隐藏仅影响本工具的显示。

打开页面即读取，页面显示时每 30 秒刷新，也可手动刷新。未连接的设备显示“未连接”，隐藏上次电量和电量条，也不计入提供电量的设备数；已连接但未提供数值时显示“未报告”。XInput 无线手柄在没有百分比时显示驱动返回的电量档位，不换算为虚构百分比。专用 2.4G 接收器、仅提供厂商私有接口的设备，以及手机电量不一定能由 Windows 读取。总设置可隐藏该栏目。

0.6.3 新增雷柏 VT3 的 2.4G 接收器读取，已在本机 `24AE:1412`、FF00:0002 / 13 字节状态报告上验证。通过 Windows HID 只读句柄接收电量广播，不向鼠标发送设置命令，也不加载 A Hub 的程序或 DLL。首次读取最多等待 5 秒；与 A Hub 同时运行时已核对为相同的 31%，关闭 A Hub 后也已读到 31%。接收器按系统设备容器标识合并，保留改名、图标和隐藏设置；尚未收到广播时显示等待状态，已有数值但未收到新报告时注明上次报告时间，接收器拔出后移除。其他雷柏型号及接收器协议暂不视为已支持。

状态帧格式研究参考 ClickSync 的 Rapoo 协议记录：<https://github.com/Nuitfanee/ClickSync/blob/main/src/protocols/protocol_api_rapoo.js>。本模块使用独立的原生 HID 只读实现，未包含该项目代码。

自定义图标存放在 `%LOCALAPPDATA%/Switchboard/wireless-icons`，设备别名和图标选择保存在现有设置文件内。

## 格式转换

图片、音视频、文档使用独立批量队列，支持多选或拖入文件、目标格式、输出目录、图片质量、进度、逐文件错误和取消。每类最多 300 个文件；混合文档只显示共同支持的目标格式。默认输出在下载目录的 `ConvertedFiles`，同名文件自动追加序号，原文件不修改。关闭到托盘后继续转换；退出时取消当前转换并清理本次临时文件。栏目可在总设置隐藏。

| 类型 | 输入 | 输出 |
| --- | --- | --- |
| 图片 | PNG / JPG / WebP / BMP / TIFF / GIF / ICO / AVIF / HEIC | PNG / JPG / WebP / BMP / TIFF / GIF / ICO / PDF |
| 音频 | MP3 / WAV / FLAC / M4A / AAC / OGG / OPUS / WMA / AIFF / APE | MP3 / WAV / FLAC / M4A / OGG / OPUS |
| 视频 | MP4 / MKV / MOV / AVI / WebM / WMV / FLV / M4V / MPEG / TS / MTS / 3GP | MP4 / MKV / MOV / WebM / GIF，或提取为以上音频格式 |
| 文字文档 | DOC / DOCX / ODT / RTF / TXT / PDF | PDF / DOCX / DOC / ODT / RTF / TXT |
| 表格 | XLS / XLSX / ODS / CSV | PDF / XLSX / XLS / CSV |
| 演示文稿 | PPT / PPTX / ODP | PDF / PPTX / PPT |

全部在本机处理。图片使用内置 Magick.NET，音视频使用内置 FFmpeg，无需另装。Word、表格和幻灯片转换调用已安装的对应 Microsoft Office，未安装时可使用已安装的 LibreOffice。PDF 提取文字为 DOCX / TXT 无需 Office；转其他文档格式时仍需要 Office / LibreOffice。PDF 导出为文档仅保留文字与分页，不保留原排版，不含 OCR；扫描件无可提取文字时显示原因。

多帧图片转换首帧；JPG、BMP、图片 PDF 的透明区域填充白色；ICO 最长边不超过 256 px。视频默认 H.264 / AAC，WebM 为 VP9 / Opus；GIF 为宽 640 px、12 fps。CSV 使用 UTF-8，仅导出首张工作表。损坏、加密、受保护或组件不能解码的文件可能转换失败，其他队列文件继续。

文档在单独进程中只读打开，禁用自动化宏和 Word / Excel 链接更新，转换后关闭本次文档；保留已有 Office 实例。取消或超时只清理本工具启动并记录的转换进程。LibreOffice 使用独立临时配置，不访问用户的现有文档窗口。

## 数据

`%LOCALAPPDATA%/Switchboard/settings.json` 保存设备 ID、快捷键和选项。`transfer-id.txt` 和 `transfer-identity.pfx` 保存本机传输身份与证书，不写入系统根证书库。`error.log` 为本地错误日志。开机启动可选，默认关闭。

## 构建

.NET 8 SDK；NuGet 使用 Magick.NET-Q8-x64 15.1.0、PdfPig 0.1.16。`tools/prepare-conversion.ps1` 下载固定版本 FFmpeg 并校验 SHA-256，构建脚本自动调用；发布目录包含组件许可证和原始说明。

```powershell
.\build.ps1 -SelfContained
```

省略 `-SelfContained` 的版本需要 .NET 8 Desktop Runtime 和 ASP.NET Core Runtime。生成目录为 `artifacts/ToolHub`。

安卓端为 Java 原生应用，不依赖 Gradle 或外部安卓库。安装 JDK 17、Android SDK Platform 35 和 Build Tools 35.0.0，设置 `JAVA_HOME`、`ANDROID_HOME` 后可直接运行脚本；也可通过 `-JavaHome`、`-AndroidSdk` 指定。未设置时兼容原本的 `%LOCALAPPDATA%/CodexTools/android-build` 工具目录及 `-ToolRoot` 参数。

默认签名密钥保存在工具目录的 `switchboard-send.jks`，仅用于个人开发测试；正式签名可通过 `-Keystore`、`-KeyAlias` 和环境变量 `TOOLHUB_KEYSTORE_PASSWORD` 指定。升级已安装的安卓端须使用相同密钥，密钥及密码不提交到仓库。

```powershell
.\phone\build-phone.ps1
```

## 验证

```powershell
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--diagnose diagnostics.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--smoke-test smoke.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--shutdown-test shutdown-tests.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--brightness-diagnose brightness.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--brightness-test brightness-tests.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--transfer-test transfer-tests.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--conversion-test conversion-tests.json' -Wait
Start-Process .\artifacts\ToolHub\ToolHub.exe -ArgumentList '--rapoo-diagnose rapoo-reports.json' -Wait
.\phone\tests\test-interop.ps1
```

diagnose 读取设备、会话、路由。smoke-test 另做音量、静音、本进程路由与三种系统默认输出角色的写入和回读，finally 按角色恢复原值。真实播放、蓝牙断连和独占应用仍需对应硬件验证。

shutdown-test 验证时间计算、输入边界、持久状态和取消；Windows 计划任务的创建、修改、回读和删除使用未来的无害测试命令，不执行关机。

brightness-diagnose 只读取显示器信息和亮度。brightness-test 验证映射、每屏写入回读和隔离性；每台屏幕只改变一个原生亮度档位，并在 finally 恢复原值。

transfer-test 仅绑定回环地址，使用生成的测试文件，验证双向多文件、空文件、中文文件名、同名保护、剪贴板选项、证书核对、拒绝、取消、上传凭据和校验失败清理。test-interop 使用同一份安卓 PeerEngine 源码和 ECDSA 证书在 JVM 上与 Windows 端进行双向传输和异常路径验证；测试用 org.json JAR 不包含在 APK 内。另已通过桌面界面的自动启动、文件选择、单击设备发送及接收内容校验。APK 已完成编译和签名验证；Android Keystore 与安卓 UI 仍需真机确认。

`tests/AudioProbe` 提供真实的静音 WaveOut 音频会话，用于验证前台识别、预设、设备循环与应用音量快捷键。关闭测试窗口后恢复测试进程的原输出偏好。发布版自带 .NET 8.0.33，无需另装运行时。
