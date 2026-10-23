# 统计完整性修复与验收记录

本阶段只修复混合 modern 与 legacy 日志的完整性判断。已确认的 modern 请求仍提供已知 token 小计；相关 legacy 快照没有足够覆盖证据时，`IsPartial` 必须为 `true`，并提示“旧格式快照覆盖关系未知，无法还原全部今日用量”。legacy 快照始终不参与 token 累加。

本记录使用合成输入说明行为和验证范围。最终核心、读取链路、格式与弹窗回归均通过，应用构建成功；最终执行失败数为 0。修复前的预期红灯单独记录，不代表最终代码仍有失败。合成测试结果不代表真实用户日志或 GUI 已验收。

以下 HEAD、工作区和未提交状态均记录阶段验收时点。随后用户追加授权上传 GitHub；上传只包含本阶段源码、合成测试和文档，不包含本地证据产物，也不执行发布。公开文档对本机绝对路径作参数化处理，原始命令路径和验收记录保留在本地专用运行目录。

## 基线和工作区保护

| 项目 | 核实结果 |
| --- | --- |
| 远程仓库 | `https://github.com/zbzbdzb/codex-daily-panel` |
| 已审阅基线 | `e8139f836359622a767e6f831cad3add14793df7`，r5 |
| 原发布分支 | `codex/initial-release` |
| 本地修复分支 | `codex/statistics-integrity` |
| 修复开始时独立仓库状态 | HEAD 与基线一致，工作树干净 |
| 修复过程中的 HEAD | 仍为基线；本阶段修改尚未提交 |
| 实际工作目录 | `<repository>`，本项目独立仓库根目录 |
| 上层目录 | `<reference-workspace>` 是另一个尚无提交的仓库；本阶段不整体暂存、重置或替换其中已有工作 |

截至阶段验收时未执行 commit、push、发布、打包或运行中程序替换。未变更 TPS 公式、工具等待算法、最近 15 分钟活动判断、额度来源、采集容量上限或 UI 布局。

## 调用链和原问题

1. `DailyController.RefreshAsync` 调用 `SessionLogMonitor.RefreshAsync(now)`；生产入口使用当前 UTC 时间，合成测试显式传入固定时间。
2. `SessionLogMonitor.Discover` 枚举指定 home 的 `sessions`、`archived_sessions`，以文件修改时间选择来源；每个来源使用独立 `SourceContext`。文件修改时间只决定是否选中文件，不决定事件归属日期。
3. `DailyLogReader.ReadAvailableAsync` 以只读方式读取完整 JSONL 行，报告半行、非法 UTF-8、超大行、不可读取和重置等状态。`Source.AddLine` 调用冻结 Core 的 `EventParser.Parse`，同时保留现有工具时序投影。modern 请求和生命周期的有界历史用于修订与跨日归属；legacy 事件按指定时区的当日过滤。
4. `Source.SummaryEvents` 输出可用事件。来源读取、截断、解析和容量等问题通过现有 partial 标志和不可用时序证据传给 `DailyAggregator.Summarize`。
5. `DailyAggregator` 先独立投影根会话和子会话元数据，再按现有 `RequestKey` 规则去重、检查修订冲突、选择最终计数，以第一次有效发生时间确定 modern 请求日期。今日 token 小计仍只来自该组 modern 请求。
6. 本阶段在这组已接受请求上建立 legacy 累计快照覆盖索引；相关 legacy 未被证明覆盖时追加完整性原因，不为其补算 token。
7. `DailySnapshot` 将已知字段、`IsPartial` 和 `Status` 传回 monitor。monitor 原先可能以来源格式提示覆盖聚合原因；本阶段仅在两者不同的时候保留两段状态。
8. 现有 `DailyWindow.Update` 根据 `IsPartial` 展示 Partial 和状态说明；托盘现有逻辑为小计加“部分”；`DailyFormatting.CopyText` 保留已知数字、状态原因和 `(partial data)`。这些显示逻辑无需调整布局。

