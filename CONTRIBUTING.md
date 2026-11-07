# 参与开发

ToolHub 桌面端使用 C# / WPF / .NET 8，安卓互传端使用 Java / Android SDK 35。

功能问题请说明 Windows 或 Android 版本、复现步骤、预期与实际结果。硬件相关问题请附设备型号；日志中可能包含设备名、文件路径和网络地址，分享前请去掉个人信息。

提交改动前运行 `dotnet build Switchboard.csproj -c Release`。文件互传改动可运行 `--transfer-test` 和 `phone/tests/test-interop.ps1`，时间计算改动运行 `--shutdown-test`。硬件写入诊断 `--smoke-test`、`--brightness-test` 需要本机设备，不在通用 CI 中运行。

界面沿用现有配色、字体和实心方块选中标记。修改功能时更新 README；不提交构建缓存、接收的文件、运行日志、个人设置或签名密钥。

新代码使用仓库的 MIT 许可证。第三方代码、资源和依赖须注明来源，并保留其许可证。
