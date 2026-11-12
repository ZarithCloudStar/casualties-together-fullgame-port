**后续更正：本报告的 A 验证不足以证明右键菜单可用。** 这里的 R 和显式目标测试主要检查切换调用返回时的数据，漏掉了正常 OnGUI 随后覆盖医疗目标的行为。用户反馈后已通过真实菜单回调和连续渲染帧确认另一根因并修复，见 [右键检查伤口修复报告](BUGFIX-WOUND-MENU-101.md)。下文 A 的通过记录仅适用于当时所述测试边界，不能当作右键菜单的完整验收。

本轮在确认根因后修复 A/B，版本保持 `5.5.0-cu-port`，运行游戏为 **1.0.1**。源码、测试 runtime、loader、存档、日志和产物均写在本仓库；游戏副本只作为读取来源，没有向 F 盘用户游戏副本或 `D:\steam` 部署，也没有结束用户自己的游戏进程。

**A：医疗目标选择的根因与证据**

先只加诊断日志，没有改变目标选择。修复前双实例记录在 [diagnosis-limbs-101](build/medical-sandbox-tests/diagnosis-limbs-101-20261112-183139/)，诊断 DLL 为 `build/medical-sandbox-tests/diagnostic-limbs.dll`；诊断版三个生产文件保存在 `build/medical-sandbox-tests/diagnostic-source/`。

客机 B.log 的关键记录（日志时间为 UTC，本地时间加 8 小时）：

```text
10:32:12.678 cursor=(0.000,-107.385) GetBodyOnPos=HOST local=False
10:32:12.688 PANEL remote-root actual=HOST name=HOST skinUI=37
10:32:12.711 cursor=(-0.948,-105.265) GetBodyOnPos=null hits=
10:32:12.713 PANEL remote-limb-Head expected=HOST actual=FRIEND
             name=EXPERIMENT selectedLimbBody=FRIEND skinUI=73 expectedSkin=37
```

候选日志显示 HOST 根 Collider 启用且刚体 `simulated=True`，Head 等肢体 Collider 启用，但肢体刚体 `simulated=False`。因此并非整个远端角色没有 Collider：身体中心能命中，渲染头部在根 Collider 外时，物理点查询没有 Body/Limb 命中，`body_to_open` 留空，随后选择相机的本地 FRIEND。主机目标判定也存在相同的边缘漏选条件。

这轮复现排除了“健康包没有 HOST 数据”和“fix8 把正常远端 body 判成 local”：同一个 HOST 副本 `local=False`；命中躯干时已经能显示 HOST 的伤口值 37，漏选头部时才显示 FRIEND 的 73。没有复现“所有客机远端部位都必然显示自己”，确认的是上述有日志支持的漏选路径。

`SyncSystems/Medical.cs` 的修复：

- 保留原物理命中及交互距离校验；未命中时，从 `ScavPlayer.BodyToPlayerDict` 按光标与身体根／未断离肢体的世界坐标距离选择最近身体，容差 0.75 格。
- 远端候选必须在原有玩家交互距离内；同时比较本地身体，避免瞄准自己时选中附近队友。远处空白光标仍打开自己的面板。
- 显式 `OpenSpecificBody` 目标优先，仍交给现有 `WoundView_SetTargetBody` 设置名字、目标身体及肢体 UI。
- 保留开面板时的 `[woundview]` 诊断：`body_to_open`、camera/owned、物理 Body/Limb 命中、各候选 Collider 状态、选择来源和最终 `view.body`。不在每帧输出。

**B：沙盒出生点的根因与证据**

用户日志已保存到 `build/medical-sandbox-tests/user-host-before.log` 和 `user-client-before.log`。旧主机日志 `10:25:09.963` 发送共享出生点 `(0,1)`；客机 `10:25:16.073` 应用 starter 状态，位置也是 `(0,1)`。

修复前 [diagnosis-101](build/medical-sandbox-tests/diagnosis-101-20261112-183038/) 的实际序列记录进一步确认：

```text
host   10:30:51.922 sequence body=(0,-106) shared=(0,1)
host   10:30:51.983 shared authoritative spawn=(0,1)
client 10:30:52.003 sequence body=(0,-106)
client 10:30:52.539 sequence body=(0,1) shared=(0,1)
```

