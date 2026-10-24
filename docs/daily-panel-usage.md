# CodexDaily v0.2.0 使用说明

CodexDaily 是 Windows x64 托盘统计面板，用于查看本机 Codex 根会话的 token、估算 TPS 与周额度日志快照。请从完整交付目录运行 `CodexDaily.exe`，不要只复制 EXE 或混用不同构建的 DLL。

先查看交付目录根的 `BUILD-PROVENANCE.json`：`selfContained=true` 表示该交付携带指定 .NET 运行时；`false` 表示框架依赖构建，运行需要 .NET 10 Windows Desktop Runtime。两类目录都会带本说明，不能因看到“使用说明”或 .NET 许可文件就认定运行时已打包。准确构建版本、源码提交和运行时信息以同目录来源记录为准；候选构建不自动等于正式发布或验收通过。

## 启动与日常操作

运行后只显示 Windows 托盘图标，不占用任务栏。点击蓝色柱状图图标打开面板；图标被折叠时，在任务栏的“^”隐藏图标区域查找。再次点击图标、点击外部、按 Esc 或点击右上角隐藏按钮会收起面板；这些操作不会退出。底部电源按钮或托盘右键“退出面板 / Exit panel”才结束程序。

面板依次显示统计日期与状态、总 token 和输入/输出、估算 TPS 与净处理时间、周额度日志快照。“明细与统计范围 / Breakdown and scope”可展开查看缓存、推理、活动时长、会话/回合和 TPS 覆盖。内容较长时可滚动，底部操作保持独立。

底部提供“复制统计 / Copy stats”、刷新、打开日志目录、退出。存在快照时，复制内容使用当前语言，包含精确整数、快照日期与更新时间、范围、估算口径和数据质量/原因；尚无快照时只输出标题、无数据与可用原因。短暂显示“已复制 / Copied”，失败时提示重试。大号 token 数字可能缩写，旁边的精确数值和复制内容保留完整数量。

程序通常每 10 秒自动刷新，面板隐藏后仍继续；读取积压时每秒继续。托盘提示包含总 token、估算速度和状态，不另列输出 token。旧快照会按原日期标记，不能当作今日新统计。

托盘右键“暂停自动刷新 / Pause automatic refresh”只停止新的自动刷新，不暂停 Codex、不取消在途读取。暂停时仍可手动刷新；取消暂停立即请求刷新。忙时再次手动刷新会合并成至多一次后续读取，不按点击次数排队。退出会停止后续刷新，取消读取并等待本轮收尾；X/Esc 只隐藏，检查是否退出应以进程和托盘是否结束为准。

普通模式按“当前用户 + 规范化后的日志 home”保持一个实例。再次启动同一 home 会请求打开已有面板，然后新进程退出；激活失败返回 1，可以等旧实例退出完成后重试。不同 home 可运行各自实例；只改变 `--settings-dir` 不会绕过同一 home 的实例限制。路径规范化不等于解析所有目录联接、符号链接或短路径别名；不要靠这些别名组织多个实例。demo/render/snapshot 不占用普通模式的实例通道。

## 外观、语言与设置位置

展开“外观与语言 / Appearance and language”：

- 主题：跟随系统 / System、浅色 / Light、深色 / Dark。默认跟随 Windows 的应用主题。
- 语言：跟随系统 / System、简体中文、English。系统 UI 语言以 `zh` 开头时选择简体中文，其他语言选择 English。

选择后应用于当前面板、托盘菜单和复制文字。设置只保存 schema 版本、主题、语言；不保存日志 home、暂停状态或统计数据，不读取 Codex 的私人外观配置。

默认文件为 `%LOCALAPPDATA%\CodexDaily\settings.json`。`--settings-dir <目录>` 可选择独立设置目录；其中仍使用 `settings.json`。显式目录读取或保存失败时不会改到真实默认设置目录。不存在设置文件时使用默认值；损坏、不支持的版本、超过 16 KiB 或不可读设置会回退默认值并显示非致命提示。保存失败时当前选择可继续使用，但不能保证重启后保留。写入采用同目录临时文件再整体替换，避免把已有文件截断成半份内容。手工编辑时只接受 `schemaVersion`、`theme`、`language` 三个唯一字段，schema 为整数1；枚举值区分大小写，分别为 `System/Light/Dark` 和 `System/ZhCN/EnUS`。额外、重复字段或无效值会回退。

