# 5.5.0-cu-port 十项问题：打包前自查

> 掉落物第①项的旧验证不足：合成添加组件没有覆盖真实开箱的服务端距离入口。第三次反馈后的实际复现、根因、修复及芯片选择交付见 [DROP-CHIP-REVIEW.md](DROP-CHIP-REVIEW.md)，以该报告的验证边界为准。

日期：2026-11-11（Asia/Shanghai）。源码、测试脚本、报告与构建产物均在本仓库。运行验证仅使用 `F:\cu-port\CU_Fullgame`（A）和 `F:\cu-port\CU_Fullgame_B`（B），向这两个测试副本部署测试 DLL；未操作 `D:\steam`。本轮未生成或替换发布 ZIP。

## 结论与测试边界

十项均已实现修复。真实 Unity 双实例、真实 9/10/11 层地形和真实网络处理链已分别以客机持货、主机持货通过。测试通过服务端传送推进到关键节点；没有声称完成全程人工攀爬/键鼠游玩。科学家对话等待自然完成，没有直接设置完成标志，也没有跳过 Talker。

网络为 LiteNetLib 直连，`--mp-nosteam`，端口 `127.0.0.1:7790`。Steam 双机、真实麦克风驱动、鼠标点击退出图标未在本轮人工验证。退出验证在大厅调用真实 `AdaptiveButton.Clicked → PreRunScript.Quit` 处理链，世界内调用同一多人退出流程，再等待进程结束。桌面工具没有列出测试游戏窗口，未改用伪造窗口句柄进行操作。

## 原版证据、根因、修复与验证

以下原版文件统一位于 `F:\cu-port\decomp\fullgame`；修复文件统一位于本仓库。标记“修复前”的行号对应本轮首次读取的代码，便于区分已被替换的逻辑。

### ① 客机掉落物立即下落

- 原版：`FreshItemDrop.cs:17` 读取本地 `Item.itemFloating`；`:22` 初始化 10 秒，`:24` 重力为 0；`:49` 扣减时间，`:63` 用 `Clamp01(1-timeLeft*0.5)` 恢复重力，`:68` 销毁清理。即生命周期 10 秒，最后约 2 秒渐增重力。`Settings.cs:236` 从本地设置写入 `Item.itemFloating`。建筑掉落公共路径在 `BuildingEntity.cs:147`、`:170`、`:181` 添加该组件。
- 根因：`Net/ScavLiteEntitySystem.cs` 修复前 `:1412`、`:1638` 只同步“剩余大于 5 秒”的 bool，没有同步剩余时间，且旧写入在小于 5 秒时可能保留 true；客户端 `:2244` 只 AddComponent。`Patches/ItemPatches.cs` 修复前 `:210` 又根据本地距离删除组件。客机 Awake 还可能被本地悬浮设置直接销毁。这是已确认的三条不等价路径；用户当时命中哪一条没有现场日志，不能唯一归因。
- 修复：`Net/ScavLiteEntitySystem.cs:866` 新增十分之一秒剩余时长，同步整个生命周期；`:2242` 客机按服务端时间建立/更新/移除组件。`Patches/ItemPatches.cs:205` 让客机 Awake 接受服务端悬浮状态；`:218` 不再由客机距离清除；`:242` 清理时允许 rb/特效未初始化。日志 `[drop]` 仅在新掉落节点出现。
- 验证：两种持货角色的整链测试均通过客机初始零重力、末期渐增重力、10 秒后组件清除与重力恢复。使用同一组件/网络注册路径的 waterbottle 掉落实例；没有逐个鼠标开启空投仓、医疗箱或实际挖矿。

### ② 多人点击退出崩溃/无响应