基线用 `dayRequests.Length == 0` 决定是否提示旧格式不可还原。同一天 A 根会话有 modern 请求、B 根会话只有 legacy 时，A 使该条件为假，B 未计入用量却可能没有任何完整性告警。最小合成反例先证实此行为，再做修复；没有按旧行号机械修改。

## 冻结 DLL 和字段契约依据

项目引用 `frozen-core/CodexUsage.Core.dll`，目标为 .NET 9。运行测试前已检查项目文件、构建脚本和离线依赖配置：`NuGet.Config` 清空包源，没有外部 NuGet 包；本机现有 SDK 为 `9.0.318`，未安装 SDK 或其他工具。发布脚本会执行 publish、GUI 和 ZIP，因此本阶段不运行该脚本。

冻结 DLL 与基线 Git blob 一致：

```text
Git blob: 1cfae06fb1aa69f4a34e2feb85296b577f900bd4
SHA-256: 6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE
```

工作区内找到对应定义和投影源码，以及带 portable PDB 的本地 Core DLL。静态核查使用 `PEReader` 与 `System.Reflection.Metadata` 读取文件字节，没有加载或执行待核查 DLL：

- 冻结 DLL 与带符号本地 DLL 的 956 个方法体，在 token、类型和方法名、IL 哈希及长度、局部变量签名和异常区域上全部匹配。
- 相关定义、引用映射及字符串与 blob 堆匹配；差异为 MVID 和一个重定位的 FieldRva。
- PDB 标识与本地带符号 DLL 对应，下列源码的实际 SHA-256 与 PDB 文档哈希一致。

| 匹配源码 | SHA-256 |
| --- | --- |
| `<reference-workspace>/src/CodexUsage.Core/Models.cs` | `6080852CA21CF6B9FCC1A97064E374BA9ED5800AB01CFB45ED4F22AB6AB45550` |
| `<reference-workspace>/src/CodexUsage.Core/EventParser.cs` | `995EA9D566207F09E9EBC272834CF54FF43C94713263F2EC98D3B90FEDB62CB4` |
| `<reference-workspace>/src/CodexUsage.Core/UsageEngine.cs` | `DE2DF83A3D3A3F1CB091A663252F6922B624405C40784ECE47C104AC827C7A91` |

这组证据支持本次所用的事件定义和解析投影；不宣称已重建冻结 Core 的完整原始构建来源。合成 parser 契约测试另行执行冻结 DLL，静态核查结果不能替代该动态测试。

实际 legacy 投影为：

| `UsageEvent` 字段 | legacy 来源或值 |
| --- | --- |
| `Legacy` | `true` |
| `Usage` | `payload.info.last_token_usage` |
| `ThreadSnapshot` | `payload.info.total_token_usage` |
| `TurnSnapshot` | `null` |
| `RequestKey` | `null` |
| `RootTurnKey` | `null` |
| `SessionKey`、`TurnKey` | 可变 `SourceContext` 的当前上下文 |
| `Timestamp`、`Ordinal` | 外层事件时间与可选顺序字段 |

因此，上下文回合只限制覆盖范围，不提供 legacy 原生请求身份。测试用真实 `EventParser` 投影合成原生 JSON，不给 legacy 自创 `RequestKey`、`RootTurnKey` 或 `TurnSnapshot`。

参考工作区的 `docs/metrics.md` 和 `docs/format-audit.md` 的相关契约允许旧 `total_token_usage` 与 modern `thread_token_usage` 做累计对账，同时明确禁止用相同向量证明请求身份。本阶段只引用这些文档已记录的字段契约，未重新打开其真实样本。这些匹配源码和参考文档并非本独立仓库所附的完整 Core 重建源码。

静态证据文件只在专用临时目录保留，未加入仓库：`legacy-static-projection-audit.ps1`、`legacy-static-projection-audit.json`、`preflight.json`。

## 覆盖判定与最小修改

