# release12 掉落终态与真实芯片机制修复

本轮所有源码、产物、隔离运行目录和测试存档均位于本仓库。未修改 `D:\steam\...`，也未替换仓库外 A/B 实例的 DLL。保留版本号 `5.5.0-cu-port`，产物位于 `build/release12-ship`、`build/release12-playtest`。

## 根因与对应代码

### 掉落恢复循环

修改前源码备份在 `build/release12-source-before`，可审阅差异在 `build/release12-review.diff`。

1. 原 `Net/ScavLiteEntitySystem.cs:1640` 只在 `WriteObjectIntoPacket` 内从原版组件采样。正剩余时间通过 `CeilToInt(timeLeft * 10)` 且下限为 1 编码，因此最后一个正值必定是 `true/1`，即日志的 `0.1s`。它不是一条新的“重新开始悬浮”事件。
2. 原同文件 `1193` 的服务端实体 Update 没有更新这两个字段；原 `Patches/ItemPatches.cs:246` 的销毁补丁也只清理重力/特效，没有发布 `false/0`。终态依赖下一次物品快照。一旦物品 tracker 停更，最后一个正值可以一直保留；父节点导致原版组件结束时也缺少立即发布终态的入口。仅凭用户提供的客机两行日志不能区分旧现场具体由哪一种停更时序触发。
3. 原同文件 `2248–2254` 每次处理同一份快照都会重新赋值 `fresh.timeLeft`；组件不存在时无条件 AddComponent 并 Info 打印。高帧率时不断回拨到 0.1；长帧让原版 Update 把计时减到负数、延迟销毁后，下一轮又添加组件并打印，形成循环。原版行为见只读反编译 `F:\cu-port\decomp\fullgame\FreshItemDrop.cs:47` 的 Update。

修复入口（以下为当前源码行号）：

- `Net/ScavLiteEntitySystem.cs:1194`：仍为 fresh 的实体独立发布寿命，不再依赖 tracker 的完整快照。
- `Net/ScavLiteEntitySystem.cs:2223`：统一有效状态判断；原版不允许悬浮的父节点立即视为结束。
- `Patches/ItemPatches.cs:248` → `SyncInfo.EndFreshDrop:2232`：原版组件销毁时立即发布 `false/0`。
- `Net/ScavLiteEntitySystem.cs:2238`：客户端按实体和 mesh 保存一次恢复状态；同一 true 状态最多添加一次。相同剩余时间不再重置计时，新剩余时间只能缩短当前寿命。收到 false/0 时销毁并重新允许下一次 false→true。
- `Net/ScavLiteEntitySystem.cs:2260`：恢复日志仅在实际添加时、且 `log.verbose` 开启时打印；来源及服务端 Start 诊断也改为 verbose。Ship 不包含测试探针。

保留原版 10 秒寿命、末两秒重力渐变、阻尼、轮廓/粒子和客户端 `forceOn`；保留已修好的最近存活玩家掉落来源判定。

### 无芯片只有元数据，且入场流程会覆盖真实状态

原 `Compatibility/ChipState.cs:36` 的 ApplyServer 只写 `modUnchipped` 和 BasePlayer 元数据。它没有进入 `Player.cs:907 SetUnchipped`，因此 `NetBody.unchipped`、本地 `WorldGeneration.unchipped` 和原版 UI 不随选择改变。

此外，原 `SyncSystems/WorldMP.cs:812` 在生成结束时用旧世界值重写本地玩家；原 `872`、`910` 接受入场消息中的旧客户端值。只在面板处补一次调用仍会被这些路径覆盖。

修复入口：

- `Compatibility/ChipState.cs:18`：统一 `ApplyGameplay`，保证 `UnchippedIsIndividual=true`，调用 `ScavPlayer.SetUnchipped`，由已有入口更新 NetBody 与本地原版 UI。
- `Compatibility/ChipState.cs:42`、`Compatibility/CharacterLobby.cs:194`：服务端确认和客户端同步均真正应用。
- `Content/NetBody.cs:434`、`970`、`1122`：创建/绑定身体时应用，并在服务端初始 SyncBody 中写入选择。
- `SyncSystems/WorldMP.cs:813`、`882`、`926`：生成完成和两个入场消息服从服务端个人选择。
- `Extras/PlayerCheckpoint.cs:170`、`ServerMain.cs:96`：文件恢复及进程内重连恢复后保持个人选择；恢复远端身体时也恢复主机自己的 UI。

继续使用原有 `CharacterRunMemory` 的存档 seed + SteamId（直连用姓名）四列表。旧三列表默认有芯片。不改消息编号、SyncVar 布局、角色绑定锁定规则。`ChipState.IsUnchipped` / `HostIsUnchipped` 接口保留，`EndingFlow.EnableEscape=false` 保留。

## 改动清单

生产文件：`Compatibility/ChipState.cs`、`CharacterLobby.cs`、`FreshDropRelevance.cs`、`Net/ScavLiteEntitySystem.cs`、`Patches/ItemPatches.cs`、`Content/NetBody.cs`、`SyncSystems/WorldMP.cs`、`Extras/PlayerCheckpoint.cs`、`ServerMain.cs`；`Player.cs` 仅更正过时注释。

