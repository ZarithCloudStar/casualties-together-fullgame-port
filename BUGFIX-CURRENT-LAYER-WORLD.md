# 同层存档后未探索区域背景和功能建筑消失

> fix6 补充：本文是 fix5 的记录。其客户端区域背景检查不能证明整层地形一致；客户端仍按 seed 生成的问题已在 [世界快照同步修复](BUGFIX-WORLD-SNAPSHOT-SYNC.md) 中修复，并增加完整地形哈希、客户端对象计数和晚加入验证。

验证日期：2026-11-12。目标：Casualties: Unknown 1.0.1 / 5.5.0-cu-port。
所有源文件、编译输出、测试存档和日志均写在本仓库；没有部署到游戏安装目录，没有修改 D:\steam。

## 根因与取证

本次真实 Unity 双实例取证确认，主要故障不是未探索区域没有实例化，也不是背景记录漏序列化。

1. 世界生成通过 `GenerateEntityAtPos` 实例化整层结构；背景快照的 `FindObjectsOfType<Tilemap>(true)` 本来就会扫描 inactive 对象。
2. `ChunkSleep.Sleep` 把远离玩家的建筑/背景设为 `SetActive(false)`。这是临时性能优化，活对象的休眠登记保存在静态字典里。
3. 原来的背景和普通对象快照直接保存 `go.activeSelf`，把临时休眠写成了持久禁用状态。
4. 读档先删除新生成的对象，再创建快照对象并设置 `active=false`。新对象没有原来的休眠登记。
5. `ChunkSleep.Watch` 原本通过 `BuildingEntity.Update` / `Item.Update` 登记；inactive 对象不执行 Update，无法重新进入唤醒系统。靠近后仍然不可见、无法交互。背景 tracker 的 OnDisable 还会从网络背景列表移除它。

基线同层保存/回大厅/读档，层级为 0、尺寸为 1024×1024：

- 保存、恢复背景都为 **26 条**，恢复阶段 skipped=0，身份和坐标缺失数为 **0**。
- 但读档后 **25 条 inactive 背景全部没有 ChunkSleepAnchor**；不是少了记录，而是恢复后永远关闭。
- 解压磁盘 `world_objects.bin`：v2，共 1827 个普通对象，其中 **1727 个 active=false**；背景 26 条，其中 **25 条 active=false**。
- 因而只比较“恢复了多少条记录”不足以发现本故障。修复验证同时检查唤醒登记、靠近后的 active 状态、功能控制器及客户端接收情况。

原始基线日志：[A.log](build/world-checkpoint-tests/baseline-20261112-024732/A.log)。
该基线初版测试的靠近目标坐标选取不正确，最终靠近断言失败（附近背景为 0）；上述 inactive/无唤醒登记、磁盘字段和保存恢复数量证据不依赖该断言。正式验证已改为选择实际休眠中的 Lifepod 背景位置。

## 修复

- 新写入的 CTWO **v3** 中，active 表示游戏启用状态：`activeSelf || ChunkSleep.IsSleeping || BagSleep.IsSleeping`。整层扫描仍包括未探索/休眠对象，无需靠近区域才能保存。
- 普通物品、建筑以及 Tilemap 背景使用同一语义。恢复后正常执行初始化，性能系统再根据距离休眠，并持有有效唤醒登记。
- v3 中真正手动禁用的对象仍保持关闭；清理恢复初始化帧产生的休眠登记，避免它被性能系统误唤醒。
- **v2 兼容读取**：旧文件没有休眠字段，无法精确区分临时休眠和故意禁用。对 prefab 默认启用但记录关闭的对象恢复启用，并给出中英文状态提示及迁移数量日志。背景采用相同处理。不会改写原 v2 文件，下一次用户保存才写 v3。
- **v1 缺少背景位置**，无法保证完整恢复。现在明确提示并保留存档，返回大厅；不再删除生成背景后继续进入残缺世界。
- 在覆盖 tiles/fluids 或删除对象前预检整个对象文件、模板、背景形状和数组长度。缺少文件/模板或不兼容时提示失败并保留文件。
- 保存任何一个背景失败都会终止本次保存退出，留在当前世界；不再静默漏掉背景。当前层保存先完成序列化，再更新 rules 头，避免捕获失败改坏旧头信息。
- 保存/载入日志包含三个文件大小、背景记录数、休眠数、逻辑启用数、对象数以及恢复/跳过统计。

## 改动文件

| 文件 | 改动 |
| --- | --- |
| `ScavPrototypeMP/Optimizinator/ChunkSleep.cs` | 查询真实休眠状态、统一快照 active 语义 |
| `ScavPrototypeMP/Optimizinator/BagSleep.cs` | 查询背包优化休眠状态 |
| `ScavPrototypeMP/Compatibility/BackgroundSnapshot.cs` | active 修复、旧格式恢复、完整性预检、失败阻止漏存、整层场景筛选与统计 |
| `ScavPrototypeMP/Compatibility/WorldObjectSnapshot.cs` | 普通建筑/物品 active 修复、v2 迁移、恢复预检、保留真正禁用状态 |
| `ScavPrototypeMP/Compatibility/WorldObjectSnapshotData.cs` | 写 v3，接受 v1/v2/v3 解析 |
| `ScavPrototypeMP/Compatibility/SaveCurrentLayerExit.cs` | 完成捕获之后才更新存档头 |
| `ScavPrototypeMP/Patches/SavesystemPatch.cs` | 缺失 tiles 进入失败预检，补三个快照文件大小日志 |
| `ScavPrototypeMP/SyncSystems/WorldMP.cs` | 修改世界前预检；无法完整恢复时提示并退出载入 |
| `ScavPrototypeMP/Compatibility/WorldCheckpointRegression.cs` | TESTING 专用真实双实例回归场景 |
| `scripts/Test-WorldCheckpoint.ps1` | 隔离存档、偏好和插件目录的双实例启动/清理脚本 |
| `scripts/Build-OfflineRoslyn.ps1` | 成功编译时清空旧 errors.log，避免保留已修复的历史诊断 |

