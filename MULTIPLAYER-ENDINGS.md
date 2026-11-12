# 多人结局系统交付记录

2026-11-11 release14 更新：Escape 已正式启用，资格改为核心玩家无芯片；Merc/Escape 先全员播放原版剧情，再统一结算。超时改回原版放守卫，移除旧30秒救援判负。最新实现、T1–T6 证据与边界见 [RELEASE14-REVIEW.md](RELEASE14-REVIEW.md)，人工流程见 [RELEASE14-MANUAL-TEST.md](RELEASE14-MANUAL-TEST.md)。

以下是前一轮历史记录（包含已被 release14 替代的 Escape 条件、失败计时与测试预期），不代表当前启用状态。此前蓝心、出生点和对话修复见 [PORT-BUGFIX-REVIEW.md](PORT-BUGFIX-REVIEW.md)。

日期：2026-11-10。源代码改动限定本仓库；运行测试只使用 `F:\cu-port\CU_Fullgame` / `CU_Fullgame_B`。未操作 Steam 正式安装目录。

## 状态与权威

`Compatibility/EndingFlow.cs` 是唯一裁决入口，其他 `EndingFlow.*.cs` 分担换层、实验室、手术、结算和 Merc。全部功能位于 `#if FULLGAME` 中，并检查网络状态。单机与 demo 不启用这些规则。

| 阶段 | 进入条件 / 行为 |
|---|---|
| Exploring | 新跑团，尚未取得 cargo |
| Cargo | 服务端接受第一个有效蓝心请求，嵌入 cargo，清除所有端蓝心 |
| Breach | 关键玩家到达 layer 10 顶部；全员冻结并播放统一 16 秒剧情，主机广播换层 |
| Laboratory | 到达 layer 11；关键玩家到 exitPoint 触发 Biopod |
| Escape | 主机无芯片，服务端的玩家枪击射线命中 Scientist；全员警报 |
| Surgery | 只有关键玩家到入口才能进入；全队传送，服务端开始 60 秒计时，刷五个守卫 |
| Escaping | 手术七个步骤全部提交并由服务端接受；解锁手术室及物理大门；任意存活玩家越过大门即 Escape |
| Finished | 固定结局类型，保留存档，全部本地 EndSequence，禁止重新旁观/拖物/攻击/枪械操作 |
| Failed | 没有存活 cargo 接替者；不写胜利或结局 |

cargo 使用现有 `ScavPlayer.GetPersistentId()`，Steam 为 Steam ID、直连为现有的姓名 UTF-8 十六进制身份，不使用会复用的网络实体编号。`hasCargo` 与 cargo 模型在每端从同一份状态应用，最多一个持有者。最近队友按角色实际位置计算，死亡玩家的旁观摄像机位置不参与计算。掉线前清除旧角色模型；转移后重连不能重新获得旧 cargo。

普通探索死亡会转移 cargo；逃生中的关键玩家死亡/脑死亡直接进入 Biopod。关键玩家掉线仍转交存活队友，取出 cargo 后掉线也会转交关键身份。没有存活队友则跑团失败。守卫实际击倒关键玩家后开启独立 30 秒救援时钟，恢复站立和意识后清零；非关键玩家死亡不选择失败结局。

钻探仓采用原版 DrillPod 的仓内范围：相对中心 `|dx| < 1.5, |dy| < 5`。按钮操作者须在交互范围内；不要求操作者就是关键玩家。结局层 9–11 关闭普通底部出口 / ContinueRun，避免绕过 cargo 与顶层条件。

## 分阶段文件与验证

每阶段保存了构建日志，ShipFullgame 与 PlaytestFullgame 均达到 0 error。最终仍有原仓库 Harmony/编译警告，未把“0 error”描述成“0 warning”。阶段过程中发现的失败保留在测试目录，并在后续修复重测。

