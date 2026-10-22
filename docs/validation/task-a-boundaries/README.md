# PR #3：同日首现时间与闭合记录丢失

起点为 `25fa9745f2b74aa1f058cb5773f83951ad456518`，工作树/仓库根为 `F:\CodexData\home\worktrees\fe65\github-export-codex-daily-panel-20261021`，分支 `fix/log-coverage-loss`。开始时工作区干净，本地 HEAD、远端分支和 PR #3 head 相同；PR #1 open、未合并，目标分支仍为 `e77c71ea536890ebd12a0c9905ac893d3a37db56`。没有更新需要归属，没有重置、清理、覆盖既有成果或改动任务 B。

两项用户提出时均为静态疑点。本轮先添加合成测试和入口注册，保留完整源码和实际 JSONL 版本，再运行基线生产代码；随后分两步修复，每步分别执行。

## 三阶段结果与输入身份

| 阶段 | 输入清单 SHA-256 | 实际结果与原始记录 |
| --- | --- | --- |
| red-monitor：25fa974 生产代码 + 新增测试 | `FC021818E2290545399B3354408FF8E0A0B39C6F59FC2E73A67D772E652021CD` | CON **2 通过、4 失败**，原 COV25/EXC17/HIS7 仍通过；退出 **-532462766**。[原始日志](red-monitor.log) / [命令、退出码与哈希](red-monitor.run.json) |
| origin-monitor：只修首现采集 | `776DB2F63E2FCE30DAF16AA43DC3DA542F2A31F591AEEEB985CD892A5C38AFF4` | CON **3 通过、3 失败**，CON-02 转绿，闭合记录反例仍失败；退出 **-532462766**。[原始日志](origin-monitor.log) / [记录](origin-monitor.run.json) |
| green-monitor：两项修复后 | `0DEFE5DF5AD08038A47C78FFD608DC356A12AAFA5E94CDA6C70D57B3FE735C56` | CON **6/6**、COV **25/25**、EXC **17/17**、HIS **7/7**，完整监控回归通过；退出 **0**。[原始日志](green-monitor.log) / [记录](green-monitor.run.json) |

三次 Monitor 的新测试源码和入口源码逐字节相同；只有生产源码在阶段间变化。运行记录中的 `head` 是当时父提交 `25fa974…`，并不代表未提交的新增测试或修复已属于该父提交。每阶段实际源码由清单逐文件标识。

红灯实际出现：

- CON-02：初始 child 的同日 `11:59:30` 请求来源删除后，再加入较新 root 元数据，`12:00:09` 副本被视为首现；对 `12:00:00–12:00:10` turn 错误给出 **ProcessingSeconds=9、TPS=4.444444444444445（40/9）、Partial=false**。CON-01 保留原始首现的对照已通过：token `(100,60,40,10)` 只计一次、complete，但 TPS/processing 不可用。
- CON-03：今日 `00:01` 先读过昨日完成，再仅移除 completion，metadata/start 原字节保留，实际 **ActiveTurns=1、ActiveSeconds=60、Partial=false**。
- CON-04：昨日已完整读取，在下一日刷新前截到 metadata/start，同样错误恢复活动。本例明确同时去掉昨日 usage 与 completion，与 CON-03 的严格 completion-only 操作区分。
- CON-06：昨日 abort 丢失、start 保留，同样恢复为活动且 complete。

红灯组逐项捕获失败后统一抛出，因此所有六个新 case 都执行；抛出后的工具/超大行等后续组没有在红灯阶段继续运行。最终绿灯运行了完整 Monitor 项目。

## 修复与场景

首现 guard 原已保存完整时间戳，但收集器仅取 `first < dayStart`。现在收集所有有效、身份完整、无请求冲突且首现 `<= now` 的原始 `(Session, Request)` 组。退休时间只约束请求归属与既有 turn 区间资格，token 仍取当前可读向量；不补回旧 token 或旧耗时。

原始 start 在截断后仍然存在，因此它可以清偿启动顺序摘要；这不能证明同 turn 的 completion/abort 仍存在。新增独立闭合 guard，仅保留原始摘要、会话、turn、时间及完成/中止种类，不保留 CompletionEvent 本体、duration 或 TTFT。闭合事实独立采集并跨午夜携带。仅当当前单 start、无原始 closure、近期、原始 latest 本可 active 时，它才阻止活动，并解释“历史回合闭合依据缺失，活动状态无法确认”。历史 closure 不注入 observed、turns 或 intervals，不重建旧区间；清偿仍只看当前成功排空来源的原始事件摘要。

| 场景 | 关键断言 |
| --- | --- |
| CON-01 | 原同日首现仍在，child 改 root 后 token 只计一次；请求早于 turn start，TPS 不可用；idle 稳定 |
| CON-02 | child 阶段删除同日首现、再改 root：已知当前 token、partial、TPS/processing 不可用；恢复后 complete，TPS 仍不合资格；idle 不重复 |
| CON-03 | 今日已读闭合事实；三行 metadata/start/completion 截成原两行，仅丢 completion：不重启、不增加今日时长、partial；00:02 idle 稳定；恢复后 complete |
| CON-04 | 昨日已读完整来源；截断直到新日才观察：同样保留闭合排除；恢复及 idle 稳定；昨日原有 9 秒处理、40/9 TPS 对照保持既有公式 |
| CON-05 | 从未见闭合记录的真实 start-only：00:01 仍 active=1、60 秒，00:02 为120秒、complete；不虚构完成 TPS |
| CON-06 | abort 丢失、恢复、再次丢失与 idle；00:14 时 start 已超过15分钟，依据休眠，避免永久 partial |

## 完整源码、实际合成输入与命令

