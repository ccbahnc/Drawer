# Third-party notices / 第三方声明

Drawer原创部分采用根目录LICENSE中的自定义许可。下列第三方部分不改用该许可，仍适用其各自条款；不得将微软等第三方运行库当作Drawer原创源码，或要求其公开专有源码。

本清单根据0.1、0.2实际Drawer.deps.json、运行时配置和锁文件核对，两版依赖相同。许可原文从对应NuGet缓存和官方上游取得；这是一份分发库存说明，不是专业法律审查意见。

| 项目 | 版本/用途 | 随附声明 |
| --- | --- | --- |
| .NET Runtime for Windows x64 | 8.0.8，自包含运行时 | licenses/dotnet-runtime-LICENSE.txt、dotnet-runtime-NOTICES.txt；Windows特殊二进制许可另见DOTNET-WINDOWS-LICENSING.md及DOTNET-LIBRARY-LICENSE.html |
| Microsoft.WindowsAppSDK | 1.7.260224002，WinUI/窗口/合成 | licenses/windowsappsdk-LICENSE.txt、windowsappsdk-NOTICES.txt、windowsappsdk-tools-NOTICES.txt |
| Microsoft.Web.WebView2 | 1.0.2903.40，依赖中的投影/加载组件 | licenses/webview2-LICENSE.txt、webview2-NOTICES.txt |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.38，Windows投影/WinRT | licenses/Windows-SDK-license.rtf、CsWinRT-LICENSE.txt；微软SDK条款及开源部分分别适用 |
| Microsoft.Windows.SDK.BuildTools | 10.0.22621.756，仅构建工具 | licenses/Windows-SDK-license.rtf；构建工具本身不作为独立工具随安装包分发 |
| Inno Setup | 6.7.3，安装引擎 | licenses/Inno-Setup-LICENSE.txt，保留原引擎版权及地址 |
| Inno Setup 简体中文翻译 | 上游中文消息文件 | licenses/ChineseSimplified-LICENSE.txt；scripts/ChineseSimplified.isl保留维护者信息 |

上游：
- https://github.com/dotnet/runtime
- https://github.com/dotnet/core/blob/main/license-information-windows.md
- https://www.nuget.org/packages/Microsoft.WindowsAppSDK/1.7.260224002
- https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40
- https://aka.ms/WinSDKLicenseURL
- https://github.com/microsoft/CsWinRT
- https://jrsoftware.org/
- https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation

官方第三方NOTICE中提到的源码获取和法定/许可允许的权利继续有效，不能被Drawer自定义限制覆盖。原文保持不翻译、不改写。对应许可证和本项目LICENSE同时随Release的许可压缩包提供；再分发安装文件时请一并保留这些声明。

项目自有Drawer图标按作者确认与原创代码使用相同许可。修改版不得冒充官方。未收录开发者桌面快捷方式、个人图标库或用户数据作为示例素材。