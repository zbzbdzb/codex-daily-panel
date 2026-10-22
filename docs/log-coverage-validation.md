# 任务 A：日志覆盖丢失修复与验证

本次仅修复日志截断、替换或删除后，统计覆盖不足却仍显示完整的问题，包括少算、错误重新计入及不可靠 TPS。基线为 PR #1 已审查提交 `e77c71ea536890ebd12a0c9905ac893d3a37db56`，任务分支为 `fix/log-coverage-loss`，PR #3 目标为 `codex/statistics-integrity`，依赖 PR #1。没有执行任务 B。

开始时已核实：当前是 Codex 创建的独立工作树，detached HEAD 与基线一致、工作区干净；PR #1 open、未合并，其 head 和远端目标分支均仍为基线，原 PR base 也未变化。本地和远端没有同名任务分支，随后在当前工作树创建任务分支；没有新建或改动其他工作树。

本轮跟进起点为同一工作树、同一任务分支的已推送提交 `84ba4bec4795b67e10468fc383bb29a40cabbcf3`，开始时工作区干净，PR #1、PR #3 及远端目标状态未变。用户指出四项静态推导，明确尚未运行复现；本轮先在该提交上添加合成用例，实际得到四项失败后再修复。没有把静态推导、作者记录或此前测试通过当成这些新场景的执行证据。

## 根因与实现

`SessionLogMonitor` 原先在三条路径撤回来源时，没有留下独立的覆盖证据：发现集合删除路径、reader 重置回调、读取失败后重新建立来源。重置还会清除格式和超大行诊断。下一次聚合只看到剩余的有效事件及当前来源标志，因此可能把较小的小计标成完整。`DailyAggregator` 无法凭剩余输入知道已消失的历史。

初版提交 `84ba4be` 保留了实际贡献小计的证据，但遗漏了决定“不计入、归属哪天、为什么不可计算”的证据：昨日首次出现的请求、新于旧 root 元数据的 child 标记、历史跨 turn 工具 call 身份，以及失读后尚未核验的尾部。它们消失后，剩余日志本身解析正常，并不能证明统计仍完整。EXC-01 至 EXC-04 实测复现了这四项缺口。

修复保留现有数值聚合：撤回旧来源的事件，新小计只来自当前可读事件。另在 monitor 的当前统计日期下保存覆盖缺失证据，传播到 `IsPartial` 与说明文字：

- 对已完整读取、可解析的旧来源，保留必要事件的运行时类型及全部投影字段的 SHA-256；不保存旧 usage 或 token 向量用于回填数值。聚合器提供可选的覆盖证据收集器，复用全来源的已接受请求、首次发生和有意义修订、legacy 所需历史前缀、当日及跨日相关时序依赖。恢复时，必须由本次成功读取且已排空的有效来源提供全部必要事件证据，多个轮转或迁移分片可以共同提供；不能只按 request id 省略依赖。
- 另收集排除依据：请求身份与首次日期、最新 child 关系、工具 call / turn / 阶段及时间身份。保留的是分类事实及重放哈希，不含旧 token、TTFT、完成 duration、工具参数或结果正文。丢失的首次日期只约束当前请求的日期；child 关系继续约束范围；历史工具依据缺失时，仅排除相关 turn / session 的 TPS。不会凭旧数据恢复小计或构造已丢失的等待时长。
- 排除依据按原始候选输入有条件生效。昨日独立请求、已确认子会话或无关工具记录本身不使今日 partial；同请求今日重放、受影响的 legacy 候选或共享 call 再出现时才激活。判断 child 候选时不套用已丢失的首次日期事实，防止两项缺失依据互相使对方休眠。只有成功排空的原始日志可证明旧依据已恢复；注入汇总的分类事实和“时序不可用”标志不能自证。明确更新的有效 root 元数据可以替代旧 child 决策，避免永久误报。
- 格式错误、未知超大行、半行和无法关联的时序内容不能靠有效事件相等来解释。reader 增量计算实际消费字节的 SHA-256，恢复还须证明原始字节前缀保留。已经读过较长镜像时，按具体前缀挑战重放一次，仍走原来的读取预算和 parser；不保留正文或工具参数。恢复原始坏内容也不会消除当前来源本身的错误。
- 失去尚未读取的尾部、读取中不稳定的内容，或从未成功读取就消失的来源时，无法证明历史完整，保留当日 partial。失读后的尾部不确定状态独立于旧前缀：旧镜像相同也不能证明未知尾部存在与否。原文件仍在、身份与游标连续且成功读至 EOF 时可以恢复；文件先消失，则之后仅重放已知事件仍不足以证明未知尾部。reader 同时检测长度小于先前观察长度的回退，即使恰好截至已消费游标也须重置并捕获缺失。
- 重复变化的证明记录数量以及事件哈希、条件依据和范围信息的总量受现有 50,000 规模约束，超过证明保留范围也不能恢复为完整；统计采集的原有上限没有改变。
- 日期切换时，先按新日期从仍存活的来源提取分类依据，与已丢失的条件依据合并，再清理上一统计日期的数值/未知字节缺口。昨日缺失不会无条件污染今日；但已知旧请求不能在午夜后被重新归到今日。条件依据继续受证明上限限制，超额保守 partial，不增加跨进程持久化。
- 临时 I/O 和拒绝访问保留同一个 reader 的游标、有限半行缓冲、parser 上下文和诊断；物理替换仍在重置前捕获缺失。先完成原文件 EOF 恢复，再允许可选的原始前缀证明重放；已排队但尚未开始的重放若打开失败，会释放该次尝试标记并延后重试，避免将主动重放误当成永久未知尾部。异常或取消时提取 reader 已消费的诊断，读取字节仍计入预算。来源提示和聚合提示分别保留，不互相覆盖。

