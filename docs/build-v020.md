# v0.2.0 构建、历史基准与产物来源

产品固定 SDK `10.0.402`，目标 `net10.0` / `net10.0-windows`，运行时 `10.0.13`。版本为 `0.2.0`；候选构建将完整源码提交加入 InformationalVersion。开发中的直接 build 使用 `0.2.0-local`，不能冒充已冻结候选。

`src/CodexUsage.Core` 的九份原始 C# 和 `frozen-core/CodexUsage.Core.dll` 均保持原字节。`historical/core-sdk9` 用原 SDK `9.0.318`、原版本属性和原 DebugType 配置链接这九份文件，独立重建历史 DLL。它与 .NET 10 产品项目没有项目引用关系。新产品 Core 不要求等于旧 DLL 的哈希。

## 前提与输出保护

Windows x64、PowerShell 7、上述两个 SDK 已可用。脚本不安装工具；新目录必须不存在且路径不能经过符号链接/目录联接。设置 `-DotNetExecutable` 和 `-LegacyDotNetExecutable` 可分别选择便携 .NET 10 与历史 .NET 9 主机。所有构建输出、CLI home、临时目录、NuGet 缓存和测试 home 均在本轮输出目录内；退出时恢复调用进程环境。每次调用 dotnet 前禁用 ASP.NET 证书自动生成。

构建脚本要求 Git 工作区干净且所有输入已提交，使用 `git archive HEAD` 导出纯源码到新目录再构建。它拒绝未跟踪文件、未提交改动、符号链接和子模块；不会暂存、重置或清理源码。结束时重新核对 HEAD 和工作区。开发阶段可直接用 dotnet 构建，但这种结果不属于此 clean-source 候选验证。

## 历史字节核验

```powershell
pwsh -NoProfile -File scripts/verify-core-build.ps1 `
  -DotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-historical-evidence-directory>
```

脚本在 `historical/core-sdk9` 的 pin 下运行，两次独立 obj/bin 构建与冻结 DLL 互比全部字节及 SHA-256。该脚本不执行 Core 程序、不读取账户日志。冻结哈希仍为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`。原始来源调查保留在 [core-source-build.md](core-source-build.md)。

## 完整候选构建

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-candidate-directory>
```

默认构建框架依赖的完整 Release 目录，不创建 ZIP。默认同时运行历史字节核验、所有 `tests/**/*.Tests.csproj` 和使用候选 DLL 的 GUI harness。`-SkipTests` 只允许开发诊断，结果显式标注测试未执行。

冻结候选后，协调者使用同一脚本的显式 `-Package` 生成自包含 win-x64 ZIP。该模式禁止 `-SkipTests`，不单文件合并、不裁剪、不 ReadyToRun。只需把两份 Microsoft 官方 runtime nupkg `Microsoft.NETCore.App.Runtime.win-x64` 和 `Microsoft.WindowsDesktop.App.Runtime.win-x64` 的 `10.0.13` 版本准备在本地 feed，传入 `-RuntimeFeed <physical-directory>`；NuGet 源仍默认清空，不静默联网还原。SDK 内的 apphost/targeting packs 提供其余框架引用。`scripts/runtime-packages.json` 固定 Microsoft catalog 的 SHA-512，脚本会实际核对 feed 包；原包 NETCore LICENSE/THIRD-PARTY-NOTICES 和 WindowsDesktop LICENSE 已逐字节保存在 `licenses/dotnet-10.0.13`，随整个 licenses 目录加入载荷。

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -RuntimeFeed <verified-local-Microsoft-runtime-feed> `
  -OutputDirectory <new-candidate-directory> -Package
```

这只创建候选；脚本不推送、不上传、不合并、不发布。普通 EXE 与持续运行验收必须针对该候选执行，不能把脚本中的 GUI harness 当作普通托盘实例验收。发布时使用已验收归档的原字节，不能无声重建替换。

## 保存的来源证据

- `build-result.json`：源码 SHA/tree、开始/结束状态、SDK/runtime、全部导出输入、实际编译源文件哈希、产物清单及错误。
- `commands/*.command.json` / stdout / stderr / binlog：实际程序、逐参数命令、起止时间和真实退出码。编译输入文件和引用路径另在 `compile-inputs` 保存。
- `historical-core/result.json`：原 SDK 历史重建，和产品新 DLL 分开报告。
- `payload/BUILD-PROVENANCE.json`：可随产品分发的相对源码清单及构建身份，不包含私人绝对路径。`payload-manifest.json` 包含每个交付文件的 SHA-256。
- 打包时另保存 ZIP SHA-256、`SHA256SUMS.txt` 和 `package-verification/result.json`。验证器检查 ZIP 文件集合、路径安全、解压后逐文件长度及哈希。它不执行解压后的程序。

`scripts/test-build-scripts.ps1 -OutputDirectory <new-test-directory>` 使用合成文件验证环境恢复、真实非零子进程退出码、输出保护、打包门槛、归档完整性及拒绝篡改/路径越界。Windows CI 执行同一构建与脚本测试；CI 的 Microsoft runtime 下载和 SHA-512 核验步骤明确列在 workflow 中。只有实际 CI 运行成功后才能宣称远端 CI 通过。CI 不证明桌面交互、所有 DPI、多屏、真实账户或八小时资源验收。
