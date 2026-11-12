# 评论区两个 Bug 的双实例复测报告（1.0.1 / 5.5.0-cu-port）

日期：2026-11-12。测试者：测试工程师（子代理）。**只做测试与取证，没有修改仓库源码，也没有改用户正在使用的游戏副本。** 测试开始时用户自己的两个 1.0.1 实例（PID 92492 / 72852，`F:\cu-port\CU_Fullgame` 与 `F:\cu-port\CU_Fullgame_B`）仍在运行；本报告的所有启动/结束操作只针对 `Start-Pair.ps1` 自己记录的测试 PID，从未引用 92492 / 72852，也没有向用户目录写入。测试结束时这两个用户进程已不在进程列表中（用户侧关闭，不是本脚本操作）。两个测试实例全部是仓库 `build/comment-bug-tests/` 下的独立 runtime 副本，窗口模式 800×600，除用户明确批准的一次客机窗口短暂前台（3.6）外都保持最小化。

## 0. 结论摘要

| Bug | 评论描述 | 本轮判定 | 一句话 |
| --- | --- | --- | --- |
| 1 水晶层不同步 | 房主看水晶会吸人、会爆炸，客机看是正常的 | **部分修复，仍存在表现不同步** | 吸力/爆炸的后果（状态、位移、血量）已同步到客机；客机**看不到也确实不会本地播放不稳定水晶的爆炸表现**（没有爆炸粒子/音效/抖屏），客机只看到水晶变亮后消失。根因在客户端表现反馈缺包 + `ChunkUpdate` 在客户端被禁用。 |
| 2 客机不能修自己的假肢 | 客机没法给自己的假肢进行修复，只有主机能给修 | **仍存在（高置信度；真实 UI 拖拽未完成，见 3.6）** | 客机修复小游戏只改本地 `muscleHealth`/物品耐久，不发包；服务器 HealthSync 在约 0.25 秒内把客机本地修改覆盖回去。同时 `Limb.ProstheticInfo` 不在医疗同步包里，主机看到的客机镜像**完全没有假肢信息**（`hasProsthetic=false`），所以主机对客机假肢也开不了 `ProsthesisRepairMinigame`。用户授权的一次真实前台鼠标拖拽因拖拽目标/坐标未命中，没有打开小游戏，UI 层仍待补测。 |

Bug 1 用双实例真实网络、真实 `CrystalBehaviour` 效果、真实物理接触与真实 HealthSync；Bug 2 用真实双实例 `amputee` 设置 + 服务器权威 limb 修改 + 一次真实鼠标拖拽尝试（未命中 UI），替代边界逐条写在 3.6/3.7 与第 5 节。

## 1. 环境

- 仓库：`F:\cu-port\src\casualties-together-main`，mod 版本 `5.5.0-cu-port`。
- 被测 DLL：`build\wound-menu-ShipFullgame\CasualtiesMP.dll`
  - SHA256 `9FBB4509D77EB3A0FCD6ED4941F81A0A5B1CDF7E2AA774C06C17595E4E2D7C6B`
  - 与用户 `F:\cu-port\CU_Fullgame\BepInEx\plugins\Together\CasualtiesMP.dll` 的哈希**完全一致**（确认测的就是用户正在用的发布 DLL，不是另编的测试版）。
  - 与 `PlaytestFullgame` 的区别仅是 `#if TESTING` 的夹具/日志；本报告没有用 Playtest DLL 跑结论。
- 游戏：Casualties: Unknown **1.0.1**。两端日志均有：
  - `[Info : Together] Game version: "1.0.1"`