- [红灯完整新测试源码](red-monitor-source/ClosureCoverageTests.cs.txt)与[红灯完整入口源码](red-monitor-source/Program.cs.txt)在执行前复制原始字节。结合基线 `25fa974…` 可重建红灯代码；不需要在当前工作树切换起点。两个后续阶段的 `*-source/` 也保存相同源码，运行记录列出原路径、快照路径、大小和 SHA-256。
- 每阶段 `*-synthetic/` 保存新组自己写出的实际 JSONL 字节版本：红灯16份、中间17份、最终21份，分别在该阶段运行记录 `synthetic_inputs` 中列出路径、大小和 SHA-256。它们来自测试自己构造的字符串，不扫描、读取或复制真实日志目录。失败 case 在断言处结束，因此红灯缺少尚未到达的恢复版本，最终版本包含完整操作过程。
- 操作顺序、删除、刷新时刻、固定 UTC+8 时区与日志 mtime 规则均在完整测试源码中；归档文件本身的系统 mtime 不代表被测日志 mtime。被测 mtime 由每个 Fixture 的固定 `_modifiedAt` 设置。仅有某个最终 JSONL 文件不能表达完整测试过程，应结合源码重放。
- 每个 `.inputs.sha256` / `.after.inputs.sha256` 覆盖 **41 个**源码、测试、固定 DLL、项目与配置输入，逐行格式为 `SHA-256  bytes  relative-path`。每次运行前后清单完全相同；最终六次成功检查共用同一清单。文档和本证据目录不纳入构建输入，避免自引用。提交后再次验证最终提交的文件字节与此清单一致。
- `.run.json` 保存实际 argv、cwd、dotnet 可执行路径、SDK、指定环境、立即保存的退出码、输入/原始日志哈希及构建 DLL 哈希。完整[采集脚本原文](capture-run.ps1.txt)也已保存，SHA-256 与每条记录一致；它是运行记录，未接入项目构建。脚本原文末尾空行保持原样，仅对此捕获文件关闭 EOF 空行检查；证据目录按原始字节保存并识别 Windows CRLF，其余空白检查保持。

实际选中 SDK 为 **9.0.318**，可执行文件 `C:\Program Files\dotnet\dotnet.exe`，[完整 --info](dotnet-info.txt)来自实际临时 cwd。没有将 `--project` 误当成由仓库 global.json 选择 SDK。临时根为 `CodexDaily-boundaries-759ab5d847834509bb74f589077a25c5`，CLI home、TEMP/TMP、输出和本组输入归档均在其中。

以下是实际 argv 的等价 PowerShell 表示，绝对路径以各次 `.run.json` 为准；每个 name 都使用独立输出，不覆盖既有运行：

```powershell
$env:TEMP = Join-Path $taskRunRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $taskRunRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:CODEX_DAILY_BOUNDARY_INPUTS = Join-Path $taskRunRoot "$name-synthetic"
Set-Location -LiteralPath $taskRunRoot

# 三阶段分别以 red-monitor、origin-monitor、green-monitor 替换 $name。
dotnet run --project "$taskRepository/tests/CodexUsage.DailyMonitor.Tests" -c Release --artifacts-path "$taskRunRoot/$name-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.Daily.Tests" -c Release --artifacts-path "$taskRunRoot/final-core-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyFormatting.Tests" -c Release --artifacts-path "$taskRunRoot/final-formatting-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyPopup.Tests" -c Release --artifacts-path "$taskRunRoot/final-popup-artifacts" --no-launch-profile
dotnet build "$taskRepository/src/CodexUsage.DailyPanel" -c Release --artifacts-path "$taskRunRoot/final-app-artifacts"
dotnet run --project "$taskRepository/tests/CodexUsage.Daily.GuiHarness" -c Release --artifacts-path "$taskRunRoot/final-gui-artifacts" --no-launch-profile "-p:ProductDirectory=$taskRunRoot/final-app-artifacts/bin/CodexUsage.DailyPanel/release" -- "$taskRunRoot/final-gui-output"
```

## 最终全量结果与边界

| 检查 | 退出码 / 结果 | 原始证据 |
| --- | --- | --- |
| Monitor | 0；COV25、EXC17、HIS7、CON6 全部通过；原主组11、工具41、超大行20、生命周期8、legacy11项断言与44组逐字节切分全部通过 | [日志](green-monitor.log) / [运行记录](green-monitor.run.json) |
| 核心聚合/解析 | 0；116/116 | [日志](final-core.log) / [运行记录](final-core.run.json) |
| 格式 | 0；15/15 | [日志](final-formatting.log) / [运行记录](final-formatting.run.json) |
| 弹窗逻辑 | 0；26/26 | [日志](final-popup.log) / [运行记录](final-popup.run.json) |
| Release | 0；0警告、0错误 | [日志](final-app.log) / [运行记录](final-app.run.json) |
| GUI 演示合成 | 0；38/38 | [日志](final-gui.log) / [运行记录](final-gui.run.json) / [夹具 JSON](gui-result.json) |

GUI 使用源码已核查的演示快照和 demo controller，不读取真实日志；加载的应用/DailyCore 哈希由夹具记录并与本轮 Release 产物核对。固定 Core DLL 的 SHA-256 仍为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`，沿用此前明确授权。没有改冻结 DLL、Core 恢复源码、项目/构建配置、UI 或任务 B。

没有读取真实 sessions、auth、凭据或私人聊天，没有合并、打包、发布或替换运行程序。未验证真实账号、多屏、真实 ACL 变化下半行连续读取、长期运行或规模性能。覆盖事实仍只在 monitor 实例内保留，不能发现启动前已消失的日志；保留范围耗尽或未知尾部无法证明时保守 partial，原头/边界指纹仍不能保证发现任意内部编辑。
