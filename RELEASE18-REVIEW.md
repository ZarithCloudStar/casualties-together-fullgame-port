# Release18 实测记录

仓库：`F:\cu-port\src\casualties-together-main`，版本 `5.5.0-cu-port`。
游戏程序只使用 `F:\cu-port\CU_Fullgame` / `CU_Fullgame_B`。没有修改 `D:\steam`。

## 注射真实路径与修复

双实例已实际进入生成的 layer11、通过科学家枪击/逃亡/手术房流程。
夹具调用 `PlayerCamera.main.ApplyWoundItem(item)`，选择**另一个玩家**的 `body.limbs[2]`，
随后使用原版 `SyringeMinigame.OnUse` 回调分帧注射，并发送生产注射 RPC；没有用一次 `Inject(12)` 代替协助路径。

路径：

1. `ScavPrototypeMP/SyncSystems/Medical.cs:1847`：`PlayerCamera.ApplyWoundItem` 的 MP Prefix，探针记录 item / selectedLimb / patient body / healer body。
2. `Medical.cs:2737`：生产 `ServerReceiver_ApplyWoundItem`，校验道具、患者与治疗者。
3. `Medical.cs:1826`：`ForceApplyWoundItem` → `item.Stats.useLimbAction`，打开原版注射小游戏。
4. 原版 `F:\cu-port\decomp\fullgame\Item.cs:2071` / `:2092` 的 `saline` 道具动作（`mult * 80f`）→ `SyringeMinigame.OnUse` → `WaterContainerItem.cs:206` 的 `Inject`。
5. `ScavPrototypeMP/Patches/MinigamePatches.cs:2233`：发送分帧剂量；`:2316`：服务端校验并对患者执行 `Inject`。
6. 原版 `Liquids.cs:968`：`onHealthUse` → `CoUtils.DoTimedOp("localanesthetic" + limb.name, ..., 6 * ml)`。
7. `ScavPrototypeMP/Patches/CoUtilsPatches.cs:59`：`[anesth]` Prefix；`Compatibility/EndingInjection.cs:31`：效果 Postfix。

本轮游戏日志确认：真实路径**会经过 Inject**。每帧剂量只有约 0.2ml，逐次要求 `6 * 本次ml > 50` 是错误的；
原版 `DoTimedOp` 累加剩余时长。另一个关键因素是 godmode：原有健康重置每帧 `CancelAll()`，会清除腹部麻醉。

修复：

- `Compatibility/EndingFlow.Escape.cs:188`：只读取**服务端核心玩家自身 CoUtils** 的 `localanestheticDownTorso`；剩余时长严格 `>50` 就在该次效果回调中锁存 `surgeryReady` 并广播。保留服务端 Tick 兜底。
- `EndingFlow.Surgery.cs:17`：godmode 的健康重置保留手术患者腹部麻醉，按游戏时间扣减；只在现有 godmode 治疗循环中应用。
- 旧 Anesthesia 客户端剂量声明不再能提交就绪，必须有真实服务端医疗效果。
- `EndingFlow.Escape.cs:215` / `:258`：逐帧将权威就绪状态应用到 `lab.canStartSurgery`。
- 补 `Body.UseItem` / `Body.CombineLiquids` 探针；Body 本身没有 Inject 方法。实测 wound 路径走 Medical 的 Limb 动作。
- `GetClientIdFromBody` 虽然名字像 client ID，实际返回 NetBody ID（`Player.cs:520`）。本轮核实后撤销了多余的 wire ID 改动。

首轮真实双实例摘录（`build/ending-tests/release18-isolated-smoke/`）：

```text
client 08:27:53.179 [anesth] fixture wound-view helper=Body_EndingClient patient=Body_EndingHost item=saline contents=347
client 08:27:53.180 [anesth] ApplyWoundItem item=saline selectedLimb=DownTorso body=Body_EndingHost healer=Body_EndingClient server=False
host   08:27:53.195 [anesth] ForceApplyWoundItem item=saline healer=Body_EndingClient limb=DownTorso body=Body_EndingHost server=True
host   08:27:54.180 [anesth] effect id=localanestheticDownTorso remaining=50.880 player=NAME_456E64696E67486F7374 server=True
host   08:27:54.180 [escape] torso anesthesia carrier=NAME_456E64696E67486F7374 remaining=50.880; surgery immediately ready (server effect)
host   08:27:54.195 [ending] surgery operator=NAME_456E64696E67486F7374 patient=NAME_456E64696E67486F7374 mask=0
host   08:27:55.408 [ending] surgery operator=NAME_456E64696E67436C69656E74 patient=NAME_456E64696E67486F7374 mask=0
```

