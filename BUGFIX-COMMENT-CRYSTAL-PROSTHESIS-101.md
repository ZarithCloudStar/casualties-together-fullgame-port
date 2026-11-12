# 评论区水晶 / 假肢修复：修复与定向复测

2026-11-12；Casualties: Unknown 1.0.1；mod 5.5.0-cu-port。

**代码修复和双实例机制验收通过；真实 UI 拖拽、肉眼/听感验收尚未完成。** 本轮先读 `COMMENT-BUG-TEST-REPORT.md`，再用原发布 DLL 取证，最后修改源码。仅写本仓库；未向 `D:\steam`、`F:\cu-port\CU_Fullgame` 或 `CU_Fullgame_B` 部署。运行实例均来自 `build/comment-bug-tests/runtime-101/`；只结束各 run 的 `pids.txt` 所记录且路径匹配的测试进程，测试结束已全部关闭。

## 1. 复现及原报告的必要修正

原发布 DLL 的补充取证：`build/comment-bug-tests/baseline-evidence2-20261112-200742/`。

- 主机上 FRIEND 的 12 条肢体没有假肢；客机本地 12 条均为 peglimb。
- 客机本地 `DownArmF=50.000` 随后回到 `99.608`，重现服务器快照覆盖。
- **原报告“没有任何客机爆炸表现入口”的推断不完整。** 原 DLL 已有 `WorldGeneration.CreateExplosion → TrapClaim.HostBoom → CreateExplosionEffect → ForceCreateExplosionEffect` 通用广播。本轮观察日志实际捕获了客机 `CreateExplosion(range=16, velocity=20, muscleDamage.max=0)` 和 `ForceCreateExplosionEffect` 调用。因此不能把原报告的“完全不播放”作为本轮再次实证的结论。
- 通用特效函数虽然把伤害参数置零，却仍调用整个原生爆炸，会处理地形、速度、ragdoll、shock、意识/听力等。`CrystalSync` 确实没有独立的 unstable **纯表现**事件。原报告指出的表现与权威职责混杂，不能通过再叠加一次完整爆炸来处理。

初次启动受当前沙盒的 Windows PlayerPrefs/Discord 注册表权限影响。隔离 harness 对 PlayerPrefs 写入使用无副作用的测试替身，并把 mod 存档 getter 指向各 run 下的 `save-host/save-client`。这些补丁只存在于测试 harness，不进入交付 DLL；没有绕过权限去写用户注册表或存档。日志中的 Discord 注册表/麦克风报错与本次补丁无关。

## 2. Bug 1：不稳定水晶

### 改动

- 在 **原生 `CrystalUnstable.Update` 实际调用 `CreateExplosion` 的指令位置**接入 `CrystalSync.ExplodeUnstable`。没有按销毁、亮度或客户端计时猜测爆炸。
- 服务端完成原生爆炸后，给距离 `max(60, range × 2.5)` 内的客机发送可靠有序 `CLIENT_CrystalEffect kind=7`，携带位置、范围、声音。
- 客机仅复用原生表现入口的资源和参数：`Special/ExplosionParticle`、`Special/blastmark`、爆炸声音、耳鸣、`Shaker.Shake`。声音沿用现有 mod 的空间位置修正和音量倍率。
- 这个函数不调用 `CreateExplosion`，不改血量、意识、听力状态、速度、ragdoll 或地形。伤害请求和服务端结算继续走既有 `TrapClaim` 路径。
- 通用爆炸包附带水晶表现标记，跳过旧 `ForceCreateExplosionEffect`，避免同一水晶再播放/执行完整爆炸。
- **保留命中判定顺序**：通用包同时携带爆炸水晶的注册 id/version。客机在伤害命中检查前关闭对应水晶副本的 collider，与原生服务端顺序一致，防止已爆炸的水晶自己挡住射线。未完成这一步时夹具曾记录 `mask=0`；最终版本记录 `mask=32767`、服务端接受一次。
- kind 1–6 的包布局和分支、重力模拟/重力环代码均保留。

