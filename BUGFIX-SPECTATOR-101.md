# 1.0.1 旁观相机与角色输入修复

版本保持 `5.5.0-cu-port`。代码、构建输出、测试 loader、游戏 runtime、日志与存档均写在本仓库。读取 `F:\cu-port\CU_Fullgame` 的游戏文件作为构建引用和测试源，未向该副本、B 副本或 `D:\steam` 部署。

## 已复现的根因

`PlayerCamera.main.body` 已成功切到 HOST，`Spectator.GetCameraFollow()` 也指向同一个 HOST 身体；`NetBody` 和 `Body` 在同一个 GO。修复前对照的 586 个渲染帧内，camera.body 指错次数为 0。因此本次主要问题不是组件查找失败、相机每帧回绑尸体或原生相机拒绝 dead body。

真正的耦合在 `Util.IsBodyLocal/GetLocalBody`、其扩展方法和 `NetBody` 本地查询：它们使用相机 body 判定本地所有权。相机切到 HOST 后，客机的 HOST 副本变成 `IsBodyLocal=True`，`SyncBody.VisualUpdate` 不再对它执行 `ApplyDataToBody`。HOST 在服务端继续移动，客机显示的副本却停在原位。与此同时，原生 `PlayerCamera.HandleInput` 仍向 camera.body 写移动、跳跃、切手、攻击等输入，造成“可操作但无效/不同步的观战角色”。

普通 GUI 的旧 `SetCameraFollow(null)` 只在非旁观分支执行；它不是本次旁观中每帧回绑的实测根因。此处仅补上明确的非旁观条件。

另一个实测边界：`CLIENT_forcespectator` 可以先于本地死亡健康快照到达。仅选择第一个 alive 玩家可能选中暂未更新死亡状态的自己；默认目标现在优先选择其他存活玩家。

## 改动

- `Util.cs`、`Content/NetBody.cs`：旁观期间，本地身体查询和所有权判定使用 `ScavPlayer.LocalPlayer.body`；相机目标继续接受正常远端同步。非旁观保持原有场景初始化行为。
- `Content/Spectator.cs`：解析并保存规范的 `Body`，支持 `NetBody.body`、Body、Limb/父子组件；相机 body、WoundView 和 Moodle 指向观察目标。停止时恢复本地 body。清空本地遗留移动、蹲下、跳跃和投掷输入，不清空远端玩家的网络输入。
- `Patches/SpectatorPatches.cs`：选择时立即定位，在原生相机定位入口与 LateUpdate 后保持 body 和坐标。旁观跟随使用目标 Body 当前姿态，避免旁观者自己的鼠标偏移和原生平滑延迟。原有手动自由镜头规则保留；其间仍禁止角色操作。
- `UI/UIBullshit.cs`：默认选其他存活玩家，无存活玩家时允许只读尸体；两个按钮共用 `Spectator_CycleTarget`，更新名字、索引和规范目标。进入时关闭本地交互/制作/交易/自定义面板，结束小游戏与 noclip；停止时关闭观察医疗面板并恢复 body。目标销毁或失效时重新选择有效身体。
- `Patches/PlayerCameraPatches.cs`：旁观时不进入原生 HandleInput，保留暂停和医疗面板导航；阻止跳过原生操作后仍发送切手、拾取或 bark 等本地消息。
- `SyncSystems/Medical.cs`、`Patches/MinigamePatches.cs`、`SyncSystems/CombatStuff.cs`：阻止独立的医疗、CPR、休息、枪械按钮和消息后缀；医疗面板只读。HarmonyX 实测会继续执行其他前缀，因此医疗原有前缀内部也明确拒绝旁观操作。
- `Player.cs`：销毁远端角色时以所有权而不是 camera.body 判断是否为本地身体，避免被观战角色离开时错误保留。

现有 `NetBody` 复活检测通过修正后的所有权找到本地身体，调用 `StopSpectatorMode` 恢复控制。结局/叙事/结果锁定源码未改动；现有拒绝旁观、阻止输入、强制恢复 local body 的流程保持接入。

## 真实双实例结果：1.0.1

最终验收：`build/spectator-tests/acceptance-101-20261112-055553/`。

同版修复前对照：`build/spectator-tests/baseline-101-20261112-055712/`。对照从保存的六个修复前文件编译，并排除新增 SpectatorPatches，保留同一测试夹具；不回滚工作树或覆盖最终 DLL。

两轮双方日志均为 `Game version: "1.0.1"`。两份测试 runtime 的原版 Assembly-CSharp.dll 与 F 盘当前副本 SHA256 相同：

`D6CA378A16A08A47C7F74CDAD0535D98F426B654FCEC1F88255B0ECDD636825C`

