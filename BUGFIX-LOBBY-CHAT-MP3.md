# 大厅读档、聊天历史与主机 MP3 修复

修改范围仅为本仓库，未向 Steam 或仓库外游戏目录部署。双实例运行使用仓库内已有的私有游戏副本，独立 loader、存档和日志均在 `build/lobby-load-tests`。

## 1. 大厅读档

确定的缺陷：

- Harmony 的高优先级闸门返回 false 后，带副作用的替换前缀仍可能运行。原 StartRun 替换前缀没有检查 `__runOriginal`，会先删除多人存档，再进入世界加载闸门。
- 原 WaitLoad 闸门拒绝后清除菜单加载 latch 和 startedLoading，丢失了一次已经开始的读档事务。
- transport promote 后，Unity Start 正式登记玩家之前存在窗口；原 pending 计数已归零。
- 装备回收显式排除了 ID 0，而主机 ID 正是 0；原生读档中没有成功就位的主机装备无法回收。

修复：替换前缀尊重 `__runOriginal`，在删除存档之前检查加载 latch；读档请求等待 admission 时明确排队，排队期间阻止 StartRun/教程覆盖请求；WaitLoad 等待完成，保留 load/new/tutorial 意图和 latch。promoted 玩家正式进入 roster 后才不再阻挡，不要求大厅提前创建 Body。主机也参与装备回收。延迟恢复期间禁止 Body.Start 抢先恢复旧状态及 WriteOne 写入未恢复的身体；单人身体恢复的世界快照补齐 endlessLoops、isMercRun、loadedRun 的保护。

双实例通过：

- `load-ready-20261112-003736`：新客机通过真实角色面板确认，无世界/无 Body，然后主机 LoadRun；实际入口为 `LoadVanillaGeneratedWorld(load)`，恢复层级 11，主机物品清单精确匹配 shotgun、boxof12gauge、emergencylight；新玩家不存在旧 checkpoint。日志记录主机恢复到保存坐标 `22,-0.38`。
- `load-queued-20261112-003837`：客机未确认时请求读档，检查排队、无世界加载；再次调用 StartRun 检查存档仍在且读档仍在队列。确认后自动以 `(load)` 加载，同样通过层级和装备检查。

## 2. 聊天历史

EnableChatbox 默认 true。规则 UI 通过 FieldInfo 绑定字段，JSON/预设按字段名读取，未发现新增 MP3 规则导致字段位置串位的证据。存档的 RULES、手动/记忆预设以及规则命令都可恢复显式 false；仅凭现有文件无法追溯是哪次操作写入 false。

确定的显示原因是 Chat.OnGUI 直接以 `!EnableChatbox` 排除历史渲染。现将历史显示与输入权限解耦；规则 false 时保留历史/公告，生存中的玩家不能输入，服务端也拒绝其文字发送。大厅和死亡后的原有交流路径继续保留。按聊天键仍可查看历史，并显示恢复提示。

主机控制台恢复命令：`krok rule EnableChatbox true`。若之后重新选择包含 false 的预设，仍会恢复该预设的输入权限。

两次 A/B 场景均在双方通过：false 时历史队列保留且 InputDisabled 为 true；true 时输入恢复。未进行聊天面板截图验收。

## 3. 主机监听客机 MP3

主机的 Net.Server_SendToClients loopback 同步调用 InvokeClientMessage，没有额外一跳网络队列。保留现有 20ms 帧、多来源授权、sequence/token 和 credit 限制。

确定的缓冲缺陷：StreamedAudioOutput.Start 会清掉组件创建后、Start 前已经收到的首批音频；原 100ms 预缓冲按等待时间启动，不能保证已收到足够样本；环形缓冲的清零回绕位置错误使用当前播放位置，可能擦除尚未播放的样本。音乐每帧还分配一块无用的淡出数组。

修复：不在 Start 清首包；MP3 独立使用 300ms 实际样本预缓冲，语音保持原预缓冲策略；按 clip.samples 正确回绕清零；音乐跳过淡出数组及额外 ToArray；延长发送结束的 drain 时间。控制消息清理旧主机 monitor 和解码器。主机日志每 10 秒报告帧数、最大间隔、缓冲量与欠载次数。

两次真实 UDP A/B 测试：客机以相同编码器发送 48kHz/960 样本、440Hz 测试帧，持续 10 秒，每次 500 帧；经真实 RelayFrame、credit/sequence 校验和主机 monitor 解码后，主机均收到 **500/500**。最大帧间隔分别 **61ms / 77ms**，第 500 帧时缓冲分别约 **251ms / 229ms**，播放期间欠载为 **0**。测试流结束后自然耗尽缓冲，最终计数均为 1，不能把这次尾部停止算作播放中丢帧。未进行真实音乐的人工听感验收。

## 编译与复现

当前 dotnet 安装只有运行时，缺少 SDK；SDK 下载也被环境网络限制阻止。因此没有宣称标准 `dotnet build`/MSBuild 成功。

使用 PowerShell 自带 Roslyn、缓存 publicized Assembly-CSharp、现有 Unity/BepInEx 引用、生成的 assembly/plugin 信息及全部资源，按两套条件编译全部源码：

- PlaytestFullgame：0 error，输出 `build/bugfix-PlaytestFullgame/CasualtiesMP.dll`。
- ShipFullgame：0 error，输出 `build/bugfix-ShipFullgame/CasualtiesMP.dll`。

这是 SDK 缺失时的编译/实机验证替代，引用集使用现有游戏运行时，不能代替恢复 SDK 后的正式 MSBuild 打包验证。

```powershell
./scripts/Build-OfflineRoslyn.ps1 -Configuration PlaytestFullgame
./scripts/Build-OfflineRoslyn.ps1 -Configuration ShipFullgame
./scripts/Test-LobbyLoad.ps1 -Scenario load-ready -Port 7851
./scripts/Test-LobbyLoad.ps1 -Scenario load-queued -Port 7852
```

测试脚本依赖仓库内现有的 release18 存档夹具及 release19 私有 A/B 副本。测试类仅编入 TESTING 构建，正式版不包含测试角色/音源入口。首次运行发现音频夹具资源名错误，修正夹具后重跑 ready 场景通过；该初次失败不是产品修复通过记录。

还需验收：恢复 SDK 后正式构建两套配置；用户自己的中途存档（地形/容器/身体健康/嵌套物品完整状态）；实际 MP3 文件、多音源切换及繁忙大地图下长时间听感；聊天面板视觉检查。本次没有扩跑结局、手术、水晶等全量回归。
