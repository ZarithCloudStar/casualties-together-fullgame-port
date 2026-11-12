# Release19：任意玩家逃生、手动推进剧情

> 本文件为首轮开发记录。后续用户回归发现了输入授权缺口；最终输入实现、诊断范围及验收结果以 `RELEASE19-INPUT-FIX.md` 为准。下文所述“由原生按钮推进”和旧测试输入覆盖方式已被替换，首轮结果不能充当最终全场景验收。

版本保持 `5.5.0-cu-port`。修改、构建产物、独立加载器、存档和日志均在本仓库。
游戏仅启动 `F:\cu-port\CU_Fullgame` / `CU_Fullgame_B`；不改两份游戏的已安装 mod，也不访问或修改 `D:\steam`。

## 根因与原版语义

1. release18 `ScavPrototypeMP/Compatibility/EndingFlow.Escape.cs:98` 优先查找仍活着的 `State.exitWalker`，只有查找失败才扫描其他玩家。核心玩家停在途中时，另一个玩家到达出口不会参与最终判定。修改后的对应入口在 `EndingFlow.Escape.cs:100`，真正出口扫描在 `:118`。
2. release18 `Compatibility/EndingFlowTests.cs:20` 把 `AutoNarrative` 直接等同于 `EndingTestPreferences.Enabled`（即 `--mp-ending-test`）；`:28` 每 0.3 秒自动调用 `Skip()`。同时 `EndingFlow.Narrative.cs:70` 在 TESTING 下无条件返回这个自动推进判定，绕过 CG 的实际攻击键。`scripts/Start-EscapeTest.ps1:21` 调用的启动器恰好会带 `--mp-ending-test`，所以准备好的人工测试场景也会自动读完。此根因作用于带测试启动参数的 Playtest；Ship 不含这些测试类型。

先核对了只读反编译文件 `F:\cu-port\decomp\fullgame\ScrollableText.cs`：

- `:42`：`textExists` 表示 Unity 文本对象仍存在，不表示文字仍在打字；原版使用 Unity 对象的销毁/null 语义。
- `:44`：`onLastPanel` 只是 `posIndex == positions.Count - 1`，刚进入末页就为 true，末页可能尚未显示完，更没有确认读完。
- `:58` / `:64`：`cover:true` 仅开启 prefab 第 0 子节点背景，不会开启自动播放或关闭。
- `:95`：`Skip()` 第一次补全当前页；当前页已完整时才翻页；完整末页再点击才关闭。
- `:101`：`Instant` 在完整末页确认后销毁对象；`OnFade` 在确认后置 `toClose=true`、启动淡黑，`:156` 在淡黑到顶后销毁。没有单页自动关闭逻辑。
- `:126` 起：Update 只自动逐字显示并显示“点击继续”；不会自动增加页索引。剧情中 `cutsceneActive=true`，暂停键也不能走普通文本的 ForceClose 分支。
- 真实游戏 prefab 的序列化事件由本轮夹具检查，绑定是 `Button.onClick -> ScrollableText.Skip`，并实际调用该事件验证翻页。
- 原版 `EndLabScript.cs:234` / `:237` / `:244` 依次为 `x>353` 强制行走、`x>366` 坠落开门、`y>270 && x<370` 真正出口。最后一项没有额外的 x 下界。
- 原版 `WorldGeneration.cs:1377` / `:1384` / `:1385` / `:1389`：第一段文本关闭后显示 CG，最少等待 3 秒，再等待攻击键，之后才显示第二段文本。

## 改动