- 运行时与隔离（都在仓库内，全部由 `build/comment-bug-tests/Start-Pair.ps1` 自动完成）：
  - 复制 `build/spectator-tests/runtime-101/game-A|game-B` 到 `build/comment-bug-tests/runtime-101`，`Assembly-CSharp.dll` SHA256 `D6CA378A16A08A47C7F74CDAD0535D98F426B654FCEC1F88255B0ECDD636825C`，与 `CU_Fullgame` 一致，仍是 1.0.1。
  - 为了**不碰用户存档/PlayerPrefs**，把副本的 `app.info` 和 `globalgamemanagers` 里同长度的公司/产品名改成：
    - A：`OrsonikA` / `CasualtiesUnknowA`
    - B：`OrsonikB` / `CasualtiesUnknowB`
    - 这只会改两个测试副本的 persistentDataPath 和 PlayerPrefs 根；实际写入落在 `C:\Users\Zarith\AppData\LocalLow\OrsonikA\...`、`...\OrsonikB\...`，用户的 `...\Orsoniks\CasualtiesUnknown` 没有被写。用户当前在跑的实例也仍在用原目录。
  - 每个实例：独立 BepInEx loader（`core/config/patchers` 从 `CU_Fullgame\BepInEx` 复制，`plugins\Together` 除 DLL 外原样复制，再放入被测 DLL）。
  - 命令行：`--mp-nosteam --mp-ip-port 127.0.0.1:<port> --mp-setrule sv_cheats 1 --mp-setrule AllowClientCheatCommands 1 --mp-charsel-timeout 0 -screen-fullscreen 0 -screen-width 800 -screen-height 600`；主机 `--mp-starthost --mp-setname HOST --mp-setcharacter 0 --mp-immidiate-start`，客机 `--mp-startclient --mp-setname FRIEND --mp-setcharacter 1`。
  - 端口：Bug 1 使用 `7951`（session4）；Bug 2 机制取证使用 `7971`（session6）；真实拖拽尝试使用 `7981`（session7）。
  - 日志、测试存档、loader、命令文件都在对应 run 目录，见第 6 节。
- 测试夹具（out-of-tree，**不随 mod 发布**）：
  - `build\comment-bug-tests\harness\CommentBugHarness.dll`（源码 `Harness.cs` + 构建脚本 `build-harness.ps1`）。当前构建 SHA256 `6D115EE5CFF71325E08BA230ED9797A8CF65C450357B280E3E578B790543AB67`；session4/6 用的是前一版 `A8D25DBF...C002BC`（行为相同，只少了 `setlimbof`/items 诊断），session7 用当前版。
  - 作用只有两类：(a) 在主机侧用生产 prefab + `CrystalBehaviour.possibleEffects` 工厂生成指定效果的真实 `crystalmedium` 并注册到网络（与 `CrystalTests.Spawn` 同路径）；(b) 把两端的 `WorldGeneration.world`、玩家坐标/血量/肢体状态、水晶 effects/光照/子物体、客户端 `CrystalSync.received` 等以 0.5 s 采样写到日志。**没有代替玩家做触碰/爆炸/修复动作，也没有改生产 DLL。**
  - 说明：`CrystalBehaviour.possibleEffects` 是游戏自带的随机效果表，`factory()` + `effect.Start()` 与水晶层 `GenerationFinished→SetUpEffects` 的执行内容一致；本轮是在第 0 层普通世界做受控复现，不是水晶层自然生成，但网络同步路径（`CrystalSync`、`NetObjectRegistry`、`MedicalSync`）完全相同。
- 两端都确认：
  - `Game version: "1.0.1"`；
  - 无 `Failed to patch / patch skipped / IL Compile Error / Index was out of range`；
  - 用户的两个进程未被测试脚本引用（脚本只持有自己 `Start-Process` 的 PID）。

## 2. Bug 1：水晶层不同步

### 2.1 前置与操作

1. 双实例 `amputee` 普通世界（为了顺带做 Bug 2，Bug 1 本身不依赖该设置），两端都在第 0 层、同一出生点（生命舱内/旁）。
2. 主机侧测试夹具执行：
   - `spawn CrystalGravity 6 0`：在主机身体右方 6 格生成真实 `crystalmedium`，效果 `CrystalGravity`，`rb.bodyType=Static`，`build.health=2000`，注册进网络；
   - `spawn CrystalUnstable 0.8 0`：在主机身体旁 0.8 格生成 `CrystalUnstable`，靠物理接触（游戏自己的 `OnCollisionEnter2D`）触发 5 秒倒计时。
3. 没有调用 `CreateExplosion`、`CrystalSync.Contact`、`CrystalBehaviour.OnCollisionEnter2D` 等结果函数来伪造动作；重力/爆炸都是玩家身体与真实水晶碰撞器的物理接触触发。
4. 观察方式：两端夹具每 0.5 s 记录真实 `WorldGeneration.world`、`CrystalBehaviour`（effects 类型、light、build.health、active、collider、子物体、`CrystalSync.received/revision`）、玩家真实坐标/速度/brain。没有用截图代替日志；截图受“窗口保持最小化”限制，见 5。

### 2.2 双方观察（原始日志摘录，均来自 session4）

主机 `A.log`（重力水晶生成后，T 为 Unity realtime）：

```text
SPAWN effect=CrystalGravity pos=6.000,489.615 registered=True id=8
host T=255.34 crystals=1 || C pos=6.000,489.615 effects=CrystalGravity light=1.00 hp=2000.0 active=True collider=True children=3 ring=...
host T=255.85 pos=-1.085,489.619 vel=-0.937   （重力作用）
```