### 最终 DLL 双实例数据

证据：`build/comment-bug-tests/final-collider-order-20261112-202815/A.log`、`B.log`。测试前置为清出一块场地并铺地板、把双方移入；生成真实 prefab 和真实 `CrystalUnstable`，由身体碰撞触发倒计时，**没有用夹具调用爆炸结果函数**。

```text
host: [observe-boom] range=16 velocity=20 muscle=30
host: [observe-sound] clip=explosion twoD=False volume=1.5
host: [observe-shake] amount=320
host: [observe-sound] clip=tinnitus twoD=True volume=1
host: [observe-shake] amount=320
host: [observe-boom-accept] accepted=True

client: [observe-boom-mask] mask=32767
client: [observe-crystal-effect] before range=16 muscle=50 velocity=(-19.91, -5.07)
client: [observe-sound] clip=explosion twoD=False volume=1.5
client: [observe-shake] amount=320
client: [observe-sound] clip=tinnitus twoD=True volume=1
client: [observe-shake] amount=320
client: [observe-crystal-effect] after muscle=50 velocity=(-19.91, -5.07) particles=2
```

该轮只捕获一次纯表现回调，无客机 `CreateExplosion` / 旧 `ForceCreateExplosionEffect` 调用；`particles` 是现场 ParticleSystem 组件总数，**不是爆炸播放次数**。表现函数前后肌肉与速度完全相同；伤害包的既有本地预测/请求仍与纯表现函数分离。

落地后抽样（包括爆炸及随后摔落，不能当作单次爆炸伤害公式）：

| 同一个 FRIEND 身体 | 服务端镜像 | 客机本地 |
| --- | ---: | ---: |
| DownArmF.muscleHealth | 77.0 | 76.8 |
| brainHealth | 88.31 | 88.25 |
| 位置 | (86.085, 423.241) | (86.085, 423.240) |

两端在既有健康包量化和异步抽样误差内一致；并非要求两个不同玩家随机伤害相同。肌肉健康为 1 字节量化，步长约 0.392；普通脑健康仍使用原有截断量化。

重力定向检查也在该轮完成：客机收到 `CrystalGravity received=True`，无玩家输入时位置从约 `(86.085,423.241)` 移到 `(85.915,423.241)`；记录到本地重力环 `ring=0.85@29.0`。这只覆盖一个受控生成的水晶，不代表已验收自然水晶层的全部效果组合。

## 3. Bug 2：假肢状态与服务端修复

### 根因与改动

1. `CharacterLimbHealthState` 原本没有假肢定义。新增 unmanaged `ProstheticHealthState`，纳入现有整体 HealthSync 的 memcpy 序列化、反序列化及 Apply。包括 `hasProsthetic`、`itemId`、`destroyItem`、`appearancePath`、`forceChange`、`armorChange`、`maxHoles`、RGB。字符串采用定长 UTF-8 容器，不把托管引用放进 memcpy 包。
2. 原生 amputee 初始化只处理本机摄像机身体。**仅添加下行字段并不能让服务器凭空知道客机假肢**，还会把“无假肢”覆盖到客机。现在新建服务器客机镜像时按服务端 amputee 设置初始化四个假肢根、共 12 条肢体；其后的存档恢复照常覆盖初始状态，不信任客户端上传任意假肢定义。
3. Apply 按单条肢体赋值/更新 overlay，不反复调用会再附肢/治疗整条肢体的 `AttachProsthetic`。非假肢定义中的原生医疗面板底色也保留。
4. 新增 `ProsthesisRepairMinigame` 服务端会话及 MedicalSync 请求（保留原消息编号，使用预留 `___24`）。材料可修的假肢类型及补片数，来自服务端执行原生 `TryProsthesisRepair` 的参数，客户端不能自行提交。
5. 每次原生 UI 成功补洞/修骨后发送 **会话 id、顺序号、修骨标志**；不上传任意 `muscleHealth` 或耐久值。服务端校验会话、顺序、材料、剩余补片、当前耐久、假肢类型、肢体状态、意识、物品可达性和医疗交互距离，再按原生公式改健康/骨折计时/耐久，发布物品及 HealthSync。
6. 原生小游戏的孔洞、补片、音效和交互分支保留；原生的健康/耐久写指令改走权威请求。联机客户端不会先写本地数值再等待回滚；离线仍执行原生写入。小游戏在会话确认前等待，不消耗补片。
7. ApplyWoundItem 请求改为可靠有序，与后续 MinigameStart/进度请求保持顺序。
8. 肌肉健康的 1 字节量化改为四舍五入，保证服务端 100 在客机仍为 100，能满足原生 `>=99.9` 自动结束条件，而非变成 99.608。

