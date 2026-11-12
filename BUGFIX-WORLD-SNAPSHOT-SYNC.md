# fix6：同层读档的权威世界快照同步

目标：Casualties: Unknown 1.0.1 / 5.5.0-cu-port。验证日期：2026-11-12。
所有源码、构建、隔离运行目录、测试存档副本和截图均在本仓库中；没有覆盖日常 CU_Fullgame / CU_Fullgame_B 的插件，没有修改 D:\steam。

## 根因

已直接读取用户指定的两个 LogOutput.log。主机 `19:23:59.453` 读取 tiles=128471、fluids=5004、objects=74987；客机 `19:23:59.534` 收到 GenerateWorld_SeedAnnounce 后自行生成第 2 层，到 `19:24:27` 才得到 spawn。原协议只有 seed，没有当前层快照。

原来的恢复逻辑在 WorldMP.Patched_FinishWorldGeneration 内，只使用主机磁盘缓冲区，并且对象恢复要求 Net.IsServer。主机生成结果随后被存档覆盖，客机保留 seed 生成结果。邻近对象/区块同步不能代替整层快照。fix5 的客户端测试只检查区域背景和维生仓存在，没有检查完整地形哈希，因而没有发现此问题。

## 新流程与协议 WS1

1. GenerateWorld_SeedAnnounce 保留原参数，追加协议号 1、是否快照模式、32 字符生成 token。普通新世界继续原来的 seed 生成；读同层档的客机只做预处理/创建基础背景，**不调用原版 GenerateWorld/WorldGenerateTerrain/WorldPlaceEntities**。
2. 主机保留原有生成的动态控制器基线，在 WorldPlacePlayer 前恢复 tiles、fluids、普通对象和结构背景，再发布已恢复世界。后续原版放置流程不重复生成起始维生仓、雕刻地形或投放实体。
3. CLIENT_WorldSnapshot 使用预留字节 ___22；SERVER_WorldSnapshot 使用 ___23。不移动原有消息编号。背景包含在 CTWO v3 对象文件中，因此传输三个长度前缀部分：tiles、fluid、objects+backgrounds。
4. 所有帧使用 **ReliableOrdered**。manifest 含协议、token、层级、尺寸、saveId、总字节、片数、SHA256；每片最多 **1000 字节**，包含 token 和连续序号；结束帧再次确认片数和摘要。每帧最多发送 16 片，避免一次塞入大包。
5. 接收严格校验序号、分片长度、总长度、SHA256、三个部分边界，无尾随数据。总包上限 32 MiB；terrain 解压必须恰好 width×height；对象解压沿用 128 MiB 上限。收包空闲超时 120 秒，总等待上限 360 秒；应用 ACK 超时 120 秒。异常/超时通过失败帧与状态提示中止载入，不静默进入部分世界。
6. 两端共享同一 preflight：层级、尺寸、saveId、格式、数组长度、地块/流体类型、对象模板、组件路径/字段和值类型、背景模板和形状。网络数据必须是 v3，且对象标识非零且唯一。全部通过后才能修改世界。实例化、状态应用或必要网络注册失败也中止载入。
7. 客机完整应用并完成初始化帧后回传同一摘要的 ACK，然后进入原有 WorldPlacePlayer / FinishedWorldgen / spawn 流程。服务端消息处理器和实际 GiveSpawnLocationToPlayer 都检查应用 ACK，防止提前请求绕过。
8. SnapshotRestored 标记与 SyncInfo.checkpointKey 提供稳定绑定。普通网络实体接管已恢复实例；较晚到达的 key/baseline 也会替换临时副本，避免重叠对象和容器物品重复。空间注销后重新创建的实例保留同一标识。
9. 主机已经完成读档后才加入的客机会收到**重新捕获的当前活动世界**，而不是过期的初始快照。并行传输各自保存期望 ACK 摘要；token/会话检查拒绝旧层的残留帧。
10. 初始化帧可能运行流体逻辑；恢复屏障中暂停 SimulationStep，并在初始化完成后重应用保存流体。完成生成时原版清空出生点上方液体的副作用也被恢复。
11. 读档世界的 ChunkScript 贴图变化按 saveId、层级和块坐标确定，不再依赖哪台机器先渲染。普通新世界的贴图生成流程不变。

FULL_VERSION_TAG 增加 `_WS1`，因为 LES 实体字段布局也有变化。即使使用 master key，也不能绕过协议版本检查。真实旧 fix5 客机得到：`Version mismatch: both peers require fix6 / WS1 checkpoint sync. 双方必须使用 fix6。`

旧存档：v2 仍由主机按 fix5 迁移休眠状态，网络重新编码为完整 v3；v1 缺少背景位置、文件缺失、模板缺失或格式/长度错误明确拒绝，不删除原文件。

