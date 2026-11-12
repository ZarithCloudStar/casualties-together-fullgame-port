# fix7：结构覆盖与玩家位置（未全部验收）

日期：2026-11-12。目标：1.0.1 / 5.5.0-cu-port。用户在额度剩余约 11% 时要求立即收尾，本报告保留失败数据，不将候选代码称为已全部修复版本。未修改 D:\steam；真实游戏均从仓库隔离运行目录启动。

## 当前结论

- ShipFullgame、PlaytestFullgame 全源码 Roslyn 构建均为 **0 error**。使用仓库既有离线构建器，不声称运行了 SDK/MSBuild。
- 真实 Unity 主客机应用检查：**129 个模板、168 个建筑节点检查均一致**；含全部 **13 个 Resources/Structures 模板**。168 包含嵌套部件及独立模板的重复检查，不代表 168 个独立网络实体。
- 结构表验证身份、位置、逻辑 active、原版功能组件集合；纯地形模板另比较 80×64 格区域哈希，整层 WS1 比较 tiles/fluids/身份集合。**逐模板可见性、网络注册和真实交互巡检没有完成，不能列为全通过。**
- 客机保存坐标已存在；服务端确实发送保存坐标，但最近完整位置观察在生成完成后仍有 **1.895 格偏差，超过 1 格要求**。此项未验收。
- 下一层权威共同出生点已实现；最小验证的坐标数据见下方更新。未跑全量回归。

## 代码改动

1. `SnapshotPrefabCatalog` 遍历实际 Resources 池注册背景，补上空 ID 的 BrickLoot / LongCorridor / RestSpot 背景；GenerateEntityAtPos 以资源路径和子节点索引保留精确 prefab 变体，修复 RestSpot foodbox 被替换成组件不同的通用 foodbox。
2. 普通快照增加梯子/藤绳路径、DrillPod working、陷阱/炮塔字段、水晶效果状态、纸条文本/字体/图片引用、精灵尺寸、支撑地块坐标；背景增加受限组件状态和水泵存在状态。恢复水泵不再重新随机销毁。NPC/终局实验室仍走专用生命周期，并非普通结构快照。
3. 保存以 ClientIdToPlayerDict 枚举所有连接玩家，写兼容的 PLRPOS 和新增 PLRPOSE（坐标、朝向、层级）。无就绪 body 的连接玩家会让保存失败，避免静默漏存。日志同时记录 BodyToPlayerDict 是否包含该 body。
4. PlayerPlacementCheckpoint 按会话、世界、层级、生成 token 持有坐标，覆盖延迟恢复和晚加入路径；最终 GiveSpawnLocationToPlayer 在 ResetEntropy 后重新应用保存位置。新生成时清除旧保存放置标记。
5. Fullgame 取消每人独立的 layer_transition_x_offset 放置；主机选择一次 SpawnLocation，并通过原有可靠 BodyTeleportPacket 发给客机。客机 Body.PlaceBody 继续禁止本地算 spawn。传送同步 noclip 锚点，避免调试移动覆盖权威传送。
6. 保持 CTWO v3 和 WS1 分片/ACK/摘要流程，新增字段可选；新增结构引用要求双方使用 `_WS1_SC1`，旧客户端明确拒绝。旧 v1/v2/v3 的实际迁移回归本轮未完成；旧文件缺少的新背景姿态仅保留原有 seed 基线，不伪称能还原未保存的数据。

## 构建

| 配置 | 结果 | SHA256 |
| --- | --- | --- |
| ShipFullgame | 0 error | `77371A1832B449690C9ADCD125B10F6679859C796AC99D74351F0563CAD81041` |
| PlaytestFullgame | 0 error | `F2F13D29708A9EFBAE44D43691FFD09E5B43994C3A9C0AA692683B6EE7DACD2C` |

证据：[Ship 构建](build/fix7-verification/build-ShipFullgame.log)、[Playtest 构建](build/fix7-verification/build-PlaytestFullgame.log)。输出：`build/bugfix-ShipFullgame/CasualtiesMP.dll`、`build/bugfix-PlaytestFullgame/CasualtiesMP.dll`。这是未完成验收的候选构建，不建议覆盖日常安装作为稳定发布。