- 原版：`AdaptiveButton.cs:67` 调用 `PreRunScript.Quit`；`PreRunScript.cs:153` 进入协程，`:164` 渐暗，`:166` 直接 `Application.Quit()`。
- 根因证据：多人 `ScavMultiplayer.cs` 修复前 `:418` 的退出回调仅恢复 runInBackground，没有在 Unity/Steam 销毁前停止传输层。`Content/Voicechat.cs:1253` 旧 StopRecording 会在退出清理时继续发送积压音频。可以确认退出顺序缺失；本机没有抓到用户的原生崩溃堆栈，不能声称精确定位了所有平台上的 native 死锁点。
- 修复：`Compatibility/MultiplayerQuit.cs:16` 防重入，`:26` 渐暗后先停麦克风/传输层再退出；`:39` 幂等停止。`Net/Net.cs:480` 先使网络失活，避免断开回调在退出中发起返回菜单。不会在渐暗期间调用会删除玩家/切场景的 ShutdownNetwork。`Content/Voicechat.cs:1253` 增加可禁用 flush 的清理，`:1644` 退出时不再发送音频；`ScavMultiplayer.cs:394`、`:420` 避免退出期间误报 singleton 丢失，并覆盖非菜单退出回调。日志 `[quit]`。
- 验证：大厅按钮处理链客机、主机进程均正常结束；主机退出码 0，客机首次记录未取得 ExitCode，但确实结束且有正常 Unity 清理日志。世界内客机、主机均退出码 0。未通过真实鼠标或 Steam 网络复现原报告。

### ③ layer 9 蓝心位置不一致

- 原版：`WorldGeneration.cs:3182` 随机选择结构坐标，`:3184` 写结构地形，`:3185` 生成实体。`:1705` 先减 `(0.5,0.5)`，`:1708`/`:1710` 按 Item、BuildingEntity 或 DOSPAWN 实例化子对象。
- 根因：蓝心所在 `DOSPAWN` 只有 Transform/SpriteRenderer/Light2D/BlueHeart/LookTarget，不属于现有 item/building 网络实体。两端独立随机生成它，原来的 EndingFlow 只同步货物归属，没有同步场景坐标。
- 修复：`Compatibility/EndingWorldObjects.cs:16` 只由服务端记录真实 BlueHeartCont origin；客机跳过本地随机实体生成，`:28` 从服务端坐标按原版偏移创建蓝心 DOSPAWN。地形与钻探仓继续使用现有同步，不另造第二套 building。EndingState 携带 origin 与 travel 标识，避免串层。日志 `[cargo]`。
- 验证：客机持货回归中两端蓝心均为约 `(-38.02,-91.17)`；位置差断言小于 0.2（原版自身有半径 0.1 的随机抖动）。主机持货回归也通过真实随机结构的坐标一致检查。

### ④ 关键玩家在钻探仓内仍不能发车

- 原版：`CargoDrillPod.cs:20` 根据 hasCargo 激活；`:31` 的 OnUse 仅检查 didReady/started，随后服务端需要调用 RegenerateWorld。原版根本没有当前补丁所声称的固定“仓内范围”。`PlayerCamera.cs:1772` 的使用半径为 `10*rangeMultiplier`。
- 根因：`EndingFlow.Travel.cs` 修复前 `:36` 用预制体原点的 `x±1.5/y±5` 判定，`:43` 查找还排除了 inactive 仓。真实测试中仓原点 `(110.24,10.58)`，激活后 collider 中心 `(109.31,10.20)`、半宽 4.22/半高 6.47；玩家身体可在 `(104.24,9.62)`，旧 x 条件必然拒绝。此外仓在远处会被优化为 inactive。
- 修复：`EndingFlow.Travel.cs:38` 使用活着的持货玩家、真实 collider 范围及身体/交互容差，倒地时读取实际躯干位置；`:55` 包含 inactive 仓。保留 cargoOwner 持久 ID 的服务端唯一权威，不通过临时客户端 ID 猜归属。发车仍仅服务端 RegenerateWorld，客机跟随。拒绝时记录 owner、hasCargo、坐标和 bounds，成功时记录 departure，均为 `[cargo]`/`[ending]`。
- 验证：客机关键玩家和主机关键玩家两条真实预制体路线均从 CargoDrillPod.OnUse 发车到 10 层。

### ⑤ layer 10 从顶部出生