`Body.PlaceBody` 先产生通用出生点；`SandboxCourse.Sequence` 在淡入等待后直接修改主机 Transform，绕过 `PlaceBody`，所以没有更新共享出生点。客机自己的教程移动与服务器通用出生包互相覆盖。自由物理运行后，修复前两端测得 HOST `(0,-107.385)`、FRIEND `(0,0.241)`，差 **107.625 格**。

`Compatibility/SandboxMode.cs`、`ServerMain.cs` 的修复：

- 主机包装原生 1.0.1 SandboxCourse 协程：第一段淡入结束后等待主机世界生成完成，再让原生协程执行实际房间放置。这样慢生成的后续 `Body.PlaceBody` 不会覆盖沙盒放置。
- 紧接原生实际放置、等待终端之前，读取主机真实身体根位置，保存为当前课程的出生点，并通过现有 `Server_TeleportCharacter` 广播给已创建客机。没有用固定延时推测最终位置。
- `GiveSpawnLocationToPlayer` 在教程中等待该课程出生点就绪，再走沙盒权威坐标分支；课程实例用于避免复用上一次沙盒状态。后续出生请求也使用此坐标。
- 客机 SandboxCourse 只保留淡入、课程安全状态和解除强制行走，不运行原生本地位移／终端创建流程；位置来自主机，终端沿现有对象网络同步。
- 正常世界的存档优先级、共享出生点、最终层位置和 WS1 等待分支保持原逻辑。生产改动相对于诊断版的差异在 [production-fix.diff](build/medical-sandbox-tests/production-fix.diff)。

**最终双实例验收数据**

最终 SDK 产物的验收是 [sdk-acceptance-slowgen-101](build/medical-sandbox-tests/sdk-acceptance-slowgen-101-20261112-183755/)，主机 WorldPlacePlayer 人为延迟 3 秒，刻意超过教程淡入时间；双方 `Game version: "1.0.1"`，A/B 均完成，断言通过。

```text
host 10:38:07.247 test: delaying WorldPlacePlayer by 3s
host 10:38:11.204 WorldGeneration.FinishWorldGeneration
host 10:38:11.226 host room placed authoritative=(0,-106)
host 10:38:11.230 broadcast room FRIEND=(0,-106)
host 10:38:11.290 authoritative spawn FRIEND=(0,-106)
```

| 检查 | 客机观察 | 主机观察 |
| --- | --- | --- |
| 沙盒生成完成后 15 秒，两人根位置 | FRIEND `(0,-107.760)`；HOST `(0,-107.385)` | HOST `(0,-107.385)`；FRIEND `(0,-107.760)` |
| 两人距离 | **0.375 格** | **0.375 格** |
| R 路径查看远端头部 | HOST；目标/selectedLimb 均属 HOST；skin=**37**；food≈**0.569** | FRIEND；目标/selectedLimb 均属 FRIEND；skin=**73**；food≈**0.829** |
| R 路径查看远端根／躯干／全部 15 个肢体 | 17 次全部选中 HOST，伤口和身体健康 UI 匹配目标 | 17 次全部选中 FRIEND，伤口和身体健康 UI 匹配目标 |
| 自己／远处空白光标 | FRIEND，自身 skin=73 | HOST，自身 skin=37 |
| 超光标容差／超交互距离 | 拒绝远端回退 | 拒绝远端回退 |
| 显式目标与光标冲突 | 显式目标优先 | 显式目标优先 |

37/73 的不同伤口由主机设置，再通过生产 `CharacterHealth` 同步；测试另断言远端已收到该特征值。饥饿 UI 与目标健康状态相符，不仅检查面板名字。已检查 [客机面板截图](build/medical-sandbox-tests/sdk-acceptance-slowgen-101-20261112-183755/save/client-panel.png) 和 [主机面板截图](build/medical-sandbox-tests/sdk-acceptance-slowgen-101-20261112-183755/save/host-panel.png)。

