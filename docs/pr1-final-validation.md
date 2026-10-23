# PR #1 修复与最终验证记录

本记录核对 2026-10-23（UTC+8）的冻结候选、独立代码审查和普通 EXE 验收。最终 GitHub 合并状态、合并提交及检查状态由 PR 页面和总审查的合并记录给出；下列本地执行结果不自行表示远端已合并。

## 版本与范围

PR #1 的源分支是 `codex/statistics-integrity`，目标是 `codex/initial-release`。已核实的远端 A/B 集成 head 为 `560382e4637b9b5ae813f3352a06d9acea4bcfe2`，与本地 `3516ea4217408267791d7ddebe11f1109bdd7a31` 同树。D1–D3/R1 本地修复为 `581e5357a1ac6855ad7ea7b2dc84afe3effb3121`；F1 修复为 `1efc3333d3627c9b98121db829b2042810ab2e1d`，tree `0dfa96a66e81a58d244c65cc3172a5ee5cf53546`。

本次总审查的本地集成提交为 `a6d53b78fb087516cf57448a7c021184995f68c6`，父提交为远端 head 和 F1 修复，代码树与 F1 修复完全相同。此前所有原始报告、红色测试及旧版本验收均保留；本文件不会把旧版本结果自动转移到新版本。

代码范围包括 legacy 覆盖完整性、A 的增量/丢源证据维护、B 的 Core 源码构建，以及下列修复。Core 冻结 DLL 本身不变；Daily 通过源码项目引用构建。

| 问题 | 修复与证据范围 |
| --- | --- |
| D1 | 临时不可读时保留已观察的分类、首现与闭合资格事实，不回填旧统计值 |
| D2 | 丢源、child/root 转换后的已知 abort/completion/多 start 冲突继续约束耗时资格 |
| D3 | 已知请求向量冲突在丢源、转 root 和逆序恢复后仍约束请求资格 |
| R1 | 公开 monitor 的 active/queued refresh 与 Dispose 交错安全退出，sources/reader 清理有屏障控制的断言；不据此声称普通 EXE 的退出曾有相同故障 |
| F1 | 唯一历史 start 继续排除与声明 duration 矛盾的回合；不赋给 explicitStart，不补活动区间、处理时间、TTFT 或 token，合法 token 小计保留 |

## F1 原始失败与修复结果

在 581e5357 的指定合成条件下，同一 monitor 已见唯一矛盾 start，随后该来源丢失、child 转 root。旧结果错误接受 9 秒净处理、TPS=40/9、TimedTurnCount=1、IsPartial=false。原 60 项断言为 56 通过、4 失败，exit1；其中 10 项 token 检查通过。这是问题证据，不是通过的回归；没有更早版本执行比较，不能据此认定引入版本。

1efc3333 的开发者自测原样复用了 F1 程序，Program.cs SHA-256 均为 `62812D0B171B67A07E0BFA3FD641E415D3999D34A89964798096E2D3E15CD68B`。原 60 项全部通过，10 项 token 检查仍通过，exit0。新增 15 个聚合场景覆盖容差内外边界、等价时区、不相关 session/turn、原因/排除数、idle 和禁止活动回填；新增 monitor 回归为 2 个连续阶段场景、84 项断言。

独立审查者已从冻结提交导出源码，独立执行原 60 项 F1，结果为 60/60、token 10/10、exit0。另补 3 组组合、90 项断言，全部通过，覆盖混合回合计量、转回 child、相容时区和缺失当前 completion；独立报告和 377 个文件封存已完成，总审查逐项复核 0 差异。原失败证据不覆盖、不放宽断言。

为保持原 harness 字节不变，其 JSON 中写死的 `BaselineCommit=581e5357...` 仍保留，表示原问题基线。此次运行版本由冻结源码 Git blob、命令的 SourceManifest、编译来源及产物哈希证明，不能把该旧标签当作本次执行了旧产品。

## 最终候选构建与回归