普通 demo 和 render 也会读取所选设置，demo 中的外观选择可以保存；`--help`、`--version` 与 snapshot 不加载设置。需要完全隔离的演示或渲染时，也请显式传入 `--settings-dir`。

## 怎样理解状态

活动状态与数据质量分别显示，可以同时出现，例如“已暂停 + 统计不完整”或“正在读取 + 数据已过期”。

| 显示 | 含义 |
| --- | --- |
| 正在读取 / Refreshing、已暂停 / Paused、正在退出 / Exiting | 当前读取与界面操作状态；不等于 token 是否完整 |
| 统计不完整 / Partial | 当前来源存在缺失、冲突或覆盖未知，只显示可核验小计；查看下方原因 |
| 暂无数据 / No data、不可用 / Unavailable、`—` | 没有可靠数值；不自动当作 0 |
| 数据已过期 / Stale | 保留的快照不再代表当前统计，仍显示其原日期与更新时间 |
| 本地 / Local | 当前可见数据没有触发上述质量标记，不证明账号账单完整 |

整轮刷新异常可以保留上一次成功快照，但会标记过期和失败原因。跨越本地日期或快照时间晚于当前时钟，也会标为过期。收到新的 Partial 快照后显示新的可核验小计，不拿旧的较大数字填补缺失。

暂停时保留上次数据及日期，界面仍会检查日期和周额度快照有效期；打开面板或复制时也重新检查。过期快照不显示为当前运行中回合；暂停、退出或活动观察过旧时，运行中回合显示不可用。旧 token、TPS、活动时长可以作为带日期的快照保留，不表示它们还在实时增长。周额度到期、观察时间在未来或整体快照过期时不继续展示为有效周额度。

缺失依据仍影响当前统计时保留 Partial；证据恢复或不再影响当前统计后解除对应提示。其他缺失或冲突仍可使整体保持 Partial。恢复一个文件不一定恢复该回合的时长/TPS 资格。

## 统计范围

- 本机时区的今日请求，来自选定 home 下的 `sessions` 和 `archived_sessions`。路径按显式 `--codex-home <目录>`、非空白的 `CODEX_HOME`、用户目录的 `.codex` 的顺序确定。
- 只汇总根会话，不计子代理、云任务、其他机器以及服务端未落入本机日志的用量。并非账号全部 token 账单。
- 初次枚举选择当天更新的日志文件，包括保存在旧日期目录中、今天继续使用的会话。最多 32 个文件，每文件最多 25000 个统计事件，全部来源合计最多 50000 个事件。单次 monitor 刷新的读取预算为 64 MiB；reader 单次批次和普通解析单行的上限分别为 4 MiB，不能把这些限制混为单文件大小上限。选中文件中会保留有界的历史统计以对账修订和午夜生命周期；触及限制、文件不可读或统计格式异常时显示 Partial。
- 超过 4 MiB 的 `response_item` / `turn_context` 普通消息行和 `compacted` 上下文压缩记录，会在持续校验完整 JSON、UTF-8、唯一根类型及结构后丢弃正文，不因此误报 token 统计不完整。工具调用与返回的允许时序字段会以有界流式方式提取，即使字段位于大段正文之后也可读取；无法识别的超大工具时序会让相应来源的估算 TPS 不可用。超大的统计行、未知根类型、歧义或损坏行仍标记 Partial，不能静默漏算。
- 只投影统计字段到内存；不保存聊天正文、不读取认证文件、不发起账号查询。日志以只读共享方式打开。统计不会写回 Codex。
- 历史分类、首现、闭合及冲突依据只保留在当前 monitor 实例内。重启后的新实例无法证明启动前已丢失来源的完整性；头部/读取边界探针也不保证发现任意内部改写。证据容量不足或未知尾部可能使统计继续保守地标记 Partial。