legacy 没有可靠请求身份，不能证明它与某个 modern 请求是同一个请求。本阶段只证明一个更窄的事实：某个相关 legacy 累计快照所代表的聚合用量已由完整 modern 历史前缀解释。

覆盖成立需要同时满足：

1. 事件属于已确认根会话的今日 legacy；昨日 legacy 和已确认子会话 legacy 不影响今日根会话完整性。
2. legacy 的上下文回合已知，最近向量和线程累计向量都完整、有效，最近向量各分量不超过累计向量。
3. 同一会话、同一上下文回合存在 modern 线程累计检查点，完整向量与 legacy 累计快照一致。
4. 检查点位于 legacy 之前或有明确顺序证明其不晚于 legacy。相同时间但缺少足够 ordinal 证据、时间与 ordinal 逆序等情形均不能猜测先后。
5. 按现有请求身份去重后的完整 modern 历史前缀，恰好对账该检查点的全部 token 分量；不能只看同日总数、同会话出现过 modern 或某个向量相等。
6. 后续有意义修订不能把当前 modern 数值偷偷回填到更早的 legacy 检查点。精确重放不推进证据；计数、回合或线程快照的实际变化单独推进覆盖证据。首版和所有有意义的中间修订都保留顺序证据，前缀内已知时间/序号倒置、或前缀最大已知序号与 legacy 边界矛盾时保持未知；后续缺失 ordinal 不能清除这些约束。原 TPS 所使用的证据保持原规则。

缺少历史、回合、完整字段、可判定顺序或稳定修订证据时，覆盖为未知，保留 modern 小计并标记不完整。累计前缀和快照只用于核对，不用累计差、最大值、最后快照或其他估计填补今日 token。

实现按会话排序并扫描 modern 请求及其有意义修订，构建检查点索引，避免为每条 legacy 重新扫描全部请求。索引保留必要的不同顺序证据和前缀最大已知序号。同位置批次的每个请求提供自己的回合和快照。缺失 ordinal 可以在时间关系明确且无已知矛盾时用于覆盖证明，但不能解除已有矛盾；LEG-30 至 LEG-36 约束这些边界。

| 修改文件 | 本阶段职责 |
| --- | --- |
| `src/CodexUsage.DailyCore/DailyAggregator.cs` | 独立保存覆盖证据；按可证明 modern 前缀检查相关 legacy；追加未知覆盖原因 |
| `src/CodexUsage.DailyPanel/SessionLogMonitor.cs` | 来源提示与聚合提示不同时保留两段状态，避免隐藏 legacy 原因 |
| `tests/CodexUsage.Daily.Tests/Program.cs` | 接入独立完整性用例 |
| `tests/CodexUsage.Daily.Tests/LegacyIntegrityTests.cs` | 最小反例、覆盖规则和边界回归 |
| `tests/CodexUsage.DailyMonitor.Tests/Program.cs` | 接入合成读取链路用例 |
| `tests/CodexUsage.DailyMonitor.Tests/LegacyIntegrityMonitorTests.cs` | JSONL 到 snapshot 的端到端合成验证 |
| `tests/CodexUsage.DailyFormatting.Tests/Program.cs` | 已知部分小计与原因的复制格式契约 |
| `docs/daily-panel-usage.md` | 解释已知小计、未知覆盖及 Partial，并链接本验收记录 |
| `docs/statistics-integrity-validation.md` | 本阶段契约、验收矩阵和运行边界 |

使用说明只补充本次完整性含义，不改变统计或操作功能。

## 合成核心验收矩阵

所有数据均为合成。LEG-01 使用 `2026-10-21T04:00:00Z`，其余聚合用例以 `2026-10-21T02:00:00Z` 为基准；测试日期为 `2026-10-21`，时区为显式创建的固定 UTC+8，不依赖本机默认时区。没有真实会话、聊天正文或认证夹具。

向量记号按 **输入、缓存输入、输出、推理输出；输入加输出** 排列：

