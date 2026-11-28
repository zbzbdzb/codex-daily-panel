# v0.2.0 构建、历史基准与产物来源

产品构建固定 SDK `10.0.402`，目标 `net10.0` / `net10.0-windows`。构建脚本检查所选 SDK 安装中存在 .NETCore 与 WindowsDesktop `10.0.13`；自包含模式将运行时固定为 `10.0.13`。框架依赖构建的 runtimeconfig 声明 `frameworks` 为 `10.0.0`，不应称其已携带10.0.13。版本为 `0.2.0`；候选构建将完整源码提交加入 InformationalVersion。开发中的直接 build 使用 `0.2.0-local`，不能冒充已冻结候选。

`src/CodexUsage.Core` 的九份原始 C# 和 `frozen-core/CodexUsage.Core.dll` 均保持原字节。`historical/core-sdk9` 用原 SDK `9.0.318`、原版本属性和原 DebugType 配置链接这九份文件，独立重建历史 DLL。它与 .NET 10 产品项目没有项目引用关系。新产品 Core 不要求等于旧 DLL 的哈希。

## 前提与输出保护

Windows x64、PowerShell 7、上述两个 SDK 已可用。脚本不安装工具；新目录必须不存在且路径不能经过符号链接/目录联接。设置 `-DotNetExecutable` 和 `-LegacyDotNetExecutable` 可分别选择便携 .NET 10 与历史 .NET 9 主机。脚本将其构建输出、CLI home、临时目录、NuGet 缓存及显式测试目录放在本轮输出目录内，并在退出时恢复所更改的调用进程环境。环境变量重定向不等于操作系统沙箱，也不保证所有 Windows Known Folder API 都重定向；普通 EXE 验收仍须显式设置合成 home/settings 及物理路径检查。每次调用 dotnet 前禁用 ASP.NET 证书自动生成。

构建脚本要求 Git 工作区干净且所有输入已提交，使用 `git archive HEAD` 导出纯源码到新目录再构建。它拒绝未跟踪文件、未提交改动、符号链接和子模块；不会暂存、重置或清理源码。结束时重新核对 HEAD 和工作区。开发阶段可直接用 dotnet 构建，但这种结果不属于此 clean-source 候选验证。

## 历史字节核验

```powershell
pwsh -NoProfile -File scripts/verify-core-build.ps1 `
  -DotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-historical-evidence-directory>
```

脚本在 `historical/core-sdk9` 的 pin 下运行，两次独立 obj/bin 构建与冻结 DLL 互比全部字节及 SHA-256。该脚本不执行 Core 程序、不读取账户日志。冻结哈希仍为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`。原始来源调查保留在历史文档中；本次公开摘要不复制其中可能包含的私人路径或原始调查附件。

## 完整候选构建

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-candidate-directory>
```

默认构建框架依赖的完整 Release 目录，不创建 ZIP。默认同时运行历史字节核验、所有 `tests/**/*.Tests.csproj` 和使用候选 DLL 的 GUI harness。`-SkipTests` 只允许开发诊断，结果显式标注测试未执行。

先将产品代码、使用说明、第三方声明及构建输入纳入同一干净源码提交，再使用同一脚本的显式 `-Package` 生成自包含 win-x64 ZIP。该模式禁止 `-SkipTests`，不单文件合并、不裁剪、不 ReadyToRun。本地 runtime feed 通过 `-RuntimeFeed <physical-directory>` 指定；精确包名、版本及 Microsoft catalog SHA-512 pin 以该提交的 `scripts/runtime-packages.json` 为准，脚本会核对 feed 包。NuGet 源默认清空，不静默联网还原；清单和 feed 仍须满足实际 publish 的全部恢复需求，不能仅凭 pin 校验或 SDK 内 apphost/targeting packs 宣称恢复成功。原包 NETCore LICENSE/THIRD-PARTY-NOTICES 和 WindowsDesktop LICENSE 保存在 `licenses/dotnet-10.0.13`，随整个 licenses 目录加入载荷；若依赖或交付成员变化，应同步核对相应来源及许可。

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -RuntimeFeed <verified-local-Microsoft-runtime-feed> `
  -OutputDirectory <new-candidate-directory> -Package