以下为 1efc3333 的实际执行及总审查只读复核，不冒称总审查重新运行：

| 检查 | 结果 | 粒度 |
| --- | --- | --- |
| 完整 Release | exit0，0 warning / 0 error | 当前 Core、DailyCore、Panel 源码一起构建 |
| Daily.Tests | 131 PASS，exit0 | 含 15 个新增 F1 聚合场景 |
| DailyMonitor.Tests | 166 PASS，另 2 个 R1 DONE，exit0 | 含既有 17 个 D1–D3 case 和新增 2 case / 84 断言；不是 168 个同粒度测试方法 |
| Formatting.Tests | 15 PASS，exit0 | 数值与复制格式 |
| Popup.Tests | 26 PASS，exit0 | 几何及交互纯逻辑 |
| 原 F1 harness | 60/60，token 10/10，exit0 | token 10 项已包含在 60 项内 |
| WPF 辅助夹具 | 38/38，exit0 | 引用同一新 Release，不能替代普通 EXE |

197 个 tracked 源文件与提交 Git blob、长度及 SHA-256 全部匹配。各执行日志 hash、退出码和禁用证书环境已复核。完整 Release 的产品 SHA-256：

| 模块 | SHA-256 |
| --- | --- |
| CodexDaily.exe | `8721C7DFC37FF69B0345BAAA7D2132D2B22F5E3F7EEF7A16B69C0E2424338358` |
| CodexDaily.dll | `4D2281C9206136BBF74F6697BD46B09DC946C4DBDE876742BC6E569063E7EEA3` |
| CodexUsage.DailyCore.dll | `D189F0C77E2FCFD29296E95447F71548C178ADD19C32FE1F80A992F03FD9070B` |
| CodexUsage.Core.dll | `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE` |

apphost 和冻结 Core 可保持同字节，不能只凭 EXE hash 判断版本。验收需要完整目录、源输入、实际加载模块路径和业务 DLL 一起对应，不可混旧 EXE 与新 DLL。

## 普通 EXE、DPI 与资源

最终普通 EXE 是完整 Release 中的 PID 57496，启动 ticks 为 `639283350469372911`。从 14:50:46.9372911 到 15:20:07.4841027，系统进程时间约 29 分 20.55 秒。显式 `--codex-home` 指向新物理合成目录，同一实例完成下列步骤；未用 demo、render 或 snapshot 新进程代替。

| 实际普通 EXE 验收 | 结果 |
| --- | --- |
| F1 丢失唯一 start、child→root、恢复、再转 child | root 时保留 100 输入、40 输出、140 总 token；TPS/净时间不可用、Partial、0 纳入/1 排除。恢复仍排除矛盾耗时；再转 child 后对应统计和提示退出当前范围 |
| 正常数据与无关来源锁定/恢复 | 正常 140、TPS 4.44、净时间 9 秒；无关来源锁定时保留这些可核验数值并显示 Partial，恢复后解除对应提示 |
| child 标记锁定及 root 恢复 | 锁定 child 来源不回退推定为 root；后续 root 元数据恢复正常统计 |
| 暂停、追加、恢复 | 实际暂停后追加第二请求，超过两个自动周期仍为 140 且 Updated 不变；恢复后 280、TPS 4.44、净时间 18 秒 |
| 闭合来源锁定/恢复 | 280 token 保留；缺失当前闭合依据时 TPS/净时间/活动不可用、active 0，显示对应 Partial；恢复后正常 |
| 可见值与复制 | 17 个功能阶段保存 UIA、实际 Copy 结果、动作记录及目标窗口 PNG；格式预期纠正后离线核验 505/505、exit0 |
| 托盘/窗口、手动刷新 | 两组各 15 次真实打开/复制/刷新/隐藏，共 30 个完整循环、60 次复制验证；固定合成输入前后路径、大小、mtime、哈希一致 |
| 刷新期间退出与清理 | 实际观察 Refreshing 且刷新按钮禁用后约 6.3ms 调用电源按钮；原启动器持有进程句柄，取得 exit0。独立核对进程、所属窗口及实际展开的通知区中目标图标均不存在 |
| 当前 DPI | 单屏 3840×2160，工作区 3840×2088，DPI144/150%，窗口690×960在工作区内；代表性 PNG 已人工读取，不外推物理混合 DPI |