- A = **(100, 60, 40, 10; 140)**。
- B = **(200, 90, 80, 20; 280)**。
- AB = **(300, 150, 120, 30; 420)**，是明确写出的预期数值。
- Ø = **(未知, 未知, 未知, 未知; 未知)**，不是零。
- Z = **(0, 0, 0, 0; 0)**，用于真实契约允许的零用量 modern 请求。

`DailySnapshot` 保存四个 token 字段，总 token 是输入加输出；缓存与推理是子集，不额外相加。下面的预期是合成输入的验收要求；最终执行状态见后面的运行结果表。

| 用例 | 合成输入 | 预期今日 token | 预期 `IsPartial` | 预期原因或覆盖依据 |
| --- | --- | --- | --- | --- |
| LEG-01 | A 根会话 modern A；独立 B 根会话仅 legacy `(900,500,100,20;1000)` | A | true | B 无现代覆盖证据；包含“旧格式”原因 |
| LEG-02 | 单个有效 modern A | A | false | 纯 modern 保持原结果 |
| LEG-03 | LEG-02 精确重放两次，并逆序输入聚合器 | A | false | 现有 RequestKey 去重，输入顺序不重复计数 |
| LEG-04 | 同 RequestKey、时间和 ordinal 的 A 与 B 冲突修订 | Ø | true | `请求修订冲突`；维持拒绝 |
| LEG-05 | 根会话、已知上下文回合，仅 legacy last=A、total=A | Ø | true | `旧格式`；不伪造今日总量 |
| LEG-06 | 同会话 turn-A 的 modern A；turn-B 只有 legacy A | A | true | `覆盖关系未知`；其他回合不能遮盖 |
| LEG-07 | modern usage=A、thread=A；随后同上下文 legacy last=A、total=A | A | false | 完整现代前缀及同范围累计检查点精确对账 |
| LEG-08 | modern usage=A、无 thread 快照；随后 legacy A | A | true | `覆盖关系未知`；请求数值相同不足以证明 |
| LEG-09 | 唯一 modern usage=A、thread=AB；legacy last=A、total=AB | A | true | `覆盖关系未知`；现代历史前缀不完整 |
| LEG-10 | 同会话 modern A 与另一个独立 SourceContext 的 legacy A，后者无回合 | A | true | `覆盖关系未知`；不跨来源自造回合 |
| LEG-11 | 昨日 modern B；今日 modern A、thread=AB；今日 legacy last=A、total=AB | A | false | 历史现代前缀 B+A 完整对账，今日仍只计 A |
| LEG-12 | modern A、legacy A 后，再重放同请求相同 payload | A | false | 精确重放不移动检查点证据 |
| LEG-13 | modern A、thread=A；legacy A 后，同请求仅 thread 改为 AB | A | true | `覆盖关系未知`；旧检查点不能继承后续修订 |
| LEG-14 | modern A、legacy A 后，同请求计数及快照改为 B | B | true | `覆盖关系未知`；保留最终 modern B，不回填 legacy |
| LEG-15 | 今日 modern A；另一根会话仅昨日 legacy B | A | false | 昨日 legacy 不污染今日 |
| LEG-16 | 根会话 modern A；已确认子会话 legacy B | A | false | 子会话不进入根会话范围 |
| LEG-17 | 同请求昨日 A、今日有效修订 B | Ø | false | 请求仍归最初发生的昨日 |
| LEG-18 | LEG-17 后今日有可对账 legacy B | Ø | false | 已覆盖历史请求的快照不创造今日 token |
| LEG-19 | 已知根会话 modern A；无元数据来源 legacy B | A | true | `缺少会话元数据` 仍存在 |
| LEG-20 | LEG-07 且调用方 `sourcePartial=true` | A | true | `来源仍在读取或部分来源不可用` 不被镜像清除 |
| LEG-21 | modern A；legacy last 含负数、total=A | A | true | `覆盖关系未知`；无效最近向量不能被有效累计量隐藏 |
| LEG-22 | modern A 和 legacy A 时间相同，双方无 ordinal | A | true | `覆盖关系未知`；相同时间不能证明先后 |
| LEG-23 | 已知回合 legacy A 在首个 modern A 之前 | A | true | `覆盖关系未知`；不可用未来首次请求补证 |
| LEG-24 | LEG-07 全部事件精确重放，并逆序交给聚合器 | A | false | 已证明镜像不重复累计、不误报 |
| LEG-25 | 原生合成 JSON：根元数据、task_started、legacy last=A/total=AB | 不聚合；解析 Usage=A、ThreadSnapshot=AB | 不适用 | 动态 parser 契约：legacy=true、上下文回合正确；RequestKey、RootTurnKey、TurnSnapshot 为 null |
| LEG-26 | 不同请求依次 ordinal=10(A)、5(B)，随后 legacy total=A | AB | true | `覆盖关系未知`；跨请求逆序使覆盖不可证，modern 总数不变 |
| LEG-27 | 同时刻两个 modern 请求：A 无 ordinal，B ordinal=2/thread=AB；随后 legacy AB | AB | true | `覆盖关系未知`；同时间前缀顺序有歧义 |
| LEG-28 | 同时刻两个 modern 请求：A ordinal=1，B ordinal=2/thread=AB；随后 legacy AB | AB | false | 已知 ordinal 证明完整多请求前缀 |
| LEG-29 | modern A；legacy last 缺缓存字段、total=A | A | true | `覆盖关系未知`；不从完整累计快照补齐缺字段 |
| LEG-30 | modern A 的早检查点 ordinal=10；稍后零用量 modern Z、无 ordinal、thread=A；再后 legacy total=A、ordinal=5 | A | true | `覆盖关系未知`；缺失序号不能清除前缀 10 与 legacy 5 的已知矛盾 |
| LEG-31 | 同时间、同 ordinal 的不同请求分别属于 turn-A/A/thread=A 与 turn-B/B/thread=AB；随后 turn-B legacy total=AB | AB | false | 同位置批次逐条读取各自回合与快照 |
| LEG-32 | modern A/序号10，随后 B/序号5，再后零用量 Z/无序号/thread=AB，最后 legacy total=AB/序号20 | AB | true | `覆盖关系未知`；后续无序号检查点不能消除前缀内部已知倒置 |
| LEG-33 | modern A/序号10，随后 B/序号5，再后首请求仅 thread 修订为 AB/序号20，最后 legacy total=AB/序号30 | AB | true | `覆盖关系未知`；最终修订不能遮住首次发生证据的倒置 |
| LEG-34 | 首请求 A/序号1 修订为 B/序号10；另一请求 A/序号5；首请求仅 thread 再修订为 AB/序号20；最后 legacy total=AB/序号30 | AB | true | `覆盖关系未知`；所有有意义中间修订中的跨请求顺序矛盾仍有效 |
| LEG-35 | modern A/序号1/无 thread；随后零用量 Z/无序号/thread=A；legacy total=A/序号2 | A | false | 时间关系明确、完整前缀对账且已知序号无矛盾，无序号本身不导致误报 |
| LEG-36 | 同请求 A/序号1，修订 B/序号10，再修订 A/无序号；legacy total=A/序号5 | A | true | `覆盖关系未知`；最终缺序号不能删除中间修订的已知序号边界 |

