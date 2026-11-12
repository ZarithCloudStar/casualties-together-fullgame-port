# Release14：Merc 剧情与 Escape 正式启用

## 实现与根因

Merc 原因是 `EndingFlow.Merc.CheckMercEnding` 直接 `Finish(3)`，而 `ApplyResult` 会关闭 ScrollableText 并立即启动 EndSequence。现在触发时进入服务端 `Narrative` 阶段，每端播放剧情并确认读完，全部在线玩家确认后才提交原有全员强制结算。触发资格仍由原来的团队围巾、核心玩家 layer10 顶部、主机解锁进度/endless 判断决定。

新增 `EndingFlow.Narrative.cs`，复刻正式版 `WorldGeneration.MercEnding()` 的关键顺序：真实 GlobalDark.Darken/等待完成、mercnote1、0.1 倍渐变与图片9、3秒及攻击键、再渐黑、mercnote2 与 endlabdrone、图片11置于文字下、末页等待/最多5秒、heartsplosion、完全黑场停留4秒、mercnote3、biopod、0.05倍渐变，然后统一 EndSequence(3)。各端独立阅读，不用主机固定秒数强关客机文字；先读完的人黑场等待。日志有 [merc] stage、dark alpha、ready 玩家以及最终 EndSequence。

Escape 原开关 false、资格判断读主机 WorldGeneration.unchipped；还存在自创的60秒超时即失败、守卫击倒30秒判负，及跳过原版逃亡叙事直接结算的问题。本版开关为 true，科学家主对话完成后，由既有枪击 RPC 在服务端重放射线命中原版 `Scientist` tag collider，校验 `ChipState.IsUnchipped(KeyPlayer)`。主机芯片仅记录日志，其他玩家不会触发原版多人芯片处刑。

Escape/Surgery 仍为服务端权威。核心在(250,234)两格内、5秒警报后，各端完成真实黑场并确认，再全员传送(287,243)开始60秒。同步局麻就绪、操作人、7个不可回退手术里程碑、阶段与 cargo；读取核心身体自己的 CoUtils 下躯干局麻 DurationOf>50，支持非核心操作者。恢复 end1/end2、injector FreshItemDrop、cargosurgery1..6、removedHeart 和手术门。计时器以服务端数值为准，客机在每秒快照间插值显示。

术后任意存活队友 x>353 触发同步的 forceWalk、x>366 触发 Ragdoll/尖叫并进入全员逃亡剧情（escapenote1、图片8/各自人物图片、3秒及攻击键、渐黑、escapenote2/图片12），最后统一 EndSequence(2)。不再依赖主机相机身体达到原版单人掉落位置才能结算。死亡/旁观玩家也参与剧情确认和结算；旧死亡协程/界面会被清除，实验室红字提示/广播界面在结局期间隐藏，避免盖住正文。

## 原版失败规则核对

依据只读反编译 `F:/cu-port/decomp/fullgame/EndLabScript.cs` 的 EscapeCoroutine/Update、`GuardSpawner.cs`、`GuardScript.cs`：

- 原版 Update 在 timeLeft<0 时关闭 doTimer，并令 endGuardSpawner.guardsToSpawn=5、openProgress=5；不是直接死亡或失败结算。
- GuardSpawner 按与身体距离、minTimeBetweenSpawn 生成守卫。现在保留此逻辑，仅主机运行，目标改为核心；普通走廊 spawner 也保留，客机只接收守卫姿态/生命，不运行 AI/开枪。
- GuardScript 的近战是 Ragdoll、击退和伤害，没有独立“被抓”判负开关或30秒救援规则。
- EndLabScript.Update 的逃亡失败入口是 `body.brainDying`，停止 EscapeCoroutine 后播放 BiopodEndingAlt2。
- `Body.brainDying` 实际是 bloodPressure<10 且 consciousness<5 且非剧情；此外多人也处理核心已经死亡的状态。

因此本版保留超时放守卫、移除击倒30秒判负，核心濒死/死亡全员播 biopodnotealt2 并结算1，非核心死亡不改变结局状态。与用户此前“失败转Biopod”定稿一致。没有发现需要改成普通死亡结算的原版分支。

## 改动清单

