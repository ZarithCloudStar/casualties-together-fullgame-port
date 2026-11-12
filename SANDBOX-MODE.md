# 多人沙盒模式

实现基线：5.4.0-cu-port。验证日期：2026-11-10。

## 行为

- 多人菜单中，已开房且尚未进入世界的主机可以看到“沙盒模式”。客户端、未连接时和世界内不显示。
- 点击复用 `PreRunScript.StartTutorial()` → `WorldMP.LoadVanillaTutorialWorld()` → `WaitLoad`。既有角色闸门负责等待所有玩家确认，没有新增角色闸门。
- 闸门拒绝时清除加载标志；后续启动前缀尊重 `__runOriginal`，不会向已入场客户端广播被拒绝的开始请求。
- 确认后通过现有开始公告同步进入教程世界，既有教程 patch 在两端选择 `SandboxCourse`。
- terminal 可以打开控制台；联网教程世界中放行 `Con.CanCheat()`，离开后立即恢复原规则判断，不修改持久化的 `sv_cheats` / `AllowClientCheatCommands`。
- 沙盒中 `EndingFlow.Active` 为 false，“保存当前层退出”隐藏且后台请求也被拒绝。
- `EndingFlow.EnableEscape` 保持 false。

## 改动文件清单

| 文件 | 改动 |
| --- | --- |
| `ScavPrototypeMP/Compatibility/SandboxMode.cs`（新增） | 按钮、主机校验、本地化提示、教程入口、仅限教程世界的控制台权限 patch |
| `ScavPrototypeMP/UI/UIMainMenu.cs` | 在角色选择、保存退出后接入同高度按钮 |
| `ScavPrototypeMP/assets/locale/EN.json` | 四个 sandbox 本地化 key |
| `ScavPrototypeMP/assets/locale/zh-CN.json` | 四个 sandbox 本地化 key |
| `ScavPrototypeMP/Compatibility/EndingFlow.cs` | Active 排除教程世界 |
| `ScavPrototypeMP/Compatibility/SaveCurrentLayerExit.cs` | 教程世界隐藏按钮并拒绝请求 |
| `ScavPrototypeMP/Compatibility/CharacterReadyGate.cs` | 拒绝 WaitLoad 后释放加载标志 |
| `ScavPrototypeMP/SyncSystems/WorldMP.cs` | 提供加载状态及取消待加载标志的接口 |
| `ScavPrototypeMP/Patches/Patches.cs` | 开始公告前缀尊重已有闸门的拦截结果 |
| `ScavPrototypeMP/Compatibility/SandboxModeTests.cs`（新增） | 仅 FULLGAME && TESTING 编译的双实例集成夹具 |
| `scripts/Test-SandboxMode.ps1`（新增） | 双实例测试驱动、隔离存档、日志、停止测试进程并还原 DLL |
| `SANDBOX-MODE.md`（新增） | 本说明与测试记录 |

生成物在 `build/sandbox-ship`、`build/sandbox-playtest`、`build/sandbox-verification`。测试部署只替换 A/B 副本的 `BepInEx/plugins/Together/CasualtiesMP.dll`；没有改动 `D:/steam`。A/B 最终均已装回本次 Ship DLL，测试进程已停止。Steam 连接、中文名、日志降噪、keypad、水晶同步相关实现未改动。

## 构建记录

两个配置均使用用户给定的项目、SolutionDir 和 NUGET_PACKAGES；另指定仓库内 `ModPluginOutputDir` 分别保存出包及测试 DLL，避免测试包覆盖出包。

| 配置 | 结果 | 日志 |
| --- | --- | --- |
| ShipFullgame | 0 error，104 warning | `build/sandbox-verification/ship-build.log` |
| PlaytestFullgame | 0 error，100 warning | `build/sandbox-verification/playtest-build.log` |

中英 locale 均通过 JSON 解析，四个 key 齐全。Ship DLL 不包含 `SandboxModeTests` 类型；Escape 禁用状态在双实例中断言通过。

Ship DLL：`build/sandbox-ship/CasualtiesMP.dll`。

SHA-256：`31ECE3335345066334132E1A0405241BA655FEB8F7BB47B2330609A1C68680AC`。

## 双实例测试记录

最终运行日志：`build/sandbox-verification/final-runtime/host.log`、`client.log`；结果文件 `result.txt` 为 PASS。

测试使用 A=`F:/cu-port/CU_Fullgame`、B=`F:/cu-port/CU_Fullgame_B`，LiteNetLib 地址 `127.0.0.1:7796`，`--mp-nosteam`。集成夹具调用与按钮相同的 `SandboxMode.Start()`，使用真实游戏、Harmony、网络开始公告、角色确认处理函数和 terminal 处理函数；没有模拟网络和场景。