## 读取与显示验收矩阵

monitor 合成夹具在专用临时目录下创建各自 home，写入 `sessions/*.jsonl` 后调用真实 `SessionLogMonitor`；时间固定为 `2026-10-21T04:00:00Z`，`now` 为十分钟后，时区为显式固定 UTC+8。各夹具只删除自己创建的目录。

| 用例 | 合成输入与操作 | 预期 token | 预期 `IsPartial` | 状态预期 |
| --- | --- | --- | --- | --- |
| LEG-M01 | A 文件 modern A；B 文件 legacy；连续刷新两次 | 两次均 A | true | 两次均保留 legacy 不可还原原因，共 3 个断言 |
| LEG-M02 | A 文件 modern A；B 文件昨天 legacy，但文件修改时间为今日 | A | false | 不含 legacy 原因 |
| LEG-M03 | 根会话 modern A；已确认子会话 legacy | A | false | 子会话不影响根统计 |
| LEG-M04 | A modern；B 的 token_count 只有合成 rate_limits、无 info | A | false | 不产生 legacy 用量事件；不访问额度接口 |
| LEG-M05 | A modern；无元数据来源 legacy | A | true | 保留 `缺少会话元数据` |
| LEG-M06 | A modern；B legacy 后有损坏合成行 | A | true | 同时保留 `格式不完整` 和 legacy 原因，共 2 个断言 |
| LEG-M07 | A modern；B legacy；另一来源只有未结束的 `{` | A | true | 同时保留来源 partial 与 legacy 原因 |
| LEG-M08 | 同文件 modern A 后接原生 legacy A；由真实 parser 提供上下文回合 | A | false | 完整历史前缀镜像不误报 |
| 复制格式新增用例 | 手工构造 token=A、partial=true、明确旧格式原因的 snapshot | A，显示 total=140 | true | 逐行保留四字段、总量、原因与 `(partial data)` |