- `EndingFlow.Escape.cs:100`：所有存活玩家参与强制行走及坠落判断；`:118`：共享门已落下后，任意存活玩家满足原版真正出口阈值就开始 Narrative。`exitWalker` 只记录表现/日志对象；到达时写 `[escape] exit reached by=... pos=...`。本地强制行走/坠落也不再按 `exitWalker` 身份筛选。
- `EndingFlowTests.cs:22` / `:30`：自动推进必须同时显式传 `--mp-auto-narrative`。场景准备参数 `--mp-ending-test` 本身不再推进剧情；人工准备脚本保持手动阅读。旧的无人值守回归脚本显式传 `-AutoNarrative`，保持原有回归用途。
- `EndingFlow.Narrative.cs:23`：保留原生 Skip 行为，并阻止同帧重复消费输入。`:33`：鼠标左键交给原生 Button 的 pointer-up；其他攻击键在按下沿补充支持，避免一个左键按下/松开消耗两页。`:100`：CG 读取真实左键/攻击键；`:103` 先等旧按键释放再等新按下沿。生产叙事代码中不再引用测试自动推进逻辑。
- `EndingFlow.Narrative.cs:136`：按“每个文字面板均需确认”的要求，也去掉共享 Merc 叙事末页 5 秒强制关闭分支；原版爆炸声/黑屏效果在确认读完后继续，不能抢走未读完的末页。
- 每个实例独立保留文字对象、输入和叙事阶段；`NarrativeComplete` 仍仅上报本地读完。服务端继续等待所有已连接玩家的完成标记。

## 实测方法与边界

`scripts/Test-Release19.ps1` 启动真正的 A/B 游戏进程、独立端口和仓库内的独立 BepInEx runtime/save。
复用原有生成 layer11、科学家对话、真实枪械 RPC、分帧麻醉注射和七步骤手术夹具；手术完成后固定原核心玩家身份与途中位置，分别让主机/客机、核心/非核心到达出口。

出口位置由夹具设置，验证 `x>366` 只开门、`y<=270` 与 `x>=370` 不触发结局；非核心场景最终使用 `(352,271)`，同时覆盖真正出口没有 x>353 限制。

每个真实文本面板：检查 prefab 绑定，静置并断言页索引不变、末页不关闭；发送一次测试攻击按下沿补全文字，再实际调用 prefab 的 `Button.onClick` 翻页。同帧重复调用也只能推进一步。CG 静置 6 秒，超过原版 3 秒最短展示期后仍应停住，再发送一次测试攻击按下沿。主机先读完，客机停在未读文本；主机继续等待 6 秒，确认仅 1 个 NarrativeComplete、没有提前结算，随后允许客机读完并检查双方结算。

初次 `release19-verified-r19-noncore-client` 已确认出口、文字和 CG，但在最终结算等待时超时，不能算通过：客机引擎帧耗时从毫秒逐渐升到数秒、最后约 20 秒，主机保持 count=1 等待。保留该目录的原始日志。最终夹具只把本测试窗口的渲染限定为 800×600 / 60 FPS，并记录长引擎阶段耗时；未放宽叙事等待、完成标记或最终结算断言。对应反向场景重跑完整通过（约 134 秒，运行时帧率约 59 FPS）。这表明该次运行在控制渲染开销后稳定；没有据此断言慢帧的唯一底层原因。

Computer Use 工具拒绝游戏窗口访问：`Computer Use was not approved to use casualtiesunknown`。因此这里的输入是**游戏内 prefab 点击事件和 TESTING 专用的一帧攻击输入夹具**，不是桌面鼠标/键盘手点实测。攻击输入夹具仅影响本地输入判定，不直接设置叙事阶段、关闭文本或提交结算；Ship 编译不包含这些类型。截图来自游戏内 ScreenCapture，已另行检查实际文字页、CG 和等待客机的黑屏；双方结算通过各自 `endScreen.gameObject.activeSelf` 和结局进度标记断言验证。

可重跑：

```powershell
.\scripts\Test-Release19.ps1 -Run release19-new-run -Port 7850
# 人工场景：默认不会自动推进剧情
.\scripts\Start-EscapeTest.ps1 -Run release19-manual -SkipBuild -Port 7860
```

## 构建

`E:\tools\dotnet\dotnet.exe`，`NUGET_PACKAGES=F:\cu-port\.nuget`；指定仓库 `SolutionDir` 和仓库内 `ModPluginOutputDir`。

| 配置 | error | warning | 日志 |
| --- | --- | --- | --- |
| ShipFullgame | 0 | 104 | `build/release19-ship.log` |
| PlaytestFullgame | 0 | 100 | `build/release19-playtest.log` |

Ship 已检查不含 `EndingFlowTests`、`Release19EndingTests`、`Ending_TestNarrativeAttack` 测试类型和自动推进开关。

最终运行结果与日志摘录在本轮测试结束后补入下节。
