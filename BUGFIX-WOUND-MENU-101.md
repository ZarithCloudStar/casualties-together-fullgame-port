本轮修复用户实际使用的“右键对方玩家 → 检查伤口”入口。版本仍为 `5.5.0-cu-port`，真实双实例运行 **1.0.1**。上轮只验证 R 路径及同帧显式目标，未覆盖后续 GUI 覆盖，是验证遗漏，已在旧报告顶部更正。

**已确认的主根因**

菜单目标和初始打开过程正确。用户客机日志 `10:53:18.646` 显示 `body_to_open=HOST`，随后 `view.body=HOST`、`name=HOST`；到 `10:56:56.936` 关闭时，`view.body` 却是 FRIEND 而名字仍是 HOST。双方原日志已保存到 `build/wound-menu-tests/user-host.log`、`user-client.log`。

在生产代码只增加覆盖诊断、尚未改变行为时，使用正常世界的实际 IMGUI 菜单回调复现。有效对照为 [diagnosis2-101](build/wound-menu-tests/diagnosis2-101-20261112-190044/)，诊断 DLL 为 `build/wound-menu-diagnosis/CasualtiesMP.dll`，当时的 Spectator 源码在 `build/wound-menu-tests/baseline-source/Spectator.cs`。

客机 B.log（时间为 UTC；本地时间加 8 小时）：

```text
11:01:21.016 GUI inspect click target=HOST event=Layout
11:01:21.017 opening body_to_open=HOST
11:01:21.033 after-toggle open=True view.body=HOST name=HOST
11:01:21.035 camera-follow overwrite spectator=False follow=null
             previous=HOST next=FRIEND local=True
11:01:23.054 FRAMES count=211 wrongBody=211 wrongLimb=211
             wrongSkin=211 wrongFood=211 view=FRIEND skin=73 peerSkin=36.9
```

主机同样复现：先打开 FRIEND，随后覆盖回 HOST；**190/190 渲染帧**的身体、肢体、伤口、健康数据均错误。

具体调用链：

1. `_GUI_PlayerInteractionGui` 的“检查伤口”调用 `OpenSpecificBody(目标.body)`，正常选中对方。
2. 同一次 `_GUI_DoMPUserInterface` 随后调用 `_GUI_DoGeneralMPGui`。
3. 非旁观时 `Spectator.GetCameraFollow()` 和 `SpectatorLastTarget` 均为 null，原来的相等条件仍成立，因而每次 GUI 都调用 `Spectator.SetCameraFollow(null)`。
4. fix8 的 `SetCameraFollow` 不仅恢复相机身体，还无条件把 `WoundView.view.body` 设为自己的身体。原生医疗 UI 之后刷新的是自己，故双方都显示自己的数据。

**修复与首次初始化问题**

`Content/Spectator.cs` 保存前一个旁观目标；只有真正选择旁观对象或从已有旁观目标返回时才绑定医疗身体。重复清理已经空闲的旁观相机保留当前医疗对象。相机和 Moodle 的本地恢复，以及真实旁观切换仍走原流程。

连续帧验证还发现第二个实际问题：只修身体覆盖后，双方伤势等数据已正确，但首次打开时原生 `WoundView.Start` 把名字写成了物种名。中间轮 [fixed-101](build/wound-menu-tests/fixed-101-20261112-190223/) 的身体／肢体／伤口／健康错误均为 0，名字错误主机为 219/219、客机为 179/179；分别显示 MILKY 和 EXPERIMENT。该轮明确未通过验收。

因此在 `SyncSystems/Medical.cs` 添加 `WoundView.Start` 后缀：原生初始化完成后，用当前选定身体重新应用既有 `WoundView_SetTargetBody`。最终日志确认：

```text
host   initialized body=FRIEND nativeName=MILKY -> 最终 name=FRIEND
client initialized body=HOST   nativeName=EXPERIMENT -> 最终 name=HOST
```

本轮没有改右键目标查找、交互距离、健康同步、角色所有权或沙盒出生逻辑。

**最终真实双实例结果**

最终验收：[acceptance-101](build/wound-menu-tests/acceptance-101-20261112-190414/)。正常世界生成后，经 `StartPlayerInteractionMenu` 的实际距离、意识和遮挡检查打开菜单，再由真实 IMGUI “检查伤口”分支执行生产回调。

