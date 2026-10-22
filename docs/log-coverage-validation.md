# 任务 A：日志覆盖丢失修复与验证

本次仅修复日志截断、替换或删除后，当前小计减少却仍显示完整的问题。基线为 PR #1 已审查提交 `e77c71ea536890ebd12a0c9905ac893d3a37db56`，任务分支为 `fix/log-coverage-loss`，PR 目标为 `codex/statistics-integrity`，依赖 PR #1。没有执行任务 B。

开始时已核实：当前是 Codex 创建的独立工作树，detached HEAD 与基线一致、工作区干净；PR #1 open、未合并，其 head 和远端目标分支均仍为基线，原 PR base 也未变化。本地和远端没有同名任务分支，随后在当前工作树创建任务分支；没有新建或改动其他工作树。

## 根因与实现

`SessionLogMonitor` 原先在三条路径撤回来源时，没有留下独立的覆盖证据：发现集合删除路径、reader 重置回调、读取失败后重新建立来源。重置还会清除格式和超大行诊断。下一次聚合只看到剩余的有效事件及当前来源标志，因此可能把较小的小计标成完整。`DailyAggregator` 无法凭剩余输入知道已消失的历史。

修复保留现有数值聚合：撤回旧来源的事件，新小计只来自当前可读事件。另在 monitor 的当前统计日期下保存覆盖缺失证据，传播到 `IsPartial` 与说明文字：

- 对已完整读取、可解析的旧来源，保留必要事件的运行时类型及全部投影字段的 SHA-256；不保存旧事件对象用于回填数值。聚合器提供可选的覆盖证据收集器，复用全来源的已接受请求、首次发生和有意义修订、legacy 所需历史前缀、当日及跨日相关时序依赖。与当日无关的昨日独立请求和无意义重放不进入缺失证据。恢复时，必须由本次成功读取且已排空的有效来源提供全部必要事件证据，多个轮转或迁移分片可以共同提供；不能只按 request id 省略依赖。
- 格式错误、未知超大行、半行和无法关联的时序内容不能靠有效事件相等来解释。reader 增量计算实际消费字节的 SHA-256，恢复还须证明原始字节前缀保留。已经读过较长镜像时，按具体前缀挑战重放一次，仍走原来的读取预算和 parser；不保留正文或工具参数。恢复原始坏内容也不会消除当前来源本身的错误。
- 失去尚未读取的尾部、读取中不稳定的内容，或从未成功读取就消失的来源时，无法证明历史完整，保留当日 partial。重复变化的证明记录和事件哈希数量分别受现有 50,000 规模约束，超过证明保留范围也不能恢复为完整；统计采集的原有上限没有改变。
- 日期切换时清理上一统计日期的缺失记录；是否影响当日根会话复用 `DailyAggregator` 的日期、根/子会话、请求首次发生和生命周期判断，避免昨日重放、已结束历史或子会话单独污染今日。
- 临时 I/O 错误保留原 reader、parser 上下文和诊断并重试；必须重新建立停止的 reader 时先捕获缺失证据。异常或取消时提取 reader 已消费的诊断，读取字节仍计入预算。来源提示和聚合提示分别保留，不互相覆盖。

额度事件不参加历史覆盖哈希对账：原逻辑只保留每来源最新额度快照，历史额度值本来也不参与今日 token。否则，完整重放后追加一个更新额度样本会使旧额度哈希无法在当前事件集合中出现，造成永久误报。额度选择与计算逻辑没有改变。

`DailyAggregator` 只增加可选的覆盖证据输出，不改变 legacy 覆盖算法、请求去重与修订选择、token、TPS 或额度数值逻辑。UI 代码、冻结 Core DLL、项目文件和构建配置均未修改。

## 合成回归矩阵

所有夹具只在新的临时目录创建自己的 `sessions` / `archived_sessions`，使用固定 UTC+8 时间与合成 JSONL。向量顺序是 input、cached、output、reasoning：A=`(100,60,40,10)`，B=`(200,80,70,20)`，AB=`(300,140,110,30)`；未知仍为 null，不用零或旧值补齐。

| 用例 | 操作与关键断言 |
| --- | --- |
| COV-01 | 截断 AB 至 A：仅 A、partial，空闲轮询仍 partial |
| COV-02 / 03 | 等长 / 更长替换：仅新 B、partial |
| COV-04 | 删除 A、B 仍有效：仅 B、partial，SourceCount=1 |
| COV-05 | 截断后完整重放 AB：恢复 AB、complete，空闲不重复计数 |
| COV-06 | sessions 迁移到两个 archive 分片：只到一片时 partial，全部到齐后 AB、complete |
| COV-07 | 删除后多个副本及重复事件重放：仅 A、complete |
| COV-08 / 09 | 昨日缺失跨日、今日删除昨日专属来源：今日独立 B 保持 complete |
| COV-10 / 11 | 损坏行被删除、坏源被干净镜像替代：仅 A、仍 partial |
| COV-12 | 文件独占锁：读取失败时未知且 partial，原文件恢复可读后 A、complete |
| COV-13 | legacy 来源消失：modern A 仍 partial，legacy 不填入 token |
| COV-14 | request 身份相同但载荷不同：仅新 B、partial |
| COV-15 | 来源首次读即失败后删除：B 仍 partial，不能遗忘未读来源 |
| COV-16 | 半行先读取、再锁失败并删除：干净事件镜像不能解除未知字节缺失 |
| COV-17 | 跨路径重放精确半行原始前缀并补全：AB、complete |
| COV-18 | 较长镜像已先排空：删除短半行来源后，经原始前缀验证恢复 AB、complete |
| COV-19 | 完整重放再追加较新额度：A、complete，周额度保留最新 30% |
| COV-20 | 删除已确认子会话来源：根会话 A 保持 complete |
| COV-21 / 22 | 删除昨日请求的今日重放、昨日陈旧启动：今日独立 B 保持 complete |
| COV-23 | 坏格式来源经历临时锁后恢复：A 仍 partial，保留格式原因 |
| COV-24 | 同一来源只移除昨日独立请求、精确保留今日请求：今日 B、complete，不要求重放无关旧日数据 |
| COV-25 | 昨日首现与今日无意义重放位于不同来源：删除今日重放，独立今日 B 仍 complete |