原 A/B 日志在 `build/release18-prior/host.log` / `client.log`，含实际小剂量注射和仍拒绝手术的历史记录。

## layer10

触发入口是 `ServerMain.CheckIfEnoughPeopleAreAtLayerFinish` 的统一底部判定，
`ServerMain.UpdateSyncClientsToClients` 使用它设置 `can_autocontinue`，随后直接调用 `RegenerateWorld`。
这条分支绕过了已有 `DidHeFinishTheLayer` 的 EndingFlow 保护。

- `ServerMain.cs:626`：EndingFlow 的 layer10 拒绝通用计数/自动续关，保留核心玩家顶部突破流程。
- `ServerMain.cs:642`：通用位置函数在 layer10 严格使用 `y > halfHeight - 5`；其余层保留底部条件。
- `EndingFlow.Travel.cs:78` / `:92` / `:110`：分别记录底部拒绝、核心玩家顶部突破、完整突破演出后的 `RegenerateWorld`。
- 自动续关和 ContinueRun 也加 `[layer] transition source=...`，便于确认真实入口。

`Release18EndingTests` 会生成真实 layer10，主客机在底部停留、检查没有续关面板和重生成；
再测试非核心玩家到顶仍等待，核心客机到顶才启动突破演出，并等待双方真正生成 layer11。

## 自动开两个可测窗口

```powershell
cd F:\cu-port\src\casualties-together-main
.\scripts\Start-EscapeTest.ps1
# 已构建时：
.\scripts\Start-EscapeTest.ps1 -SkipBuild -Run my-escape-test
```

- 自动构建 PlaytestFullgame、确认角色面板、联网并进入生成的实验室。
- A：有芯片，背包有 `shotgun` / `boxof12gauge`，通过 `godmode 1` 开启 godmode。
- B：无芯片、唯一 cargo 核心玩家。
- A/B 位于 `x=22/23`，在 `x<17` 科学家剧情触发条件之前。大厅实际地面在 `y≈0`，由真实 scientist 场景绑定计算放置高度；最终自然落地坐标是 `(22,-0.38)` / `(23,-0.76)`。`y=234` 属于之后的逃亡区域，不能当大厅入口。完成后释放夹具定位和移动限制，并等待 3 秒验证自然落地、垂直速度和未触发剧情。
- 现有 godmode 是服务端全员健康保护命令，因此 B 也受到保护。
- 需要手动：B 向左走过入口触发剧情，阅读对话，再用 A 的枪射击科学家。角色确认/进入关卡无需手点。
- 脚本打印 PID、host/client 日志和截图路径，默认独立端口 7799，可通过 `-Port` 修改。
- 重复运行前关闭已准备的测试窗口，或指定空闲 `-Port`；脚本会拒绝已占用端口，避免客机误加入上一次会话。
- 使用仓库 `build/ending-tests/<Run>/runtime-A|B` 的独立 BepInEx 加载目录及独立存档。通过 Doorstop 参数启动指定 A/B 的原游戏程序，不改已安装 DLL，不影响原会话。

## 构建与运行证据

构建：`NUGET_PACKAGES=F:\cu-port\.nuget`，指定 `E:\tools\dotnet\dotnet.exe` 和仓库 `SolutionDir`。
PostBuild 输出限定到仓库 `build/ending-ship` / `build/ending-playtest`。

ShipFullgame：0 error、104 warning，`build/release18-ship.log`。
PlaytestFullgame：0 error、100 warning，`build/release18-playtest.log`。
四个 PowerShell 脚本通过 Parser 检查；Ship DLL 不含 Release14EndingTests / Release18EndingTests / EndingFlowTests 类型名称。

最终构建 SHA256：

```text
Ship     30E9080ACA5E06277428FA02F29AED6176532DB2199A752B3380DBC12968130D
Playtest 2920205993B0733C538584E5CA2CCB8A94615B3B5E8E4D9F35E6F29FAFD162FF
```