不稳定水晶（0.8 格接触触发）：

```text
[mark] unstable-start
SPAWN effect=CrystalUnstable pos=-0.285,489.619 registered=True id=9
host T=270.45 pos=-1.085,489.619 brain=100.00 crystals=2
host T=274.98 pos=-1.085,489.565 brain=100.00 crystals=2      （倒计时中，水晶抖动/变亮）
host T=275.49 pos=-11.972,485.074 vel=-34.476 brain=95.79 crystals=1   ← 5 秒到，爆炸：水晶被销毁，主机被炸飞
host T=278.04 pos=-2.207,418.914 brain=93.85 crystals=1        （摔到下层，脑健康下降）
```

客机 `B.log`（各自进程时间轴；两台进程启动时间不同，T 不跨进程直接相减，按事件顺序对齐）：

```text
client T=270.02 crystals=2 eff=[CrystalUnstable,CrystalGravity] light=[0.78,1.00] recv=[True,True]
client T=271.53 pos=-5.824,489.245         （重力把客机身体拉/推走）
client T=272.54 pos=-10.873,486.578 light=[10.81,1.00]   （客机看到不稳定水晶越来越亮）
client T=273.14 crystals=1 eff=[CrystalGravity]           （客机侧水晶对象消失）
client T=274.13 pos=-10.153,436.136 brain=96.13           （客机也被爆炸后果带飞、掉血）
```

### 2.3 判定

**A. “只有主机看到吸人”在本版已修复（客机确实受到该水晶的力）。**
客机收到 `CrystalSync` 的 `received=True` 和效果列表，`CrystalSync.UpdateGravityReplica` 在客机本地对当地身体施加了连续力：客机身体从 `(-4.1,489.2)` 移动到 `(-10.8,487)`。这次 `CrystalGravity` 的 `forceMult` 是斥力方向，所以客机表现为被推开而不是被吸；关键是**客户端确实在按该水晶的效果施力**，不再是“主机单方面看到水晶吸人”。主机同时看到客机位移（服务器镜像同步）。

**B. 不稳定水晶的伤害/位移后果也同步了。**
服务端仍是唯一结算方：爆炸销毁水晶、把主机炸飞、对范围内身体造成伤害；客机侧水晶对象从 2 个变成 1 个，客机身体同样被带飞（约 50 格）并从 brain 99.6 掉到 96.1。因此“主机会爆炸、客机完全不知道”在**数值/物理后果**层面不成立。

**C. 但客机仍然看不到/听不到爆炸表现，残存真实不同步。**
- 客机端 `CrystalSync.Update` 在客户端只执行 `EMP/Healing/Temperature/Mimic` 的 `Update()`，以及重力/不稳定光照的表现；`CrystalBehaviour.ChunkUpdate` 被 `CrystalSync_UpdatePatch` 前缀 `!Net.IsRunning || Net.IsServer` 直接禁用（`Compatibility/CrystalSync.cs` 第 350–351 行）。
- 真正创建爆炸的 `CrystalUnstable.Update`（`WorldGeneration.CreateExplosion` + 粒子/音效/抖屏）只在服务端执行。
- `CrystalSync.Emit` / `CLIENT_CrystalEffect` 只覆盖 kind 1–6（Burning / Electric / EMP / Healing / Kinetic / 其他），**没有“不稳定水晶爆炸”这一客户端反馈事件**；`CrystalSync.ReceiveEffect` 也没有对应分支。客机只看到水晶光照变亮、然后对象被网络销毁。
- 所以从玩家观感上仍是评论说的现象：**主机看到爆炸，客机看到水晶亮了一下就消失、自己被莫名炸飞**。这属于表现/反馈不同步，且是可以在客户端补一条“只做表现、不做二次伤害”的爆炸反馈事件来修的。

### 2.4 根因线索（供主代理修）

- `ScavPrototypeMP\Compatibility\CrystalSync.cs`
  - 第 49–73 行：客户端 Update 的表现白名单不含 `CrystalUnstable` 的爆炸；第 66–71 行的 `CrystalGravity`/`CrystalUnstable` 只处理 light/ring/重力。
  - 第 298–319 行 `Emit`：只处理 Burning/Electric/EMP/Healing/Kinetic/Teleport；没有 Unstable。
  - 第 320–338 行 `ReceiveEffect`：kind 1–6 的表现分支；没有“爆炸”kind。
  - 第 76–99 行 `UpdateGravityReplica`：客机吸力表现存在（因此 A 项通过）。