| 项目 | 主机查看 FRIEND | 客机查看 HOST |
| --- | --- | --- |
| 连续渲染帧 | 221 | 187 |
| 错误 body / selectedLimb | 0 / 0 | 0 / 0 |
| 错误伤口 UI / 健康 UI | 0 / 0 | 0 / 0 |
| 错误玩家名 | 0 | 0 |
| 最终玩家名 / 头部皮肤 UI | FRIEND / 73 | HOST / 37 |
| 查看自己的面板 | 通过 | 通过 |
| 关闭自己的面板后，再点对方检查伤口 | 通过，另观察 30 帧 | 通过，另观察 30 帧 |

主机设置不同的头部皮肤值 37/73、饥饿值 57/83，再使用生产 CharacterHealth 同步；测试先验证客机收到不同伤势，然后逐渲染帧比对名字、身体、选中肢体、伤口文本和饥饿条。正常恢复会使原始数值略有变化，UI 与当前目标的取整值／比例一致。

已检查 [客机菜单面板截图](build/wound-menu-tests/acceptance-101-20261112-190414/save/client-menu-panel.png)：目标为 HOST，头部皮肤 37，饥饿约 57%；[主机截图](build/wound-menu-tests/acceptance-101-20261112-190414/save/host-menu-panel.png) 对应 FRIEND 的数据。

测试限制：真实 Unity 双进程、普通世界、生产网络、实际 OnGUI 菜单回调和渲染，注入的是“检查伤口”按钮的点击结果，不是操作系统鼠标事件。没有直接调用测试版打开面板方法代替菜单回调。身体在测试口袋中使用 FreezeAll 约束保持近距离；未关闭 Collider 或刚体模拟。首次夹具未持续保持约束，未通过菜单前置检查，没有计入有效复现。测试没有改用户的游戏进程。

最终同一 DLL 的 [fix8 旁观专项](build/spectator-tests/woundmenu-fix8-101-20261112-190514/) 也通过：372 个渲染帧 camera.body 错误为 0，相机目标距离为 0；HOST 位置移动 30 格，客机副本也移动 30 格；旁观输入进入原生 HandleInput 次数为 0。切尸体／切回、关闭旁观恢复自己、服务端复活后恢复操作、独立医疗及 CPR 禁止、原结局输入锁均通过。两个测试 loader 的 DLL 哈希均与最终 Playtest 产物一致。沙盒和存档没有重跑，本轮未改对应生产代码。

**构建与复跑**

使用 `E:\tools\dotnet\dotnet.exe` SDK 9.0.320，最终两个正式构建均为 **0 error**：

- [ShipFullgame DLL](build/wound-menu-ShipFullgame/CasualtiesMP.dll)，103 warning；[构建日志](build/wound-menu-ShipFullgame-build.log)。
- [PlaytestFullgame DLL](build/wound-menu-PlaytestFullgame/CasualtiesMP.dll)，100 warning；[构建日志](build/wound-menu-PlaytestFullgame-build.log)。

```text
Ship SHA256     9FBB4509D77EB3A0FCD6ED4941F81A0A5B1CDF7E2AA774C06C17595E4E2D7C6B
Playtest SHA256 99BF8E02CFF5D22EC6E8C6C389C2C42E1D632ADE6CBC2D1281867B285603C6C7
```

最终双实例验收双方 `Game version: "1.0.1"`；`Failed to patch / patch skipped / IL Compile Error / Index was out of range` 均为 0。源码／DLL 哈希及运行检查见 `build/wound-menu-tests/final-source-sha256.json`、`final-dll-sha256.json`、`runtime-checks.json`。

```powershell
$env:NUGET_PACKAGES='F:\cu-port\.nuget'
$env:DOTNET_CLI_HOME="$PWD\build\dotnet-home"
foreach ($config in @('ShipFullgame','PlaytestFullgame')) {
    & E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c $config --no-restore "-p:SolutionDir=$PWD\" "-p:ModPluginOutputDir=$PWD\build\wound-menu-$config"
}
.\scripts\Test-WoundMenu.ps1 -Run acceptance-101
# 复现修复前行为（保留的诊断 DLL）
.\scripts\Test-WoundMenu.ps1 -Run diagnosis-101 -Baseline -TestDll build\wound-menu-diagnosis\CasualtiesMP.dll
```

测试文件 `Compatibility/WoundMenuRegression.cs` 只在 FULLGAME && TESTING 编译，且需显式测试参数。Ship 不包含测试夹具。修改与产物均在本仓库，**未向用户游戏副本或 D:\steam 部署**；日常游戏需使用本报告的最终 Ship DLL。
