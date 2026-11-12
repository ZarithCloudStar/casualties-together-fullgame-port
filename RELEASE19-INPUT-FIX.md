# Release19 输入授权补修

版本仍为 `5.5.0-cu-port`。全部修改及运行产物位于本仓库；游戏只启动 F:\cu-port 下的 A/B 副本，未修改游戏安装目录的插件或 `D:\steam`。

## 用户报告与可确认的根因

用户 `build/ending-tests/r19-verify-r19-noncore-client/client.log`：09:35:24.317 显示 escapenote1，09:35:29.490 出现一条未标来源的 `[narrative] click`，随后无人输入 6 秒断言失败。

首版 `EndingFlow.Narrative.cs:23` 的 `AcceptNarrativeClick` 仅检查“是否同帧消费过”和 `toClose`，**没有检查本次调用是否获得新输入沿的授权**。因此任何 `Skip()` 调用都能被误记为 click。原生按钮的 `UnityEvent.Invoke`、Submit、延迟 pointer 回调、直接调用 Skip 均可进入该路径。同帧去重无法阻止跨帧的迟到回调。

此外，旧 TESTING 代码分别覆盖 `AttackPressed` / `TextAttackPressed` 的最终布尔结果，使测试绕过了生产输入采样。本次删除这两个 Harmony 补丁，也删除 `EndingFlowTests.Tick` 的直接周期 Skip；保留显式 `--mp-auto-narrative` 的旧回归用途时，改为经过同一输入采样和授权状态机。

**历史来源的证据边界：**旧失败日志没有调用栈、输入状态或来源，无法事后唯一判定 09:35:29.490 是物理输入、Submit、迟到 pointer 回调还是其它 Skip 调用。加诊断后的 `release19-input-trace-r19-noncore-client` 复测未自然重现那一次提前推进，约 129 秒完整通过。不能把推测写成已定位的历史调用者。

诊断已验证旧路径可在 `mouse0=False/False/False`、`attack=False/False/False` 下接受 `UnityEvent.Invoke -> Release19EndingTests.Click -> ScrollableText.Skip`；这些是**明确标识的夹具调用**，不是冒充用户历史失败的调用栈。最终回归另行重放无输入的 `Button.onClick`、直接 `Skip()`、`Button.OnSubmit`、延迟 `OnPointerClick`，要求全部拒绝；未知历史调用者只要没有新输入沿，也无法再推进。

后续独立启动的 `build/ending-tests/r19b-175131-r19-noncore-host/host.log` 提供了新的明确证据：09:53:07.995 / frame=4409 原始 Unity 采样是 `mouse0=True/True/False`、`attack=True/True/False`，样本没有 Fixture 标记，随后 `source=physical-mouse-down accepted=True`，09:53:08.506 起又出现多个同类按下沿。这次 `revealed page waits for click` 失败确实有 Unity 鼠标新按下输入，不能视作零输入的自动 Skip。这里 physical 标签表示来自 Unity Input 的原始采样，不能据此区分硬件鼠标与其它进程注入的 OS 输入；也不能用这次证据反推 09:35 旧失败的唯一调用者。

## 最终实现与诊断

- `ScavPrototypeMP/Compatibility/EndingFlow.Narrative.cs:65`：统一采样本地 mouse0/攻击键的 held/down/up 和窗口焦点；生产代码只读 Unity Input。
- `:75`：文本和 CG 共用按下沿状态机。进入新面板/CG、失焦后重新等待释放；仅窗口有焦点、已观察到释放、随后出现新的 down 才消费。保持和松开都不再推进，同帧最多消费一次。
- `:49`：Skip 必须持有本输入循环授予的“当前文本对象 + 当前帧 + 尚未消费”授权；授权只在 `NarrativeTextInput` 调用原生 Skip 的同步范围内存在，finally 立即撤销。按钮回调、Submit、迟到 pointer-up、直接 Skip 没有授权，一律记录 `accepted=False`。
- `:30` / `:37` / `:52` / `:80`：记录来源、frame、focus、attack/mouse held/down/up、输入模块、pointer id/button/eligible/press/clickTime，以及 Skip/UI 调用栈。`physical-*` 与 `fixture-*` 明确区分。
- `Release19EndingTests.cs:106` / `:248`：TESTING 只在显式排队期间注入完整的 neutral/down/held/up 样本；不再覆盖“接受输入了吗”的最终布尔结果。无排队时返回真实采样；人工准备场景 `r18-ready` 不启用本夹具。
- `EndingFlow.Escape.cs:118`：真正出口仍扫描任意存活玩家。`exitWalker` 仅作记录；共享门落下后按原版 `y>270 && x<370` 开始 Narrative，保留 `[escape] exit reached by=... pos=...`。