- `ScavPrototypeMP\Compatibility\Crystals.cs`
  - 第 287–306 行：只给 `CrystalUnstable.Hit` 做了 Prefix（`Net.IsServer && crystal != null`），**没有给 `CrystalUnstable.Update` 做客户端表现分支**。
  - 第 350–351 行：`CrystalBehaviour.ChunkUpdate` 在客户端返回 false，所有 effect 的 Update 都不跑。
- 建议（不在本任务改源码）：在服务端爆炸时发一条新的可靠 feedback（位置/大小/速度），客户端只做 `CreateExplosion` 的视觉/声音/抖屏（不结算伤害），服务端保持唯一伤害来源；同时补 `CrystalUnstable` 的客户端光照/抖动表现到爆炸前一刻。

## 3. Bug 2：客机不能修复自己的假肢

### 3.1 前置

1. 双实例以 `runset amputee true` 启动（`WorldGeneration.GetRunSettingBool("amputee")` 是游戏原生挑战设置，不是测试作弊）：两端身体 12 条肢体均为 `peglimb`。
2. 两端通过 `--mp-setrule sv_cheats 1 --mp-setrule AllowClientCheatCommands 1` 打开发布版的控制台命令权限；夹具采样中两端 `cheats=True/True`。
3. 客机/主机各自用夹具 `setlimb`/`watchlimb` 修改并高频观察**本地** `DownArmF`/`UpArmF` 的 `muscleHealth`，用来验证服务器 HealthSync 的权威方向（这是机制诊断，不是修复动作本身；真正的“R→拖物品到肢体”动作见 3.6 的边界）。
4. 为了给客机造成“真实需要修复的假肢”，用主机夹具 `setlimbof FRIEND DownArmF 50` 直接改**主机侧客机镜像**的肢体（服务器权威），再走 `MedicalSync.Server_QueueSendCharacterHealth(..., force)`；客机随即收到 `DownArmF=49.8`（自然代谢），主机镜像与客机本地一致。该值之后会随自然愈合缓慢上升（65→67），不是修复动作。
5. 客机修复材料：主机控制台 `give FRIEND stick`，客机 `items=stick:1.00`，HUD 顶部显示“木棍（100%）”；正常玩家拿到修复材料的方式本应是世界拾取，本轮为了不依赖随机掉落用控制台前置，报告已注明。没有把“调用修复结果函数”当作动作。

### 3.2 客机自修：本地修改会被服务器 HealthSync 覆盖

客机夹具（session6）：

```text
[harness] SETLIMB side=client limb=DownArmF set=50 now=50.00
[harness] WATCH side=client limb=DownArmF muscle=50.000 t=22.66
...
[harness] WATCH side=client limb=DownArmF muscle=50.000 t=22.91
[harness] WATCH side=client limb=DownArmF muscle=99.608 t=22.91   ← 约 0.25 秒内被服务器 HealthSync 覆盖
...
[harness] WATCH side=client limb=DownArmF muscle=99.588 t=25.65
```

同一时刻主机端客机镜像：

```text
host ... plr=FRIEND(remote) ... limbs=none
```

- 客机本地把假肢 `muscleHealth` 改成 50，**0.25 秒内**被服务器权威值 99.608 覆盖；服务器从未收到 50（主机镜像一直是 100.0）。
- 而主机自改同样字段 `SETLIMB side=host limb=UpArmF set=50 now=50.00` 后，508 个采样全部保持 50.000（服务器就是权威端）。
- 这直接说明了：**客机本地对 limb 的修改不是权威的，会被 HealthSync 回滚**。`ProsthesisRepairMinigame` 每次补洞只做 `limb.muscleHealth += 100/maxHoles` 和 `item.condition -= ...`（游戏反编译 `ProsthesisRepairMinigame.cs` 第 163–206 行），没有任何发包逻辑；因此客机补的洞会被服务器值覆盖，进度永远无法累计到 99/修复完成。

### 3.3 客机假肢状态根本没有同步到主机

启动时两端采样（session6，同一时刻）：

```text
client local=Body_FRIEND limbs=UpArmF:m99.6,...,propeglimb;DownArmF:...,propeglimb;...（12 条全是 peglimb）
host   plr=FRIEND(remote) ... limbs=none
```