本轮原始离线 UI 核验实际为 **499/505、6 项失败、exit1**。六项均错误预期可见净时间为 `9s`，冻结格式合同和实际 UI 为 `9.0s`；复制文本 `9.00 seconds` 已通过。新的外部脚本仅修正该字面量与输出文件名，重算同一原始证据得到 **505/505、exit0**。原失败和原脚本保留，没有放宽产品规则或修改产品。早期 UI 驱动的隐藏托盘/组合 tooltip 定位、暂停时窗口遮挡，以及只读汇总命令读错退出文件名等工具错误也保留；不能写成所有命令一次全绿。

本候选独立保存 **350 个资源样本**，样本跨度 29 分 12.83 秒；104 可见、246 隐藏，未采到 responding=false。两组固定输入循环分别为 78.19/79.47 秒，随后隐藏稳定观察分别为 **234.17/326.04 秒**。782 条离线资源/动作条件通过；总审查另读原始样本、循环事件和 129 文件清单复算，无差异。

| 两稳定段末 60 秒最低值 | stable1 | stable2 | 差值 |
| --- | ---: | ---: | ---: |
| private bytes（MiB） | 119.70703125 | 122.52734375 | +2.82031250 |
| working set（MiB） | 175.30468750 | 178.12109375 | +2.81640625 |
| handles | 827 | 817 | -10 |
| GDI / USER | 34 / 41 | 34 / 41 | 0 / 0 |

两个稳定段内部存在时间有序的局部内存下降，但第二段仍残留约 2.82 MiB 抬升。只有两组交互及 5 秒采样，无法区分缓存/分配高水位、延迟回收或潜在慢泄漏；本次没有确认泄漏，也不宣称无泄漏。此风险作为后续观察项保留，未据此虚构资源阈值或逐周期斜率。

原资源报告 `maxToEndTailMinimumDrop` 是全段最大值减末60秒最小值，没有要求最大值先出现，不能将其统一解释为“峰后释放”。原件保留，最终解释采用独立纠正报告。退出前 96 MiB 合成负载仅用于观察刷新和退出，未验证全量处理、吞吐或瞬时峰值。界面忙态观察和退出 Invoke 非原子，不外推具体 I/O 在调用瞬间仍必然执行。

修复前 581e5357 的普通实例 PID47596 已有 954 文件封存，均核对一致；正常退出码0，进程、窗口、目标托盘消失。原离线聚合断言是 582 通过、2 条严格等时失败、2 项可选 UIA 证据缺失，整体通过字段仍为 false。后续 40 条精度补证及独立源码/截图/时间线核验将两条失败解释为 UIA→Copy→PNG 非原子采集跨过正常刷新；不是 40 个新产品测试，也没有把原 exit1 改成 exit0。

该旧轮 01/02 没有同阶段 UIA/PNG，使用实际人工复制；04 首次 Copy 失败保留。1035 个资源样本覆盖 86分28.452秒混合操作，末值较启动高且有回落，不能证明泄漏或无泄漏；96 MiB 退出负载刷新到退出期间没有资源样本。上述旧轮证据不替代最终 F1 候选验收。

## 可复现入口与边界

仓库使用固定 SDK 9.0.318、无外部 NuGet 包。运行前须已有相应 SDK/Windows Desktop targeting packs，不自动安装依赖。每次选择新的输出目录，并在任何 dotnet 前设置：

```powershell
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
```