原超大行回归中“删除坏统计行后立刻 complete”的预期与本任务冲突，现改为小计保持已知、partial 且保留 4 MiB 原因；其余原测试预期未修改。

## 本次实际执行结果

没有采用 PR #1 作者自述作为本次测试结果。执行前独立核实冻结 DLL 的 Git blob 为 `1cfae06fb1aa69f4a34e2feb85296b577f900bd4`、SHA-256 为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`，与已审查基线相同。仓库缺少该 DLL 完整重建来源，已说明这一限制并取得用户本次执行固定 DLL 隔离合成测试的明确确认。没有恢复 Core 源码，也没有宣称本次重新审计其全部内部行为。

使用已安装的 .NET SDK 9.0.318。已核实仓库没有自定义构建任务或外部包，工作树祖先目录没有 `Directory.Build.targets`。CLI home、临时夹具和构建产物使用专用 GUID 临时根；这只是正常输出位置隔离，不是操作系统安全沙箱。关闭 SDK 首次运行、遥测和开发证书生成。没有执行完整发布脚本。

| 检查 | 实际结果 |
| --- | --- |
| 修改前原日志监控回归 | 退出 0；既有测试通过 |
| 初始新增 13 个场景，生产代码仍为基线 | 2 通过、11 失败；实际捕获数值减少却 complete、损坏原因消失 |
| 最终 23 个场景配基线 reader/monitor 的临时复现副本 | 7 通过、16 失败；预期红灯，未修改工作树起点或恢复文件 |
| 审阅发现的跨日误报，收集器修复前 | 23 通过、2 失败；COV-24/25 实际捕获误报，然后增加按实际投影选择的证据输出 |
| 修复后的日志监控项目 | 退出 0；新增 25/25 场景、原主组 11、工具 41、超大行 20、生命周期 8、legacy 11 项断言通过；另有 44 组 JSON/UTF-8 的逐字节切分验证通过 |
| 核心聚合与解析契约 | 退出 0；116/116 |
| 格式 | 退出 0；15/15 |
| 弹窗几何与交互逻辑 | 退出 0；26/26，非真实 GUI 验收 |
| 应用 Release 构建 | 退出 0；0 警告、0 错误 |
| 独立静态审阅及修复后复审 | 跨日误报发现已通过 COV-24/25 红绿灯修复；最终无未决重要问题，静态审阅不当作测试执行 |

中间编译曾因在 LINQ lambda 捕获 `ReadOnlySpan` 报 CS9108，以及证据收集参数与既有局部变量重名报 CS0136；分别改为先计算结束偏移、使用不冲突的参数名后复验通过。这些失败没有被记作测试通过。

从仓库目录设置 `$taskRepository`，以新临时根 `$taskRunRoot` 执行的命令如下；实际产物没有加入 Git：

```powershell
$taskRepository = (Get-Location).Path
$taskRunRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexDaily-coverage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRunRoot, (Join-Path $taskRunRoot 'scratch') | Out-Null
$env:TEMP = Join-Path $taskRunRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $taskRunRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Set-Location -LiteralPath $taskRunRoot

dotnet run --project "$taskRepository/tests/CodexUsage.DailyMonitor.Tests" -c Release --artifacts-path "$taskRunRoot/verified-monitor" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.Daily.Tests" -c Release --artifacts-path "$taskRunRoot/final-core" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyFormatting.Tests" -c Release --artifacts-path "$taskRunRoot/final-formatting" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyPopup.Tests" -c Release --artifacts-path "$taskRunRoot/final-popup" --no-launch-profile
dotnet build "$taskRepository/src/CodexUsage.DailyPanel" -c Release --artifacts-path "$taskRunRoot/verified-app-build"
```

每条实际执行命令分别将完整 stdout/stderr 写入临时根中的同名 `.txt`，立即保存并检查退出码。基线复现副本只复制仓库源码、测试、已确认 DLL 和原构建配置，再用 `git show e77c71ea536890ebd12a0c9905ac893d3a37db56:<path>` 提取原 reader/monitor；不是在当前工作树切换、重置或覆盖文件。

## 未执行项与限制

- 没有读取真实 sessions、auth、凭据、私人聊天或实际账号额度；没有真实 GUI、多屏或长期运行验收。
- 读取错误恢复动态测试采用合成文件独占锁；没有更改文件权限或执行真实 Windows ACL 拒绝访问验收，拒绝访问停止/重建分支另做静态核查。
- 没有运行完整发布脚本、publish、打包、发布、替换运行程序或合并 PR。
- 覆盖缺失证据在当前 monitor 实例内按统计日保存；不能发现启动前已删除、从未观察到的日志，也没有增加跨进程持久化。
- 保持原 reader 头部/边界指纹检测边界，不能保证发现保持这些探针不变的任意内部编辑。读取中已检测到的不稳定内容不会作为恢复证明。
- 未知未读内容、损坏内容无法重现、证明保留范围耗尽时，会保守维持当日 partial；不估算缺口，也不保证能通过语义相似但无法核对全部证据的日志转换解除。
- 保持原文件发现和统计采集上限；本次没有开展真实负载或上限规模的性能基准。
