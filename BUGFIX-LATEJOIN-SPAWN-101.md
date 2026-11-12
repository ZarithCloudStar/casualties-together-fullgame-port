本轮修复“中途加入时在同伴身边出生 / SpawnOnPlayer”在正式版失效的问题。版本保持 `5.5.0-cu-port`，运行游戏为 **1.0.1**。改动、构建、测试 runtime、独立存档及日志都在本仓库，没有向用户游戏目录或 `D:\steam` 部署。

**日志确认的根因**

用户主机日志已保存在 [user-host.log](build/latejoin-tests/user-host.log)，客机日志在 [user-client.log](build/latejoin-tests/user-client.log)。日志时间为 UTC，本地时间加 8 小时。

```text
11:21:11.418 FRIEND shared authoritative at=(0,487)
11:29:59.327 FRIEND spawn/reminder at=(39.59,379.62)
11:30:31.209 admitted player=FRIEND1
11:30:38.080 Received WorldPlacePlayer from FRIEND1
11:30:38.080 shared authoritative player=NAME_465249454E4431 at=(0,487)
11:30:38.080 Sending a spawn location for FRIEND1 to (0,487)
```

`GiveSpawnLocationToPlayer` 的 FULLGAME 分支，对没有保存位置的玩家先无条件应用共享开局出生点。原有读取 `SpawnOnPlayer` 的 `LateSpawnLocation` 排在后面的分支里，正常正式版新玩家不会走到它，所以开关无法发挥作用。

还有一个需要防止的边界：`ScavPlayer.LateJoin` 是加入时设为 true 的持久标志，玩家之后正常换层也不会清除。直接使用它套同伴出生规则，会把正常换层误当成再次入服。

**生产改动**

- `Player.cs`：增加服务器的一次性 `late_join_spawn_pending`。玩家在已有世界中入服时设定；出生流程完成后消耗；正常世界生成／场景切换时清除。保留原有 `LateJoin` 的含义。
- `ServerMain.cs`：保持沙盒权威位置、checkpoint 位置、断线保存位置的优先级。没有已恢复位置的新入服玩家，在开关开启时使用原有主机／其他存活同伴选择器，验证目标不是自己、身体存活且坐标在世界内，再通过现有服务器传送与状态包发送坐标。关闭选项或没有合适目标时保留原共享出生点。
- 同伴出生使用现有传送、区块更新与随身物品回收流程；没有新增网络包，也没有让客机自行计算出生位置。
- 保留 `[spawn] request` 和 `[spawn] companion authoritative` 诊断，记录玩家、LateJoin、一次性标记、开关、保存位置状态、共享位置和选中的同伴坐标。

生产差异在 [production-fix.diff](build/latejoin-tests/production-fix.diff)，修复前两个文件保存在 `build/latejoin-tests/baseline-source/`。本轮 `Medical.cs`、`Spectator.cs` 与上一轮修复哈希一致，没有修改医疗或旁观功能。

**真实顺序连接验收**

最终 SDK Playtest 产物验收：[acceptance-101](build/latejoin-tests/acceptance-101-20261112-194605/)，结果为 PASS。整个过程最多两个 Unity 实例同时连接，主机始终保持世界，依次执行：

1. HOST + FRIEND 一起进入正常世界，然后移动到开局出生点下方约 100 格。
2. FRIEND 断开连接；主机继续移动到另一个位置。
3. 新身份 FRIEND1 加入，开关开启。
4. FRIEND1 退出；新身份 FRIEND2 加入，主机关闭开关。
5. FRIEND2 退出；原身份 FRIEND 再加入，主机重新开启开关，检查保存位置优先。

| 情况 | 服务端记录 | 客机记录 | 结果 |
| --- | --- | --- | --- |
| 新身份 FRIEND1，开启 | HOST、FRIEND1 均 `(40,347.600)`，相距 **0.000 格** | 同一坐标，相距 **0.000 格** | 同伴出生通过 |
| 新身份 FRIEND2，关闭 | FRIEND2 `(0,501)`，HOST `(40,347.600)`，相距 158.529 格 | 同样位于 `(0,501)` | 原共享出生点行为通过 |
| 原 FRIEND，有保存位置，开启 | 恢复到 `(2.109,401.951)`，与当前 HOST 相距 66.255 格 | 同样恢复到 `(2.109,401.951)` | 保存位置优先通过 |