- 生产：Compatibility/EndingFlow.cs、EndingFlow.Merc.cs、EndingFlow.Narrative.cs（新增）、EndingFlow.Escape.cs、EndingFlow.Surgery.cs、EndingFlow.Results.cs、EndingFlow.Travel.cs、EndingGuardSync.cs。
- 文案：assets/locale/EN.json、zh-CN.json，把资格提示从“主机无芯片”改为“核心玩家无芯片”。
- 夹具：EndingFlowTests.cs、Release14EndingTests.cs（新增）；保留旧 escape-* 场景别名并转入新流程。
- 脚本：Test-EndingFlow.ps1、Test-Release14.ps1、check_release14.py。
- 交付说明：RELEASE14-NOTES.md、RELEASE14-MANUAL-TEST.md、本报告；MULTIPLAYER-ENDINGS.md 顶部标明当前规则，旧记录作为历史保留。完整包放在仓库 build/release14 内。

## 构建与验证

用户指定 dotnet/NUGET_PACKAGES/配置，另用 ModPluginOutputDir 将构建复制目标放在仓库内。ShipFullgame：0 error / 104 warning；PlaytestFullgame：0 error / 100 warning。日志：build/release14-ship-build.log、build/release14-playtest-build.log。既有编译警告未在本任务中扩展修复。

运行的是指定 A/B 两个实际游戏进程、127.0.0.1:7790、--mp-nosteam 与 --mp-ending-test。测试不读写 Steam 安装目录；测试存档、截图、结果均在仓库 build/ending-tests 内。包使用 Ship DLL；自动化使用 Playtest DLL，两者共享生产代码，夹具不编入 Ship。

最终同一 Playtest DLL 的7个双实例场景全部通过，共227条通过断言。完整结果在 `build/r14-verified-results.json`，校验结果在 `build/release14-verification.json`。结果行保存实际日志目录和 DLL SHA256，包含下面两组运行，不把探索失败计入通过。

| 需求 | 自动结果与具体证据 |
|---|---|
| Merc | `r14-verified-r14-merc`：非核心持围巾、核心顶部、两端三段文字/图片/真实黑场中间 alpha、全部在线 ready 后 EndSequence(3)；截图可见 mercnote1/2/3 |
| T1 非核心枪杀科学家 | `r14-stable-r14-escape`：客机发生产 SERVER_PlayerBodyShoot 消息，服务端验证枪械/射击起点/发送者并命中真实 Scientist collider；非核心带芯片射手仍存活，核心有芯片拒绝、核心无芯片允许 |
| T1 芯片独立性补充 | `r14-verified-r14-remote`：核心在客机、主机有芯片，仍可进入并完成 Escape；日志明确 carrierUnchipped=True / hostUnchipped=False |
| T2 核心到门、全体传送及60秒 | 上述两成功场景均断言核心入口、双方实际完成黑场确认、传送位置收敛、服务端剩余58–60秒、客机共享值与计时 UI。没有用夹具站位覆盖传送来伪造通过 |
| T3 非核心协助手术 | `r14-stable-r14-escape`：原版 Inject 达到核心下躯干局麻阈值；客机操作真正 CargoMinigame，4夹钳/拔出/2软管全部由原版 PhysicsUpdate/Update 与正式 hooks 发送；服务端 mask=127、hasCargo=False、门打开且计时停止 |
| T4 任意队友出口、全员动画/结算 | 同一场景由非核心客机分别跨 x>353 / x>366；两端依序 escapenote1、图片8/人物图、第二次渐变、escapenote2，最终 type2 与各自元进度 |
| T5 核心死亡 | `r14-verified-r14-coredeath`：显式将核心脑健康置0，两端 biopodnotealt2、真实黑场、type1，死者也结算 |
| T5 击倒与原版濒死阈值 | `r14-verified-r14-capture`：先 Ragdoll/守卫击倒回调，把旧 downSeconds 设99仍不失败；再设 bloodPressure=0、consciousness=0，断言原版 brainDying=True 后走 Biopod。原版没有独立被抓终局，未伪造一个 |
| T5 原版超时放守卫 | `r14-stable-r14-timeout`：将计时推进边界，不立即失败；原版 spawner 实际在19:30:27/34/41/48/55各放一名守卫，恰好5名；客机可见同步守卫。期间19:30:38真实近战击倒核心也没有提前失败，之后显式击杀核心进入 Biopod |
| T6 非核心死亡继续 | `r14-stable-r14-noncoredeath`：非核心客机一直保持死亡，不复活；存活核心完成真实手术和出口，死者看完两段逃离剧情并 EndSequence(2)，死亡结算界面不会覆盖正文 |

