# Crystalline Hollow 水晶同步移植与自查记录

2026-11-10。仓库基线 5.4.0-cu-port；移植来源为用户指定的上游 5.5.0 / 9e6a5d8 `Fullgame/Crystals.cs`。

已移植并加固；上游 **14/14 个 Harmony 目标全部保留，没有为了编译删除补丁**。本记录区分实际 Unity/网络测试、入口调用测试和仍需人工实机验证的项目。

## 改动文件

| 文件 | 内容 |
| --- | --- |
| `ScavPrototypeMP/Compatibility/Crystals.cs` | 上游 14 个补丁；FULLGAME 隔离、Together 命名空间、触发者/权威边界、协程过滤、敌水晶空目标保护、Septic 遍历全部身体 |
| `ScavPrototypeMP/Compatibility/CrystalSync.cs` | 服务端结算；客机接触上报；效果配置/已使用状态/冷却快照；声音、闪屏、电击/动能物理反馈；近战、枪击替代流程适配 |
| `ScavPrototypeMP/Compatibility/CrystalTests.cs` | `FULLGAME && TESTING` 的显式启用双实例集成夹具，Ship 不含此类或测试开关 |
| `ScavPrototypeMP/Net/MSG_ID.cs` | 仅增加 4 个保留字节的别名 `___11`～`___14`，原消息编号不变 |
| `scripts/Test-Crystals.ps1` | 使用 A/B、127.0.0.1:7790 和独立测试存档启动双实例；阶段文件位于本仓库 build 下 |
| `scripts/check_crystals.py` | 比对 14 个上游目标、消息编号、Escape 禁用及 Ship 中无测试/逐次激活日志；生成 SHA256、自查 JSON 和移植差异 |
| `CRYSTALS-PORT.md` | 本记录 |

构建日志、运行日志、测试存档、原 A/B DLL 备份及自查产物均位于 `build/crystal-tests/`。除测试副本的正常运行输出与 DLL 部署外，没有修改仓库外的源码或配置；未修改 `D:\steam\...`。

## API 与运行路径适配

1. 原样复制上游文件即可编译为 ShipFullgame，说明当前正式版 Assembly-CSharp/publicizer 与 14 个目标签名兼容。没有删除目标，也没有通过 `Prepare=false` 或忽略安装失败来绕过补丁。
2. 当前 `TranspilerHelpers.PatchCoroutines` 的 `params HashSet<string>` 默认是空集合。上游调用只传第三个 `null`，会把 `FlashBrief` 排除。现在显式传入 `{ "FlashBrief" }`，继续复用已有辅助方法，不修改全局 helper。
3. `CrystalEffect` 是普通 C# 对象，宿主是 `effect.crystal`。继续复用已有 `BodyGetterOverrider` / `AnyDataContainerComponent`；新增代码需要 `Unity.VisualScripting` 导入才能解析 Component 的 `GetOrAddComponent` 扩展。
4. 本仓库的 `Body_Attack_MultiplayerPatch.DamageBuilding`、`Stolen_ShootApplyDamageBuilding` 会绕过原版 `SendMessage("BuildingHit")`。新增局部 Postfix 补接真实结算入口，使用参数中的 attacker / gun 的已知 NetBody；枪击不通过“最近玩家”兜底。
5. `Body_Attack_MultiplayerPatch.LastAttacker` 不具备事件作用域。新建局部 `Body.Attack` Prefix/Finalizer 作用域，异常也恢复；每次水晶派发均用 `try/finally` 清理 BodyGetter 和临时数据，非身体/无主命中不会继承上一人的身份。
6. 网络 prefab 的实例化路径不调用 `SetUpEffects`。服务端配置通过快照传递到客机；客机不独立随机选择特效；重复快照不重新执行 Start，也不重复创建火焰/Tesla 等子物体。
7. 测试夹具中 Unity/Mono 不提供三参数 `File.Move`，`File.Replace` 在当前受限环境也失败。最终改为每阶段独立文件，写完后创建 ready 标记；这不是游戏 API 的删减。