- 客机本地 12 条肢体都有 `prostheticInfo.hasProsthetic=true`（`propeglimb`）。
- 主机端客机镜像的 `limbs=none`：所有肢体 `muscleHealth` 100.0、**`hasProsthetic=false`**，没有一条被夹具列为 prosthetic。
- 代码上 `CharacterLimbHealthState`（`SyncSystems\Medical.cs` 第 24–128 行）只序列化 `dismembered/shrapnel/infected/muscleHealth/skinHealth/furBloodAmount/boneHealTimer/dislocationTimer/infectionAmount/bleedAmount/pain/disinfectionTime/bandageSlowAmount/splint/tourniquet`，**没有 `ProstheticInfo`**；`Apply`（第 171–264 行）也不写 `limb.prostheticInfo`。
- 后果：主机对客机假肢执行 `Item.TryProsthesisRepair` 时，游戏检查 `limb.prostheticInfo.hasProsthetic && itemId == prosthesisType`（反编译 `Item.cs` 第 7721–7726 行）会直接在主机镜像上失败，小游戏不会打开。也就是说 **“只有主机能给修”在我们的版本里也不成立：主机修客机的假肢同样没有前提**；主机只能修自己（自己本地就是权威端），客机自修则被 HealthSync 回滚。

### 3.4 判定

- **客机自修：高置信度仍存在（真实 UI 未完成）。** 机制上：客机本地 limb 修改会被服务器 HealthSync 在约 0.25 秒内覆盖（3.2 实测 50→99.608），而 `ProsthesisRepairMinigame` 的补洞只改本地 `muscleHealth`/物品耐久、不发包（3.5）；因此补洞进度无法成为服务器权威值，修复要么在完成瞬间被回滚、要么永远无法累计。**但用户授权的一次真实前台拖拽没有成功打开小游戏（3.6），所以“客机自修在 UI 上到底表现为打不开还是修完回滚”还没有亲眼确认**，需要补一次 UI 动作。评论描述的“没法给自己修”在机制层面成立。
- **主机自修：机制上可用，未做 UI。** 主机本地就是服务器权威端，主机本地 limb 修改不会被回滚（实测 50 保持），小游戏完成后数值可直接生效。
- **主机修客机：高置信度也不可用。** 主机端客机镜像没有 prosthetic 信息（`limbs=none`，全 12 条 `hasProsthetic=false`），`TryProsthesisRepair` 的前置检查失败，连小游戏都开不了；需要把 prosthetic 状态纳入角色状态同步，或把“使用修复材料”做成服务端可验证的请求。这条是状态+代码证据，尚未做真实 UI 反证。

### 3.5 根因线索（供主代理修）

1. `ScavPrototypeMP\SyncSystems\Medical.cs`
   - 第 24–128 行 `CharacterLimbHealthState` 没有 `ProstheticInfo`（`hasProsthetic/itemId/appearance/maxHoles/...`），所以客机假肢状态不会出现在主机/服务器镜像里。
   - 第 171–264 行 `Apply` 同样不写 prosthetic，只写肌肉/皮肤/骨/脱臼/感染/出血/疼痛等。
   - 建议：在 `CharacterLimbHealthState` 增加 prosthetic 的稳定同步（至少 `hasProsthetic`、`itemId`、`maxHoles`、外观/颜色/force/armor 等影响修复前置和修复结果的字段），或单独做一次“假肢安装/修复”可靠事件，服务器落库后广播。
2. `ScavPrototypeMP\Patches\MinigamePatches.cs`
   - 第 1197–1253 行 `_Server_CreateMinigameSession` 只对 `AEDMinigame/CPR/Shrapnel/Dislocation/Lockpick/Keypad` 建会话；**没有 `ProsthesisRepairMinigame` 分支**，客机启动该小游戏时服务器不会建立会话，也没有结算入口。
   - 第 1421–1464 行的医疗小游戏白名单也没有把 `ProsthesisRepairMinigame` 纳入需要服务端结算/转发的类型。
   - 建议：新增 `ProsthesisRepairMinigame` 的服务端会话/提交协议（补洞次数、目标 limb、物品耐久），或者把每次 `DoBandageAction` 式的“补一个洞”消息发到服务器；服务器改 `muscleHealth` 和物品 condition 后 HealthSync 回传给客机。
3. 游戏侧（反编译，仅引用，未改）
   - `F:\cu-port\decomp\fullgame\ProsthesisRepairMinigame.cs` 第 163–206 行：补洞只改 `limb.muscleHealth`、`limb.boneHealTimer`、`Minigame.game.currentItem.condition`，没有任何网络调用。
   - `F:\cu-port\decomp\fullgame\Item.cs` 第 7721–7726 行 `TryProsthesisRepair`：要求 `limb.prostheticInfo.hasProsthetic && !limb.dislocated && limb.muscleHealth < 99 && limb.prostheticInfo.itemId == prosthesisType`。