额度事件不参加历史覆盖哈希对账：原逻辑只保留每来源最新额度快照，历史额度值本来也不参与今日 token。否则，完整重放后追加一个更新额度样本会使旧额度哈希无法在当前事件集合中出现，造成永久误报。额度选择与计算逻辑没有改变。

`DailyAggregator` 增加可选的证据输出，以及已知请求首次日期输入；当前 token 向量仍从当前可读事件选择。legacy 覆盖算法、请求修订选择、token 分量、TPS 公式及额度口径均未改变。UI 代码、冻结 Core DLL、项目文件和构建配置均未修改。

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

跟进排除依据与恢复边界用例：

| 用例 | 操作与关键断言 |
| --- | --- |
| EXC-01 | 反向删除昨日首现、保留今日重放：A 不重新计入，只有 B、partial；恢复原始依据后 complete |
| EXC-02 | 删除较新的 child 标记、保留旧 root 与 usage：A 不被提升，只有 B、partial；恢复标记后 complete |
| EXC-03 | 删除昨日跨 turn 的共享工具 call 冲突：token 小计保留、partial、TPS 与处理秒数未知；恢复原始冲突后覆盖 complete，TPS 仍未知 |
| EXC-04 | 原文件追加未读 B、失读后删除，只剩旧 A 镜像：A、partial；随后读到 AB 只计一次，仍不能证明未知尾部已完整恢复 |
| EXC-05 / 06 | 原文件仍在的失读可恢复至 AB、complete；没有历史冲突的工具对照仍为 6 秒处理时间、TPS=40/6 |
| EXC-07 | 长镜像曾失读，短半行源被删除：先恢复长镜像 EOF，再验证原始前缀，至多两次刷新恢复 AB、complete |
| EXC-08 | 只丢失 A 的工具排除依据：partial；独立根会话 B 仍提供原有 6 秒及 TPS，A 不进入 TPS 样本 |
| EXC-09 | 缺失的旧首现先处于休眠、后来才出现今日重放：B、partial，恢复旧首现后 complete |
| EXC-10 | 缺失 child 标记后出现明确更新的有效 root 关系：AB、complete，不永久排除 |
| EXC-11 | 直接 reader 读取 4 MiB 后，将已观察但未读完的大文件截至游标：检测重置，旧覆盖保留未读长度 |
| EXC-12 | reader 配置路径为合成 `.jsonl` 目录触发 Windows 拒绝访问，再替换为普通文件：原实例重试并读至 EOF，空闲不重复 |
| EXC-13 | child 只有 legacy usage、无现代 request/turn：删除 child 标记仍 B、partial，不遗忘排除范围 |
| EXC-14 | 缺失的旧首次日期跨午夜后遇到次日重放：只计次日独立 B、partial；恢复原始首现后 complete |
| EXC-15 | 昨日最后一次刷新原来源仍存活，午夜之后才发现删除：不遗忘旧首现，今日重放仍只计 B、partial；恢复原始依据后 complete |
| EXC-16 | 同一次刷新删除短源并锁住长镜像：撤销未开始的证明重放，解锁后恢复原游标，再核验前缀；至多三次刷新 AB、complete |
| EXC-17 | 先缺失旧首现、再获得并失去 child 标记：两种缺失依据不能互相使对方休眠，B、partial，空闲也不恢复 complete |

两项原测试预期与本任务目标冲突，已更正：超大行用例删除坏统计行后仍须保留 partial 和 4 MiB 原因；工具时序用例替换文件删除已知工具生命周期后，不能将等待视为零并恢复可信 TPS。其余原测试预期未修改。

## 本次实际执行结果

