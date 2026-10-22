# PR #3：child 历史与跨午夜活动依据的执行证据

本轮从任务 A 分支 `fix/log-coverage-loss` 的最新提交 `71753d82695f4634887f6a6010eb6164461511bb` 开始。开始时本地 HEAD、远端分支和 PR #3 head 相同，工作区干净；PR #1 仍 open、未合并，目标分支仍为 `e77c71ea536890ebd12a0c9905ac893d3a37db56`。没有切换起点、覆盖既有改动或修改任务 B。

## 输入身份与记录格式

红灯对应 **71753d8 的生产代码，加上 HIS-01/02/03A/03B 测试及入口注册**。绿灯对应 **修复后的未提交生产源码，加上全部 7 个 HIS 场景**。运行记录中的 `head` 是当时的父提交，不代表未提交输入已在该提交内；每次实际输入由独立 manifest 标识。

| 快照 | 输入 manifest 的 SHA-256 |
| --- | --- |
| 红灯 | `E5EE82D575BA1AD8038C486138592AF087EDDDEE7CC756A420DE4FE5082A97CC` |
| 全部最终绿灯及 Release 构建 | `FE4DCF1CE046F3E3D167F5A74AF1C153FA7C101E92E090CE7331DB2C25D5C1AC` |

每个 `.inputs.sha256` 按路径排序，逐行记录 `SHA-256  字节数  仓库相对路径`，覆盖 `src`、`tests`、`frozen-core` 的已跟踪及未跟踪文件，以及 `Directory.Build.props`、`NuGet.Config`、`global.json`。包含新测试、默认 Compile 源码、链接源码、项目、应用 manifest 与固定 DLL；不包含本目录或文档，避免证据自身导致输入哈希循环。

每次执行前后各生成一次清单；所有运行的 `.after.inputs.sha256` 都与对应前置清单相同，`inputs_stable=true`。最终六个成功检查的清单也互相相同。`.run.json` 保存完整 argv、实际 cwd、dotnet 路径、选中 SDK、指定的隔离环境变量、起止时间、立即捕获的原生命令退出码、输入及原始日志哈希、实际生成 DLL 的哈希。没有导出全量环境变量。

`.log` 是各次命令合并 stdout/stderr 的原始重定向文件，复制进仓库时未手工改写或追加结果。原始日志和元数据含本机工作树/临时目录路径。本目录 `.gitattributes` 禁止行尾转换，保留已记录的字节哈希；空白检查识别日志和运行 JSON 的原始 Windows CRLF。七个 `.log` 明确加入 Git，未复制其余临时文件。提交前另核对文件字节哈希与记录相同；源码提交后再核对提交输入与最终绿灯清单。JSON 记录和只读复审不替代实际运行结果。

使用 `C:\Program Files\dotnet\dotnet.exe`，在实际临时 cwd 选中的 SDK 为 **9.0.318**，详见 [dotnet --info](dotnet-info.txt)。从临时目录启动时不能声称仓库 `global.json` 决定了 SDK；记录采用实际选中版本。源码/项目未增加外部包或自定义构建任务；祖先目录未发现额外 `Directory.Build.targets`、`Directory.Packages.props`、MSBuild/Directory.Build rsp 或 `.editorconfig`。

## 红灯与最终结果

| 运行 | 对应输入 | 退出码与实际结果 | 原始日志 / 命令和哈希记录 |
| --- | --- | --- | --- |
| red-monitor | 红灯快照 | `-532462766`，HIS **0 通过、4 失败**；此前 COV 25 与 EXC 17 仍通过。新组统一抛出后，后续测试组未执行 | [日志](red-monitor.log) / [记录](red-monitor.run.json) |
| green-monitor-01 | 绿灯快照 | `0`，HIS **7/7**、COV **25/25**、EXC **17/17**；主组 11、工具 41、超大行 20、生命周期 8、legacy 11 项断言，以及 44 组逐字节切分验证全部通过 | [日志](green-monitor-01.log) / [记录](green-monitor-01.run.json) |
| final-core | 同一绿灯快照 | `0`，核心聚合与解析 **116/116** | [日志](final-core.log) / [记录](final-core.run.json) |
| final-formatting | 同一绿灯快照 | `0`，格式 **15/15** | [日志](final-formatting.log) / [记录](final-formatting.run.json) |
| final-popup | 同一绿灯快照 | `0`，弹窗几何与交互逻辑 **26/26** | [日志](final-popup.log) / [记录](final-popup.run.json) |
| final-app | 同一绿灯快照 | `0`，应用 Release 构建，**0 警告、0 错误** | [日志](final-app.log) / [记录](final-app.run.json) |
| final-gui | 同一绿灯快照及刚构建应用 DLL | `0`，GUI 合成夹具 **38/38**，`passed=true` | [日志](final-gui.log) / [记录](final-gui.run.json) / [夹具 JSON](gui-result.json) |