## 结构覆盖表

实际资源清单共 148 个带建筑节点或 Structures 模板的根资源；全部 13 个 Structures 已列入。下表取真实双实例的应用屏障数据，不是源码字符串测试。

证据：[主机日志](build/world-checkpoint-tests/fix7-minimal-20261112-050920/A.log)、[客机日志](build/world-checkpoint-tests/fix7-minimal-20261112-050920/B.log)、[原始 Resources 清单](build/world-checkpoint-tests/fix7-minimal-20261112-050920/save/resource-inventory.json)、[主机表](build/world-checkpoint-tests/fix7-minimal-20261112-050920/save/coverage-host.json)、[客机表](build/world-checkpoint-tests/fix7-minimal-20261112-050920/save/coverage-client.json)。`terrain` 表示地形格区域哈希一致，不是独立 GameObject。

| 模板 | 主机恢复节点数 | 客机同步节点数 | 功能测试结果 |
| --- | ---: | ---: | --- |
| animalcorpse | 1 | 1 | 一致；逐类型交互未完成 |
| bananaplant | 1 | 1 | 一致；逐类型交互未完成 |
| barbedwirefence | 1 | 1 | 一致；逐类型交互未完成 |
| beartrap | 1 | 1 | 一致；逐类型交互未完成 |
| BioContainer | 8 + terrain | 8 + terrain | 一致；逐类型交互未完成 |
| bioterminal | 1 | 1 | 一致；逐类型交互未完成 |
| BloodCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| bloodsuckerden | 1 | 1 | 一致；逐类型交互未完成 |
| bouncecap | 1 | 1 | 一致；逐类型交互未完成 |
| brownshroom | 1 | 1 | 一致；逐类型交互未完成 |
| cactus | 1 | 1 | 一致；逐类型交互未完成 |
| ceilingrye | 1 | 1 | 一致；逐类型交互未完成 |
| ceilingturret | 1 | 1 | 一致；逐类型交互未完成 |
| ceilingturretslim | 1 | 1 | 一致；逐类型交互未完成 |
| climbingropeextended | 1 | 1 | 一致；逐类型交互未完成 |
| coil | 1 | 1 | 一致；逐类型交互未完成 |
| coldpuff | 1 | 1 | 一致；逐类型交互未完成 |
| containercrate | 1 | 1 | 一致；逐类型交互未完成 |
| corecrystal | 1 | 1 | 一致；逐类型交互未完成 |
| corpse | 1 | 1 | 一致；逐类型交互未完成 |
| crimsonbush | 1 | 1 | 一致；逐类型交互未完成 |
| crimsontree | 1 | 1 | 一致；逐类型交互未完成 |
| crystalbig | 1 | 1 | 一致；逐类型交互未完成 |
| crystalmassive | 1 | 1 | 一致；逐类型交互未完成 |
| crystalmedium | 1 | 1 | 一致；逐类型交互未完成 |
| crystalmini | 1 | 1 | 一致；逐类型交互未完成 |
| crystalsmall | 1 | 1 | 一致；逐类型交互未完成 |
| DigestionCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| drillpod | 1 | 1 | 一致；逐类型交互未完成 |
| drillpodbroken | 1 | 1 | 一致；逐类型交互未完成 |
| dropcapsule | 1 | 1 | 一致；逐类型交互未完成 |
| drybush | 1 | 1 | 一致；逐类型交互未完成 |
| EmissiveCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| fan | 1 | 1 | 一致；逐类型交互未完成 |
| foodbox | 1 | 1 | 一致；逐类型交互未完成 |
| frigiant | 1 | 1 | 一致；逐类型交互未完成 |
| fungalibungali | 1 | 1 | 一致；逐类型交互未完成 |
| geotree | 1 | 1 | 一致；逐类型交互未完成 |
| geyser | 1 | 1 | 一致；逐类型交互未完成 |
| glassshards | 1 | 1 | 一致；逐类型交互未完成 |
| glowplant | 1 | 1 | 一致；逐类型交互未完成 |
| glowshroom | 1 | 1 | 一致；逐类型交互未完成 |
| grate | 1 | 1 | 一致；逐类型交互未完成 |
| gunmine | 1 | 1 | 一致；逐类型交互未完成 |
| heatfern | 1 | 1 | 一致；逐类型交互未完成 |
| highpowereddrillpod | 1 | 1 | 一致；逐类型交互未完成 |
| highpowereddrillpodbroken | 1 | 1 | 一致；逐类型交互未完成 |
| hydreed | 1 | 1 | 一致；逐类型交互未完成 |
| icestalactite | 1 | 1 | 一致；逐类型交互未完成 |
| icestalagmite | 1 | 1 | 一致；逐类型交互未完成 |
| jumppad | 1 | 1 | 一致；逐类型交互未完成 |
| ladder | 1 | 1 | 一致；逐类型交互未完成 |
| landmine | 1 | 1 | 一致；逐类型交互未完成 |
| lavageyser | 1 | 1 | 一致；逐类型交互未完成 |
| lavalamp | 1 | 1 | 一致；逐类型交互未完成 |
| leadbush | 1 | 1 | 一致；逐类型交互未完成 |
| Lifepod | 7 + terrain | 7 + terrain | 一致；逐类型交互未完成 |
| lifepodchest | 1 | 1 | 一致；逐类型交互未完成 |
| lifepodheater | 1 | 1 | 一致；逐类型交互未完成 |
| LifePodLight | 1 | 1 | 一致；逐类型交互未完成 |
| lifepodpump | 1 | 1 | 一致；逐类型交互未完成 |
| lifepodshower | 1 | 1 | 一致；逐类型交互未完成 |
| LoreNote | 1 | 1 | 一致；逐类型交互未完成 |
| medcrate | 1 | 1 | 一致；逐类型交互未完成 |
| medicalstation | 1 | 1 | 一致；逐类型交互未完成 |
| mushlight | 1 | 1 | 一致；逐类型交互未完成 |
| mushroomdropper | 1 | 1 | 一致；逐类型交互未完成 |
| mushroomplatform | 1 | 1 | 一致；逐类型交互未完成 |
| mushroomtree | 1 | 1 | 一致；逐类型交互未完成 |
| oilpipe | 1 | 1 | 一致；逐类型交互未完成 |
| OxygenCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| piston | 1 | 1 | 一致；逐类型交互未完成 |
| pistonleft | 1 | 1 | 一致；逐类型交互未完成 |
| pistonright | 1 | 1 | 一致；逐类型交互未完成 |
| pop | 1 | 1 | 一致；逐类型交互未完成 |
| pussack | 1 | 1 | 一致；逐类型交互未完成 |
| radbarrel | 1 | 1 | 一致；逐类型交互未完成 |
| rechargingstation | 1 | 1 | 一致；逐类型交互未完成 |
| reinforceddoor | 1 | 1 | 一致；逐类型交互未完成 |
| ReliefCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| sandrose | 1 | 1 | 一致；逐类型交互未完成 |
| sawblade | 1 | 1 | 一致；逐类型交互未完成 |
| scrapeater | 1 | 1 | 一致；逐类型交互未完成 |
| shuttledoor | 1 | 1 | 一致；逐类型交互未完成 |
| shuttleelevator | 1 | 1 | 一致；逐类型交互未完成 |
| sidestabber | 1 | 1 | 一致；逐类型交互未完成 |
| sidestabberflip | 1 | 1 | 一致；逐类型交互未完成 |
| skullcrusher | 1 | 1 | 一致；逐类型交互未完成 |
| SoothingCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| soundcannon | 1 | 1 | 一致；逐类型交互未完成 |
| spaceheater | 1 | 1 | 一致；逐类型交互未完成 |
| Special/defibrack | 1 | 1 | 一致；逐类型交互未完成 |
| Special/holidaytree | 1 | 1 | 一致；逐类型交互未完成 |
| Special/jackolantern | 1 | 1 | 一致；逐类型交互未完成 |
| Special/marbleBackground | 1 | 1 | 一致；逐类型交互未完成 |
| Special/mushroomrope | 1 | 1 | 一致；逐类型交互未完成 |
| Special/mushroomropeend | 1 | 1 | 一致；逐类型交互未完成 |
| Special/sandvinehook | 1 | 1 | 一致；逐类型交互未完成 |
| Special/sandvinerope | 1 | 1 | 一致；逐类型交互未完成 |
| Special/veinbottom | 1 | 1 | 一致；逐类型交互未完成 |
| Special/veinloop | 1 | 1 | 一致；逐类型交互未完成 |
| Special/veintop | 1 | 1 | 一致；逐类型交互未完成 |
| spentfuel | 1 | 1 | 一致；逐类型交互未完成 |
| spicevine | 1 | 1 | 一致；逐类型交互未完成 |
| spicevineceiling | 1 | 1 | 一致；逐类型交互未完成 |
| spikestabber | 1 | 1 | 一致；逐类型交互未完成 |
| stalactite | 1 | 1 | 一致；逐类型交互未完成 |
| stalagmite | 1 | 1 | 一致；逐类型交互未完成 |
| stalagmitedark | 1 | 1 | 一致；逐类型交互未完成 |
| stoneplant | 1 | 1 | 一致；逐类型交互未完成 |
| Structures/BlueHeartCont | 1 + terrain | 1 + terrain | 一致；逐类型交互未完成 |
| Structures/BrickLoot | 11 + terrain | 11 + terrain | 一致；逐类型交互未完成 |
| Structures/ChipSite | 0 + terrain | 0 + terrain | 一致；逐类型交互未完成 |
| Structures/CratePod | 1 + terrain | 1 + terrain | 一致；逐类型交互未完成 |
| Structures/CrystalSpawnPlatform | 4 + terrain | 4 + terrain | 一致；逐类型交互未完成 |
| Structures/LongCorridor | 5 + terrain | 5 + terrain | 一致；逐类型交互未完成 |
| Structures/MedicalBuilding | 8 + terrain | 8 + terrain | 一致；逐类型交互未完成 |
| Structures/MiniPod | 2 + terrain | 2 + terrain | 一致；逐类型交互未完成 |
| Structures/RestSpot | 4 + terrain | 4 + terrain | 一致；逐类型交互未完成 |
| Structures/SteelBridge | 3 + terrain | 3 + terrain | 一致；逐类型交互未完成 |
| Structures/SteelThing | 0 + terrain | 0 + terrain | 一致；逐类型交互未完成 |
| Structures/WoodCross | 0 + terrain | 0 + terrain | 一致；逐类型交互未完成 |
| Structures/WoodHorizontal | 0 + terrain | 0 + terrain | 一致；逐类型交互未完成 |
| techball | 1 | 1 | 一致；逐类型交互未完成 |
| terminal | 1 | 1 | 一致；逐类型交互未完成 |
| TurbulentCrystal | 1 | 1 | 一致；逐类型交互未完成 |
| turret | 1 | 1 | 一致；逐类型交互未完成 |
| tutorialcraftingbutton | 1 | 1 | 一致；逐类型交互未完成 |
| wallflower | 1 | 1 | 一致；逐类型交互未完成 |