原 FRIEND 退出前测量位置为 `(3,402)`，实际恢复与该测量点差约 **0.892 格**；测试没有把它传送到主机来满足“接近”断言。断开连接到最终状态保存之间存在帧间移动，因此恢复断言允许 2 格误差。

关键主机记录：

```text
11:47:25.499 FRIEND1 lateJoin=True admissionPending=True spawnOnPlayer=True savedPose=False
11:47:25.500 companion authoritative player=FRIEND1 companion=HOST at=(40,347.600)
11:47:34.684 PASS admission marker consumed after spawn
11:47:56.370 FRIEND2 lateJoin=True admissionPending=True spawnOnPlayer=False savedPose=False
11:47:56.370 shared authoritative ... at=(0,501)
11:48:29.199 FRIEND lateJoin=True admissionPending=True spawnOnPlayer=True savedPose=True
11:48:49.001 PASS new layer resets pending admission instead of treating it as another late join
```

最后一条是对独立 Server_PlayerState 调用生产 `OnWorldgenStart` 的生命周期检查，**不是完整正常换层验收**。

测试边界：真实连接、断开、身份更换、角色确认、生成、服务器出生请求、可靠状态包和客机最终位置应用；只在主机准备测试房间及初始两人下移时手动传送。中途加入的最终位置全部来自生产出生流程，夹具没有代设结果。为隔离重力、身体碰撞和后台按键干扰，生成完成后夹具关闭测试身体的刚体模拟，因此上述距离是网络放置验收，仍需常规自由物理游玩确认观感。开关由夹具直接改主机规则，没有模拟设置菜单鼠标点击或验证客机设置显示。

两轮早期诊断夹具未成功执行新身份加入：一轮启动器阶段数组展开错误，另一轮初始两人的物理位移超过过严的准备断言。它们的失败文件及日志保留，未算作有效对照或通过；根因依据是用户原始日志和对应生产分支，最终修复依据上述完整顺序连接验收。

**构建、产物与仍需确认项**

SDK 9.0.320 的正式构建：

- ShipFullgame：**0 error**，103 warning；[DLL](build/latejoin-ShipFullgame/CasualtiesMP.dll)、[日志](build/latejoin-ShipFullgame-build.log)。
- PlaytestFullgame：**0 error**，100 warning；[DLL](build/latejoin-PlaytestFullgame/CasualtiesMP.dll)、[日志](build/latejoin-PlaytestFullgame-build.log)。

```text
Ship SHA256     705BDCFF5F57E7E4FB652C49A06AC9984606C76BCB36C41009343B70A4B3100B
Playtest SHA256 AF35D90C4406CC6FDEAA91DBD2E6E8AD2365CBE796A63A8BC9BA9F7DC46E2A85
```

测试 loader 的 DLL 与最终 SDK Playtest 哈希一致。五份日志都为游戏 1.0.1，patch 错误检查均为 0，见 [runtime-checks.json](build/latejoin-tests/runtime-checks.json)。

主机每次断开玩家时出现一次旧有 `CPRHandler.IsCPRBeingPerformedOnThisBody → MedicalSync.UpdateCPREffects` 空引用，本轮共 4 次。修复前诊断 DLL 的首次 FRIEND 退出时已有相同调用栈，因此不是此次出生分支引入；它没有阻断本轮保存、后续加入或出生验收。该断线医疗清理问题已记录，本轮未混入修复，也没有将日志描述为“零异常”。

未覆盖：Steam/P2P、没有任何存活同伴、主机死亡后选择另一同伴、完整换层、完整 checkpoint 文件重载和沙盒晚加入。本轮保留对应既有分支，仅验证了内存保存状态重连的位置优先级。

复跑命令（本仓库）：

```powershell
$env:NUGET_PACKAGES='F:\cu-port\.nuget'
$env:DOTNET_CLI_HOME="$PWD\build\dotnet-home"
foreach ($config in @('ShipFullgame','PlaytestFullgame')) {
    & E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c $config --no-restore "-p:SolutionDir=$PWD\" "-p:ModPluginOutputDir=$PWD\build\latejoin-$config"
}
.\scripts\Test-LateJoinSpawn.ps1 -Run acceptance-101
```

测试夹具 `Compatibility/LateJoinSpawnRegression.cs` 仅在 FULLGAME && TESTING 编译，且需显式参数；Ship 不包含它。最终源码与 DLL 哈希清单位于 `build/latejoin-tests/final-source-sha256.json`、`final-dll-sha256.json`。新 DLL 尚未替换进用户实际游戏目录。