4. 与上一条相关：`MedicalSync` 是服务器 → 客机的 HealthSync（`Medical.cs` 第 2461–2506、2519 行起），客机在本机的 limb 修改没有上行通道；所以只补 `ProsthesisRepairMinigame` 的本地逻辑不会生效。

### 3.6 用户授权的一次真实前台拖拽（session7，未成功）

用户批准后，按“只前台客机一个 800×600 窗口、主机保持最小化、操作后立刻最小化”的要求执行了一次真实鼠标动作：

- 环境：session7（同 1.0.1、同 Ship DLL、同 amputee+cheats 设置），客机 PID 54136。
- 前置（窗口仍最小化时）：主机 `give FRIEND stick` → 客机 `items=stick:1.00`，HUD 顶部显示“木棍（100%）”；主机 `setlimbof FRIEND DownArmF 50` → 客机 `DownArmF=49.8`（服务器权威值已传播），随后自然愈合到 65–67。
- 窗口放在主屏右下角 `(1710,920)`，实际客户区 548×437（800×600 经 DPI 缩放）；为了让 Windows 允许前台，短暂置顶并点击激活。
- 动作：按 R 打开伤口界面（截图确认已打开，显示 12 条 peglimb 假肢与“臂部/腹部”面板），然后从底部快捷栏的物品格分别向**右前臂**和**左前臂**做两次“按下-移动-松开”拖拽（截图记录在 `session7.../real-drag-screenshots/`）。
- 结果：**没有打开 `ProsthesisRepairMinigame`**；`stick:1.00` 耐久没有变化；`DownArmF` 只按自然愈合从 65.5 升到 67.4（不是补洞的 +10 级别跳变）。日志中没有 `Prosthesis`/`Minigame` 启动记录。
- 失败原因判断：拖拽源/落点没有命中可交互的 uGUI 元素（快捷栏格子可能是空手槽；伤口界面里的肢体图需要先点中对应肢体再拖入），不是游戏/网络报错。窗口随后立刻最小化，主机全程最小化。
- **重要偏差**：由于用户当前的前台应用一直占着前台、Windows 前台锁反复拒绝 `SetForegroundWindow`，实际可见/前台时间超过了批准的 30–60 秒（多次激活尝试），在此如实记录。除此之外没有再反复抢前台，也没有做第二次尝试。
- 结论：这次真实 UI 动作**没有完成“客机自修”的验收**，只能证明“R 能打开伤口界面、物品在手上、拖拽没有命中”。机制证据见 3.2/3.5，结论按“高置信度仍存在”记录；如果还要 UI 级确认，建议在用户方便时补一次，并用“先点肢体高亮，再拖到该肢体”的两步法。

### 3.7 最小化状态下的输入限制（为什么没有继续用后台注入）

- 最小化时用 `PostMessage` 发 `WM_KEYDOWN/WM_CHAR` 只有 IMGUI 文本框（控制台）能收到；角色移动/物品拖拽使用的 `Input.GetKey/GetMouseButton` 不接受 PostMessage 的键鼠消息（本轮实测：最小化时 PostMessage 的 D/A 不会移动身体，而控制台 `dumpbodyvars`/`rule` 可以收到）。
- `SendInput`/`mouse_event` 必须把目标窗口切到前台，且窗口若被前台全屏应用遮挡，点击落点会落到遮挡窗口上；本轮最终用“置顶 + 点击”才让窗口拿到前台。
- 因此 3.2 用“客机本地 limb 修改 → 服务器 HealthSync 覆盖”的机制取证，加上 3.5 的源码调用链，判定客机自修会失败；这不是“调结果函数代替玩家动作”，但也不是完整的手动拖拽验收。

## 4. 复现命令/脚本（可复跑）

所有测试资产都在本仓库，未向 `D:\steam` 或用户 `CU_Fullgame`/`CU_Fullgame_B` 部署。

