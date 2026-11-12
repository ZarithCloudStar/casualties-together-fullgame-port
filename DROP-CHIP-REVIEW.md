# 掉落悬浮与玩家芯片选择复核

> release12 后续修复见 [RELEASE12-FIX-REVIEW.md](RELEASE12-FIX-REVIEW.md)。下文 B 节记录的是当时仅实现元数据的历史状态；现在已接入原版按玩家无芯片机制，并修复掉落终态恢复循环。

本轮源码版本保持 `5.5.0-cu-port`。正式版和测试版产物分别放在 `build/drop-chip-ship` 与 `build/ending-playtest`；不修改 Steam 安装目录。原 A/B 实例正在运行且 DLL 被映射锁定，因此以 A/B 为来源，在本仓库 `build/runtime-A`、`build/runtime-B` 建立隔离启动目录，使用 A/B 原游戏数据、独立 BepInEx 目录、独立测试存档和 `127.0.0.1:7791` 完成验证。原有两个实例保留。

## A：根因与修复

真正遗漏的是**服务端的掉落生产入口**。原版正式版 `BuildingEntity.CheckDamage` 使用 `PlayerCamera.main.body` 距离 `< 8` 决定是否给开箱/破坏产生的物品添加 `FreshItemDrop`；5.4 的对应逻辑在 `Update`。移植版 `PatternTranspilers.BuildingUpdate` 只在 demo 分支替换旧距离检查，正式版新入口没有补丁。

实际复现见 `build/drop-chip-tests/sources-before-fix/host.log`：主机距仓 40 米、客机距仓约 2.46 米，真实 `dropcapsule.SetHealth(0)` 的掉落 `soup`、`experimentflesh` 都记录 `fresh=False`。这类掉落的同步初值自然是 false/0，客机修复 Awake、forceOn 或恢复剩余时间都无从生效。

反证见 `baseline-diagnostic2`：手工给服务端物品加上组件后，客机 `forceOn=True`、`parent=null`、`gravity=0`，三秒实际坐标位移为 0；组件直至到期保持同一个实例。因此之前“组件存在且重力正确”的合成测试不能代表真实开箱路径。正常物品没有挂在 worldGrid 下，`InstantiateResource` 的那个 SetParent 分支只用于背景。

新增 `Compatibility/FreshDropRelevance.cs`：对正式版 `CheckDamage` 的唯一距离调用做有完整模式检查的替换，使用最近存活玩家距离，保留原来的 `< 8`、全部掉落分支及原版组件。未改原版 10 秒常量、末两秒重力渐变、速度阻尼、拾取父节点规则或主机物品物理。离线时仍调用原版距离。保留已有 `[drop]` 并补充来源/最近玩家/主机距离日志。旧客户端恢复补丁保留，只修正了两处错误注释。

`DropFrameProbe` 的前 60 帧及后续半秒采样、OnDestroy 跟踪仅编入 `FULLGAME && TESTING`，Ship 不含测试类或逐帧诊断。

最终来源回归 `verify-fixed-3`：

| 来源 | 稳定段主机最大位移 | 稳定段客机最大位移 | 客机与同步权威位置最大差 |
|---|---:|---:|---:|
| 客机通过真实密码 RPC 打开空投仓 | 0.019 m | 0.034 m | 0.015 m |
| 远端医疗箱 CheckDamage | 0.043 m | 0.045 m | 0.014 m |
| 远端铅灌木采集 CheckDamage | 0.019 m | 0.031 m | 0.014 m |
| 主机附近医疗箱对照 | 0.005 m | 0.016 m | 0.008 m |

稳定段指开始观察后 2～6 秒，避开原版初速度与末期重力渐变。CSV 记录完整 10.8 秒轨迹：每件物品只有一个非零组件 ID，没有反复重建；首次观察到组件消失为主机 9.992～10.063 秒、客机 10.050～10.147 秒（0.1 秒采样粒度）。另测悬浮中拾取，两端组件结束且容器同步正确。客机故意设置 `Item.itemFloating=false`，仍服从服务端悬浮状态。

这里“采集”验证的是可破坏实体。反编译 `WorldGeneration.DamageBlock` 的铜矿/木块等地形掉落原本不加 `FreshItemDrop`；本轮保留主机原版行为，不将这些掉落改为悬浮。

## B：芯片状态实现

- 面板新增「有芯片 / 无芯片」及说明，首次默认有芯片。独立 `DraftUnchipped` 不会被切换角色覆盖；确认时和角色原子提交。
- `ChipState`、`ScavPlayer.modUnchipped`、末尾追加的 `BasePlayer.modUnchipped` SyncVar 是独立 mod 元数据，**没有写 `WorldGeneration.unchipped`，也没有复用原有 ScavPlayer/NetBody.unchipped 规则字段**。主机可通过 mod 设置页选择芯片状态。
- 复用保留编号 `___5` 上的现有 `SERVER_SetCharacter`，在 payload 中增加可选芯片字节；不改 intro 或任何 Netmsg 编号。兼容旧 1/2/6 字节格式，新格式为 3/7 字节，布尔值和长度均验证。
- pending admission 在创建 BasePlayer、发送初始快照前携带芯片；保存后重连先读服务端绑定。迟到的未确认重放不再取消已确认状态；已有身体后拒绝改变芯片。
- `character_choices.txt` 增加第四列 `0/1`，仍按存档 seed、SteamId（无 Steam 时姓名）绑定。旧三列记录默认有芯片。首次确认后随存档保留；大厅中新局草稿不能覆写旧存档。新生成 seed 才携带本局草稿建立新的绑定。
- 读档在创建身体前恢复角色和芯片；保存值优先于大厅草稿。本地通过同步采用服务端状态，不用跨存档的 PlayerPrefs 保存芯片。
- 服务端接口：`ChipState.IsUnchipped(ScavPlayer)` 与 `ChipState.HostIsUnchipped`，对客户端调用/空玩家抛出明确异常。未来 Escape 可直接查询；当前 Escape 判定、禁用开关均未改。
- EN/zh-CN 增加 `casualtiestogether_chip_chipped`、`casualtiestogether_chip_unchipped`、`casualtiestogether_chip_hint`。

