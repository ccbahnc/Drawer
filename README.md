# Drawer

Windows 桌面边缘抽屉工具，用于收纳快捷方式、文件和网址。鼠标停留在抽屉对应屏幕边缘可展开，离开后收回。

**当前是小范围测试版，主要测试环境为 Windows 11 x64。安装包未签名，Windows 10 未完成实机验收。**

- 顶部、左侧和右侧多抽屉，支持调整行列、缩放及自动让位。
- 文件夹分组、拖放、分页、多选、复制剪切、删除和撤销重做。
- 快捷方式的自定义显示名称；不修改外部原快捷方式名称和目标。
- 浅色/深色与纯色、透明、模糊、亚克力；中文/English。
- 可选显示导航栏、显示图标文字和开机启动。

下载：在本仓库 **Releases** 中选择对应的 `setup.exe`，不要将 GitHub 的自动源码ZIP当成安装程序。
安装前从托盘正常退出现有 Drawer；升级保留用户数据和已有选项。默认程序目录为 `D:\Program Files\Drawer`，没有D盘时使用系统Program Files，可选择合法安装位置；遇到权限不足按安装器提示授权或更换目录。

用户数据在 `%LOCALAPPDATA%\Drawer`，与安装位置独立。卸载默认保留；若主动选择并确认清理用户数据，将删除该目录内的Drawer数据及相应恢复备份。备份请保存到这些目录之外。

已知限制、实际验收范围见 [版本说明](docs/RELEASE-NOTES.md)；构建方法见 [BUILD](docs/BUILD.md)。可复现问题请用Issues提供程序版本、Windows版本、缩放、材质和简短步骤；日志可能包含个人名称或路径，分享前自行检查和脱敏。不要上传真实用户库、快捷方式副本或完整配置。

采用 [Drawer 免费使用与源码共享许可 1.0](LICENSE)，Copyright (c) 2026 ccbahnc。所有人可免费使用，包括企业内部办公；禁止对软件及修改版直接变现，未经授权也禁止围绕Drawer提供收费定制、维护或安装服务。免费再分发须提供对应源码、沿用许可并保留声明。自有图标采用相同许可，修改版须标明非官方；第三方部分见 [原有许可声明](THIRD-PARTY-NOTICES.md)。本项目是源码可用软件，不是MIT/GPL或OSI标准开源许可。

## English

Drawer is a Windows desktop edge launcher with multiple drawers, folders, drag and drop, configurable layouts, and appearance settings. Download the installer from **Releases**. These unsigned test builds target Windows 11 x64; Windows 10 has not been tested on a physical machine.

Exit any running Drawer instance normally before upgrading. User data remains in `%LOCALAPPDATA%\Drawer`. Uninstall keeps data by default; explicitly choosing data removal deletes Drawer data and its recovery backups. The custom Drawer Free Use and Source Sharing License permits free use, including internal business use. Monetization and Drawer-specific paid services require separate written permission. Redistribution requires corresponding source and retained notices. See LICENSE and THIRD-PARTY-NOTICES.md; this is source-available software, not an OSI-approved open-source license. See the release notes for tested behavior and known limitations.
0.2新增全局 **全屏时可呼出 / Allow in full screen**，默认关闭。用户已验收本次全屏呼出效果；其他既有功能未在本轮重测。独占全屏等受限程序仍不保证覆盖。

发布仓库：https://github.com/ccbahnc/Drawer 。安装包、对应源码和许可附件见 [Releases](https://github.com/ccbahnc/Drawer/releases)。