### 最终 DLL 定向机制测试

完整证据：`build/comment-bug-tests/final-medical-clean-20261112-203300/`，包括 `A.log`、`B.log`、`cmd-host.txt`、`cmd-client.txt`、`mechanism-result.txt`、两端隔离存档。

**操作边界：这不是实鼠 UI 验收。** 夹具通过原生 `PlayerCamera.ApplyWoundItem` 打开真实修复界面，等待真实网络会话，然后对界面已生成的补片/孔洞设置合成手部释放，执行原生 `ProsthesisRepairMinigame.Update` 成功分支。原生补洞到网络到服务器再回客户端的链路都是真实的；`R → 点肢体 → 拖物品` 的输入命中没有在本轮完成。

所有用例均以服务端损伤肢体为前置；测试材料用服务端 `give` 提供。服务器和客机双方均识别 12 条 peglimb，小游戏读取到 `maxHoles=10`。

| 用例 | 服务器 limb.muscleHealth | 修复者材料耐久 | 客机结果 |
| --- | --- | --- | --- |
| 客机自修 DownArmF，一个补片 | 50.000 → 60.000 | 自己的 stick 1.0000 → 0.7515 | 收到约 60；后续快照未回到 50 |
| 主机修客机 DownArmF，一个补片 | 60.000 → 70.000 | 主机 stick 1.0000 → 0.7515 | 收到约 69.8；假肢仍为 peglimb |
| 主机自修 UpArmF，一个补片 | 50.000 → 60.000 | 主机同一 stick 0.7515 → 0.5030 | 主机保持 60，客机镜像约 60 |
| 客机用 woodscraps 连续补五洞 | 50 → 60 → 70 → 80 → 90 → 100 | 1 → .9015 → .8030 → .7045 → .6060 → .5075 | 到 100 自动结束小游戏 |
| 客机修骨折假肢两次 | 肌肉保持 50；骨折计时 15 → 5 → MendBone | woodscraps .5075 → .4090 → .3105 | `broken=False`，小游戏结束 |
| 重复 seq=1 / 错会话 65000 / 越序 seq=3 | 均保持客机第一次修后的 60.000 | 均保持 .7515 | 未重复扣耐久、未重复治疗 |

客户端显示前会收到权威 HealthSync；采样中约 `59.98` / `69.8` 是包量化及帧内原生变化，不是修复值回滚。测试最终仍保留 12 条 peglimb。合成手部测试不证明真实鼠标命中、拖拽体验或屏幕最终文案。

### 假肢存档检查

先写原生磁盘 checkpoint，再捕获并应用 `PlayerSavedState`，最后人为清除测试肢体假肢/健康并调用原生 body-only 存档恢复：

```text
[mechanism-memory-save] restored=peglimb muscle=50.19608 expected=50 holes=10
[mechanism-disk-save] loaded=True restored=peglimb muscle=50 holes=10
```

覆盖的是假肢定义和健康恢复。夹具在同一活跃会话反复强制恢复物品，可能生成重复测试物品；不把该操作当作完整背包/正常重连验收。完整背包、旁观、沙盒、存档菜单等旧修复未做无关全量回归；对应实现及原有入口/防护保留，仍需后续正常实机流程确认。