完整脚本入口为 `scripts/build-daily-panel.ps1 -OutputDirectory <新目录>`；Core 字节核验入口为 `scripts/verify-core-build.ps1 -OutputDirectory <新目录>`。四套回归对应 `tests/CodexUsage.Daily.Tests`、`CodexUsage.DailyMonitor.Tests`、`CodexUsage.DailyFormatting.Tests`、`CodexUsage.DailyPopup.Tests`，可在新的 `--artifacts-path` 下用 `dotnet run --project <csproj> -c Release --no-launch-profile` 执行。新增 F1 场景随前两套运行；监控合成输入可通过 `CODEX_DAILY_INTEGRITY_INPUTS` 保存到新目录。普通 EXE 验收另用显式隔离合成 home 和完整 Release，不能用辅助 snapshot 代签。

精确命令、环境、输入版本、输出、哈希、图像及资源数据保留在本地专用证据目录。以下目录名相对于本次本地审查的 `F:/Documents/ChatGPT`，原始证据未新增上传到 GitHub；清单 hash 可核对交接副本。本记录不宣称所有机器或所有日志组合都覆盖。

| 证据目录与主文件 | 完整清单 / SHA-256 |
| --- | --- |
| `codex-daily-panel-f1-candidate-20261023-143856/ACCEPTANCE-REPORT.md`：完整 Release、回归、17阶段普通 EXE、资源与退出；`SEAL.json` 确认 writer 停止 | `EVIDENCE-MANIFEST.json`，1690 文件逐项及文件集合复核 0 差异；`7E0854289F4B8D57C6C0F6A08577BA02B777E62DEF91D790CD01C930F748FD07` |
| `codex-daily-panel-code-review-20261022/f1-independent-1efc3333-20261023-145120-357/independent-review-report.md`：源码对照、原 F1、3组组合回归 | `evidence-seal.json`，377 文件复核 0 差异；`23B886324D0532DC91068EE12D3FEE837D457EC46B30D9063A6D127874F3384A` |
| `codex-daily-panel-581e5357-acceptance-20261023/ACCEPTANCE-REPORT-final.md`：历史普通 EXE，非最终候选 | `EVIDENCE-SHA256.json`，954 文件复核 0 差异；`3BDE1CF58E078548A6DFF62AAF3B9B05F7C3ED0F74065E8699BD6B75B0CD04D9` |
| `codex-daily-panel-code-review-20261022/f1-validation-20261023-130010-667/f1-validation-report.md` | 原 56/4 红色结果、run/command.json、stdout.txt、results.json 与 verification.json 保留 |
| `codex-daily-panel-global-review-20261023/f1-authorized-20261023-1433` | 保存授权、集成树、源码/产物/封存及资源复核记录，并保存已完成的合并前门禁快照；最终上传 head/base、检查和合并结果若后续产生，应另行存档并重新核对。当前文稿不预签这些结果 |

最终待合并材料提交只允许更新 README、使用说明及本记录；合并前核对除这三份文稿外的所有 Git 路径与受验收候选一致，并核对目标分支集成结果。以相同源码及构建输入复用本轮执行证据，不把文稿提交误称为另一次全部重跑。

未执行或受限：真实账号/真实会话、自然午夜、物理多屏混合 DPI、拔屏、长期稳定性、其他机器。已测的合成日期和几何逻辑不能消除这些边界。最终是否阻塞按实际影响判断，不承诺零缺陷。

## 仓库检查与历史环境事件

2026-10-23 15:31–15:33（UTC+8）的 GitHub 查询无 Actions workflow、check run、commit status、正式 PR review 或 review thread，目标分支无保护规则；空 status 的合成状态显示 `pending`，并不存在一个实际在运行的检查。没有 CI 不等于通过，本次依据上述可复现本地证据。合并前仍须对最终上传 head 重新读取精确 head/base、检查/审查/保护规则和目标集成结果。此记录没有宣称远端已经包含本地修复或 PR 已合并。

历史修复验证的 SDK 原始输出记录曾自动生成并安装 ASP.NET Core HTTPS 开发证书。后续禁用自动生成不等于此前证书已撤销或清理，没有清理证据；本次审查未访问或修改证书存储。该历史副作用继续披露。