测试边界：真实 Unity 双进程、生产网络传输／教程生成／出生包／健康同步／目标选择和原生 WoundView 渲染。医疗测试只注入 R 的采样与光标世界坐标；沙盒位置数据在任何人工移动和物理约束前收集。之后医疗定点测量使用刚体 FreezeAll 约束，**没有关闭 Collider 或 rb.simulated**，因此修复前肢体漏选不是夹具关闭物理造成。超距离选择守卫检查只在同一帧临时移动远端 Transform，立即还原。

**fix8 与构建检查**

最终 SDK DLL 的 [旁观专项](build/spectator-tests/sdk-medical-fix8-check-101-20261112-183848/) 通过：548 个渲染帧，错误 camera.body=0，最大相机距离=0；HOST 权威移动 30 格，客机副本位移 30 格；8 类输入探针进入原生 HandleInput 次数=0；切尸体／切回、独立医疗与 CPR 禁止、复活恢复操作、原结局输入锁全部通过。

与上轮 fix8 的源码哈希清单比对，Spectator、NetBody、Util、Player、UIBullshit、SpectatorPatches、PlayerCameraPatches、MinigamePatches、CombatStuff、SpectatorRegression 共 10 个文件全部未变。存档／WS1／一般换层逻辑未修改，本轮没有重跑存档恢复、一般换层、晚加入或无关全量回归。

使用 `E:\tools\dotnet\dotnet.exe` 的 SDK 9.0.320 正式构建：

| 配置 | 结果 | 产物 |
| --- | --- | --- |
| ShipFullgame | **0 error**，103 warning | `build/medical-sandbox-ShipFullgame/CasualtiesMP.dll` |
| PlaytestFullgame | **0 error**，100 warning | `build/medical-sandbox-PlaytestFullgame/CasualtiesMP.dll` |

日志：[Ship](build/medical-sandbox-sdk-ship.log)、[Playtest](build/medical-sandbox-sdk-playtest.log)。最终 SDK A/B 验收和旁观专项共四份日志中，`Failed to patch / patch skipped / IL Compile Error / Index was out of range` 均为 0，结果记录在 [runtime-checks.json](build/medical-sandbox-tests/runtime-checks.json)。初期诊断使用已有离线 Roslyn 构建；最终验收使用正式 SDK 产物。

```text
Ship SHA256     B2B319A86F2834084B9872822E945E4B203F8697681F40EBE35B6C5A16D2E433
Playtest SHA256 05116A1CFBC6C55BE8B01E7B7FB4338C92FFAC0C69954F3F01B0D11AEB2259F7
```

新测试夹具 `Compatibility/MedicalSandboxRegression.cs` 仅在 FULLGAME && TESTING 编译，并要求显式测试参数；Ship 不包含它。最终源码哈希见 `build/medical-sandbox-tests/final-source-sha256.json`。

可复跑命令（在本仓库）：

```powershell
$env:NUGET_PACKAGES='F:\cu-port\.nuget'
$env:DOTNET_CLI_HOME="$PWD\build\dotnet-home"
foreach ($config in @('ShipFullgame','PlaytestFullgame')) {
    & E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c $config --no-restore "-p:SolutionDir=$PWD\" "-p:ModPluginOutputDir=$PWD\build\medical-sandbox-$config"
}
.\scripts\Test-MedicalSandbox.ps1 -Run acceptance-101 -SlowGeneration -TestDll build\medical-sandbox-PlaytestFullgame\CasualtiesMP.dll
.\scripts\Test-Spectator.ps1 -Run medical-fix8-101 -Port 7887 -TestDll build\medical-sandbox-PlaytestFullgame\CasualtiesMP.dll
```

两条双实例脚本应顺序运行。中间两次追加检查启动时出现进程窗口标题 `Fatal error`，没有生成游戏日志，未计入验收；仅结束了这两轮自身启动的进程。后续串行 SDK 验收和旁观专项均正常完成并结束自身进程，没有把无日志轮次记成通过。

**仍需用户实机确认**：用最终 DLL 在日常双实例实际移动鼠标按 R，检查 HOST／FRIEND／自己，尤其头部、伸出身体的手部与重叠角色；再正常进沙盒确认镜头和房间观感。本轮 R 是采样注入，不能称为桌面手动键鼠验收；Steam/P2P、迟到加入沙盒、菜单往返多次及存档恢复未覆盖。