下列资源未计入普通结构通过数，明确列出以免误称覆盖所有生命周期：

| 资源 | 原分类/原因 | 本轮结果 |
| --- | --- | --- |
| cavetick | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| crystalenemy | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| grabberplant | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| grabbershroom | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| guard | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| laserdrone | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| LifepodStart | ordinary snapshot | 未纳入普通结构通过数；专用生命周期未重测 |
| overgrowntick | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| shadecrawler | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| snowstrider | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| Special/EndLab | dynamic controller not safely restorable；no Resources prefab；ordinary snapshot | 未纳入普通结构通过数；专用生命周期未重测 |
| Special/MovementCourseSpawn | no Resources prefab | 未纳入普通结构通过数；专用生命周期未重测 |
| Special/TutorialStructure | ordinary snapshot | 未纳入普通结构通过数；专用生命周期未重测 |
| thornbackelder | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| thornbackyoung | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |
| trader1 | no Resources prefab | 未纳入普通结构通过数；专用生命周期未重测 |
| trader2 | no Resources prefab | 未纳入普通结构通过数；专用生命周期未重测 |
| trader3 | no Resources prefab | 未纳入普通结构通过数；专用生命周期未重测 |
| wallbiter | dynamic controller not safely restorable | 未纳入普通结构通过数；专用生命周期未重测 |

