> v0.2.0 构建分离说明：本文记录的是 2026-10-22 的 SDK9 历史来源调查与当时验证，后文“当前仓库”等措辞均指该历史基线。现产品固定 SDK10；原 SDK9 pin、版本与项目已隔离到 `historical/core-sdk9`，核验脚本仍可执行原字节重建。现行命令与产品来源要求见 [v0.2.0 构建文档](build-v020.md)。新产品 DLL 不要求与旧冻结 DLL 相同。
# Core 源码来源与可复现构建

本记录对应任务 B，调查与验证日期为 2026-10-22（UTC+8）。起点是 PR #1 的已审查提交 `e77c71ea536890ebd12a0c9905ac893d3a37db56`，任务分支为 `build/core-source`，PR 目标为 `codex/statistics-integrity`。本任务依赖 PR #1，没有修改统计完整性业务实现或业务测试。

**结论：已找回与冻结 Core 对应的完整 9 份 C# 源码，并用固定 SDK 和配置重新生成了与冻结 DLL 逐字节相同的产物。** 合成行为验证另行执行；这三个结论分别有构建、字节比较和测试证据。

## 来源与对应证据

仓库可达历史不是 shallow，但导入前的 Core 开发历史没有保留。根提交 `e8139f836359622a767e6f831cad3add14793df7` 直接加入冻结 DLL；PR #1 没有改变它。历史核验命令包括：

```powershell
git rev-parse --is-shallow-repository
git log --all --full-history --name-status
git rev-list --objects --all
git log --all --full-history --find-object=1cfae06fb1aa69f4a34e2feb85296b577f900bd4 --raw --no-abbrev
```

冻结基准是 `frozen-core/CodexUsage.Core.dll`：

| 属性 | 值 |
| --- | --- |
| Git blob | `1cfae06fb1aa69f4a34e2feb85296b577f900bd4` |
| 文件长度 | `155648` 字节 |
| SHA-256 | `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE` |
| MVID | `56cd4f19-81a0-4ba5-ab4c-7b76b3619ea7` |

