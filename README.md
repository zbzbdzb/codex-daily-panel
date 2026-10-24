# CodexDaily

Windows 托盘统计面板，用于查看本机 Codex 今日根会话的 token 小计、扣除首 token 与工具等待后的估算平均 TPS、活动时长和周额度日志快照。

## 版本、来源与验收记录

实际交付身份由同目录的 `BUILD-PROVENANCE.json` 和 `--version` 标识。先确认版本、源码提交、tree 与交付类型，再在本项目 GitHub Releases 的对应版本及其关联 PR 中查询验收摘要、构建与成员哈希、CI 结果和已知限制。源码说明、候选目录或标签名称本身不代表验收或发布通过；不同提交、tree 或包的结果不能直接互用。

对应发布的最终结果由 PR / Release 文本和附件记录。仓库中的[3a16 历史候选记录](docs/v020-validation-status.md)固定描述该候选及其当时状态，不追写后续候选的验收进度。

## v0.2.0 接口与界面

面板提供以下功能；受测范围和实际交互结果请核对对应交付的验收记录：

- 主题选择跟随系统、浅色或深色；语言选择跟随系统、简体中文或 English，保存主题与语言偏好。
- 总 token、输入/输出、估算速度与净处理时间、周额度快照分层显示；明细和范围可展开，小工作区可滚动，底部操作保持独立。
- 读取/暂停/退出与 Partial/无数据/过期分开显示。刷新失败保留原快照日期与更新时间；新的 Partial 快照显示新的可核验小计。
- 暂停停止新的自动触发，在途读取可完成；手动刷新仍可用，忙时重复手动请求合并成至多一次后续刷新，恢复会请求刷新。
- 普通模式按当前用户及规范化日志 home 限定实例；同 home 再次启动发送本地激活请求。不同设置目录不能绕过实例限制。
- 增加 `--settings-dir`、`--help` 和 `--version`；保留既有 demo、render、snapshot 与数字字段合同。

详细操作、准确参数规则及默认设置位置见[使用说明](docs/daily-panel-usage.md)。

## 环境与启动

产品目标为 Windows x64、.NET 10。框架依赖构建需要 .NET 10 Windows Desktop Runtime；自包含 win-x64 交付携带其运行时。先检查 `BUILD-PROVENANCE.json` 的 `selfContained`，不要仅凭 EXE 文件名或许可文件判断。完整保留同一交付目录中的 EXE、DLL、运行时与说明，避免混用不同构建。

支持目标与已验证环境需分别核对。对应版本的 PR / Release 验收记录应列明 Windows 版本、显示比例、机器及多屏范围；未列明的环境不推定为已经验证。

日志路径优先级是显式 `--codex-home`、非空白 `CODEX_HOME`、用户目录下的 `.codex`。普通模式读取选定 home 的 `sessions` 和 `archived_sessions`。合成隔离示例中的目录须提前新建，并检查目录及祖先无指向真实数据的链接：

```powershell
.\CodexDaily.exe --codex-home .\synthetic-home --settings-dir .\synthetic-settings
.\CodexDaily.exe --demo --settings-dir .\synthetic-demo-settings
.\CodexDaily.exe --help
.\CodexDaily.exe --version
```

启动后驻留托盘；点击图标展开，Esc、点击外部或隐藏按钮收起。退出使用电源按钮或托盘菜单。只读日志不会写回 Codex，但偏好设置会写入专用设置目录；demo 也会读取所选设置，隔离演示应传入设置目录。

## 数值含义与限制

token按请求身份去重和有效修订统计，输入包含缓存、输出包含推理，不能再次加上这两个子集。总 token 为输入与输出之和；未知显示 `—`，已知为零才显示 `0`。No data 不证明没有用量。

对同一组合格完成回合：

```text
净处理时间 = 回合总耗时 − 首 token 等待与工具等待区间的并集
估算平均 TPS = 对应回合输出 token 总量 ÷ 净处理秒数总量
```

TTFT来自整轮日志字段，未必等同可见首字；工具等待按可靠配对的时间戳估算，重叠只扣一次。缺失、冲突、不可归属或非正净时间不纳入；剩余时间可能包含推理、网络和调度，不能称实时或纯解码TPS。

活动时长是有效区间在本地今日及当前时刻范围内的并集，包含等待，不证明后台持续运行或人工工作时长。周额度只是本机日志快照，不查询实时账号额度。只汇总可识别根会话，不包括子代理、云任务未落地数据、其他机器或完整服务端账单。

缺失依据仍影响当前统计时保留 Partial；证据恢复或不再影响当前统计后解除对应提示。其他原因仍可使整体保持 Partial；Partial=false也不证明账单完整。历史资格依据只在当前monitor实例中保留，不回填缺失数值或活动区间。

## 构建与来源

源码固定 SDK10.0.402，构建检查所选主机含.NETCore/WindowsDesktop10.0.13；产品与历史Core构建分开。先提交并保持干净工作区，使用全新输出目录：

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-candidate-directory>
```

默认得到框架依赖Release及构建时证据，不生成ZIP。最终自包含构建使用显式 `-Package -RuntimeFeed <verified-local-feed>`，不允许SkipTests。脚本不上传或发布。细节见[构建与来源](docs/build-v020.md)。

九个Core原始C#文件及冻结DLL保留原字节；SDK9.0.318的历史两次重建与冻结值比较，.NET10产品从源码另行编译，不要求新DLL等于旧哈希。第三方来源及随包许可见[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，历史可选集成参考不表示当前面板调用那些工作流。

历史验证曾由SDK自动生成并安装ASP.NET Core HTTPS开发证书，没有撤销或清理证据。后续设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` 不抹去该事件；运行dotnet前应禁用后续自动生成。后续禁用不能被描述为此前没有系统变化。

## 验证记录的使用

逻辑测试、辅助WPF、渲染矩阵和普通EXE分别提供不同范围的证据，不能互相替代。验收记录应把实际剪贴板/托盘、物理DPI、数值退出码及持续资源观察绑定到受测提交、产物哈希和时间；限定时长的观察不能证明长期无泄漏。PR #1 及[3a16 历史候选记录](docs/v020-validation-status.md)保留原版本的修复、失败与证据边界，不作为其他版本的验收证明。

源码、包内说明、许可与完整交付须在最终构建和8小时观察之前冻结。后续验收摘要、合并/tag和下载复核结果写入对应 PR / Release 及附件；不为追记进度修改已经受测的源码树或归档。