原版 ScrollableText 的逐字显示、分页和 OnFade 保持原有语义：一次确认补全当前页，下一次确认翻页，完整末页再次确认才关闭。cover 只是背景；textExists 表示对象存在；onLastPanel 仅是末页索引，不能当完成标记。

## 实测方式

每个场景运行真实 A/B 游戏进程、真实生成 layer11、生产枪械 RPC、分帧麻醉注射和七步骤手术；位置由夹具控制。

首段文字静置 6 秒，中途重放四类没有输入沿的回调，页索引和 toClose 必须不变。每页分别验证攻击键补全和鼠标按下翻页；按住期间再重放按钮回调，跨帧松开后页索引也不能二次变化。完整末页继续静置 6 秒。CG 超过 3 秒最低展示期后仍额外静置到 6 秒，再由新按下沿进入下一段。

主机先读完、客机保持未读文本；服务端仅 count=1 时再等 6 秒且没有结算；客机读完达 count=2 后双方必须显示结算并写对应进度。Merc 也要求末页点击确认，不能由旧的 5 秒计时强关。

工具仍拒绝 Computer Use 游戏窗口控制；本轮输入是 TESTING 专用的完整 Unity 输入样本和无授权 UI 事件重放，**不是桌面鼠标/键盘手点**。生产输入采样、释放/按下沿状态机、Skip 授权、叙事协程和服务端全员结算判定均在真实游戏进程中执行；没有由夹具直接设置剧情阶段或提交 NarrativeComplete。

测试窗口限定 800×600 / 60 FPS。之前的慢帧超时与被终止的场景记录均保留，不计作最终通过。

`Test-Release19.ps1` 增加仓库内独占运行锁，并在开始时复制一份固定 DLL，五个场景均从该快照启动。防止另起同脚本回归重叠，以及测试中途重新构建造成各场景 DLL 不一致。锁不会屏蔽真实 Unity 输入，无排队期间的外部输入仍如实进入判定和日志。

人工场景后来多次在自动回归进行时另行启动，原 A/B 测试进程也被结束。最终启动器把原 A/B 的游戏文件复制到本轮仓库 runtime，使用 `CT_EndingTest_A.exe` / `CT_EndingTest_B.exe` 及对应 `_Data`，独立于人工 `CasualtiesUnknown.exe` 进程。`Plugin.cs:32` 仅在 FULLGAME && TESTING 下允许这两个进程名；Ship 仍仅支持原进程名。复制后的 Assembly-CSharp.dll 与原游戏 SHA256 一致；运行加载同一原版场景与生产网络代码。初次私有进程因未放开 BepInProcess 而未加载 mod，已停止，不计为通过。

随后发现生产文件在 17:59:00 被并行改写：`Ending_NarrativeClickPatch.Prefix()` 变为无条件 `true`，`Ending_NarrativeAttackPatch` 被删除。这使授权函数及输入循环不再接入原生方法，导致 `release19-private-acceptance` 的回调拒绝断言失败，且日志没有 Skip 授权跟踪。该文件完整保存在 `build/release19-concurrent-native-Narrative.cs`，然后恢复两处 Hook，重建并重跑。恢复后的 Narrative 源码 SHA256 为 `9C1E76C9013CDB94A5AF33C24164D67AC675F9E3A3FAAF26138A9702705AC941`。最终脚本保存六个相关 C# 文件的 `source-sha256.json` 并在整轮测试期间校验，任何并行改写都会报告失败，不能把旧 DLL 测试结果算到新源码上。

无人值守运行若出现 `edge source=physical-mouse-down` 或 `physical-attack-down`，启动器会明确报告 `unexpected Unity input during unattended narrative`，避免把已有外部输入的运行称作“零输入”。最终验收还独立扫描五组原始日志，要求原始输入沿为零、无授权 Skip 接受数为零。

夹具调整过程也保留了 `release19-input-fixed` 和 `release19-input-final` 两次失败记录：第一处把“排队时页未完整”误当成“实际按下时仍未完整”，短页在两帧间补字完成后，原版正确翻页却触发旧断言；第二处使用固定 75ms 等待，在一次慢帧中断言先于实际 down（日志断言 09:46:46.820，实际 down 09:46:46.859）。最终夹具记录实际 down 帧的页状态，并按样本帧序列完成后断言，不放宽“没有输入沿不得推进”和“一次沿最多一步”。这两次失败不计入最终验收通过数。

## 构建与最终结果

ShipFullgame：0 error / 104 warning，`build/release19-ship.log`。
PlaytestFullgame：0 error / 100 warning，`build/release19-playtest.log`。

最终运行结果、摘要日志及 DLL 哈希在全场景完成后补入。