M01 至 M08 为 8 组夹具、11 个新增断言。monitor 原有断言共 80 个（主组 11、工具 41、超大行 20、生命周期 8），另有 44 组 JSON/UTF-8 输入按每个字节分割验证的独立汇总。不要把日志的 PASS 行数当作断言总数。格式测试新增 1 个用例，总数为 15。

## 实际运行证据与最终复验

专用临时运行根目录：

```text
<dedicated-temp-root>
```

产物、CLI home、合成日志和运行证据都位于该目录及其子目录。最终复验使用下列环境设置和构建参数；公开文档将仓库和临时根路径参数化，实际路径只保留在本地记录中。重跑时先在仓库根目录初始化以下两个路径；其余参数、产物目录名和结果日志名与实际运行一致。

```powershell
$taskRepository = (Get-Location).Path
$taskRunRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexDaily-integrity-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRunRoot, (Join-Path $taskRunRoot 'scratch') | Out-Null
$env:TEMP = Join-Path $taskRunRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $taskRunRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Set-Location -LiteralPath $taskRunRoot

dotnet run --project "$taskRepository/tests/CodexUsage.Daily.Tests" -c Release --artifacts-path (Join-Path $taskRunRoot 'verified-core') --no-launch-profile *> (Join-Path $taskRunRoot 'verified-core.txt')
dotnet run --project "$taskRepository/tests/CodexUsage.DailyMonitor.Tests" -c Release --artifacts-path (Join-Path $taskRunRoot 'verified-monitor') --no-launch-profile *> (Join-Path $taskRunRoot 'verified-monitor.txt')
dotnet run --project "$taskRepository/tests/CodexUsage.DailyFormatting.Tests" -c Release --artifacts-path (Join-Path $taskRunRoot 'verified-formatting') --no-launch-profile *> (Join-Path $taskRunRoot 'verified-formatting.txt')
dotnet run --project "$taskRepository/tests/CodexUsage.DailyPopup.Tests" -c Release --artifacts-path (Join-Path $taskRunRoot 'verified-popup') --no-launch-profile *> (Join-Path $taskRunRoot 'verified-popup.txt')
dotnet build "$taskRepository/src/CodexUsage.DailyPanel" -c Release --artifacts-path (Join-Path $taskRunRoot 'verified-app-build') *> (Join-Path $taskRunRoot 'verified-app-build.txt')
```

在独立仓库目录执行的最终只读核查命令为 `git diff --check`、`git status --short`、`git rev-parse HEAD`、`git ls-remote origin refs/heads/codex/initial-release`。`git diff --check` 退出 0；HEAD 和远端分支均仍为基线。未运行会 publish、启动 GUI 或生成交付包的构建脚本。