## 位置保存/恢复日志对比

最终最小观察（真实双实例，最终放置代码已含强制重应用）：

```text
21:09:59.360 checkpoint authoritative guest layer=0 saved=(77.913,475.580)
21:10:10.157 client restored actual=(77.913,473.685), error=1.895 -> 未通过
21:10:10.159 server restored guest actual=(77.913,473.685), error=1.895 -> 未通过
host expected=(0.000,506.617), actual=(0.000,506.616), error≈0.000
```


证据：[客机位置](build/world-checkpoint-tests/fix7-minimal-20261112-050920/save/placement-client-restored.json)。早先保存日志能记录两名玩家，客机 body 也在 BodyToPlayerDict 中；主要问题并非单纯“只存了主机”。已补上晚加入/放置优先级，但生成后实际位置偏移仍需继续排查（站立/布娃娃及生成结束后的物理过程）。没有把发送坐标正确等同于最终位置正确。

## 最小验证与下一层坐标

本轮唯一最小收尾运行已结束：`build/world-checkpoint-tests/fix7-minimal-20261112-050920`。该模式只读取既有双人存档、输出结构表与位置观察，随后换层；跳过 129 模板接近/交互巡检、截图和其他全量回归。最小模式的“observations complete”只表示收集完成，不表示所有位置断言通过。