红灯四个具体结果：

- HIS-01：child 历史首现先被删除，再改 root，实际 input=300、output=110、`Partial=false`，预期独立 B 的 input=200、output=70、partial。
- HIS-02：child 历史 call 冲突先被删除，再改 root，实际处理秒数=6、TPS≈6.6667、`Partial=false`，预期当前 token 小计可知、时序未知、partial。
- HIS-03A / 03B：删除昨日已完成的后继来源后，实际 `ActiveTurns=1`、今日 `ActiveSeconds=60`、`Partial=false`。分别覆盖今日先读历史后删除，以及昨日已读、删除直到新日才观察到。

最终 HIS 场景验证删除、恢复及空闲轮询；另有 HIS-04 证明不同会话即使共用原始 response id，也不受休眠 child origin 影响；HIS-05 验证更晚原始启动可以正常提供活动状态和当前区间；HIS-06 验证十五分钟之后历史顺序依据休眠。恢复后的历史依据不提供旧 token 或 duration 数值。

## 实际命令

每次从同一专用临时根启动，`$taskRepository` 是本轮工作树，`$taskRunRoot` 是 `CodexDaily-historical-fd56bf93161946c9b9716d859613f75a` 临时根。以下为实际 argv 的等价 PowerShell 表示；绝对路径与每次参数以对应 `.run.json` 为准。

```powershell
$env:TEMP = Join-Path $taskRunRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $taskRunRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Set-Location -LiteralPath $taskRunRoot

# red-monitor 与 green-monitor-01 分别使用对应名称的独立 artifacts 目录。
dotnet run --project "$taskRepository/tests/CodexUsage.DailyMonitor.Tests" -c Release --artifacts-path "$taskRunRoot/red-monitor-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyMonitor.Tests" -c Release --artifacts-path "$taskRunRoot/green-monitor-01-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.Daily.Tests" -c Release --artifacts-path "$taskRunRoot/final-core-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyFormatting.Tests" -c Release --artifacts-path "$taskRunRoot/final-formatting-artifacts" --no-launch-profile
dotnet run --project "$taskRepository/tests/CodexUsage.DailyPopup.Tests" -c Release --artifacts-path "$taskRunRoot/final-popup-artifacts" --no-launch-profile
dotnet build "$taskRepository/src/CodexUsage.DailyPanel" -c Release --artifacts-path "$taskRunRoot/final-app-artifacts"
dotnet run --project "$taskRepository/tests/CodexUsage.Daily.GuiHarness" -c Release --artifacts-path "$taskRunRoot/final-gui-artifacts" --no-launch-profile "-p:ProductDirectory=$taskRunRoot/final-app-artifacts/bin/CodexUsage.DailyPanel/release" -- "$taskRunRoot/final-gui-output"
```

GUI 夹具源码在运行前已阅读：采用合成 `DailySnapshot` 和 `demo=true` controller，传入自己的不存在演示 home；不创建 monitor 读取真实 sessions，不调用账号接口。实际加载的是本轮 Release 构建的 DLL，夹具记录的 app/core SHA-256 与构建产物相符。只保存结果 JSON，合成界面 PNG 留在临时输出目录，不加入 Git。此结果不等同于多屏、真实账号或长期 GUI 验收。

## 边界

固定 `CodexUsage.Core.dll` 的 SHA-256 仍为 `6B3D5C61F342C68D3C356DE726341AE93DD1C7321E7384C3892D42618A363EEE`，Git blob 为 `1cfae06fb1aa69f4a34e2feb85296b577f900bd4`；沿用用户对该固定 DLL 隔离合成执行的明确授权。本轮没有替换 DLL、恢复 Core 源码或修改项目构建方案。

没有读取真实 sessions、auth、凭据或私人聊天；没有运行发布脚本、publish、打包、发布或合并。未验证真实 Windows ACL 变化时的半行连续读取、真实日志、规模性能或长期运行。原来的进程内保留、证明上限和头部/边界指纹检测限制继续有效。