已有运行证据按发生时的代码状态记录：

| 检查 | 状态 | 已观察结果 | 证据 |
| --- | --- | --- | --- |
| 最小反例，修复前 | 已执行失败，预期的红灯 | 80/81；唯一新增反例未标 partial | `red-minimal.txt` |
| 扩展核心矩阵，修复前 | 已执行失败，预期的红灯 | 94/105，11 个失败 | `red-matrix.txt` |
| 首轮覆盖修复核心 | 已执行通过，中间版本 | 105/105，退出 0 | `green-core-01.txt` |
| 扫描优化后核心 LEG-01 至 LEG-29 | 已执行通过，中间版本 | 109/109，退出 0 | `green-core-02.txt` |
| monitor 最小混合反例，修复前 | 已执行失败，预期的红灯 | 保留数值，但未标 legacy partial，未处理断言异常退出 | `pipeline/monitor-red.txt` |
| monitor 状态合并前 | 已执行失败，预期的红灯 | 来源格式提示隐藏 legacy 原因，未处理断言异常退出 | `pipeline/monitor-status-red.txt` |
| monitor 状态合并后 | 已执行通过，中间版本 | 新增 11/11 断言，原有组亦通过，退出 0 | `pipeline/monitor-green.txt` |
| 格式测试 | 已执行通过，中间版本 | 15/15，退出 0 | `pipeline/formatting-green.txt` |
| 早期无序号检查点试验 | 已取代的中间验证 | 曾有 110/111 红灯与 111/111 绿灯；LEG-30 当时预期会把缺失 ordinal 当成解除已有矛盾，契约复核后撤销该预期，不能据此声称完整 | `red-checkpoint-index.txt`、`green-core-03.txt`、`final-core.txt` |
| 前缀顺序反例，收紧前 | 已执行失败，预期的红灯 | 111/115，LEG-30、32、33、34 失败；进一步加入中间修订边界后为 111/116、共 5 失败；退出 1 | `red-prefix-order.txt`、`red-prefix-history.txt` |
| 前缀顺序修复 | 已执行通过，中间复验 | 116/116，退出 0 | `green-prefix-history.txt` |
| 最终核心，含 LEG-01 至 LEG-36 | 已执行并通过 | 116/116，退出 0 | `verified-core.txt` |
| 最终 monitor | 已执行并通过 | 主 11 + 工具 41 + 超大行 20 + 生命周期 8 + 完整性 11 = 91 个断言；另有 44 组 JSON/UTF-8 case 的每字节切分验证，退出 0 | `verified-monitor.txt` |
| 最终 formatting | 已执行并通过 | 15/15，退出 0 | `verified-formatting.txt` |
| 最终 popup 回归 | 已执行并通过 | 26/26，退出 0；这是几何与交互逻辑测试，不是 GUI 实机验收 | `verified-popup.txt` |
| 最终应用 build | 已执行并通过 | `Build succeeded`，0 warning、0 error，退出 0 | `verified-app-build.txt` |
| 最终独立静态审阅 | 已执行，无未决重要问题 | 静态代码审阅，不是动态实测 | 本阶段审阅结论 |
| 最终合成性能复验 | 已执行并通过对应夹具检查 | 结果见下表；只验证该组覆盖镜像输入 | `benchmark-verified.txt` |
| 最终 diff/HEAD/远端只读核查 | 已执行并通过 | `diff --check` 退出 0；本地 HEAD 与远端原分支仍为基线，无提交或推送 | Git 命令结果 |

用户授权上传后，在相同隔离环境中再次运行上述四个测试项目与应用构建，产物和日志前缀由 `verified-` 改为 `upload-`。结果仍为核心 116/116、monitor 91 项断言及 44 组边界输入、formatting 15/15、popup 26/26 全部通过，应用构建 0 警告、0 错误；五条命令退出码均为 0。该次复跑未修改生产代码。