| 阶段 | 主要文件 | 自测证据 |
|---|---|---|
| 1 cargo / 存档 | `EndingFlow.cs`、`MSG_ID.cs`、`Plugin.cs`、`SavesystemPatch.cs`、`PlayerCheckpoint.cs`、`DeferredPlayerSaveRestore.cs`、`WorldObjectSnapshot*.cs`、`CharacterReadyGate.cs`、`CharacterRunMemory.cs`、EN/zh-CN | `stage3`；后续所有流程重复验证 cargo；`regression-disconnect` 验证真实断连转移及全灭无结局 |
| 2 钻探仓 / 顶层 | `EndingFlow.Travel.cs`、`ServerMain.cs`、`BuildingSynchronizer.cs` | `stage3-travel`：等待关键玩家、主机发车、实际生成 layer 10、强制剧情、实际生成 layer 11；客户端跟随 |
| 3 Biopod / 结算 | `EndingFlow.Results.cs`、`PlayerCameraPatches.cs`、`ClientMain.cs` | `stage3-travel`：关键玩家出口、两端 EndSequence(1)、各自 wonchar/didbiopod；`regression-reload`：重启读档重进结算、禁止旁观隐藏、统一回大厅、保留连接及 mp_save |
| 4 Escape | `EndingFlow.Escape.cs`、`EndingFlow.Surgery.cs`、`EndingGuardSync.cs`、`EndingLabObjects.cs`、`MinigamePatches.cs`、`CombatStuff.cs`、`ObjectSync.cs`、`WorldMP.cs`、`ScavLiteEntitySystem.cs` | `regression-escape-success`；`regression-escape-timeout`、`regression-escape-down`、`regression-escape-killed` |
| 5 Merc | `EndingFlow.Merc.cs` | `regression-merc`：主机未通关拒绝、主机前置通过、非关键队友持围巾、关键玩家顶部、两端 type 3 与本地元进度 |
| 6 回归 | `EndingFlowTests.cs`、`EndingTestPreferences.cs`、`scripts/Test-EndingFlow.ps1`、`scripts/Run-EndingRegression.ps1`、本记录 | 最终构建日志与上述双实例日志；已有 SteamworksNet、ApplyNameAndColor 的中文修复、keypad 权威裁决没有回退 |

## 消息表

仅给既有保留字节添加别名，未移动任何既有编号，`___6` 继续保留。普通换层仍复用原 `CLIENT_Announce_RegenerateWorld`，返回大厅仍复用 `CLIENT_GoBackToMainMenu`。

| 字节 | 别名 / 方向 | 载荷与用途 |
|---|---|---|
| 232 / `___7` | CLIENT_EndingState，S→C | 限长 JSON 状态；稳定关键身份、cargo、阶段、主机前置、倒计时、手术步骤、守卫存档状态、结局及完成标记。可靠有序，状态变化立即发，运行中每秒补发，供晚到玩家跟随 |
| 233 / `___8` | SERVER_EndingRequest，C→S | action byte + x/y float + value int。Heart=0、Drill=1、Door=2、Surgery=3、SurgeryProgress=4、SurgeryComplete=5。发送者取网络身份，不能指定另一玩家；服务端做生命/意识、阶段、距离、操作者和步骤验证及限频 |
| 234 / `___9` | CLIENT_EndingEvent，S→C | kind byte；Notice=0 另带本地化 key，Drill=1、Breach=2、Escape=3、Door=4、Surgery=5、Finish=6。先发权威状态，再发可靠有序事件 |
| 235 / `___10` | CLIENT_EndingGuards，S→C | 10Hz 有序丢旧包的姿态快照；网络 ID + 版本、位置/朝向、手臂/头部角度、健康、死亡/近战、移动 sprite。实体创建和攻击复用现有建筑同步 |

手术一次只允许一位操作者，但可以给另一位关键玩家手术。原版 CargoMinigame 的 4 个夹钳、1 次取出、2 根软管对应 7 个单调步骤。服务端校验目标几何、顺序、操作者距离和最短时间，不能仅凭一个“完成”包解锁。cargo 在取出步骤由服务器清除，计时停止；软管结束后开门。死亡/掉线的操作者释放锁，其他队友可接续已保存的步骤。keypad 使用的原有 tracker / session 路径未改为 cargo 路径。

## 存档与元进度