## 数值含义

**总输入 / 总输出（Total input/output）** 为今日按请求身份去重的输入和输出。每个请求按最早有效发生时间归入日期，最后有效修订替换 token 数值；午夜之后收到的修订不会变成另一天的新请求。缓存是输入的一部分，推理是输出的一部分；两者不能再次加到总量中。旧格式 token_count 累计快照不相加；仅有旧格式时不编造今日总量。缺失数值显示 `—`。

已知为零的统计仍显示 `0`；`No data` 不是零用量的证明。缺失依据仍影响当前统计时保留 Partial；依据恢复或不再影响当前统计后解除对应提示。其他缺失或冲突仍可使整体保持 Partial，恢复冲突记录不自动恢复时长/TPS 资格。`Partial=false` 不代表完整账号账单。

同一天混合出现现代请求和旧格式快照时，若无法证明快照已被现代请求覆盖，保留已确认的现代请求小计，并提示“统计不完整：旧格式快照覆盖关系未知，无法还原全部今日用量”。其他会话或回合存在现代请求，不能消除这项提示；已完整对账的镜像快照不会重复累计或触发这项告警。覆盖未知时保留小计与提示；历史合成回归不代表当前完整交付已完成普通 EXE 验收。

**总 token（Total tokens）** 为输入与输出相加，在面板、托盘提示和复制统计中同时提供。缓存和推理不再另加。输入或输出未知、无效或总和超出整数范围时保持未知；统计不完整时托盘提示同时包含“统计不完整 / Partial”状态。

**平均速度 · 估算（Average speed · estimate / TPS）** 使用当天合格完成回合的输出 token，除以同一组回合扣除首字延迟和可识别工具等待后的净处理秒数。首字延迟直接读取完成记录的 `time_to_first_token_ms`；工具等待以调用与返回记录的时间戳配对估算。每个回合将首字等待与工具等待区间先求并集，重叠或并行工具等待只扣一次；不同回合分别累计。

这仍是日志估算平均速度：TTFT 是整轮首 token 延迟，未必等同界面的可见首字，也不能还原工具返回后每次模型请求的首字等待；剩余时间仍可能包含推理、网络和调度。它不是实时或纯解码 TPS。运行中、缺少可靠时长或 TTFT、工具配对不完整/有冲突/归属不明、对账不完整、跨日以及净时间不为正的回合不计入；不会退回旧口径或把缺失等待视为零。展开“明细与统计范围”可见 TPS 纳入与排除的完成回合数；复制统计中列出已扣除的首 token 与工具等待并集时长。因此当日总输出不一定等于 TPS × Net processing time。

开始/结束事件与声明耗时相差超过 1 秒或声明耗时的 5%（取较大值）时，该回合不计速度。回合结束后收到的有效修订，仍按原始发生时间定位；只有最终 token 和回合快照能重新对账时才保留该回合 TPS。缺少真实回合内发生证据时，不臆造开始时间。

同一实例已观察到的唯一历史 start 即使暂时不在当前可读来源中，仍约束同 session/turn 的声明耗时资格。它只用于拒绝矛盾回合，不用于补出活动区间、TTFT、处理时长或 token。合法 token 小计和时长/TPS 的可用性分别判断。这是继承的 F1 资格修复；历史失败与修复记录仍保留，不能将旧版本结果改名为本版本界面验收。

**净处理时间（Net processing time）** 为纳入估算 TPS 的同一组回合扣除首字和工具等待区间并集后的时长之和。**活动时长（Active time）** 为裁剪到本地当日和当前时刻后的有效回合区间并集，重叠并发时间只计算一次，仍包含首字与工具等待。只有近期、该 root 最新且未被已知闭合或后继事实排除的开始事件，才可在 15 分钟观察资格内暂计至当前；超时后停止贡献该未闭合区间，没有其他有效区间时显示 `—`。这不是服务端仍在执行或人工工作时长的证明，历史摘要也不补出缺失活动区间。