所有上述场景均断言两端 GlobalDark alpha 实际经过0与1之间的值，而非只检查调用过 Darken。每端最终有 `.done`、阶段日志及截图。人工鼠标手术、音画观感和异机网络仍按下文边界处理。

可复现：构建 PlaytestFullgame 到 build/ending-playtest，运行 `scripts/Test-Release14.ps1 -Run <新名字>`。归档校验命令：`python scripts/check_release14.py`。旧 escape-success/killed/down/timeout 别名也已映射到新夹具；自动阅读只在显式 --mp-ending-test 的 TESTING 夹具中存在，正式包按原版按钮/攻击键手动阅读。

二进制 SHA256：

- Ship：`F7B6B0C3C60518212AA47D218AEF215B0B125DCD70DBEA8622DC14B1429AAFC8`
- Playtest：`45811EEE73047A8AF057E85E601D6E4300B4D39E4D201BD8D0D82E8AD70D1B5E`

已验证 Ship 不含 Release14EndingTests、EndingFlowTests、AutoNarrative、DropSourceRegression；包内 DLL 与 Ship 输出一致。没有把 Playtest 自动化描述成 Ship 二进制独立运行过夹具。

探索失败已保留：`r14-first`/`r14-second` 的固定站位覆盖传送；`r14-third` 成功手术后发现 doTimer 晚一帧关闭（已改为同次提交）；早期 `r14-final`/`r14-acceptance` 的初始生成/立即开局竞态。夹具现在只在自身负责的开局中等待双方大厅和世界完成确认，去掉冲突的 --mp-immidiate-start；没有改变生产开局代码。截图发现的实验室红字覆盖叙事和旧死亡界面问题均已修复，最终场景重测通过。

额外旧主线回归 `r14-biopod-regression`：真实 layer9 蓝心位置/动画/唯一 cargo、FreshItemDrop 寿命与重力、核心操作钻探仓、两端 layer10 底部出生、非核心顶层不触发、两端 breach 渐变、实验室出生点、提前到出口不能跳过对话，均有通过断言。**本轮不算完整 Biopod 回归通过**：原测试进程61844/64480在科学家对话开始后退出，日志停在19:37:29/34；随后19:37:38/43出现无测试参数的新A/B进程，插件 DLL 也换成其他 hash。脚本最终记录超时，未杀掉不属于本次启动的新进程，也未覆盖运行中的A/B DLL。当前A/B版本不作为本轮成果，以交付包和上面的 hash 为准。科学家自然阅读结束后的旧主线出口三项门槛仍需补验；新增Escape场景的对话完成门槛已自动通过。

结束前对当前源码再构建 Ship/Playtest，仍分别0 error/104 warning、0 error/100 warning（release14-source-check-*.log）。期间有其他操作触碰了历史测试源文件；没有覆盖它们。重新构建的生产 Ship 与交付版逐字节比较，仅 PE 构建时间戳及16字节 MVID 不同，生产内容完全相同；证据见 build/release14-source-equivalence.json。归档7场景继续绑定已测试的 Playtest hash，不混入后来重编译的测试 DLL。

## 仍需人工复测的边界

夹具用生产枪击消息、真实场景 collider、原版 Inject 与 CargoMinigame 拖动/Update 方法推动流程，但没有替代人工键鼠精确操作的验收。初始位置/伤害/时间边界由显式夹具设置，超时测试会缩短等待到边界后再观察真实守卫生成。需要明日确认渐变观感、音乐/音效、人手手术操作、出口物理通行和异机 Steam 网络；未宣称完成 Steam、高延迟/丢包和所有游戏内容回归。

剧情结算采用所有在线玩家确认；未点击阅读的玩家会让全队在剧情末尾等待，掉线玩家不持有屏障。正常局遵守原版点击阅读，没有自动跳过；自动点击仅存在于显式 TESTING 夹具。