| 项目 | 结果 |
| --- | --- |
| 客端连接但未确认角色，主机第一次请求 | PASS：WaitLoad waiting=1，中文等待提示，不进入世界，加载标志释放 |
| 未确认时主机再次请求 | PASS：仍被拦，无“World is already loading”异常 |
| 被拦请求是否广播开始 | PASS：开始公告数据没有改变；最终日志仅在 waiting=0 后广播开始 |
| 客端调用 Start | PASS：提示“只有主机能开始沙盒模式”，不进入世界 |
| 客端确认角色后主机重试 | PASS：waiting=0，开始加载；加载中再次请求提示不可用 |
| 双方场景及身体 | PASS：两端均为教程世界、SandboxCourse，两端均有两个玩家身体 |
| terminal / 控制台 | PASS：两端都有 TerminalScript，OnUse 打开控制台；两端 Con.CanCheat 及命令权限检查通过 |
| 结局隔离 | PASS：两端 EndingFlow.Active=false，EnableEscape=false |
| 保存退出保护 | PASS：两端请求均被拒绝；按钮隐藏条件已代码检查 |
| 沙盒阶段异常 | 最终运行未出现 Exception 或沙盒错误；启动时两端均有麦克风初始化失败日志 |

主机 [sandbox] 日志及断言：21:59:55（首次拦截）、22:00:00（确认后开始）、22:00:04（完成）。客户端 22:00:07 完成；以上为北京时间，游戏日志内部时间为 UTC。

首次直接运行 Ship 双实例时，受限进程禁止写注册表，`PlayerPrefs.SetString` 在 `Net.TransportCreated` 中失败，无法开房；Discord URI 注册也被拒绝。记录保存在 `build/sandbox-verification/ship-host.log`、`ship-client.log`。因此实际网络集成测试使用 PlaytestFullgame，并启用仓库已有的 EndingTestPreferences 内存偏好设置及隔离存档，跳过结局测试自动流程。没有更改出包的注册表或连接行为来绕过环境限制。

第一轮 Playtest 客户端连接时曾记录一次 `GUILayout: Mismatched LayoutGroup.repaint`，之后仍通过沙盒流程；最终运行没有重现。第一轮日志保存在 `build/sandbox-verification/host.log`、`client.log`。没有修改无关的菜单布局代码。

## 复测

构建 Playtest 测试目录后运行：

```powershell
$env:NUGET_PACKAGES = 'F:\cu-port\.nuget'
E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c PlaytestFullgame -p:SolutionDir=F:\cu-port\src\casualties-together-main\ -p:ModPluginOutputDir=F:\cu-port\src\casualties-together-main\build\sandbox-playtest
.\scripts\Test-SandboxMode.ps1
```

测试脚本要求 A/B 没有运行。它使用独立存档目录、测试内存偏好设置，结束后停止自己启动的进程并恢复 A/B 原 DLL。`--mp-sandbox-test` 夹具只有在同时传 `--mp-ending-test` 并指定 `CT_ENDING_TEST_SAVE` 时启用，不进入 Ship。

## 需要实机及遗留风险

- 桌面自动化只能看到现有 D 盘游戏，无法访问受限环境启动的 A/B 窗口；没有操作该 D 盘实例。因此按钮视觉布局、鼠标点击、客户端按钮不可见，以及物理接近 terminal 后交互仍需用户会话实机补验。自动集成已覆盖相同处理函数，但不能据此宣称完成鼠标/UI 验收。
- Ship 在正常用户权限下的完整双实例流程仍需补验；受限环境只验证其编译与产物隔离，实际网络流程由共享业务代码的 Playtest 完成。
- terminal 控制台采用既有多人命令体系。服务器专用命令（如 give、世界管理）仍要求主机或原有管理员通道；本改动只解除沙盒中的作弊规则限制，没有向客户端授予持久管理员权限。
- 现有多人教程提示仍保留；没有改动教程 patch 的警告文案或对话框。完整教程课程、离开沙盒后的多次往返、Steam 联机、后加入、掉线重连和超过两人的房间尚未实测。
- 麦克风初始化受测试环境影响；第一轮的偶发菜单布局异常也需要留意。最终沙盒加载、场景同步、terminal 和结局隔离未发现异常。
- 本次未重跑 keypad、结局、水晶和 Steam 全套回归；这些功能相关实现除明确列出的结局 Active guard 及共用 WaitLoad 拦截修复外没有修改。