**周额度日志快照（Weekly log snapshot）** 仅使用本机日志中有效的 10080 分钟窗口，显示剩余百分比 `100 − usedPercent`、采样时间和重置时间。有重置时间的快照到期失效；没有重置时间时仅接受最近 15 分钟的快照。它是历史快照，账号后续使用可能尚未写入。没有有效周窗口时显示“暂无有效周额度快照 / No valid weekly snapshot”；不能由 token 数推算。多额度桶出现歧义时不选择一个冒充整个账号。

## 命令行与隔离使用

日志 home 的优先级是显式 `--codex-home`、非空白 `CODEX_HOME`、用户目录下的 `.codex`。路径按启动时的工作目录解析为绝对路径。`--settings-dir` 只选择偏好设置，不改变日志 home。

| 参数 | 行为 |
| --- | --- |
| `--codex-home <目录>` | 明确选择日志 home，读取其中的 `sessions` 与 `archived_sessions` |
| `--settings-dir <目录>` | 明确选择主题/语言设置目录 |
| `--demo` | 使用合成演示数值，不读取真实日志；时间标签按当前时间生成 |
| `--render <PNG路径>` | 隐含 demo，按所选设置渲染后退出；现有入口固定 144 DPI 渲染，没有主题/语言/DPI CLI 开关 |
| `--snapshot <JSON路径>` | 导出数字快照后退出；可与 `--demo` 组合导出演示快照 |
| `--help` | 输出帮助，不解析 home、读取日志或加载设置 |
| `--version` | 输出构建版本后退出，不读取日志或加载设置 |

选项名区分大小写；超过64个参数、未知选项、缺少值或路径值以 `--` 开头时返回2。重复的带值选项使用最后一次值。

`--help` / `--version` 在上述语法检查通过后直接返回：不解析路径、不读取日志或设置，也不再检查 render/snapshot 互斥；两者同时出现时帮助优先。因此有效信息查询可以忽略不适用的路径或输出组合，但不能忽略未知选项和缺值。

非信息查询模式下，render 与 snapshot 不能组合；无效/空白路径返回2。路径规范化拒绝设备路径、保留设备名、文件流及不支持的字符等。该检查不证明目标存在或可写；实际读写失败另按执行结果处理。

| 退出码 | 含义 |
| --- | --- |
| `0` | 正常完成/退出，或收到既有普通实例的本地激活协议确认；协议确认不单独证明窗口已可见 |
| `1` | 执行失败，例如输出不可写、启动失败或已有实例无法激活 |
| `2` | 参数或路径无效 |
| `3` | snapshot 已输出，但首次读取及最多 64 轮补读后仍有积压 |

snapshot 返回 0 不要求 `IsPartial=false`，也不证明真实账号完整。输出文件的父目录必须已存在；render/snapshot 会覆盖指定的已有文件，请选择新路径。snapshot 失败时尽力写入异常类型；输出本身不可写时可能没有 JSON 文件，避免因此再次抛出写入异常。程序是 WinExe；从 PowerShell 观察退出码时可用 `Start-Process -Wait -PassThru`，不要把异步启动完成当作应用已退出。

普通 EXE 的合成隔离示例（先创建相应的物理目录）：

```powershell
.\CodexDaily.exe --codex-home .\synthetic-home --settings-dir .\synthetic-settings
.\CodexDaily.exe --demo --settings-dir .\synthetic-demo-settings
.\CodexDaily.exe --demo --snapshot .\synthetic-output\demo.json
.\CodexDaily.exe --help
.\CodexDaily.exe --version
```

普通端到端验收的第一条命令不带 demo/render/snapshot。应核对测试目录、子目录和祖先路径没有通过联接/符号链接指向真实数据。显式 home/settings 参数是路径选择方式，不是操作系统沙箱。render、控件夹具、snapshot 与普通 EXE 的监控→聚合→UI 链路分别提供不同证据，不能互相替代。

## 构建、升级与移除

产品源码固定 .NET SDK `10.0.402`，目标 .NET 10；自包含构建固定 runtime `10.0.13`。原九份 Core 源码和冻结 DLL 保留不变，历史字节核验单独使用 SDK `9.0.318`，不要求新产品 DLL 与旧 DLL 哈希相同。