最小运行原始第 1 层坐标：

| 观察项 | 坐标/距离 |
| --- | --- |
| 主机 | (0.001232, 494.615540) |
| 服务端观察的客机 | (77.010520, 473.276825) |
| 客机自身报告 | (77.010520, 473.926361) |
| 服务端两人距离 | **79.911026 格，原始断言失败** |
| 客机与服务端客机位置差 | 0.649536 格 |
| 服务端广播的共同出生基点 | (0.000, 494.000) |

证据：[下一层原始 JSON](build/world-checkpoint-tests/fix7-minimal-20261112-050920/save/placement-next-layer.json)、[主机日志](build/world-checkpoint-tests/fix7-minimal-20261112-050920/A.log)、[客机日志](build/world-checkpoint-tests/fix7-minimal-20261112-050920/B.log)。

**这不是有效的共同出生点验收。** 主机在 `21:10:20.041` 给客机发送共同基点 `(0,494)`；客机在 `21:10:22.848` 又打印 `guest moved locally to 77.000,475.617`。原因是 load-only 测试继承了上次保存前的 `placement-move.json`，到新层重新执行了测试移动，污染了最终距离。已给测试夹具加上 `!checkedRestore` 条件防止重放，并再次构建两个配置；遵从用户收尾要求，**没有重开场景验证此夹具修正**。共同出生点的最终 ≤2 格要求因此仍标记未验证，不能用发送基点相同代替实测通过。

## 未完成/需要后续确认

- 客机生成完成后的稳定位置仍超过 1 格误差；该需求未修完、未验收。
- 所有模板恢复后的可见/可交互/网络注册逐项巡检未完成；覆盖表中的组件一致不等同于所有功能操作通过。
- 晚加入、旧 v2/v3 档迁移、新世界 seed 路径的最终源码全量回归未重跑；Steam/P2P、日常角色/设置、终局层和其他 mod 需实机确认。
- 下一层采得原始距离 79.911 格，但被夹具残留移动污染；修正夹具后未复测，仍未有效验收。

自动化入口：`scripts/Test-WorldCheckpoint.ps1 -Coverage -Positions` 遍历 Resources 放置模板；`-Minimal -SeedSave <测试存档>` 仅收取最小观察。生产构建不含 TESTING 测试组件。

最终最小运行的 WS1 数据：普通对象 2160 个、背景 31 个；主客机 objects, backgrounds, tiles, fluids, identities 均一致。