- 原版 `SaveInfo.isMercRun` 已存在，继续使用；延迟读档同时接纳新的 ending 状态中的 Merc 标记。
- 不改游戏程序集中的 SaveInfo 类。通过 SaveGame 后处理向其 JSON 增加 `endingFlow`、`hasCargo`、`cargoOwner`，因此每个 PlayerCheckpoint 与主机根文件都带有状态；`mp_rules.json` 增加可选 `ENDING`，世界快照文档增加可选 `endingFlow`，保持旧版本解析方式。
- `mp_save/ending_flow.json` 记录检查点之后的转移/手术/通关事件；与嵌入状态按更新时间选择较新者。写临时文件再替换，平台/沙箱不支持 Replace 时回退覆盖。
- 旧版本 layer 10/11 存档没有明确 cargo 身份时，迁移为主机持有并记录 `[cargo]` 日志；无法从不存在的数据还原旧关键玩家。新存档使用明确身份。
- 保存阶段、剩余时间、救援时间、手术 mask、操作者、五守卫计数和守卫位置/健康/死亡；恢复守卫时不再次掉落死亡战利品。守卫的弹药/瞄准内部计时不是逐字段序列化，恢复后使用新的 AI 时序。
- 实验室关键场景对象保留原生引用，由已有实体 ID 绑定到原对象，避免科学家/手术设备被客户端普通清理或快照重建拆掉。仅保护 EndLabScript 引用的设施及灯光；普通物品继续走既有清理/快照，避免读档凭空重新长出已拿走的物品。设施的门/手术行为由 ending 阶段重放。
- Finished 不删除 mp_save，保存 completed/type。各端用自己的角色写 wonchar、CHARWIN；Biopod 写 didbiopod/BIOPOD，Escape 写 didescape/COMPANYSWORST、全队未杀守卫时 PACIFIST，Merc 写 didmerc/TOBECONTINUED。死亡与旁观者一并结算。

## 双实例测试口径与记录

测试是两个真实游戏进程、真实 loopback 网络、真实 Harmony 与游戏协程，不是仅编译/静态推演。测试输入用夹具控制，以免每轮重打 0–9 层：在已生成世界设置 layer 9 并实例化原版 BlueHeartCont，之后通过真实 RegenerateWorld 生成 layer 10/11。最终一轮客户端也先创建蓝心，确认它在归属同步后被移除。

死亡输入直接设置脑健康；60 秒失败与 30 秒救援失败分别把计时器推进至临界点，验证归零分支，**没有声称实际等待完整 60/30 秒**。成功手术最终使用原版 CargoMinigame 的 held/handPos、PhysicsUpdate/Update 驱动夹钳与软管，通过正式 Harmony hook 发网络进度，不直接伪造一个完成包。科学家触发最终使用 TurretScript.Shoot 射线命中；没有自动操作玩家的真实鼠标/枪械库存。

| 用例 | 结果与日志目录（`build/ending-tests/`） |
|---|---|
| cargo 唯一、蓝心清除、身份广播、重复请求、写盘、死亡转移 | 通过：`stage3`，在后续成功/失败流程中重复 |
| 关键玩家不在仓内不发车；进入后全队 9→10→11 | 通过：`stage3-travel`、`regression-escape-success` |
| Biopod 两端强制结算与各自角色元进度 | 通过：`stage3-travel` |
| 主机有芯片拒绝 Escape，无芯片允许 | 通过：`regression-escape-success` 及失败分支 |
| 另一客户端给关键玩家手术、原版拖拽物理的七步同步、五守卫启用的 sprite、客机逃出触发全队 Escape | 通过：`regression-escape-success`；场景引用保护收窄后已重测 |
| 超时、守卫击倒无人救、关键玩家被杀 → Biopod | 通过：`regression-escape-timeout` / `regression-escape-down` / `regression-escape-killed`，两端均有结算和元进度断言；被杀用例主机死亡仍 EndSequence(1) |
| 真实网络断开 → cargo 转移；随后全灭不授结局 | 通过：`regression-disconnect` |
| 主机前置、远处非关键队友围巾、Merc 全队结算 | 通过：`regression-merc` |
| 重启加载已完成存档、两端强制结算、防旁观隐藏、防客机独自退出、统一回大厅、保留存档 | 通过：`regression-reload` |

自动化参数仅在 PlaytestFullgame + 显式 `--mp-ending-test` 下生效。测试存档通过进程环境写到仓库 `build/ending-tests/<case>/save`，PlayerPrefs 使用仅此模式下的内存替身，禁止测试授予真实平台成就，Discord 注册被停用。ShipFullgame 不包含测试类和环境覆盖。

可复现命令（先构建 PlaytestFullgame 到 `build/ending-playtest`）：

```powershell
.\scripts\Run-EndingRegression.ps1 -Scenarios @('merc','escape-timeout','escape-down','escape-killed','disconnect','reload','escape-success')
```

reload 用例会复制仓库中 `stage3-travel/save` 的通关夹具到自己的测试目录。脚本只停止自己记录的 A/B 进程，不按名称停止其他游戏进程。