## 结算与状态边界

- 服务端唯一执行身体数值、EMP 电量、治疗、生成掉落/敌人和爆炸等结算。客机只报告本机身体的接触；服务端从连接身份解析身体，校验对象 ID、Version、距离、世界状态、存活建筑及启用中的碰撞器。
- 燃烧 0.75 秒、电击 30 秒沿用原版的单个效果冷却；EMP/Healing 沿用单个效果的 activated 标记。多人同帧触碰一次性效果，由服务端先接收到的有效触发者取得效果；后续玩家不会在自己的副本上再次消费。
- 客机不再执行 `Burn` / `Shock` / Healing 的身体数值修改。身体数据沿用强制确认的 MedicalSync；EMP 物品条件值沿用 SyncInfo。可靠反馈事件只播放声音、指定受害者的闪屏/震屏与本机物理反应。
- 无归属的射击/世界命中可消耗 EMP 或启动不稳定水晶，但不扣任何“最近玩家”的物品。无来源的徒手反伤不猜测玩家，并限频 `[crystal]` warning。
- 致命近战命中仍会派发 BuildingHit 效果；已关闭碰撞器或死亡水晶的迟到接触上报被拒绝。
- 连续效果仍以服务端为权威：Septic 遍历半径内全部 NetBody；温度效果在实际创建 Heater 后安装现有多人重写；重力在客机仅对本机身体施加连续力，并保留环形视觉。动能、传送的拥有者反馈另行转发。后三类没有纳入本次四类断言集。
- 逐次 `[crystal] activated` / `feedback` 仅 TESTING 编译存在。Ship 只保留限频诊断 warning；可执行自查确认 Ship 无测试类、测试命令和上述 Info 文本。

## 构建

- ShipFullgame：**0 error / 104 warning**。使用用户给出的原始 build 命令完成最终构建；日志 `build/crystal-tests/ship-exact-build.log`。
- PlaytestFullgame：**0 error / 100 warning**。日志 `build/crystal-tests/playtest-build.log`。
- 新增水晶源码未产生编译 warning；现存项目 warning 未清理。
- `python scripts/check_crystals.py` 通过：14 个原目标相同、只增加消息别名、Escape=false、Ship 无测试夹具/逐次日志。结果见 `build/crystal-tests/selfcheck.json`。

最终 A/B 均部署 ShipFullgame DLL，且与 `build/crystal-ship/CasualtiesMP.dll` 逐字节一致。SHA256：

`c49bc1686388ad7bef3d8322d7e818b323448bbb9eb9799ea6a1ac48b5bace2e`

测试进程已停止。没有将 Playtest DLL 留在 A/B。

## 最终双实例记录

运行目录：`build/crystal-tests/verified/`。A/B 均为实际 Unity 实例，使用 `--mp-nosteam --mp-ip-port 127.0.0.1:7790`；主机 `--mp-starthost`，客机 `--mp-startclient`。对象使用真实 `Utils.Create("crystalmedium", ...)` 创建，夹具确定指定单个特效以避免随机混合干扰。正式游戏仍使用原版随机效果配置。

主机 **23 个 PASS**；客机 **12 个 PASS**。主机统计：59 次真实碰撞回调、11 个有效接触上报、8 次预期效果激活；客机收到 4 种针对自己的反馈。完整判定结果：`build/crystal-tests/verification-result.json`；精简证据：`host-evidence.log` / `client-evidence.log`。