源码构建脚本 `scripts/build-daily-panel.ps1` 要求干净的已提交源码和不存在的新输出目录。默认构建完整的框架依赖 Release、执行构建时回归并保存来源证据，**默认不生成 ZIP**；冻结后显式指定 `-Package` 和已核验的本地 Microsoft runtime feed 才创建自包含归档。完整源码检出中的 `docs/build-v020.md` 说明构建入口；该文档和开发脚本不随 ZIP 交付，不应在包内当作可打开的相邻文件。此说明兼用于源码与包内，因此不提供会随目标分支变化的远端验证链接。

升级时先从应用菜单正常退出旧实例，把新包完整解压到新目录，再启动新 EXE。不要覆盖仍在运行的目录；只复制 EXE 或混用 Core、DailyCore、面板 DLL 会失去构建身份保证。默认偏好设置位于解压目录外，因此新目录仍会读取同一份主题/语言设置；使用不同 `--settings-dir` 可独立保存偏好。

没有安装程序、后台服务或开机启动项。移除时先正常退出，再删除不再需要的解压目录。若也要清除偏好，另行处理 `%LOCALAPPDATA%\CodexDaily\settings.json` 或自己指定设置目录内的文件；删除程序目录不会自动清除外部偏好，日志 home 不属于卸载范围。自包含包内的 Microsoft .NET 许可随 `licenses` 目录保留。

本文说明使用方法与实现边界，不作为某次验收通过证明。按交付目录的 `BUILD-PROVENANCE.json` 和 `--version` 确认源码提交与构建身份，再到本项目 GitHub Releases 的对应版本及其关联 PR 记录查询实际验收摘要、附件、受测环境和限制；没有相应记录的事项不推定为通过。普通 EXE、真实 DPI、同实例日志变化、退出清理和持续资源观察应分别对应受测产物。合成测试和渲染不证明真实账号、自然午夜、所有多屏/混合 DPI、其他机器或长期稳定性。

历史修复验证期间，SDK 曾自动生成并安装 ASP.NET Core HTTPS 开发证书；后续禁用自动生成不代表该历史证书已清理。运行 dotnet/构建脚本前应设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`；现有构建脚本也设置该变量。没有历史证书撤销或清理证据；应用移除不会处理这些历史开发环境事项。不要将后续禁用自动生成描述为“没有系统变化”。

## English quick start

Run `CodexDaily.exe` from the complete Windows x64 delivery directory. Check `BUILD-PROVENANCE.json`: a self-contained delivery includes its runtime; a framework-dependent build requires .NET 10 Windows Desktop Runtime. Click the blue bar-chart tray icon to open the panel. Esc, outside-click and Hide only hide it; use **Exit panel** or the power button to quit.

Expand **Appearance and language** to select System/Light/Dark and System/简体中文/English. Preferences default to `%LOCALAPPDATA%\CodexDaily\settings.json`; `--settings-dir` selects another directory. Ordinary instances are scoped by current user and normalized log home: a second launch of the same home activates the existing panel.

**Pause automatic refresh** stops new automatic reads. In-flight reads may finish, manual refresh still works, and resuming requests a refresh. **Partial** means only the verifiable subtotal is available. **Stale** retains the original snapshot date and update time; it is not current usage. Unknown is not zero. Copy stats includes exact counts, dates, scope, estimated-TPS notes and current quality reasons.

Only local root sessions are counted. Child/cloud sessions and full account billing are excluded. Weekly allowance is a local log snapshot, not a live account query. Use `--codex-home` and `--settings-dir` together for isolated synthetic testing; `--help` and `--version` do not read logs/settings. Rendering and snapshots are auxiliary modes, not ordinary tray-app acceptance evidence.

This iteration's acceptance target is the local Windows 11 Insider 26220 machine. Other machines, non-Insider Windows and physical mixed-DPI/multi-monitor scenarios are deferred and unverified. This guide describes interfaces; it is not evidence of completed ordinary EXE, eight-hour or release acceptance. Historical SDK validation generated and installed an ASP.NET Core HTTPS development certificate; no revocation or cleanup is evidenced.