测试文件：`Compatibility/DropSourceRegression.cs`、`EndingFlowTests.cs`、`scripts/check_release12.py`。夹具新增真实 gameplay/深度计断言、组件实例统计、到期后继续观察、冻结 true/0.1s 输入、关闭服务端 tracker 的到期场景。修正测试文件清单的跨进程发布竞态及固定测试角色位置的时序。相关角色固定补丁只在 `FULLGAME && TESTING` 中编译，且仅在显式 drop-chip 测试模式启用。

## 双实例验证记录

使用仓库内 `build/runtime-A`、`build/runtime-B`，两个真实游戏进程，直连 `127.0.0.1:7791`，运行 PlaytestFullgame。原有仓库外两个实例保留。测试以实际面板确认方法、游戏生成/保存/重载、原版掉落生产器、真实密码 RPC 和 LES 同步执行；不是人工鼠标验收。

| 阶段目录（`build/drop-chip-tests/` 下） | 内容 |
|---|---|
| `release12-fixed-3` | 首次完整通过四类来源、拾取、芯片 gameplay、true/0.1s 重放和保存；随后又补了关闭 tracker 的用例及恢复初始化处理 |
| `release12-acceptance-final2` | 最终 DLL 完整验收，四类来源、拾取、冻结终态输入、新悬浮状态、关闭服务端 tracker 后双端 false/0、保存退出通过 |
| `release12-reload` | 使用最终验收存档重启，客机延迟 25 秒重连；恢复客机 Dune/无芯片，主机有芯片；相反的主机大厅草稿不能覆盖保存值 |
| `release12-legacy` | 同一存档转换为旧三列表，重启并延迟重连，默认有芯片及原版显示恢复 |
| `release12-host-unchipped` | 主机选择无芯片开新局，双方实际无芯片状态和深度计隐藏检查 |

每阶段有 `host.log`、`client.log`、`result.json` 和双端 `.done`，最终四阶段共 127 条通过断言。游戏画面在 `host-chip-gameplay.png`、`client-chip-gameplay.png`；主流程保留八份 CSV，每件物品连续记录 12 秒。汇总及二进制 SHA256 在 `build/release12-results.json`，可用 `python scripts/check_release12.py` 复核，本轮已运行通过。

最终掉落主流程四类来源共六件物品，每端各六组寿命记录：

| 来源 | 主机稳定段最大位移 | 客机稳定段最大位移 | 客机与权威位置最大差 |
|---|---:|---:|---:|
| 客机通过真实密码 RPC 开空投仓 | 0.005 m | 0.000 m | 0.001 m |
| 远端医疗箱 | 0.042 m | 0.046 m | 0.015 m |
| 远端铅灌木采集 | 0.054 m | 0.075 m | 0.036 m |
| 主机附近医疗箱对照 | 0.033 m | 0.044 m | 0.011 m |

稳定段为第 2～6 秒。全部只有一个悬浮组件 ID，首次观察到组件消失为 9.939～10.096 秒，之后仍继续观察至 12 秒；双端最终为 false/0、重力为 1。0.1 秒重放用例独立注入旧状态两秒：只创建一个组件，随后保持销毁；false→true 新状态仍能重新悬浮。关闭 tracker 用例中服务端不再执行物品完整快照，组件原版到期后仍成功把 false/0 发给客机。

芯片实效以原版深度计作为可见指标：主流程及重载主机有芯片时显示、客机无芯片时隐藏；主机无芯片场景检查主机也隐藏。双端还同时断言所有玩家 `ScavPlayer.unchipped == NetBody.unchipped == modUnchipped`，本地原版世界状态与选择一致。

失败尝试保留，不算通过：`release12-before`、`release12-fixed-1`、`release12-fixed-2`、`release12-acceptance-final` 的测试角色/密码会话位置时序问题；`release12-acceptance` 的 `medcrate.ids` Windows sharing violation。通过后的最终阶段已使用修正后的夹具。

## 构建与边界

使用用户指定构建命令，额外传 `ModPluginOutputDir` 将复制目标限定在仓库内。

- ShipFullgame：0 error，104 warning，见 `build/release12-ship-build.log`。
- PlaytestFullgame：0 error，100 warning，见 `build/release12-playtest-build.log`。

已有警告未作为本轮额外修复范围。Ship 的静态隔离检查确认没有 DropSourceRegression、固定位置补丁或 DropFrameProbe。双实例运行的是 PlaytestFullgame，**没有把 Ship 二进制的独立实机运行宣称为已完成**。两配置共用本次生产逻辑。

遗留边界：Steam 传输、真实高延迟/丢包、人工鼠标点击未覆盖；测试采用本机直连与显式夹具。未做全游戏所有无芯片行为的逐项验收，已覆盖原版深度计。没有修改 Escape 或其他结局启用开关；没有更改原版不带 FreshItemDrop 的地形掉落行为。两端部署时使用同一轮构建。