- 原版：`Body.cs:2185` 普通层从顶部扫描；`:2203`/`:2205`/`:2207` 在第 10 层从底部向上找首个空位。`WorldGeneration.cs:2487` 调用 PlaceBody。
- 根因：`Patches/BodyPatches.cs` 修复前 `:303` 的保留 X 路径绕过原版，调用 `Util.cs:1318` 只有顶部扫描的旧助手；`ServerMain.cs` 修复前 `:576` 对远端玩家重复使用该助手。DeferredPlayerSaveRestore 还无条件读取旧 PLRPOS，跨层存档可覆盖新出生点。
- 修复：`Util.cs:1318` 增加与原版等价的第 10 层底部扫描；本地 Body 使用自己的 origColSize。`Patches/BodyPatches.cs:305` 客机跳过本地 PlaceBody；`:310` 主机确定权威点，`:335` 同步其他玩家。`ServerMain.cs:571` 后续出生使用已选定的 SpawnLocation，避免不同帧重复扫描产生不同 Y。`DeferredPlayerSaveRestore.cs:37` 仅同层保存恢复 PLRPOS，`:114` 位置恢复使用 Server_TeleportCharacter。日志 `[spawn]`。
- 验证：最终主机持货回归主机、客机的服务端出生指令均为 `(0,-506)`，客机实际位置处于底部。两种持货角色均通过；跨层存档恢复分支做了源码审查，本轮未额外重启读档回归。

### ⑥ 非关键玩家到 10 层顶层没有提示

- 原版：`WorldGeneration.cs:1199` 使用 `halfHeight-5` 判断第 10 层顶层。原版只有一个玩家，没有多人等待提示。
- 根因：旧 `EndingFlow.Travel.TickFlow` 只检查 KeyPlayer 的到顶分支，漏掉非关键玩家。
- 修复：`EndingFlow.Travel.cs:75` 按同一高度为非关键玩家发送一次 Notice，离开后再次到达可重发；EN/zh-CN 添加 `ending_wait_carrier_surface`，中文为“等待关键玩家到达顶层。”。日志 `[ending]` 记录 Notice key，不逐帧刷屏。
- 验证：两种持货角色回归均检查“非关键玩家先到顶不能触发 Breach”；服务端日志分别记录本地主机与远端客机的等待 Notice。真实鼠标/人工阅读弹窗未单独验收。

### ⑦ 到顶剧情突然黑屏

- 原版：`WorldGeneration.cs:1234` Darken，`:1235` 等待渐暗结束，`:1240` 才创建 cover 文字；`:1254`/`:1255` 再次渐暗后切换实验室文案。`GlobalDark.cs:89` 的 Update 用 unscaledDeltaTime 渐进，不是瞬间置黑。
- 根因：旧 `EndingFlow.Travel.cs` BreachPresentation 中 Darken 后立刻 CreateText(cover:true)，遮罩盖住了渐变。
- 修复：`EndingFlow.Travel.cs:137` 复用原版 Darken/WaitUntil，先渐变再建 cover，第二次切换也等待渐变；服务端演出窗口为 19 秒。`:133` 在 doingRegen 时禁止再次启动演出，修复首轮回归发现的换层淡出重播。日志 `[ending] breach fade started/completed`。
- 验证：两端均在触发后 0.15 秒测得 `0<alpha<1` 且此时没有文字 cover；两份包含第十项的最终路线日志中，各端每轮只有一次 Breach 开始。

### ⑧ layer 11 出生在岩层

- 原版：`Body.cs:2178`/`:2180` 直接使用 ENDLABSPAWN；`WorldGeneration.cs:4031`/`:4033` 创建 EndLab 场景。
- 根因：与第 ⑤ 项共享的多人顶部扫描/保留 X/Y-only 放置绕过了原版实验室分支。客户端“缺少 ENDLABSPAWN”的假设在本轮场景中未复现：两端都确实有该节点，不能把假设写成已证实事实。
- 修复：`Util.cs:1324` 服务端显式解析 ENDLABSPAWN，缺失则报 `[spawn]` 错误并拒绝岩层/顶部回退。`Patches/BodyPatches.cs:305` 客机不再执行本地 Find 或扫描；其他玩家经 Server_TeleportCharacter 及现有 body 包同步完整坐标，后续出生复用该权威点。
- 验证：两条真实整链均在服务端/客机验证 ENDLABSPAWN，出生指令 `(54,-10)`，两端玩家均在实验室生成点 3 米内。

### ⑨ 科学家对话前强制结局一