## 验证记录

测试存档均位于 `build/drop-chip-tests/<phase>/save`；已有测试 PlayerPrefs 拦截使用进程内字典，不写 HKCU，禁用 Steam 成就。UI 截图为 Unity 实际渲染结果，已目视核对中英文芯片按钮、选中状态与布局。

| phase | 结果与边界 |
|---|---|
| baseline-diagnostic2 | 合成组件路径通过；不能当作真实掉落已修复 |
| sources-before-fix | 确认真实远端开仓根本未创建悬浮组件 |
| verify-fixed-1 | 测试角色下落导致真实 keypad 距离校验拒绝；保留失败记录 |
| verify-fixed-2 | 已真实开仓并创建悬浮；夹具把容器内物品算成散落物，断言失败，之后改为只观察散落物 |
| verify-fixed-3 | 空投仓/医疗箱/采集/主机对照、持续坐标、组件寿命、客机设置差异、拾取、芯片面板、服务端查询、保存退出通过 |
| reload-final | 重启进程，主机先载入已保存世界，25 秒后客机接回；恢复 Dune/无芯片并跳过已有绑定的面板。主机相反的大厅草稿不能覆写保存的有芯片状态 |

构建和额外回归的最终机器记录见 `build/drop-chip-results.json`；测试中的失败尝试也保留在各自目录，不计为通过。

## 改动文件

生产新增：

- `ScavPrototypeMP/Compatibility/FreshDropRelevance.cs`
- `ScavPrototypeMP/Compatibility/ChipState.cs`

生产修改：

- `ScavPrototypeMP/Compatibility/CharacterLobby.cs`
- `ScavPrototypeMP/Compatibility/CharacterSelection.cs`
- `ScavPrototypeMP/Compatibility/CharacterRunMemory.cs`
- `ScavPrototypeMP/Compatibility/PendingCharacterAdmission.cs`
- `ScavPrototypeMP/Net/ScavLiteEntitySystem.cs`
- `ScavPrototypeMP/Player.cs`
- `ScavPrototypeMP/UI/LobbyCharacterPanel.cs`
- `ScavPrototypeMP/UI/LobbyCharacterSelection.cs`
- `ScavPrototypeMP/Patches/ItemPatches.cs`（仅纠正父节点注释）
- `ScavPrototypeMP/assets/locale/EN.json`、`zh-CN.json`

测试/记录：`Compatibility/DropChipRegression.cs`、`DropSourceRegression.cs`、`EndingFlowTests.cs`；`scripts/Test-DropChip.ps1`、`scripts/check_drop_chip.py`；本报告与旧报告的更正入口。基线差异在 `build/drop-chip-review.diff`，新增源码以本清单为准。

## 重跑与限制

先用用户给定命令构建 ShipFullgame/PlaytestFullgame，额外将 `ModPluginOutputDir` 分别指定为仓库内 `build/drop-chip-ship`、`build/ending-playtest`。然后：

```powershell
.\scripts\Test-DropChip.ps1 -Phase fresh-run -Scenario drop-chip-verify -WaitForResult
.\scripts\Test-DropChip.ps1 -Phase restore -Scenario drop-chip-reload -LoadSave "$PWD\build\drop-chip-tests\fresh-run\save" -ClientDelaySeconds 25 -WaitForResult
.\scripts\Test-DropChip.ps1 -Phase old-format -Scenario drop-chip-reload-legacy -LoadSave "$PWD\build\drop-chip-tests\fresh-run\save" -ClientDelaySeconds 25 -WaitForResult
```

脚本仅关闭自己启动且路径匹配仓库隔离目录的进程，保留日志/CSV/截图/存档；不会关闭现有 A/B。`runtime-*` 的数据目录是指向 A/B 的 junction，勿对这些链接目录做递归写入或递归清理。

限制：主客机必须同时换成本轮相同 DLL，新增 SyncVar 后不支持混用旧二进制。Steam 实机联机未复测，相关握手代码未改。窗口自动化被权限检查拒绝，未完成真实鼠标点击验收，开仓验证使用实际客户端 minigame/RPC 路径。直连 CLI 的纯中文测试名被仓库既有 ASCII SanitizeTextInput 过滤为空，未改该既有规则；本轮不能宣称通过 Steam 中文显示名验收。麦克风初始化失败仍是环境诊断，不在本次修复范围。