此前的 [PR #1 验收记录](statistics-integrity-validation.md) 提到了参考工作区中的源码、带符号 DLL 和 PDB。本次只沿这一项目线索读取 `<reference-workspace>/src/CodexUsage.Core` 的源码、项目文件及 `bin/Release/net9.0`、`obj/Release/net9.0` 构建材料，并读取该参考项目根的构建配置。没有扫描无关私人目录。公开记录使用参数化路径，本机路径只保留在被忽略的本地证据中。

本次独立核实的证据链为：

1. 参考项目 `obj/Release/net9.0/CodexUsage.Core.dll` 与冻结 DLL 的 SHA-256、长度和全部字节相同。
2. 参考项目 `bin/Release/net9.0/CodexUsage.Core.dll` 带 CodeView 调试目录，SHA-256 为 `616B0ED6A47F8B7F05EDEEEB25D9AE1D84D781D5FA4B4595226392C7157E516A`，长度为 `156160`。它的 portable PDB 的 SHA-256 为 `6670A51CAAEAFD6FC6B0772F5AD5EC14168F7641E7700EBA3E62009A3968C887`。PDB GUID `1183fcba-6a25-40b8-89c0-8ff0ee7a8c4a`、stamp `3720499661` 和 age `1` 与该 bin DLL 对应；PDB 内容校验也与 DLL 的 PdbChecksum 条目相符。
3. PDB 的全部 12 个 Document 校验通过：下表 9 份原始源码，加上 SDK 生成的 GlobalUsings、TargetFramework 属性和 AssemblyInfo 共 3 份文件。没有只抽查公开 API 或三个已知文件。
4. 冻结 DLL 与带符号 bin DLL 的 961 个 MethodDef 中，956 个带方法体项的原始方法体字节全部相同；其余 5 项没有方法体。静态核验还确认：完整元数据在排除 MVID 和唯一 FieldRVA 地址差异后相同，该 FieldRVA 的 6 字节初始化数据相同，原生资源相同，均无托管资源。带符号版本与冻结版本本身的文件字节不同，不能仅用该静态比较声称构建可复现。
5. 将这 9 份原始源码、原项目及配置复制到本工作树的独立探测目录，使用 SDK `9.0.318`、Release、`DebugType=None`、`DebugSymbols=false` 构建，成功得到冻结 DLL 的全部相同字节。导入仓库后，再由核验脚本在两个全新输出路径独立构建并逐字节比较。此项才直接建立本次仓库源码到冻结产物的对应关系。

静态助手只使用 `PEReader`、`System.Reflection.Metadata` 和文件字节读取，未通过 `Assembly.Load` 或入口调用执行待核查 DLL。它属于本地调查工具，源码、完整报告与运行日志留在被忽略的 `artifacts/core-source-audit/`，没有把含本机绝对路径的 PDB 或报告加入公开仓库。读者无需这些本地材料，也能用下文的仓库源码重建并与 Git 中的冻结基准直接比较。

## 恢复文件与配置

以下文件从参考项目原样复制到 `src/CodexUsage.Core/`，没有重写、反编译、删减或格式化。`.gitattributes` 对这组 C# 文件禁用 Git 换行转换，以保留原始字节和 PDB 校验关系。

| 源文件 | SHA-256（与参考 PDB Document 相同） |
| --- | --- |
| `AccountQuota.cs` | `5873058E13D5278821F7DD56F899AEF76B490818EC065DC02E730098A024DEB3` |
| `CliThreadUsage.cs` | `0D15D0C5547B74BB601B385E5B5F9FFCCEDD6DC29AB4570A39E7656F8F304E2B` |
| `EventParser.cs` | `995EA9D566207F09E9EBC272834CF54FF43C94713263F2EC98D3B90FEDB62CB4` |
| `IncrementalLogReader.cs` | `F19CB1AA27A3DE704522F6EB12C23E34C35F1AC0D2C641456FA67D600E465CE7` |
| `LocalStateStore.cs` | `0F42A7498C7FE2E677C8E20E4A564E724084CFCCACC18DBA6B833E55C8BBBE37` |
| `LogMonitor.cs` | `99A41035A630C031902340070C263DD7AC1E27DF10152A5C81BF7882FF07DD0F` |
| `Models.cs` | `6080852CA21CF6B9FCC1A97064E374BA9ED5800AB01CFB45ED4F22AB6AB45550` |
| `OfficialAccountRpc.cs` | `A8F410AAC9DB0DD3782696646BAE781B6AA3A074AE063B674FB713D4A806C91D` |
| `UsageEngine.cs` | `DE2DF83A3D3A3F1CB091A663252F6922B624405C40784ECE47C104AC827C7A91` |

完整恢复保留了原 DLL 中的本地状态和 CLI 账户等类型；本次没有启用这些路径、调用账户接口或启动 Codex CLI。删去未被 Daily 入口使用的类型会改变冻结产物，故没有为缩小文件集而删改它们。

原始 `CodexUsage.Core.csproj` 的 SHA-256 为 `908885A1D5DB4B160CD71456359B974FBEB5430CD5070D16587BF59E07EADA05`，内容仅为 `Microsoft.NET.Sdk` 和 `TargetFramework=net9.0`。导入后仅为 Release 添加 `DebugType=None`、`DebugSymbols=false`，从而将已验证的命令行参数固化在项目中。参考根 `Directory.Build.props` 与当前仓库已有文件逐字节相同，SHA-256 为 `CCF7575C58D76743468B9AE7460D85D911BF96018FF75437AF0995495EBF8E03`；该文件未修改。

| 输入 | 固定或核实的配置 |
| --- | --- |
| SDK | `global.json` 固定 `9.0.318`，`rollForward=disable`、`allowPrerelease=false` |
| 目标框架 | Core 为 `net9.0`；面板为 `net9.0-windows` |
| 编译器 | SDK 所带 Roslyn；参考 PDB 记录 `4.14.0-3.26064.1+450493a9b4ec6337bced0120e97cb76f4ed783db`，C# `13.0` |
| Release | 优化开启，AnyCPU，`DebugType=None`、`DebugSymbols=false` |
| 通用属性 | Nullable、ImplicitUsings、warnings-as-errors、analyzers、Deterministic 开启 |
| 版本属性 | `0.1.0`，InformationalVersion `0.1.0-local.20261011`，不附加 Git revision |
| 依赖 | Core 没有 PackageReference、项目依赖、外部生成器或额外资源；只依赖 SDK 的 .NET 框架引用 |
| targeting packs | SDK `9.0.318` 对应 `Microsoft.NETCore.App.Ref 9.0.20`；构建 WPF 面板另需 `Microsoft.WindowsDesktop.App.Ref 9.0.20` |
| NuGet | 显式使用仓库 `NuGet.Config`，清空包源；没有外部包需要锁文件或联网还原 |

原冻结构建的终端日志没有保留，不能声称已找回当时的确切命令。关闭调试信息是本次实测可生成相同 DLL 的配置；不能把这一结果改写成对原始操作历史的证明。源码及工具链对应关系由 PDB、项目材料和本次实际精确重建共同支持。

DailyCore、DailyPanel 及两个直接使用 Core 的合成测试项目已从固定 DLL 引用切换为 `ProjectReference`。GUI harness 继续引用其指定产品目录的 DLL，以便保持“检查构建后产品”的既有用途。冻结 DLL 保留不变，仅作为历史字节核对基准。

## 构建步骤

在 Windows x64、PowerShell 7、已安装 .NET SDK `9.0.318` 及上述 targeting packs 的环境中，从仓库根运行：

```powershell
pwsh -NoProfile -File scripts/verify-core-build.ps1 -OutputDirectory artifacts/Core-rebuild-local
```

`OutputDirectory` 必须不存在；省略时自动生成唯一目录。脚本不安装工具，不删除或覆盖已有输出，不执行 Core、面板或测试。它先核验 SDK 和冻结基准哈希，再执行两次独立构建，实际参数形式为：

```powershell
dotnet build src/CodexUsage.Core/CodexUsage.Core.csproj --configuration Release --artifacts-path <output>/first --configfile NuGet.Config --nologo --verbosity minimal
dotnet build src/CodexUsage.Core/CodexUsage.Core.csproj --configuration Release --artifacts-path <output>/second --configfile NuGet.Config --nologo --verbosity minimal
```

每次均使用全新的 obj/bin 目录，不复用参考项目的 DLL。输出位于 `<output>/<first|second>/bin/CodexUsage.Core/release/CodexUsage.Core.dll`。脚本分别将两次产物与冻结文件逐字节比较，也比较两次产物，并要求 SHA-256 都等于固定值。任何失败都终止，不用新 DLL 覆盖冻结文件。

脚本将 CLI home 隔离到 `<output>/.buildhome`，设置 `DOTNET_CLI_TELEMETRY_OPTOUT=1`、`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`、`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，退出时恢复原进程环境和目录。`result.json` 记录实际输入哈希、SDK/OS/架构、构建参数、退出码、产物哈希、长度与三组字节比较结果；构建和 `dotnet --info` 的原始输出分别保存。失败时也保留已取得的证据。

若只需构建面板，可运行：

```powershell
dotnet build src/CodexUsage.DailyPanel -c Release
```

完整发布脚本 `scripts/build-daily-panel.ps1` 也已接入 Core 核验，在合成测试和 publish 之前运行。本次没有运行完整发布脚本。

## 本次实际验证

环境：Windows x64 `10.0.26220`、PowerShell `7.6.5`、SDK `9.0.318`（commit `415ddb56e6`）、MSBuild `17.14.51+25f168cee`、.NET `9.0.20`。未安装或更新 SDK、包或工具。

| 验证层级 | 实际执行及结果 |
| --- | --- |
| 候选源码构建 | 从参考项目复制到独立探测目录后编译，退出 0，0 warnings、0 errors |
| 候选源码到冻结 DLL | 探测构建产物 SHA-256 为固定值，与冻结基准匹配 |
| 仓库 Core 独立重建 | `pwsh -NoProfile -File scripts/verify-core-build.ps1`，两次构建均退出 0、0 warnings、0 errors |
| 产物字节一致性 | 两份 DLL 均为 155648 字节；两份产物互比以及各自与冻结文件比较均完全相同，SHA-256 为固定值 |
| 输出保护 | 重用已存在输出目录会被立即拒绝；既有证据文件哈希未改变 |
| 干净文件树重建 | 对待提交 Git tree 执行 `git archive` 后解压到独立目录，在无 `.git`、无 bin/obj 残留的副本运行同一核验脚本；两次构建与冻结基准的三组字节比较全部通过 |
| 源码字节保留 | 9 份参考原文件、工作树文件及 Git 导出文件的 SHA-256 逐一相同 |
| 失败保护 | 在独立合成副本中将基准设为单字节无效文件，脚本在任何 Core 构建前拒绝，记录 failed；环境变量与当前目录均恢复 |
| 聚合与 parser 合成回归 | `116/116`，退出 0 |
| Monitor 合成回归 | 91 项断言，以及 44 组 JSON/UTF-8 输入逐字节切分验证通过，退出 0 |
| 格式合成回归 | `15/15`，退出 0 |
| 弹窗几何/交互逻辑回归 | `26/26`，退出 0 |
| WPF 应用构建 | Release build 退出 0，0 warnings、0 errors；没有启动 GUI |

四组测试和应用构建分别使用 `artifacts/core-source-validation/<name>` 下的独立构建目录。上述四个 CLI 环境变量均已设置，测试的 `TEMP`、`TMP` 也设为各自 `<run>/temp`，以便合成文件只写入任务证据目录。实际命令如下，`<run>` 依次为 aggregation、monitor、formatting、popup、app 对应的目录：

```powershell
dotnet run --project tests/CodexUsage.Daily.Tests -c Release --artifacts-path <run>/output --no-launch-profile
dotnet run --project tests/CodexUsage.DailyMonitor.Tests -c Release --artifacts-path <run>/output --no-launch-profile
dotnet run --project tests/CodexUsage.DailyFormatting.Tests -c Release --artifacts-path <run>/output --no-launch-profile
dotnet run --project tests/CodexUsage.DailyPopup.Tests -c Release --artifacts-path <run>/output --no-launch-profile
dotnet build src/CodexUsage.DailyPanel -c Release --artifacts-path <run>/output --nologo -v:minimal
```

这些测试在源码身份和执行路径核验后才运行，只使用原有合成输入，没有修改测试用例或断言。应用、聚合测试和 monitor 测试实际构建出的 Core DLL 也重新核对为相同 SHA-256。Monitor 最后一行只输出主程序的 11 项计数；91 是主程序、工具时序、超大行、生命周期及 legacy 分组的断言总数，44 组切分验证另计。

失败保护的首次检查发现，PowerShell 7.6.5 将通过 .NET API 传入的空值环境变量恢复成空字符串，未保留原先“不存在”的状态。脚本已改为对原本缺失的变量使用 `Env:` provider 删除，并复验原值缺失、为空和非空三种情形；正常与失败路径均通过。首次检查失败和修复后的证据均保留在本地，没有把初次结果写成通过。

干净文件树验证实际执行 `git write-tree`、`git archive --format=zip --output=<archive> <tree>`、`Expand-Archive`，再调用导出副本中的 `scripts/verify-core-build.ps1`。修复后的最终结果保存在本地 `artifacts/core-clean-checkout-final/verification/result.json`，初次结果保留在 `artifacts/core-clean-checkout/verification/result.json`。这是本地源码快照验证，没有创建额外工作树或发布归档。

原始 C# 文件包含混合 LF/CRLF 和末尾空行。为保留 PDB 哈希，没有格式化它们；默认 `git diff --cached --check` 会将原样 CR 和空行报告为 whitespace。实际范围检查分两步执行，均退出 0：

```powershell
git diff --cached --check -- . ':(exclude)src/CodexUsage.Core/*.cs'
git -c core.whitespace=cr-at-eol,-blank-at-eof diff --cached --check -- 'src/CodexUsage.Core/*.cs'
```

第二条只调整本次命令对 CR 和文件尾空行的识别，不修改 Git 配置或文件；真实源码字节另由上述 9 份哈希核验约束。

## 结论边界与变更范围

- **已证明源码能够构建：** 仓库内完整 Core 源码在上述固定环境的 Release 构建成功。
- **已证明指定产物字节一致：** 本次重建的 Core DLL 与从根提交保留的冻结文件相同，不只是 API 或 IL 相似，也没有替换冻结基准来获得相等结果。
- **已证明合成行为回归通过：** 既有 Daily 合成测试通过；测试不能替代源码对应证据或字节比较。
- **未证明的事项：** 导入独立仓库前的源码提交历史、原始构建终端命令、作者身份的外部认证；其他 OS/SDK/编译配置；Debug/PDB、整个 WPF 应用或发布 ZIP 的字节可复现性；真实用户日志及实际 WPF 界面行为。
- **未执行：** 真实 sessions/archived_sessions、auth、凭据或私人聊天读取；账户查询、Codex CLI 启动；真实 GUI 验收；publish、发布脚本、版本发布或 PR 合并。
- **范围：** 原样恢复 Core 源码、构建项目/引用、SDK 固定、核验脚本和构建文档。`SessionLogMonitor.cs`、`DailyAggregator.cs`、事件解析源码、Token/TPS/额度/覆盖语义及业务测试未作逻辑修改。原始 Core 解析实现按哈希原样恢复。