- 原版：`EndLabScript.cs:95` 开始 ScientistCutscene，先等玩家 `x<17`，播科学家主对话/玩家响应；`:145`/`:146` 播完 endsc9 后才进入等待出口的循环；`:162` 距出口小于 4，`:172` 渐暗，`:174` 才 BiopodEnding。endsc10～13 是循环中每 14 秒的可选催促，不是必须播完的主对话。
- 根因：`EndingFlow.Results.cs` 修复前 `:38` 只用出口距离 Finish(1)，另有 brainDying 直接结束；旧 LabDialogue 又把 1～13 简化为循环，与原版入场/响应/出口顺序不一致。
- 修复：`EndingFlow.Results.cs:34` 只由服务端、活着的关键玩家触发；`:42` 要求持久化 `labDialogueComplete`。`:117` 恢复入场门槛、1～9 主对话、玩家响应/思考和原版等待时长；主机在 Talker 真正 FinishedTalking 后于 `:155` 置完成标志并同步。移除对话前 brainDying 快捷结算。10～13 继续作为可选催促；恢复科学家 sprite 动画。日志 `[ending]` 区分入场、对话开始、完成、结算。
- 验证：两条路线均通过提前把关键玩家放到出口不结束、自然等待主对话完成、完成本身不结束、非关键玩家到出口不结束、关键玩家最后到出口才结局一。客机收到含完成标志的结果。

### ⑩ 蓝色光环、扩散脉冲与靠近/拾取表现丢失

- 原版：`BlueHeart.cs:144`～`:179` 为距离/镜头、首次接近奖励与 cargo1、音乐衰减、脉冲计时、材质三个属性、抖动/呼吸；`:182` Pulse 重置。`:132`～`:142` 是本地拾取触发，不能直接放行。`:56`～`:69` 是 5 秒上浮、加速、增光和 blinding。注意 pulseSpeedAdd 的持续增加在 Cutscene `:63`，不是普通 Update；本次依原文件恢复。
- 根因：旧 `EndingFlow.HeartUpdate` 无条件返回 false，整个原版 Update 被 Prefix 屏蔽，表现与权威拾取一起消失。
- 修复：`EndingFlow.cs:276` 仍禁止原版本地 Cutscene；`EndingFlow.Heart.cs:21` 按原版公式恢复视觉、声音、接近台词与奖励，远端奖励亦由服务端记录。`:81` 使用服务端 HeartPickup 事件/同步状态防重入；`:114` 仅播放原版 5 秒表现，不执行本地 EmbedCargo。`:71` 服务端计时完成后唯一 EmbedCargo，然后同步销毁蓝心。保存带有 pending/progress，可恢复待完成拾取；死亡/断线可沿既有 owner 转移。日志只记录场景激活、首次靠近、一次动画开始、最终 commit，脉冲每帧不打日志。
- 验证：两端材质 `_DesiredDistance` 随帧变化、颜色 cyan 且 alpha>0；接近时 wasClose、音乐衰减通过。导出并目视检查 Unity 实际渲染截图，均看得到扩散蓝环与 cargo1 台词。拾取期间先锁归属但 hasCargo=false，两端各仅一次动画，随后服务端单次 commit，蓝心全局消失、cargo 唯一。两端采用原版按本地距离调制的脉冲，不承诺相隔不同距离时像素/相位完全一致。声音调用与原版相同，未进行人工听音验收。

## 双实例记录与复现

| 记录目录（build/ending-tests 下） | 结果 | 内容 |
|---|---|---|
| review-full-route-1 | 早期失败，已修 | 找不到 inactive 钻探仓；保留日志，没有当作成功 |
| review-full-route-2 | 通过，早期 9 项版 | 客机持货完整 9→10→11→科学家→结局；尚未加入第十项 |
| review-full-route-3 | 通过 | 含第十项，客机持货；两端蓝环截图、掉落物、权威拾取、换层、自然对话与出口条件 |
| review-full-route-host | 通过 | 最终源码的主机持货整链；同时覆盖远端非关键玩家先到顶的 Notice |
| review-quit-lobby-2 | 通过处理链 | 大厅客机/主机退出；真实鼠标点击未验证 |
| review-quit-world | 通过 | 世界内客机/主机退出，均退出码 0 |

