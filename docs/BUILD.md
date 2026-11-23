# 构建与检查

本目录是一个独立版本快照。程序源码和原始资源逐文件匹配该版基线；只有发布构建包装脚本做了路径归一化，0.1将原r1应用源码与r2安装器组合。

## 工具

- Windows x64、PowerShell 7、.NET SDK 8.0.400特性版本对应的最新补丁（global.json）；首次还原需要联网。
- Visual Studio 2022 MSBuild、WinUI C#构建组件、MSVC x64和Windows SDK；WinUI资源编译需要Visual Studio的MSBuild，不仅是dotnet build。
- Inno Setup 6.7.3；构建时显式提供本机ISCC.exe路径，不附带或依赖开发机的工具缓存。
- NuGet依赖由 packages.lock.json 固定：Windows App SDK 1.7.260224002、Windows SDK BuildTools 10.0.22621.756。沿用已验收应用依赖，本次没有升级；公开发布前仍需确认维护状态和分发许可。

在该快照根目录的PowerShell 7执行：

```powershell
dotnet run --project .\checks\Drawer.FinalRegression.csproj -c Release
.\scripts\Build.ps1 -Iscc 'C:\Path\To\Inno Setup 6\ISCC.exe'
```

脚本编译不自动安装、启动程序或修改自启。生成 staging/win-x64、output、symbols及构建清单；不要提交这些输出。必要检查使用隔离测试目录，不用真实配置做破坏性测试。

发布包装会重新生成载荷清单并编译目录授权助手，0.1不再要求另一份开发机r1目录的预编译载荷。应用源码没有改动，但新的构建时间戳会使自行编译的哈希与发布附件不同，不能冒用原附件哈希。

本次整理没有执行构建或重跑应用回归。原构建/检查结果见版本说明；这里只完成归档、字节校验、敏感数据筛查和包装脚本语法检查。归一化包装尚未在独立干净构建环境验证，不声称可复现构建已验收。

原Inno隔离环境测试里包含生成时的本机路径和SID，未作为通用测试脚本发布。核心C#回归已保留。历史通过记录不代表这些环境夹具可以直接在任意机器运行。