## 自查与修复

1. 原版蓝心本地 Cutscene 可让两个进程各自 EmbedCargo：改为服务端接受第一个有效请求，状态统一应用。
2. 已断线玩家的旧检查点仍写 cargo：重新加入后由当前服务端归属覆盖；掉线角色模型立即去 cargo。最近接替者使用角色位置，不使用死亡后的相机位置。
3. 普通底部出口和 ContinueRun 可绕过结局条件：在结局层阻断，保留管理器的专用换层。
4. 原版 EndLab 客户端清理会拆掉 scientist/surgicalDevice 直接引用：保留并绑定关键场景对象；普通物品不纳入保护，防止存档物品幽灵/复制。
5. 原版守卫只追本地相机、客户端可能重复 AI：实验室守卫只在服务端运行 AI，逃生优先关键玩家；远端身体枪伤走服务端并强制健康同步，客户端接收姿态。保持其网络 ID，避免每帧在视野外释放后再创建。
6. 默认旁观入口会隐藏 endScreen：结局后禁止旁观、固定自己的结算身体，拦截世界操作，同时保留 pause / 主机回大厅通道。
7. 存档旁文件覆盖更新的检查点倒计时：按更新时间选取；读档仍等身体恢复完成后再检查角色死亡/缺席。
8. 第一次回大厅测试主机等候短于客机异步加载，断言在销毁后的 camera 上执行：调整测试等待，已重测通过。一次成功夹具仅移动布娃娃根节点被物理拉回：改用现有 ScavPlayer.pos / NetBody.SetBodyPosition，已重测通过。相关首次失败日志保留。
9. layer 10 的 `vein` 只有 `Special/veinloop` Resources 路径：补正式版映射及宽高同步，消除原空 prefab 实例化错误；demo 分支不改。
10. 尝试直连中文 CLI 名时，原有 ASCII SanitizeTextInput 拒绝名称；测试恢复英文直连名，没有扩改这个名称入口，也未回退 Steam 名称的 ApplyNameAndColor 修复。

## 构建与仍需实机验证

最终日志：`build/ending-final-ship.log` / `build/ending-final-playtest.log`。ShipFullgame 为 RELEASE;FULLGAME，无 TESTING。最终 Ship 为 104 warning / **0 error**，Playtest 为 100 warning / **0 error**，新 ending 文件没有编译警告。最终 DLL 位于 `build/ending-ship/CasualtiesMP.dll`。

PE 元数据核验确认 Ship 中存在 EndingFlow，不存在 EndingFlowTests / EndingTestPreferences；保留消息字节与本表一致，新增 EN/zh-CN 15 个 key 完全对应，记录在 `build/ending-verification.json`。九组日志核验汇总位于 `build/ending-tests/verified-results.json`，全部通过；首次失败的原始记录另存，未把失败记录改写成通过。

A/B 均已部署最终 Ship DLL，测试进程已关闭。源包与 A/B 的 SHA-256 一致：

```text
A47F5F8D94D9D723F61869A72DE3B7FD4B854F7EBDEED9473E9EFF4B3D65E31C
```

需要用户实机确认的范围：

1. Steam 两账号真实连接、中文 Steam 名字形与断线重连；本轮使用 --mp-nosteam，SteamworksNet 与原中文渲染修复未改。
2. 使用真实鼠标完成手术，队友救起被守卫击倒的关键玩家，完整等待 60/30 秒；自动化覆盖原版物理处理及临界时间分支，未模拟人手操作体验。
3. 客机用真实库存枪械射击科学家、击杀守卫，确认全队 PACIFIST 条件；已有建筑/枪械通道复用，未自动跑完装备、装弹和瞄准的交互链。
4. 守卫远端动画为简化姿态，不承诺同步每个激光瞄点/粒子/近战动画帧；其实体、碰撞、健康、伤害由服务端和现有建筑系统处理。
5. 逃生中途保存再重启，验证手术步骤与守卫恢复的实际体验；已保存这些状态，但自动重启用例覆盖的是已完成跑团，不是所有手术中间帧。
6. 原空投 keypad 真实输入、Steam 成就弹窗与 PlayerPrefs 跨进程持久化。自动测试用内存 prefs 且禁平台成就；原 keypad 权威解锁逻辑未改。

同一房间应使用同一新版 DLL；未增加旧版本客户端的功能协商，旧客户端无法执行新增的强制结算协议。