## 4. 源码范围与构建产物

本轮生产源码仅以下 9 个文件：

- `Compatibility/CrystalSync.cs`：真实爆炸指令接入、kind 7、纯表现、客机水晶碰撞体生命周期。
- `ClientMain.cs`、`Extras/TrapClaim.cs`：分离水晶表现与既有爆炸伤害通道，不重复完整爆炸。
- `Compatibility/ProstheticHealthState.cs`（新增）：完整假肢定义、非假肢底色与 overlay Apply。
- `Compatibility/ProsthesisRepairAuthority.cs`（新增）：材料元数据、会话预算、权威进度请求、原生写指令接入。
- `Content/NetBody.cs`：新客机镜像 amputee 前置。
- `SyncSystems/Medical.cs`：健康包字段、Apply、请求接收、请求顺序、肌肉量化。
- `Patches/MinigamePatches.cs`：服务端假肢修复会话。
- `Net/MSG_ID.cs`：预留消息别名，不重排旧编号。

上面路径均相对 `ScavPrototypeMP/`。网络健康包布局改变，**主机和客机须使用本次同一 DLL**；不要与旧版同号 DLL 混用。

采用仓库既有 SDK-less Roslyn 构建流程（本环境无 SDK/MSBuild），使用现有 publicizer 输出和 1.0.1 引用。最终结果：

| 配置 | 结果 | DLL SHA256 |
| --- | --- | --- |
| ShipFullgame | 0 error，errors.log 0 字节 | `C5F81552BC4EAE4329769DD2466CB38F8DBCA7F4191A46EA89D78E5CFE5E276B` |
| PlaytestFullgame | 0 error，errors.log 0 字节 | `82F93C88D5DFD784D895211890A6A27B31B7E455718FA67FE69C6CB60253BACA` |

产物：`build/bugfix-ShipFullgame/CasualtiesMP.dll`、`build/bugfix-PlaytestFullgame/CasualtiesMP.dll`。最终双实例测试使用 ShipFullgame，运行目录中 DLL 哈希对应上表。无补丁失败、IL 编译错误、索引越界或测试命令异常（最终 clean run）。

复跑（只操作隔离副本）：

```powershell
& .\scripts\Build-OfflineRoslyn.ps1 -Configuration ShipFullgame
& .\scripts\Build-OfflineRoslyn.ps1 -Configuration PlaytestFullgame
& .\build\comment-bug-tests\harness\build-fix-harness.ps1
& .\build\comment-bug-tests\Start-FixPair.ps1 -Run retest-medical -Port 8002 -Amputee
& .\build\comment-bug-tests\Test-RepairMechanisms.ps1
```

`Test-RepairMechanisms.ps1` 的 PASS 明确限于机制测试，不代表实鼠 UI 通过。它不自动终止进程；收尾时只使用该 run 的 `pids.txt`，并核对进程路径仍在隔离 runtime 下。

## 5. 仍待实机确认

- **真实 UI 三组**：客机 `R → 点肢体 → 拖修复材料` 自修、主机修客机、主机自修，各完成至少一个补片并核对 UI/耐久/数值。
- 爆炸粒子、音效、抖屏的肉眼/听感并排比较。本轮验证了真实对象与调用参数，没有录屏或听感证据。
- 自然水晶层、大量效果组合、Steam/P2P、高延迟/丢包、跨版本拒绝、中途入场等未覆盖；本轮没有跑无关全量回归。
- 正常存档菜单恢复、完整背包恢复及既有旁观/沙盒全链路不列为本轮已重新通过。

本轮尝试使用 computer-use 技能选择隔离游戏窗口。自动审批拒绝了打开隔离客机窗口，工具原文为 **`Computer Use was not approved to use casualtiesunknown`**；未使用自制 UI 注入绕过，也没有把机制测试冒充真实拖拽。没有尝试操作用户游戏副本或 `D:\steam`。