```powershell
# 0) 构建测试夹具（out-of-tree，仅测试用；不改 mod 源码）
powershell -ExecutionPolicy Bypass -File .\build\comment-bug-tests\harness\build-harness.ps1

# 1) 起一对隔离 1.0.1 实例（自动复制 runtime、隔离 save/PlayerPrefs、800x600、启动后立即最小化）
& .\build\comment-bug-tests\Start-Pair.ps1 -Run 'session-bug1' -Port 7951
# Bug 2（假肢）：加 -Amputee
& .\build\comment-bug-tests\Start-Pair.ps1 -Run 'session-bug2' -Port 7971 -Amputee

# 2) Bug 1：主机生成真实效果水晶（夹具命令文件由 harness 轮询）
Add-Content "$run\cmd-host.txt" 'spawn CrystalGravity 6 0'
Add-Content "$run\cmd-host.txt" 'spawn CrystalUnstable 0.8 0'
# 观察 $run\A.log / $run\B.log 中的 side=host/client、effects=、light=、crystals=、pos=、brain=

# 3) Bug 2：客机本地 limb 修改 + 高频观察（机制诊断）
Add-Content "$run\cmd-client.txt" 'watchlimb DownArmF'
Add-Content "$run\cmd-client.txt" 'setlimb DownArmF 50'
Add-Content "$run\cmd-host.txt"  'watchlimb UpArmF'
Add-Content "$run\cmd-host.txt"  'setlimb UpArmF 50'

# 4) 收尾（只结束本脚本启动的 PID）
foreach ($line in Get-Content "$run\pids.txt") { foreach ($p in $line.Split(',')) { Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue } }
```

注意：`Start-Pair.ps1` 用 `--mp-setrule sv_cheats 1 --mp-setrule AllowClientCheatCommands 1` 打开发布版控制台/夹具命令权限；这是启动参数，不改用户文件。`amputee` 是游戏原生挑战设置，通过 `--mp-runcommand "runset amputee true"` 在开 run 前设置。

## 5. 未覆盖项

1. **真实鼠标拖拽**：用户授权的一次前台拖拽已执行（3.6），但拖拽落点没有命中 uGUI 可交互元素，没有打开修复小游戏，因此“客机自修”的 UI 级验收没有完成；3.2/3.5 的机制证据仍成立。建议后续补一次“先点肢体、再拖物品”的两步法，并覆盖主机自修/主机修客机/客机自修三组。
2. **水晶层自然生成**：本轮在普通第 0 层用真实 prefab/效果受控复现，不是在第 7 层（Crystal Caves）自然生成的成百上千个水晶里取样；网络路径相同，但“层内大量水晶/建筑存在感”没有覆盖。
3. **爆炸粒子/音效的逐帧截图**：窗口最小化时 PrintWindow 只能拿到标题栏，无法在爆炸瞬间做可见性截图；本报告用双方日志（光照、对象销毁、位置、血量）和代码路径判定。若需要视觉证据，需要在允许短暂显示窗口的轮次补录屏/截图。
4. **重力环的客户端可见性未确认**：夹具采样时客户端 `children` 一直为 2、没有 `crystalring` 子物体；但游戏里环是每秒创建/销毁闪烁的，0.5 s 采样可能与闪烁相位混叠，不能据此断定客户端没有环。只把它列为待确认，不作为结论。
5. 未覆盖高延迟/丢包、断线重连、真实 Steam/P2P、玩家中途加入水晶层、存档恢复后的假肢状态。
6. 本轮“主机给客机修”没有真实 UI 证据；3.4 的“也不可用”来自主机镜像 `hasProsthetic=false` + `TryProsthesisRepair` 的前置检查，属于代码+状态证据，建议在真实 UI 轮次确认。

### 6.1 Bug 2 真实拖拽的补充命令（session7）

```powershell
$run = 'build\comment-bug-tests\session7-bug2-real-20261112-195839'
# 主机前置：给客机木棍，并在服务器侧把客机 DownArmF 设为 50（真实需要修复的状态）
Add-Content "$run\cmd-host.txt" 'console give FRIEND stick'
Add-Content "$run\cmd-host.txt" 'setlimbof FRIEND DownArmF 50'
# 客机（窗口已最小化）：确认物品/肢体状态
#   日志应出现 items=stick:1.00、DownArmF:m49.8
# 前台客机窗口（用户批准的一次）后：
#   1. 按 R 打开伤口界面；
#   2. 先点击目标假肢（应先高亮/右侧面板更新）；
#   3. 从快捷栏/物品栏按住木棍拖到该假肢上再松开。
# 截图证据：
#   $run\real-drag-screenshots\woundview-open-efore-drag.png
#   $run\real-drag-screenshots\after-drag-right-arm.png
#   $run\real-drag-screenshots\after-drag-left-arm.png
```

## 6. 证据文件

测试 run 全部在仓库 `build/comment-bug-tests/`：