## 实机验证

运行真实 Unity 游戏、LiteNetLib 主客机以及原版资源，使用仓库既有隔离 CT_EndingTest_A/B.exe 和独立 BepInEx 目录。Playtest fixture 的偏好与存档重定向只写测试目录，日常实例无需安装测试 DLL。

### 用户当前存档副本：第 2 层，主机完成读档后客机才启动

从当前 mp_save **读取并复制**到测试目录；测试时的文件为 tiles **128542**、fluid **5181**、objects **75031** 字节，v3、1024×1024、saveId=32173。这是测试时的当前存档，字节大小与 `19:23:59` 日志中的早先版本略有不同。

- 两端应用后的真实场景：**2731/2731 个普通对象、78/78 个背景**；对象标识集合一致。
- 整层 tiles、fluids 以及实际 worldBlocks/fluid 数组哈希一致，包含不在玩家视野内的部分。
- 实际传输 217334 字节，**218 片**；发送、接收、ACK 摘要一致。客机记录 `seed terrain generation bypassed`，应用后才进入 WorldPlacePlayer。
- 主机恢复前后全部背景身份/坐标一致；休眠背景均有唤醒登记；进入未探索维生仓区域后背景可见。
- 客机调用真实 `LifepodButton.OnUse()`，走原有网络请求；主机观察到同一个 checkpointKey 的加热状态变化并回执。
- 同坐标、同一捕获时刻的真实游戏截图：统一测试视觉设置后，场景区域归一化 RGB MAE **1.3964%**，**94.6560%** 像素的最大通道差 ≤16/255。比较区域排除 HUD、测试文字和玩家动画；不修改或对齐截图像素。不同角色的夜视/色阶本来不同，因此截图使用相同 fullBright/default-vision 设置，只有 TESTING fixture 会设置这些选项。

证据：[主机日志](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/A.log)、[客机日志](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/B.log)、[主机应用数据](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/save/host-applied.json)、[客机应用数据](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/save/client-applied.json)、[主机截图](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/save/host.png)、[客机截图](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/save/client.png)、[像素比较](build/world-checkpoint-tests/fix6-final-original-save-20261112-040129/save/screenshot-comparison.json)。

### 其他回归

| 场景 | 结果 / 证据 |
| --- | --- |
| 新世界 → 第 0 层同层保存/读取 | 两端正常完成 seed 路径；读取后整层 tiles/fluids/标识一致、维生仓按钮往返通过。[日志](build/world-checkpoint-tests/fix6-final2-layer0-20261112-035149/A.log) |
| 新世界 → 第 2 层 v2 合成旧档 → 读取 | **2566 个对象、65 个背景**两端一致，主机迁移 v2，线上使用 v3；完整地形/流体一致、客机按钮通过。[主机](build/world-checkpoint-tests/fix6-verified-legacy-layer2-20261112-035627/A.log)、[客机](build/world-checkpoint-tests/fix6-verified-legacy-layer2-20261112-035627/B.log) |
| v2 同区域截图 | 相同视觉设置后场景 MAE **1.5086%**。[比较数据](build/world-checkpoint-tests/fix6-verified-legacy-layer2-20261112-035627/save/screenshot-comparison.json) |
| 真实 fix5 客机连接 fix6 | 进入世界前拒绝，客机显示明确的 fix6/WS1 提示。[日志](build/world-checkpoint-tests/fix6-old-client-20261112-040006/B.log) |
| v1 / 缺背景模板 | 主机 preflight 拒绝，未删除生成结构。[日志](build/world-checkpoint-tests/fix6-verified-legacy-layer2-20261112-035627/A.log) |

可靠传输失败注入均为真实网络帧，不是静态字符串检查。六项均有 `applied=false, restoredObjects=0`，且没有 `spawn unlocked`：

| 注入 | 拒绝原因与证据 |
| --- | --- |
| 第一片序号错误 | [sequence/length mismatch](build/world-checkpoint-tests/fix6-fault-sequence-20261112-034641/save/failure-evidence.json) |
| 字节被修改 | [SHA256 digest mismatch](build/world-checkpoint-tests/fix6-fault-checksum-20261112-034726/save/failure-evidence.json) |
| 缺最后一片 | [incomplete digest](build/world-checkpoint-tests/fix6-fault-truncate-20261112-034934/save/failure-evidence.json) |
| 网络对象文件引用不存在的模板 | [template unavailable](build/world-checkpoint-tests/fix6-fault-template-20261112-034813/save/failure-evidence.json) |
| manifest 协议号不匹配 | [protocol version mismatch](build/world-checkpoint-tests/fix6-fault-version-20261112-035027/save/failure-evidence.json) |
| manifest 后不再发送数据 | [receive timeout](build/world-checkpoint-tests/fix6-fault-timeout-20261112-035116/save/failure-evidence.json)，TESTING 将此项空闲超时缩短为 3 秒；Ship 保持 120 秒 |