辅助性能检查只用于防止覆盖判定重复扫描，不改变任何采集上限。下表为相同合成覆盖镜像夹具、关闭分层编译后的稳态测量；总量和完整性按夹具校验。该测量不证明真实日志性能，也未覆盖采集上限负载。

| modern/legacy 对数 | 事件数 | 初版中位耗时 ms | 最终中位耗时 ms | 初版分配字节 | 最终分配字节 |
| --- | --- | --- | --- | --- | --- |
| 500 | 1001 | 14.433 | 1.032 | 31,377,224 | 2,126,832 |
| 1000 | 2001 | 50.050 | 2.094 | 122,165,608 | 4,268,592 |
| 2000 | 4001 | 194.569 | 5.026 | 481,953,032 | 8,572,856 |

辅助基准项目位于 `$taskRunRoot/review-benchmark/verified`，只复制当前 DailyCore 源码、同哈希冻结 DLL 和合成夹具程序，不引用真实日志。沿用上述全部隔离环境变量，实际构建与运行命令为：

```powershell
$taskBenchVerified = Join-Path $taskRunRoot 'review-benchmark/verified'
$env:DOTNET_TieredCompilation = '0'
dotnet build (Join-Path $taskBenchVerified 'ReviewBenchmark.csproj') -c Release *> (Join-Path $taskRunRoot 'benchmark-verified-build.txt')
dotnet (Join-Path $taskBenchVerified 'bin/Release/net9.0/ReviewBenchmark.dll') 500 1000 2000 *> (Join-Path $taskRunRoot 'benchmark-verified.txt')
```

两条命令退出码均为 0。此前 `benchmark-final.txt` 的 0.977/1.926/4.768 ms 是顺序约束收紧前的中间版本测量，已被上表取代。

该辅助 benchmark 首次运行遗漏了 SDK 首次运行和证书生成的关闭环境变量。SDK 输出报告“Installed an ASP.NET Core HTTPS development certificate”。本阶段没有执行 trust 命令、扫描证书存储或删除证书；是否实际新增证书尚未独立核实。该环境副作用提示已经单独记录，不能把后续测试目录隔离写成“整个阶段没有任何目录外副作用”。之后的命令显式设置 `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1` 和 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`。

## 未执行项目、限制与下一阶段前置条件

| 项目 | 分类 | 原因或前置条件 |
| --- | --- | --- |
| 真实用户会话和聊天正文验证 | 未执行 | 本阶段限定合成数据；未经后续明确范围授权不读取真实日志 |
| `auth.json` 或实际账号额度查询 | 未执行 | 不属于完整性修复；本阶段没有该需求 |
| 完整发布脚本、publish、GUI 验收、打包、运行程序替换 | 未执行 | 本阶段不发布、不改 UI；本记录中的文本链路验证不能写成真实 GUI 验收 |
| 长时间真实运行、多屏与混合 DPI | 未执行 | 与本次聚合完整性修复无关，需独立场景与验收范围 |
| 冻结 Core 完整原始构建重建 | 未执行，范围限制 | 已核实本次定义与投影所需匹配源码，但未建立完整构建来源和独立重建流程 |
| SDK、Windows 或必要字段源码缺失 | 当前无此阻塞 | 现有环境具备所需 SDK，相关字段和投影已找到匹配依据；不据此宣称其他环境也具备依赖 |
| 未知 legacy 数量的还原 | 证据限制 | 旧格式没有可靠请求身份；未知时必须保留未知，不估算缺口 |
| commit、GitHub push、release | 未执行 | 本阶段选择保留未提交修改；用户明确禁止自动推送或发布 |

本阶段完成后停止。下一阶段如需要真实日志验证，应先约定可读取的数据、脱敏与留存边界、独立临时路径和验收标准；如需分发，应先审阅本地 diff 和最终复验结果，并另行授权提交、推送或发布。若要替换或重建冻结 Core，需要先补齐完整构建来源和依赖记录。实时额度接入属于另一项需求，本阶段不启动。