```

这只创建候选；脚本不推送、不上传、不合并、不发布。普通 EXE 与持续运行验收必须针对该候选执行，不能把脚本中的 GUI harness 当作普通托盘实例验收。发布时使用已验收归档的原字节，不能无声重建替换。

产品 publish 显式设置 `DisableTransitiveFrameworkReferenceDownloads=true`，避免 SDK 为未引用的框架预备下载 runtime pack；该属性传给产品的项目引用。产品仍需恢复其真实声明的 NETCore 与 WindowsDesktop 框架及运行时包，不关闭正常 runtime pack 下载，也不增加 ASP.NET 产品引用。核对恢复闭包时分别检查三项目 `project.assets.json` 的 `frameworkReferences` 和 `downloadDependencies`，并结合 runtimeconfig、deps 及实际载荷，不能把一次 restore 成功单独解释为依赖完整。

脚本在 publish 返回后立即复制三项目的实际 assets 到载荷外 `publish-assets/` 并核对框架和固定运行时下载集合，写入 `build-result.json` 的 `publishRestoreInputs`。后续测试项目恢复可能覆盖共享 artifacts 目录中的 assets，不能拿覆盖后的文件代替实际发布阶段的恢复证据。

SDK10 产品及测试编译使用 `<本轮输出根>=/_/codexdaily` 的稳定 `PathMap`，同时覆盖导出的源文件、生成源码及托管 CodeView PDB 路径。SDK9 历史重建调用不传此属性。`scripts/verify-artifact-privacy.ps1` 在载荷文档和公开 provenance 生成后、清单和 ZIP 封存前读取实际 Portable PDB 文档及产品托管 DLL CodeView，要求它们使用映射路径；同时检查公开文本中的当前本机源码/输出根及 Windows 用户目录。Microsoft 原生 apphost 已知的上游构建路径单独记账，不能当作本机路径被消除的证据。报告保存在载荷外的 `artifact-privacy.json`；检查失败会阻止封包。此门禁针对上述可检查路径范围，并非对任意内容隐私的无条件保证。

完整构建还实际运行 `scripts/test-artifact-privacy.ps1`：编译带原始路径和映射路径的合成 DLL/PDB，确认门禁拒绝原始文档/CodeView、即使省略 PDB 也拒绝泄露的 DLL，并拒绝公开 JSON/说明中的合成私人路径、损坏 PDB 和缺失程序集。该脚本验证门禁行为，不运行产品或读取会话。

单实例激活使用运行时自带的管道 ACL API：端点归当前 User SID 所有，使用受保护、不继承的权限表，仅向该 SID 允许必要读写/实例权限，并明确拒绝 NETWORK 身份。客户端连接本机 `.`，在发送固定激活消息前核对实际端点 owner。Settings/IPC 测试会读回自建管道 ACL，并验证双 worker、超时、异常回执、取消和释放；构造 SID 的匹配测试不等于真实跨用户、跨机或混合提升环境验收。

## 保存的来源证据

- `build-result.json`：源码 SHA/tree、开始/结束状态、SDK/runtime、全部导出输入、实际编译源文件哈希、产物清单及错误。
- `commands/*.command.json` / stdout / stderr / binlog：实际程序、逐参数命令、起止时间和真实退出码。编译输入文件和引用路径另在 `compile-inputs` 保存。
- `historical-core/result.json`：原 SDK 历史重建，和产品新 DLL 分开报告。
- `payload/BUILD-PROVENANCE.json`：可随产品分发的相对源码清单及构建身份，不包含私人绝对路径。`payload-manifest.json` 包含每个交付文件的 SHA-256。
- 打包时另保存 ZIP SHA-256、`SHA256SUMS.txt` 和 `package-verification/result.json`。验证器检查 ZIP 文件集合、路径安全、解压后逐文件长度及哈希。它不执行解压后的程序。
- `artifact-privacy.json`、`commands/artifact-privacy.*` 和 `privacy-selftests/`：实际产物路径检查、失败原因及编译合成样本的正负验证。原始证据含真实本机构建路径，保持在载荷外。

`scripts/test-build-scripts.ps1 -OutputDirectory <new-test-directory>` 使用合成文件验证环境恢复、真实非零子进程退出码、输出保护、打包门槛、归档完整性及拒绝篡改/路径越界。仓库 Windows CI 的构建与脚本测试流程需按实际触发记录核对；CI 的 Microsoft runtime 下载和 SHA-512 核验步骤明确列在 workflow 中。当前workflow声明push、pull_request和workflow_dispatch，未单独声明release事件；只有对应提交的实际 CI 运行成功后才能宣称远端 CI 通过。CI 不证明桌面交互、所有 DPI、多屏、真实账户或八小时资源验收。

## 冻结顺序与最终结果查询

使用说明由 `docs/daily-panel-usage.md` 原样复制为 `payload/使用说明.md`。先纳入定稿和第三方声明，形成干净源码提交并记录 HEAD/tree；再构建完整交付，保存来源、成员清单与归档哈希。普通EXE及8小时验收均针对这份已经冻结的最终候选字节执行，不在观察结束后为补记进度改变构建source tree、payload、清单或ZIP。

构建成功、合成验证器成功和归档内容一致性检查分别有各自范围，不能代替普通EXE或持续观察。若受测后确需改变源码或交付文件，应建立新的候选身份并重新评估、执行所需验收；原8小时记录只证明原受测字节在已记录条件下的观察，不能换名作为新包的记录。

按 `BUILD-PROVENANCE.json` 与 `--version` 找到准确提交，再到本项目 GitHub Releases 对应版本及其关联 PR 查询最终验收摘要、CI/checks、合并/tag、公开下载复核与附件。源码中的[3a16 历史候选记录](v020-validation-status.md)固定保留2026-10-23该候选的构建结果、失败、限制及当时待办，不作为后续版本的动态状态页。新的验收摘要由 PR / Release 文本及附件承载，避免封包后仅为更新进度改源码或重包。

环境范围和门槛以对应版本的验收计划、实际记录为准；合成渲染比例不能当成物理DPI或多屏环境已验证。原始日志、失败和受限事项应保留，未观测时段或未执行项目不补签。

历史SDK曾自动生成并安装ASP.NET Core HTTPS开发证书，没有撤销或清理证据。后续 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` 不表示历史副作用未发生，也不表示已经清理。

来源记录使用相对路径只说明该文件的内容范围。交付隐私检查还应覆盖PDB文档名、DLL CodeView及其他实际成员，区分本机私人构建路径和Microsoft apphost自带的上游路径；不能用干净的provenance JSON代替整个payload检查。具体失败与修复结果必须绑定准确候选，最终以对应 PR / Release 记录为准。