没有采用 PR #1 作者自述作为本次测试结果。执行前独立核实冻结 DLL 的 Git blob 为 `1cfae06fb1aa69f4a34e2feb85296b577f900bd4`、SHA-256 为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`，与已审查基线相同。仓库缺少该 DLL 完整重建来源，已说明这一限制并取得用户本次执行固定 DLL 隔离合成测试的明确确认。没有恢复 Core 源码，也没有宣称本次重新审计其全部内部行为。

使用已安装的 .NET SDK 9.0.318。已核实仓库没有自定义构建任务或外部包，工作树祖先目录没有 `Directory.Build.targets`。CLI home、临时夹具和构建产物使用专用 GUID 临时根；这只是正常输出位置隔离，不是操作系统安全沙箱。关闭 SDK 首次运行、遥测和开发证书生成。没有执行完整发布脚本。

| 检查 | 实际结果 |
| --- | --- |
| 修改前原日志监控回归 | 退出 0；既有测试通过 |
| 初始新增 13 个场景，生产代码仍为基线 | 2 通过、11 失败；实际捕获数值减少却 complete、损坏原因消失 |
| 最终 23 个场景配基线 reader/monitor 的临时复现副本 | 7 通过、16 失败；预期红灯，未修改工作树起点或恢复文件 |
| 审阅发现的跨日误报，收集器修复前 | 23 通过、2 失败；COV-24/25 实际捕获误报，然后增加按实际投影选择的证据输出 |
| 初版 84ba4be 的日志监控项目 | 原 25/25 覆盖场景和既有测试通过，但未覆盖后来报告的四项排除依据缺口 |
| 本轮生产修改前，针对 84ba4be 的 EXC-01 至 06 | 2 通过、4 失败；四项问题均实际触发错误的 complete；EXC-03 还显示已恢复 TPS 估算 |
| 本轮三项边界补充，修复前 | EXC 前 14 项通过，新增 15/16/17 三项失败；分别实际出现 input=300 且 complete、第三次解锁刷新仍 partial、组合依据丢失却 complete |
| 本轮最终日志监控项目 | 退出 0；COV 25/25、EXC 17/17，原主组 11、工具 41、超大行 20、生命周期 8、legacy 11 项断言通过；另有 44 组 JSON/UTF-8 的逐字节切分验证通过 |
| 核心聚合与解析契约 | 退出 0；116/116 |
| 格式 | 退出 0；15/15 |
| 弹窗几何与交互逻辑 | 退出 0；26/26，非真实 GUI 验收 |
| 应用 Release 构建 | 退出 0；0 警告、0 错误 |
| 独立静态审阅及最终复审 | 初版曾发现跨日误报（COV-24/25）；本轮另发现恢复时证明重放、legacy child 候选及操作顺序/组合遗漏，分别增加 EXC-07/13/15/16/17 并修复。最终复审确认这三项顺序/组合遗漏已解决，未发现所审修法及直接回归中的新增实质问题；静态审阅不当作测试执行 |

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

每条实际执行命令分别将完整 stdout/stderr 写入临时根中的同名 `.txt`，立即保存并检查退出码。本轮根目录为 `CodexDaily-exclusions-7485eb7951d345498df2cdbff9bd9ae9`；原四项红灯为 `red-monitor.txt`，三项后续边界红灯为 `red-edge-monitor.txt`，最终结果为上述五条命令的同名日志。本轮四项红灯直接在尚未修改生产代码的 84ba4be 上执行；初版历史基线复现才使用临时副本及 `git show e77c71ea536890ebd12a0c9905ac893d3a37db56:<path>` 提取原 reader/monitor，没有在当前工作树切换、重置或覆盖文件。

## 未执行项与限制

- 没有读取真实 sessions、auth、凭据、私人聊天或实际账号额度；没有真实 GUI、多屏或长期运行验收。
- 读取错误恢复动态测试采用合成文件独占锁；拒绝访问重试分支通过将配置路径设为合成目录后替换成文件来执行。没有更改文件权限或执行真实 Windows ACL 拒绝访问、ACL 变化时半行连续读取的动态验收。
- 没有运行完整发布脚本、publish、打包、发布、替换运行程序或合并 PR。
- 覆盖缺失及条件性分类证据只在当前 monitor 实例内保存；不能发现启动前已删除、从未观察到的日志，也没有增加跨进程持久化。
- 保持原 reader 头部/边界指纹检测边界，不能保证发现保持这些探针不变的任意内部编辑。读取中已检测到的不稳定内容不会作为恢复证明。
- 未知未读内容、损坏内容无法重现、证明保留范围耗尽时，会保守维持当日 partial；不估算缺口，也不保证能通过语义相似但无法核对全部证据的日志转换解除。
- 保持原文件发现和统计采集上限；本次没有开展真实负载或上限规模的性能基准。