| 项目 | 测试方式与结果 |
| --- | --- |
| 主机燃烧 | 重复调用真实接触派发入口；温度仅 +0.2，触发者作用域恢复。通过 |
| 客机燃烧 | 将客机身体移入真实 prefab 碰撞器，经过 Unity OnCollisionEnter2D；燃烧的是客机，客机收到燃烧反馈。通过 |
| 客机电击 | 同样使用真实碰撞；服务端/客机碰撞上报在同一冷却内仅产生一次电击。客机收到 ragdoll/速度反馈。通过 |
| 同时争用治疗 | 同一帧按主机、客机身体依次调用接触入口；主机脑健康 50→55，客机不重复获得本次治疗。通过 |
| 远程治疗 | 客机通过真实 LiteNetLib receiver 上报（包括重复 claim）；客机收到一次治疗约 55 的脑健康，而非重复治疗的 59.5；黑色 renderer 和 activated 已同步。通过 |
| EMP 归属/去重 | 调用现有近战替代结算入口，分别以客机、主机为攻击者；客机电池 1→0.67，仅扣一次，客机收到 0.67 和隐藏 Tesla 的状态。通过 |
| 致命近战 | 主机通过同一结算入口将水晶打至死亡，EMP 仍激活。通过 |
| 无主命中 | 清空攻击者的世界命中消耗 EMP，不改变上一个攻击者的电池。通过 |
| 快照重复 | 客机持续接收后续快照，已消耗状态不回滚，子物体数不增加、Start 不重放。通过 |
| 日志 | 最终 verified 的两份日志均无异常栈、无 Failed to patch、无测试 FAIL、无水晶 warning。仅存在原有麦克风设备初始化失败（每实例两条），不属于水晶路径 |

早期迭代日志保留，未删除失败证据：测试准备曾因电击后昏迷而掉出电池；阶段文件曾有共享/替换权限错误；跨水晶全局事件计数曾干扰去重断言，均已修正。`run2` 另出现原有 `ClientReceiver__SetBlock → WorldGeneration.UpdateChunk` 越界栈，不在水晶栈内；没有借本任务修改地块接收器，最终 verified 没有复现。

## 自查与保护项

`EndingFlow` 及其子文件、SteamworksNet、中文字体/中文名处理、release 降噪、keypad 代码均未修改。`EndingFlow.EnableEscape = false` 已由脚本再次断言。此次直连测试不等同于重测 Steam 连接或全部既有结局流程。

可复跑：

```powershell
$env:NUGET_PACKAGES="F:\cu-port\.nuget"
E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c PlaytestFullgame -p:SolutionDir=F:\cu-port\src\casualties-together-main\ -p:ModPluginOutputDir=F:\cu-port\src\casualties-together-main\build\crystal-playtest
.\scripts\Test-Crystals.ps1 -Phase rerun
```

脚本返回启动进程 ID，判定结果随后写入两份日志及 `server.done` / `client.done`。夹具借用现有 `--mp-ending-test` 的存档/PlayerPrefs 隔离机制，但通过专用前缀阻止 EndingFlowTests 场景运行；Ship 不包含这些测试钩子。复跑完成后应停止本次生成的两个 PID，并将 Ship DLL 重新复制到 A/B（本次已完成）。

## 仍需人工实机验证及遗留风险

- **尚未完成像素画面/音效人工对照**。computer-use 工具未列出新启动的游戏窗口；本次可见性证据是 Unity 的 renderer 色值、Tesla activeSelf、子物体数和本机反馈回调断言，并非截图或人工观感确认。
- 主机接触、多人争用和近战攻击测试直接调用实际派发/结算入口；**尚未完整走双方玩家输入→近战网络包，以及真实实弹命中流程**。枪击和无主射击的钩子已接好并编译/安装成功，实弹归属仍需人工对照。
- 未覆盖高延迟/丢包、断线重连、真实离开并重入模拟范围、大量水晶性能、所有多效果组合，以及 CrystalEnemy 瞄准/激光视觉、重力、动能、传送、变质掉落、不稳定爆炸的完整实机表现。快照有 ID+Version 和恢复请求保护，但不能把该设计视为这些场景都已验收。
- 水晶效果状态是当前会话的同步状态，没有新增水晶专用磁盘存档格式；保存退出后的效果重建/已消耗标记保留仍沿用现有世界重建机制，应另行验证。
- 新保留消息需要所有参与者更新同一 DLL；不支持与仍未移植水晶功能的旧 5.4.0-cu-port 客机混用。