最终路线的 host.log/client.log 均以 `[route-test] ... full-route complete` 收尾，有 `server.done`/`client.done`。测试摘要见 `build/port-review/results.json`。所有测试保存均通过 CT_ENDING_TEST_SAVE 指向仓库内独立目录；测试夹具禁用 Steam achievements。沿用既有测试夹具的 PlayerPrefs 测试进度行为，不把这些测试标志视为真实游玩进度验收。

复现：先构建 PlaytestFullgame，将 ModPluginOutputDir 指向本仓库 `build\ending-playtest`，再执行：

```powershell
.\scripts\Test-PortBugRoute.ps1 -Scenario full-route
.\scripts\Test-PortBugRoute.ps1 -Scenario full-route-host
```

该脚本要求先关闭既有 A/B 实例；只清理本轮启动且路径匹配 A/B 的进程。超时/断言失败返回失败并保留日志。夹具、截图逻辑只编入 FULLGAME && TESTING，不进入 ShipFullgame。

蓝环截图：`build/ending-tests/review-full-route-3/host-blue-ring-1.png`、`host-blue-ring-2.png`、`client-blue-ring-1.png`、`client-blue-ring-2.png`。完整视觉记录也保留在主机持货测试目录。

## 改动文件清单

- 生产逻辑：`ScavPrototypeMP/Compatibility/DeferredPlayerSaveRestore.cs`、`EndingFlow.cs`、`EndingFlow.Travel.cs`、`EndingFlow.Results.cs`。
- 新增生产文件：`ScavPrototypeMP/Compatibility/EndingFlow.Heart.cs`、`EndingWorldObjects.cs`、`MultiplayerQuit.cs`。
- 网络与生命周期：`ScavPrototypeMP/Net/ScavLiteEntitySystem.cs`、`Net/Net.cs`、`Content/Voicechat.cs`、`ScavMultiplayer.cs`。
- 出生点/掉落：`ScavPrototypeMP/Patches/BodyPatches.cs`、`Patches/ItemPatches.cs`、`ServerMain.cs`、`Util.cs`。
- 本地化：`ScavPrototypeMP/assets/locale/EN.json`、`zh-CN.json`。
- 测试：`ScavPrototypeMP/Compatibility/EndingFlowTests.cs`、新增 `EndingRouteRegression.cs`；`scripts/Test-EndingFlow.ps1`、新增 `Test-PortBugRoute.ps1`。
- 文档：本报告、`MULTIPLAYER-ENDINGS.md` 的最新报告入口。构建 DLL、截图与日志位于 build 下。

## 构建、保留项与遗留风险

- 版本仍为 `5.5.0-cu-port`。ShipFullgame 为 RELEASE;FULLGAME；PlaytestFullgame 同样能编译。最终日志：`build/ship-review-build.log`、`build/playtest-review-build.log`，均 0 error；既有警告未作为 0 warning 宣称。
- Ship DLL：`build/ship-review/CasualtiesMP.dll`；Playtest DLL：`build/ending-playtest/CasualtiesMP.dll`。最终哈希与配置检查记录在 `build/port-review/results.json`。不生成 ZIP，先交此报告。
- `EndingFlow.EnableEscape=false`（`EndingFlow.cs:57`）。未改变 Steam 连接/握手、中文名编码、release 编译常量/日志降噪、keypad、沙盒、水晶同步源码。退出清理仅作用于退出阶段，仍需 Steam 联机复验该阶段。
- SyncInfo 新增剩余时间字段；主客机必须同时换成同一轮 DLL，不能混用旧 5.5.0-cu-port 二进制。
- 未独立回归：Steam 双机、真实麦克风退出、真实键鼠退出、三类掉落来源逐个开箱/挖矿、跨层/同层读档、拾取的 5 秒内断线/死亡/重启保存，以及有人死亡/旁观时的实验室玩家响应。待完成拾取与对话标志的保存路径做了实现与代码审查，未把它们标成实测通过。
- 运行环境有既有麦克风设备初始化失败、veinloop/空 background id 相关诊断；整链断言没有因此失败。本轮不扩展修改这些不在十项内的问题。
- 第②项已消除明确的退出顺序缺口并通过本机直连验证，但仍需用户原 Steam/设备组合验收才能确认覆盖原生崩溃。