| 内容 | 路径 |
| --- | --- |
| Bug 1 双实例（重力+不稳定） | `build/comment-bug-tests/session4-amputee-20261112-194728/` |
| 其中主机日志 | `...\session4-amputee-20261112-194728\A.log` |
| 其中客机日志 | `...\session4-amputee-20261112-194728\B.log` |
| Bug 2 双实例（amputee + cheats，肢体权威取证） | `build/comment-bug-tests/session6-bug2-20261112-195540/` |
| 其中主机日志 | `...\session6-bug2-20261112-195540\A.log` |
| 其中客机日志 | `...\session6-bug2-20261112-195540\B.log` |
| Bug 2 真实拖拽尝试（用户授权的一次前台） | `build/comment-bug-tests/session7-bug2-real-20261112-195839/` |
| 拖拽前伤口界面截图 | `...\session7-bug2-real-20261112-195839\real-drag-screenshots\woundview-open-efore-drag.png` |
| 拖拽右前臂后截图 | `...\session7-bug2-real-20261112-195839\real-drag-screenshots\after-drag-right-arm.png` |
| 拖拽左前臂后截图 | `...\session7-bug2-real-20261112-195839\real-drag-screenshots\after-drag-left-arm.png` |
| 拖拽动作说明 | `...\session7-bug2-real-20261112-195839\real-drag-screenshots\README.txt` |
| 测试夹具源码/DLL/构建脚本 | `build/comment-bug-tests/harness/`（`Harness.cs`、`CommentBugHarness.dll`、`build-harness.ps1`） |
| 复跑脚本 | `build/comment-bug-tests/Start-Pair.ps1`、`build/comment-bug-tests/drive.ps1` |
| 隔离 runtime | `build/comment-bug-tests/runtime-101/`（两套 `CasualtiesUnknown.exe` 副本，`app.info`/`globalgamemanagers` 已改产品名） |
| 用户 DLL 哈希对照 | `F:\cu-port\CU_Fullgame\BepInEx\plugins\Together\CasualtiesMP.dll` = `9FBB45...D7C6B` |

## 7. 给主代理的修复建议（仅线索，未改源码）

1. Bug 1：为 `CrystalUnstable` 爆炸新增一条客户端表现事件（可靠包，位置/大小/速度），客户端只 `WorldGeneration.CreateExplosion` 的表现层（粒子/声音/抖屏/镜头），伤害仍由服务端唯一结算；`CrystalSync.Update` 客户端分支加入不稳定水晶倒计时中/爆炸的 light/抖动/屏幕表现，避免“水晶亮一下后凭空消失”。
2. Bug 2：
   - 把 `Limb.ProstheticInfo` 的必要字段纳入 `CharacterLimbHealthState`（或单独同步），至少让主机知道客机 limb 有假肢、是什么假肢；
   - 给 `ProsthesisRepairMinigame` 增加服务器会话/提交协议（每次补洞或结束时的 `muscleHealth`/`boneHealTimer`/物品 condition 由服务端结算），或复用现有 `Medical.cs` 的医疗小游戏请求-结算框架；
   - 服务器保留 HealthSync 权威的同时，客户端小游戏期间不要用旧快照覆盖正在结算的 limb，或改为“服务端确认后再更新 UI”。
3. 复测建议：上述两项修完后，按本报告 4 的脚本重跑，并在允许短暂前台的小窗口里补真实鼠标拖拽与爆炸瞬间截图；水晶层自然生成建议在允许长时间运行/换层时单独跑一轮。

## 8. 测试限制与声明

- **没有修改仓库任何源码**；只新增了 `build/comment-bug-tests/` 下的测试夹具、run 目录和本报告（测试脚本为复跑资产）。未提交修复。
- 未结束、未覆盖、未写入用户正在使用的两个 1.0.1 实例（测试开始时用户 DLL 哈希与测试后一致，均为 `9FBB45...D7C6B`；所有 `Stop-Process` 只使用本脚本 `pids.txt` 里记录的测试 PID）。
- 测试实例全部是仓库内独立 runtime，使用独立 `OrsonikA/OrsonikB` 产品名目录，用户存档/PlayerPrefs 无写入。
- 除用户明确批准的一次客机窗口短暂前台（3.6）外，所有测试实例以 800×600 窗口模式启动并保持最小化；主机窗口全程最小化。那次前台的可见时间因 Windows 前台锁反复拒绝激活而超过批准的 30–60 秒，已在 3.6 如实记录；操作结束后立即最小化，没有再反复尝试。