最终回归 **5/5 PASS**，五个场景使用同一份上述 Playtest DLL，结果写到 `build/release18-final-results.json`。
`build/release18-manifest.json` 记录 SHA256、各场景结果和可测窗口 PID；`build/release18-evidence.txt` 汇总真实注射链、就绪和换层日志。
固定交付文件为 `build/release18-delivery/ship/CasualtiesMP.dll` 和 `build/release18-delivery/playtest/CasualtiesMP.dll`。
Playtest 从上述五项实测的运行快照复制；独立交付目录避免常规构建输出被其它回归重新生成后混淆 SHA256。
全量检查最终五组 host/client 日志，未发现测试 FAIL 或 NullReference/MissingReference/IndexOutOfRange/InvalidOperation 等运行异常。

各场景日志目录均在 `build/ending-tests/` 下，含 `host.log`、`client.log`、`pids.txt` 和 `result.json`：

| 场景 | 日志目录 | 关键截图（同目录） |
| --- | --- | --- |
| 客机帮助主机核心玩家 | `release18-final-r14-escape` | `client-injection.png` |
| 主机帮助客机核心玩家 | `release18-final-r14-remote` | `host-injection.png` |
| 非核心死亡后，核心单人注射/手术 | `release18-final-r14-noncoredeath` | `host-injection.png` |
| godmode 下主机帮助客机核心玩家 | `release18-final-r14-god-assist` | `host-injection.png` |
| layer10 底部/顶部及进入 layer11 | `release18-final-r18-layer10` | `host-layer10-bottom.png`、`client-layer10-bottom.png`、`host-layer10-top.png`、`client-layer10-top.png`、`host-layer11-arrived.png`、`client-layer11-arrived.png` |

五项分别用时 86.43s / 89.95s / 86.14s / 92.04s / 79.68s，全部通过。
前四项也验证七个原版手术步骤、同步 cargo 移除、门与计时器、真实出口条件、叙事和全员结局。
最终 godmode 场景日志摘录：08:46:22.925 godmode 生效；08:46:24.216 累计 8ml 仍拒绝；
08:46:24.242 服务端患者剩余 **50.670s**，当次立即提交就绪。

最终 layer10 主机日志：

```text
08:47:34.137 [layer] transition denied source=EndingFlow from=10 reason=bottom topRequired=507
08:47:41.316 [r18-test] PASS layer10 bottom stays layer10/Cargo for both players
08:47:41.316 [r18-test] PASS bottom cannot satisfy generic finish/autocontinue
08:47:41.316 [r18-test] PASS bottom does not open continue panel
08:47:43.580 [r18-test] PASS non-core top waits for cargo carrier
08:47:43.610 [layer] transition source=EndingFlow.Breach from=10 to=11 carrier=NAME_456E64696E67436C69656E74 y=507.9923 topRequired=507
08:48:02.668 [layer] transition source=EndingFlow.RegenerateWorld from=10 to=11 after=breach-presentation
08:48:07.611 [r18-test] PASS party entered layer11 through top breach
```

用户实机窗口准备已实测通过：

- 最终启动命令：`.\scripts\Start-EscapeTest.ps1 -Run release18-user-ready -SkipBuild -Port 7801`。
- 最终保留窗口：A PID **65484**，B PID **65384**，端口 **7801**。端口 7799 已有其它会话，因此使用独立空闲端口。其它会话未被终止。
- `build/release18-user-ready.log`：脚本打印 PID / 日志并返回 READY。
- `build/ending-tests/release18-user-ready/host.log`：08:56:35.832 通过芯片/cargo、两种物品、godmode 许可检查；08:56:38.837 通过释放控制后的真实地面稳定检查。
- 同目录 `client.log`：08:56:38.845 通过客机自然落地及无芯片 cargo 检查。
- 截图：同目录 `host-escape-ready.png` / `client-escape-ready.png`，已视觉检查实际大厅地面和双玩家。
- 所有 test save / runtime / 日志都位于仓库 `build/ending-tests/`。

### 测试边界与夹具修正

- 这些是两个真实游戏进程、真实生成场景、生产 RPC 和原版小游戏回调测试；位置由夹具设置，不代表完整徒步通关。
- 首轮 smoke 确认了注射链和开面板；原 R14 夹具可能错过瞬时 stage1，已将等待改为单调 `>=1`。
- 第一轮反向协助已确认麻醉即时就绪，但开面板时客机仍被其旧定位夹具固定在手术房出生点；已增加客户端手术位置确认，生产距离校验保持不变。
- Windows computer-use 对游戏访问被工具拒绝。实际运行和截图均由游戏内测试夹具完成，没有把静态分析当作实测。