| 观测 | 修复前同版对照 | 最终验收 |
| --- | --- | --- |
| 死亡进入旁观 | index=0，FRIEND 尸体 | index=1，存活 HOST |
| 切 HOST 后 camera.body / follow | 均为 HOST | 均为 HOST |
| HOST 在客机被判本地拥有 | True | False；owned 始终 FRIEND |
| 服务端 HOST 移动 | (0,482) → (30,482) | (0,482) → (30,482) |
| 客机 HOST 副本位移 | 0.000 | 30.000 |
| 渲染帧采样 | 586 帧，body 指错 0 次 | 662 帧，body 指错 0 次 |
| 最大 camera / 目标 XY 差 | 18.321570 | 0.000000 |
| 8 次角色输入进入原生 HandleInput | 8 | 0 |
| 远端 moveDir / 看向位置 | 会进入原生输入写入 | 保留探针 (0.25,0.5)，look 未变化 |
| HOST → 尸体 → HOST | 原实现无立即定位保证 | 两次选择当场 delta=0.000；尸体也不能操作 |
| 独立拖拽、攻击、切手、枪械、医疗回调 | 无旁观保护 | 原生入口进入次数为 0 |
| CPR 小游戏 | — | 拒绝启动 |
| 手动停止旁观 | 恢复本地 body | 恢复 FRIEND，follow=null |
| 服务端复活 | — | 自动退出旁观，恢复 FRIEND；D 输入进入原生方法一次且 moveDir.x=1 |
| 原有 Narrative/结果锁定入口 | — | 拒绝重新进入旁观、继续拒绝原生角色输入 |

关键日志在双方 `A.log` / `B.log`，包含 active、curspectatefolowing 索引、标签、camera body/target/owned 的实例 ID、坐标和差值。`save/spectating-host.png` 为客机渲染截图，已检查 HOST 位于镜头中心、旁观标签为 HOST。

测试边界：是真实 Unity A/B 进程、真实场景生成、生产健康同步/死亡、CLIENT_forcespectator 接收、LES 位置同步、原生相机渲染、生产目标切换处理器及服务端复活。夹具关闭测试身体刚体模拟，HOST 移动由 60 次服务端位置更新驱动；按键为原生输入方法内部的显式采样注入，独立回调测试直接调用生产入口。没有用夹具设置相机位置或替代所有权/输入拒绝的最终结果。结局锁定测试只同步暂置 Narrative 状态核对原有入口守卫，然后立即恢复，不代表完整结局重跑。

Computer Use 没有返回可控制的游戏窗口，故没有把这些输入称为桌面手动 WASD/鼠标。仍需用户在常规双实例中实际点 `<` / `>`、按 WASD/跳跃/交互/攻击，确认自由运行物理场景中的观感与禁止操作；再验证重生后的正常操作。未做无关全量回归。

早期 `baseline`、`fixed`、`target-trace`、`fixed2`、`acceptance`、`acceptance2` 使用旧 1.0 runtime，仅用于定位；其中默认目标时序失败、独立医疗前缀异常的日志均保留，不计为最终 1.0.1 验收。最终验收与同版对照均通过后结束其自有测试进程。

## 构建与可复跑命令

当前机器没有 .NET SDK，采用仓库已有的离线 Roslyn 编译器、publicizer 输出、NuGet 缓存和 Unity 引用，包含正常嵌入资源；未把它称为完整 SDK/MSBuild 构建。

- ShipFullgame：0 error，`build/spectator-ship-build.log`；产物 `build/bugfix-ShipFullgame/CasualtiesMP.dll`。
- PlaytestFullgame：0 error，`build/spectator-playtest-build.log`；产物 `build/bugfix-PlaytestFullgame/CasualtiesMP.dll`。
- 最终测试加载的 Playtest DLL 与上方最终产物哈希相同，`5D3564137EE803C4074378F6B822384254996E9E4247BECF5115C362D7A53BDD`。
- Ship DLL SHA256：`BB6972F3F7FC02DF280C89CD458BB1B355A5B74644E96B6AFFF487DED353531B`。

```powershell
.\scripts\Build-OfflineRoslyn.ps1 -Configuration ShipFullgame
.\scripts\Build-OfflineRoslyn.ps1 -Configuration PlaytestFullgame
.\scripts\Test-Spectator.ps1 -Run fixed-101
# 仅复跑修复前对照；六份原文件保存于 build/spectator-tests/original-source
.\scripts\Build-OfflineRoslyn.ps1 -Configuration PlaytestFullgame -SpectatorBaseline
.\scripts\Test-Spectator.ps1 -Run baseline-101 -Baseline -TestDll build\spectator-tests\baseline-build-PlaytestFullgame\CasualtiesMP.dll
```

启动器只在仓库下创建 1.0.1 runtime 和独立 loader，逐次验证游戏程序集哈希，保存 DLL 哈希与日志；结束时只停止自身启动的进程。最终源码清单在 `build/spectator-tests/final-source-sha256.json`，结局源码清单在 `ending-source-sha256.json`。