## 改动文件

| 文件（ScavPrototypeMP/ 下） | 作用 |
| --- | --- |
| Compatibility/WorldSnapshotTransfer.cs（新增） | WS1 分片/校验/ACK/超时/中止、两端恢复协调、晚加入、网络实例绑定、确定性贴图变化 |
| SyncSystems/WorldMP.cs | 客机快照分支、恢复提前至放置玩家前、spawn 门禁、保留已恢复对象 |
| ServerMain.cs / ClientMain.cs | 生成模式参数、开始/换层时的客户端门禁、实际 spawn 等待 |
| Net/MSG_ID.cs | 复用 ___22 / ___23 |
| Net/ScavLiteEntitySystem.cs | 同步 checkpointKey、绑定已恢复实例与较晚到达的 baseline |
| ScavMultiplayer.cs | WS1 版本标识及不可绕过的版本检查 |
| Compatibility/WorldObjectSnapshot.cs | 共享 preflight/恢复，完整组件验证、稳定标识、失败中止、客户端不参与物理模拟 |
| Compatibility/WorldObjectSnapshotData.cs | 可选 networkKey，保存格式仍为 v3 |
| Compatibility/BackgroundSnapshot.cs | 两端恢复、背景稳定标识、恢复/网络注册失败阻止继续 |
| Patches/SavesystemPatch.cs | terrain 行跨度使用 width；快照模式下应用错误向协调器传播 |
| Compatibility/WorldCheckpointRegression.cs | 完整场景数量/哈希、真实客户端交互、晚加入/旧档/错误数据、同步游戏截图 |

脚本：`scripts/Test-WorldCheckpoint.ps1`、`scripts/Compare-WorldScreenshots.ps1`、`scripts/Test-WorldSnapshotVersion.ps1`。fix5 报告顶部加了其验证范围说明，并指向本文。

## 构建与交付

使用仓库现有 `Build-OfflineRoslyn.ps1` 完整编译源码、对应配置宏和既有 publicized Unity 引用/生成源；本机没有 SDK/MSBuild。

- **ShipFullgame：0 error**；[DLL](build/bugfix-ShipFullgame/CasualtiesMP.dll)，SHA256 `C07A8F3FDD483EA96DED8F8B28F04EE68F4A1EF56B00333480E10A23B7F92F8F`。
- **PlaytestFullgame：0 error**；[DLL](build/bugfix-PlaytestFullgame/CasualtiesMP.dll)，SHA256 `3F0E03DCAB7E6AA1C143B8E8C11DD4AA837B868890C47836D050508014F6F210`。
- [Ship 构建日志](build/fix6-verification/build-ShipFullgame.log)、[Playtest 构建日志](build/fix6-verification/build-PlaytestFullgame.log)；两个 errors.log 都为 0 字节。
- [fix6 更新包](build/fix6/CasualtiesTogether-1.0.1-fix6-update.zip)：供已安装 fix5 的双方覆盖 DLL；不包含 BepInEx/依赖的首次安装包。主客机都必须更新。

复现命令示例：

```powershell
& scripts/Build-OfflineRoslyn.ps1 ShipFullgame
& scripts/Build-OfflineRoslyn.ps1 PlaytestFullgame
& scripts/Test-WorldCheckpoint.ps1 fresh -Port 7882
& scripts/Test-WorldCheckpoint.ps1 legacy -Port 7885 -Legacy -Layer2
& scripts/Test-WorldCheckpoint.ps1 late -Port 7886 -SeedSave '<当前存档目录>' -LateJoin
& scripts/Test-WorldCheckpoint.ps1 broken -Port 7875 -SeedSave '<存档副本>' -Fault sequence
& scripts/Test-WorldSnapshotVersion.ps1 -Port 7888
```

## 仍需日常实机确认的范围

- 已实际测试真实 Unity 双实例和用户当前存档副本；未覆盖日常两个安装目录的 DLL。更新双方后，用日常角色/图形设置复查用户原截图所在区域；角色夜视、HUD、动画和光照差异不应误判为地形差异。
- 本次覆盖层 0/2、v3/v2 迁移、局域网 ReliableOrdered、晚加入和六种失败。Steam/P2P、长时间广域网丢包、层 10/11 和其他 mod 的资源尚未实机覆盖；相同应用协议会经过对应 transport。
- 数量/哈希一致指**快照应用屏障**及记录的地形状态；进入游戏后仍使用原有权威实体和空间同步，活跃物理、流体和角色状态会继续变化。原先不在普通对象快照范围内的动态控制器仍遵循既有主机生成/网络同步策略；没有扩大磁盘快照的类型范围。
