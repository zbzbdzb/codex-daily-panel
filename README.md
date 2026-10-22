# CodexDaily

Windows 托盘统计面板：查看本机 Codex 今日根会话的 token 用量、扣除首字和工具等待后的估算平均 TPS、活动时长，以及日志报告的周额度快照。

启动后驻留托盘，不占任务栏。点击蓝色柱状图图标展开面板；再次点击、点击外部或按 Esc 收起。托盘提示显示今日总 token 与估算 TPS（≈）。

## 环境要求

- Windows x64。
- 构建：PowerShell 7 与固定的 .NET SDK 9.0.318，需具备 .NET / Windows Desktop targeting packs。
- 运行：.NET 9 Windows Desktop Runtime。

项目不依赖外部 NuGet 包，`NuGet.Config` 清空包源。当前版本为 `0.1.0-daily.20261021-r5`。

## 构建与完整验证

在仓库根目录执行，输出目录必须尚不存在：

```powershell
pwsh -NoProfile -File scripts/build-daily-panel.ps1 -OutputDirectory artifacts/CodexDaily-local-win-x64
```

脚本先在两个全新输出路径重建 Core，并逐字节核对冻结基准，再运行聚合、日志读取、格式和弹窗测试，发布程序，并针对发布后的 DLL 运行真实 WPF 控件验收。它在相邻的 `.verification` 目录保存构建、测试、界面渲染与文件哈希证据，最后生成 ZIP。已有交付不会被覆盖。

只检查 Core 的源码构建与字节一致性，不运行面板、业务测试或发布流程：

```powershell
pwsh -NoProfile -File scripts/verify-core-build.ps1 -OutputDirectory artifacts/Core-rebuild-local
```

该目录也必须尚不存在。源码来源、全部文件哈希、构建配置及本次实际验证结果见[Core 源码与可复现构建](docs/core-source-build.md)。

## 运行

解压 ZIP，运行 `CodexDaily.exe`。默认读取 `CODEX_HOME` 下的 `sessions` 和 `archived_sessions`；未设置时使用用户目录的 `.codex`。

```powershell
./CodexDaily.exe --codex-home <目录>
./CodexDaily.exe --demo
```

面板提供复制统计、刷新、打开日志目录和退出操作，每 10 秒自动刷新。详细参数见[使用说明](docs/daily-panel-usage.md)。

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

程序只读打开日志，只保留允许的数值、时间戳和哈希身份，不读取认证文件、不进行账号查询、不保存聊天正文或工具参数/输出，也不写回 Codex。仓库不包含真实日志、快照、认证配置或本机诊断证据。

## Core 源码与冻结基准

面板和合成测试通过 `ProjectReference` 编译仓库内的 `src/CodexUsage.Core`。`frozen-core/CodexUsage.Core.dll` 保留为不变的历史核对基准，SHA-256：

```text
6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE
```

恢复的 9 份原始 C# 文件在固定 SDK 和 Release 配置下可重建出与该 DLL 字节一致的产物。源码逐字节保留；Core 的 Release 配置关闭调试信息，构建核验脚本会检查两次输出及历史基准。该结论限于 Core DLL，不代表发布 ZIP、WPF 程序或其他环境的全部产物均已证明可复现。第三方参考与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 `licenses/`；其中旧集成参考的署名一并保留，不代表本面板启用那些集成。

## 验证范围

发布脚本验证统计、读取边界、超大 JSON/UTF-8、时间归属、重叠等待、托盘几何与点击状态、发布版 WPF 布局和显示。它不替代真实多屏混合 DPI、拔屏或长时间运行验收。
