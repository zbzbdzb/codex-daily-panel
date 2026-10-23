# CodexDaily

Windows 托盘统计面板：查看本机 Codex 今日根会话的 token 用量、扣除首字和工具等待后的估算平均 TPS、活动时长，以及日志报告的周额度快照。

启动后驻留托盘，不占任务栏。点击蓝色柱状图图标展开面板；再次点击、点击外部或按 Esc 收起。托盘提示显示今日总 token 与估算 TPS（≈）。

## 环境要求

- Windows x64。
- 产品构建：PowerShell 7 与固定的 .NET SDK 10.0.402 / runtime 10.0.13；历史 Core 字节核验独立使用 SDK 9.0.318。
- 正式自包含 win-x64 ZIP 运行时无需另装 .NET；框架依赖开发构建需要 .NET 10 Windows Desktop Runtime。

产品没有外部应用库依赖，`NuGet.Config` 默认清空包源。v0.2.0 候选构建将完整源码提交写入版本；直接开发构建标为 `0.2.0-local`。

## 构建与分层验证

先提交并保持工作区干净，再从仓库根运行；输出目录必须不存在：

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 `
  -DotNetExecutable <SDK10-dotnet.exe> `
  -LegacyDotNetExecutable <SDK9-dotnet.exe> `
  -OutputDirectory <new-candidate-directory>
```

脚本从当前提交导出纯源码，在隔离环境构建完整 Release，运行所有逻辑测试、历史 Core 字节比较与候选 DLL 的 GUI harness，并保存命令、退出码、编译输入和产物哈希。默认不创建 ZIP。冻结后显式使用 `-Package -RuntimeFeed <verified-local-feed>` 才构建自包含 win-x64 归档；不会推送或发布。全部参数、清单与验证边界见 [v0.2.0 构建文档](docs/build-v020.md)。控件夹具不替代普通 EXE 的日志读取、托盘和退出验收。

历史 SDK9 验证独立运行：

```powershell
pwsh -NoProfile -File scripts/verify-core-build.ps1 `
  -DotNetExecutable <SDK9-dotnet.exe> -OutputDirectory <new-historical-directory>
```

## 运行

解压 ZIP，运行 `CodexDaily.exe`。路径优先级为显式 `--codex-home`、非空白的 `CODEX_HOME`、用户目录的 `.codex`；读取选定目录下的 `sessions` 和 `archived_sessions`。

```powershell
./CodexDaily.exe --codex-home <目录>
./CodexDaily.exe --demo
```

面板提供复制统计、刷新、打开日志目录和退出操作，通常每 10 秒自动刷新，读取积压时每秒继续。托盘菜单的暂停只停止自动触发，不取消在途刷新；手动刷新仍可用，恢复会请求刷新。隔离验收应显式指定仅含合成日志的新目录。详细参数、容量和历史证据边界见[使用说明](docs/daily-panel-usage.md)。

## TPS 口径

对同一组可核验的完整完成回合：

```text
净处理时间 = 回合总耗时 − 首 token 等待与工具等待区间的并集
估算平均 TPS = 对应回合输出 token 总量 ÷ 净处理秒数总量
```

TTFT 直接读取整轮日志字段，工具等待以调用与返回的时间戳配对。并行工具和首字等待的重叠区间只扣一次。缺失、冲突或无法归属的记录，以及非正净时长，不参与估算；不会回退到包含等待的旧口径。面板显示纳入和排除回合数。

整轮首 token 未必等同可见首字，也不能还原每次模型请求的延迟；剩余时间可能包含推理、网络和调度。这是日志估算平均速度，不是实时或纯解码 TPS。今日 token 总量仍统计全部有效请求，活动时长仍包含等待。

## 数据范围与隐私

仅统计本机日志中可识别的根会话，不汇总子代理、云任务、其他机器或账号完整账单。周额度是本地日志中的历史快照。

程序只读打开日志，只保留允许的数值、时间戳和哈希身份，不读取认证文件、不进行账号查询、不保存聊天正文或工具参数/输出，也不写回 Codex。仓库包含合成测试输入、执行记录及部分本地路径和环境信息；产品的只读行为与这些验证材料的内容分别核对。

未知统计显示 `—`，已知为零才显示 `0`。`No data` 不代表没有用量；`Partial=false` 只表示当前可见范围没有触发对应完整性提示，不能证明账号账单完整。历史证据只在当前 monitor 实例中保留，无法识别启动前已经丢失的全部来源。

## Core 源码与冻结基准

面板和合成测试通过 `ProjectReference` 编译仓库内的 `src/CodexUsage.Core`。`frozen-core/CodexUsage.Core.dll` 保留为不变的历史核对基准，SHA-256：

```text
6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE
```

恢复的 9 份原始 C# 文件逐字节保留。历史 SDK9 配置隔离在 `historical/core-sdk9`，它的两次独立输出须与冻结基准逐字节一致；产品 SDK10 则从原源码重新编译，不要求新 DLL 维持旧哈希。该结论限于 Core DLL，不代表发布 ZIP、WPF 程序或其他环境的全部产物均已证明可复现。第三方参考与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 `licenses/`；其中旧集成参考的署名一并保留，不代表本面板启用那些集成。

## 验证范围

构建脚本验证统计、读取边界、超大 JSON/UTF-8、时间归属、重叠等待、托盘几何与点击状态及合成 WPF 控件布局。普通 EXE、实际 DPI 与资源观察的版本和结果另行记录；上述自动化不替代真实多屏混合 DPI、拔屏或长期稳定性验收。本次 PR 的修复、回归与证据边界见[PR #1 最终验证记录](docs/pr1-final-validation.md)。