`DeferredPlayerSaveRestore.cs` 未改动；其等待世界生成完成后再恢复玩家的顺序不是本次根因。

## 针对性验证数据

测试运行真实 1.0.1 Unity 游戏、LiteNetLib 主客机和原版资源。使用真正的 `SaveCurrentLayerExit.Request()` → 大厅 → `PreRunScript.LoadRun()`。保存前不让任何玩家靠近目标，等待 ChunkSleep 关闭远处结构。补充 MiniPod、MedicalBuilding、Lifepod 模板生成；全层对比仍包含自然生成的所有背景。

各运行使用新种子，**跨运行总数不同是预期现象**；一致性比较发生在每次运行的保存前后。

| 检查 | 原始基线 | v3 回归 | v2 迁移回归 |
| --- | ---: | ---: | ---: |
| 层级（保存→读取） | 0→0 | 0→0 | 0→0 |
| world_tiles.bin 字节 | 141394 | 139898 | 138194 |
| world_fluid.bin 字节 | 6957 | 6244 | 6166 |
| world_objects.bin 字节 | 54705 | 51715 | 53980（转换为 v2 后） |
| 保存前→读取后背景数 | 26→26 | **25→25** | **27→27** |
| 背景身份/位置缺失数 | 0 | **0** | **0** |
| 读取后 inactive 且无唤醒登记 | **25** | **0** | **0** |
| 普通对象快照→恢复 | 1827 条快照 | **1756→1756，skipped=0** | **1809→1809，skipped=0** |
| 靠近原未探索目标后的永久关闭背景 | 初版目标选取错误，未用于判断 | **0** | **0** |
| 维生仓 controller/heater/button 与加热切换 | 未验证 | **PASS** | **PASS** |
| 客户端收到活跃背景与功能维生仓 | 未验证 | **PASS** | **PASS** |

v3 额外通过：真正手动禁用的一件 bandage 读档、靠近后仍只存在一份并保持禁用。

v2 回归将保存的数据改为旧格式，并把远处对象写为 inactive：26 条背景、1774 个普通对象。恢复日志确认迁移 1774 个普通对象；结构位置/数量、维生仓功能和客户端同步全部通过。此项是合成旧格式回归，用户的原始问题存档未提供。

失败路径通过：v1 无背景位置、背景资源缺失，都在删除结构前被拒绝，场景 BuildingEntity 数量不变。

本次采用**全层对象身份/坐标计数 + 形状恢复校验 + 实际功能调用**验证，没有把生成的图片或静态断言当作实机截图。

证据：

- v3：[主机日志](build/world-checkpoint-tests/final-20261112-025205/A.log)、[客户端日志](build/world-checkpoint-tests/final-20261112-025205/B.log)、[PASS](build/world-checkpoint-tests/final-20261112-025205/save/result.txt)、[保存前背景](build/world-checkpoint-tests/final-20261112-025205/save/before-backgrounds.json)、[读取后背景](build/world-checkpoint-tests/final-20261112-025205/save/after-backgrounds.json)。
- v2：[主机日志](build/world-checkpoint-tests/legacy-20261112-025330/A.log)、[客户端日志](build/world-checkpoint-tests/legacy-20261112-025330/B.log)、[PASS](build/world-checkpoint-tests/legacy-20261112-025330/save/result.txt)。

## 编译与产物

环境没有 .NET SDK/MSBuild，使用仓库既有 `Build-OfflineRoslyn.ps1`，基于缓存依赖、Unity 引用、publicized Assembly-CSharp 以及配置对应的生成源和编译宏，完整编译项目源文件：

```powershell
& scripts/Build-OfflineRoslyn.ps1 ShipFullgame
& scripts/Build-OfflineRoslyn.ps1 PlaytestFullgame
& scripts/Test-WorldCheckpoint.ps1 final
& scripts/Test-WorldCheckpoint.ps1 legacy -Port 7862 -Legacy
```

- **ShipFullgame：0 error**，`build/bugfix-ShipFullgame/CasualtiesMP.dll`
  - SHA256 `49F0230F04CA8448476104BE8F3572BE3DB47E20BEB9A568490B9A588EABC043`
- **PlaytestFullgame：0 error**，`build/bugfix-PlaytestFullgame/CasualtiesMP.dll`
  - SHA256 `900B344DAB0566C5C970714F1FE96245F4F7FCE4334E967C44FE168B4233223D`

最终编译日志：[ShipFullgame](build/world-checkpoint-tests/build-ShipFullgame.log)、[PlaytestFullgame](build/world-checkpoint-tests/build-PlaytestFullgame.log)；两个最终 errors.log 均为 0 字节。

真实双实例场景运行的是 PlaytestFullgame。ShipFullgame 不包含 TESTING 专用测试逻辑。未跑无关全量测试，未自动安装 DLL。

## 仍需用户实机确认

1. 使用本次 Ship DLL，读取原来出现问题的 v2 存档，前往此前未探索的维生仓/建筑区域，确认显示、碰撞和交互正常；再同层保存退出、读取一次。
2. 本次覆盖生成层 0 的整层背景及资源场景；其他层级/特殊种子、用户原始截图对应位置、已有其他 mod 的自定义资源仍需实机确认。
3. v2 无法还原其未记录的“故意禁用”与“性能休眠”的区别，迁移偏向恢复功能；v3 已区分。若旧文件本来缺少记录或资源已改变，不会猜位置或静默裁剪，而会明确拒绝完整载入。
