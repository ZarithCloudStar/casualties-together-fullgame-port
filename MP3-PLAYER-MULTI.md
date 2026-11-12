# MP3 播放器多来源自定义音源

适用仓库版本：`5.5.0-cu-port`，正式版 Unity/Harmony 移植。

## 玩家怎么用

1. 每人在自己的 `Application.dataPath/custommusic` 放入音频，通常是 `CasualtiesUnknown_Data/custommusic`。支持 `.mp3`、`.wav`、`.ogg`；实际是否能解码由 UnityWebRequest 决定。进入世界后建立清单；本局新增、删除或替换文件后，可重新进入世界，或由主机关掉再打开共享规则重新扫描。
2. 主机在模组“规则”面板、“同步 MP3 播放器”旁，打开 **允许主客机同步 MP3 自定义音源**。它默认关闭，且必须同时打开“同步 MP3 播放器”。客机无法修改此项，包括远程管理员。
3. 使用 MP3 物品打开原来的歌曲菜单。开启共享时，原面板右侧出现黑底白框、沿用下拉框字体的“音乐来源”玩家栏。主机总在第一项；其他在线玩家显示姓名及自定义曲目数量，0 首的客机项置灰。玩家过多时可滚动。
4. 点“主机”：左侧仍是原版的 **内置曲目 + 主机自定义曲目**。点 B：只显示 B 的自定义曲目；点 C：只显示 C 的自定义曲目。客机条目带短哈希后缀，同名文件可以分辨。
5. 展开列表，左键单击只选中，列表保持展开；**右键**直接播放；同一项两次左键间隔 **小于 0.35 秒**时直接播放。原播放按钮也保留，并使用当前来源。点击外部可收起下拉框。列表展开时，点玩家栏也可以直接切换来源。
6. “停止音乐”停止当前 MP3 物品的播放。关掉菜单只清理菜单 UI，音乐继续从物品位置播放。

示例：A 是主机，A 有 A1/A2/A3，B 有 B1/B2，C 有 C1/C2。三端都能看到 A、B、C 来源。主机栏含内置曲目和 A1/A2/A3；B 栏只含 B1/B2；C 栏只含 C1/C2。任意端可请求任意来源，但操作者需要够得到 MP3 物品，作为音源的客机也必须在该物品 82 范围内。

**音乐文件不会分发、下载或写入其他玩家的 custommusic。** 客机负责读取自己的文件，将播放中的声音实时传给主机，再传给附近玩家。音源客机离开物品 82 范围或掉线后，这首歌会停止；回来后需重新选择播放。普通听众离开范围只停止自己的接收与输出，返回范围可继续听到正在播放的片段。

## 规则、权限与兼容行为

| EnableMP3Sync | AllowClientMP3Sources | 实际行为 |
|---|---|---|
| false | 任意 | 原版单机菜单及本机内置/自定义曲目，走原版 MusicManager；不使用多人音源 |
| true | false（默认） | 现有多人行为：只显示主机内置/自定义曲目、原下拉框和播放按钮，主机服务端音源；不扫描/上报客机清单，不创建玩家栏或快捷播放事件 |
| true | true | 玩家来源栏、客机清单、右键/双击播放及客机音源流全部启用；主机来源仍走原服务端音源 |

字段为 `MultiplayerGameRules.AllowClientMP3Sources = false`，紧邻 `EnableMP3Sync`。规则列表反射公开字段，因此无需另建一份 UI 注册表。名称与描述使用：

- `casualtiestogether_rule_name_AllowClientMP3Sources`
- `casualtiestogether_rule_desc_AllowClientMP3Sources`

控制台 `currentrules`、`rule` 的补全及规则预设序列化也按字段反射自动纳入。主机可用 `rule AllowClientMP3Sources true/false` 修改。UI 对客机将该项设为只读；服务端控制台处理同时拒绝远程客机修改。远程管理员重置其他规则时保留该字段，客机切换本地预设时也不能覆盖它。

`SavesystemPatch` 已将整个规则结构写入 `mp_rules.json` 的 `RULES` 对象，并用 `ToObject<MultiplayerGameRules>()` 恢复，因此新字段自动保存；旧存档缺字段时保持 false。没有写入、修改任何实际玩家存档或预设文件。

联机中关闭新规则：立即撤销后台扫描结果、清空共享清单、取消客机加载/编码、清空客机音频输出，并把已打开的菜单切回主机列表及原操作。已有主机曲目和主机服务端音源保留。关闭 `EnableMP3Sync` 时停止多人音源，已打开菜单重新建立单机列表。重新开启组合规则会重新扫描，并为已打开的菜单添加玩家栏。

新增规则改变了原有 memcpy 规则结构，并新增协议语义；**本功能要求房间内所有端安装同一份新构建**，不作为与旧版二进制混用的兼容承诺。

## 实现设计

### 清单

- 世界和本地玩家就绪后才扫描；只有两项规则同时开启才扫描或上报。
- 扫描顶层 custommusic，不扫描内置 Resources。文件读取、SHA-256 在后台任务运行，不把文件数据放进网络清单。
- 元数据为文件名、字节大小、内容 SHA-256；`trackId = SHA256(UTF8(filename + NUL + size + NUL + contentHash))`。以 trackId 去重，来源由真实连接 clientId 决定，客机不能声明别人的 owner。
- 主机保存全部在线来源，广播时按 owner 分包。每包附在线 owner 名单，收件方移除离线来源；每个来源分别携带名称及完整曲目元数据，避免一个大房间形成超大单包。
- 约每秒检查玩家名单和名称变化；新玩家上报后广播新清单。主机原曲目列表继续使用既有加载器和列表协议，`MP3Menu.dropdownList` 始终保持主机列表，玩家栏切换只改实例 dropdown.options。
- 每端元数据上限 256 首、文件名上限 240 字符。按文件名排序后取前 256 首可读、非空、支持的文件，跳过重解析点文件；上报计数是扫描到的候选文件数量，解码失败在播放时提示。相同内容但不同文件名保留为不同条目；相同 trackId 的重复上报条目合并。

### 播放与音频

1. 服务端校验请求者已注册、清醒、可交互到目标 MP3 物品，且物品状态有效。
2. 主机来源使用原 clips 索引，仍由 `MP3PlayerServerAudioStreamer` 播放、重采样并 Opus 编码。
3. 客机来源先校验 owner 在线、trackId 属于该来源，owner 在物品 82 范围内，再发送 `CLIENT_MP3PlayLocal`。
4. owner 按本地文件名解析路径，重新校验大小和 SHA-256，用 UnityWebRequest 加载。读取已解码 AudioClip PCM，混合声道为单声道，连续线性重采样至 48 kHz，按 40 ms / 1920 样本编码。分帧读取，避免额外复制整首 PCM。
5. owner 的本地输出直接使用 PCM，经过现有空间音效和 MP3 音量设置，不等待服务器回传。编码不依赖 AudioListener 音量，也不采集麦克风。
6. 服务端只接收被授权 owner、物品和会话的帧，检查帧大小、频率、序号与发送频率。按 **服务端物品当前世界坐标** 选择 82 范围内玩家，排除 owner，并复用 `CLIENT_StreamedAudioToClients`。主机回环接收已显式开放，所以主机也能听客机音源。
7. 每次切歌/停歌使用递增 token，迟到的旧会话帧不会恢复旧歌。帧携带 clientSource 标记，关闭共享规则后会拒绝仍在途中的客机帧；主机帧继续有效。

主机音源的原音频读取与编码路径保留，包括原先依赖主机音频输出/主音量的限制；新增互斥保护避免清理、切歌与音频线程同时修改 PCM 缓冲，读取器在任何退出分支都会静音自身。客机音源采用独立 PCM 读取路径，不受该限制。

### 清理与失败

- 停歌、切歌：取消上一个 owner 的加载与编码，停止并清空输出/缓存，拒绝旧 token。
- 客机音源加载失败、文件被删改、GetData 或编码失败：本地显示错误，向服务端报告失败，服务端通知请求者并停止该会话，不主动断线。
- owner 离开 82 范围、掉线、物品销毁/失效、切层、离开世界、关闭对应规则：停止/取消并清理组件、AudioClip、输出 AudioSource、滤波器和 Opus 资源。owner 开始加载限时 30 秒，开始送帧后超过 3 秒无有效帧会结束；故障清理不无限等待。
- 听众出范围：立刻静音并销毁本地输出；服务端不再向其发新帧。无帧输出约 3 秒后也会自行清理。
- 正常客机歌曲结束保留短暂尾音排空时间再结束会话。
- 菜单销毁会清理玩家栏；下拉框临时行事件跟随 TMP 生成的行销毁。没有向其他物品或菜单注册全局快捷播放行为。

## 协议表

所有 ID 保留原编号；新消息别名使用原保留字节 `___15`～`___21`。`knetid` 是 ushort；字符串和 byte[] 使用现有 LiteNetLib 序列化。以下不包含 Net.CreateWriter 自动写入的消息 ID。

| ID | 消息 | 方向 / 交付 | 主要字段与用途 |
|---|---|---|---|
| 179 | SERVER_MP3RequestMusicList | 客机→服务端 / ReliableOrdered | int 占位值；请求原主机列表；共享开启时也触发来源汇总 |
| 180 | SERVER_MP3PlayCustomMusic | 客机→服务端 / ReliableOrdered | int 主机索引、itemSyncId；原菜单播放路径，服务端转入统一校验与主机音源 |
| 181 | CLIENT_StreamedAudioToClients | 服务端→范围内听众 / Unreliable | itemSyncId、byte 频率索引、length-prefixed Opus bytes；末尾增加 uint token、bool clientSource；客机音源排除 owner，允许主机回环 |
| 182 | CLIENT_MP3MenuServerIsSendingMusicList | 服务端→客机 / ReliableOrdered | 保留压缩列表：int count、逐项字符串；内置 + 主机自定义 |
| 240 / ___15 | SERVER_MP3Library | 客机→服务端 / ReliableOrdered | int count；逐项 filename、long size、SHA-256、trackId；仅组合规则开启时接收 |
| 241 / ___16 | CLIENT_MP3Libraries | 服务端→客机 / ReliableOrdered | int 在线 owner 数、ownerId 列表；本包 ownerId、ownerName、track count、逐项元数据；每来源一包 |
| 242 / ___17 | SERVER_MP3PlayTrack | 任意操作者→服务端 / ReliableOrdered | itemSyncId、ownerId、trackId、int hostIndex；owner=0 使用主机索引，客机使用 trackId；owner=0/index=-1/空 trackId 表示停止 |
| 243 / ___18 | CLIENT_MP3PlayLocal | 服务端→指定 owner / ReliableOrdered | itemSyncId、uint token、单条曲目元数据；授权 owner 本地加载和编码 |
| 244 / ___19 | SERVER_MP3AudioFrame | owner→服务端 / Unreliable | itemSyncId、uint token、uint sequence、byte 频率索引、length-prefixed Opus bytes；单帧最多 1024 字节 |
| 245 / ___20 | CLIENT_MP3Stop | 服务端→客机 / ReliableOrdered | itemSyncId、uint token、bool active、bool error、bool clientSource；开始新会话时清除旧输出、停止时撤销；error=true 时显示本地化失败提示 |
| 246 / ___21 | SERVER_MP3SourceEnded | owner→服务端 / ReliableOrdered | itemSyncId、uint token、bool failed；自然结束/离开/失败上报，只接受当前 owner 与 token |

ownerId=0 固定代表主机。主机本地播放请求通过现有 Net 回环进入相同服务端校验。上报和播放路由没有传输文件字节。

## 改动文件

| 文件 | 内容 |
|---|---|
| `ScavPrototypeMP/Content/MP3MultiLibrary.cs`（新增） | 元数据扫描/校验、汇总、来源协议、播放路由、会话、帧转发、世界/规则生命周期 |
| `ScavPrototypeMP/Content/MP3MultiMenu.cs`（新增） | 玩家栏、来源切换、右键/双击、TMP 局部展开行为、动态 UI 清理与回退 |
| `ScavPrototypeMP/Content/MP3PlayerClientAudioStreamer.cs`（新增） | owner 校验/加载、PCM 重采样、Opus 编码、本地输出及资源清理 |
| `ScavPrototypeMP/Content/MP3PlayerMPPatches.cs` | 保留主机列表与音源路径，接入快捷播放路由、主机回环、会话后缀、范围与输出清理、PCM 互斥保护 |
| `ScavPrototypeMP/Net/MSG_ID.cs` | 7 个保留字节消息别名，不重排既有 ID |
| `ScavPrototypeMP/ScavMultiplayer.cs` | 新规则公开字段，默认 false，放在 EnableMP3Sync 后 |
| `ScavPrototypeMP/Content/ConsoleCommands.cs` | 新规则主机权限校验，远程重置保留字段，重置时应用规则 |
| `ScavPrototypeMP/UI/UIMainMenu.cs` | 新规则客机只读，客机本地预设不覆盖主机选择 |
| `ScavPrototypeMP/assets/locale/EN.json` | 规则名称/说明、来源 UI、加载/错误/权限提示 |
| `ScavPrototypeMP/assets/locale/zh-CN.json` | 对应中文本地化 |
| `MP3-PLAYER-MULTI.md`（本文） | 设计、玩家操作、协议、构建与待实机验证项 |

## 编译交付

只执行 dotnet build，没有执行测试套件、`Test-Release*.ps1`、游戏或多人实机验证。所有源代码和产物均写入本仓库，未修改 `D:/steam`。为避免项目 PostBuild 默认把 DLL 复制到仓库外的游戏目录，构建命令额外指定仓库内 `ModPluginOutputDir`：

```powershell
$env:NUGET_PACKAGES="F:\cu-port\.nuget"
E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c ShipFullgame -p:SolutionDir=F:\cu-port\src\casualties-together-main\ -p:ModPluginOutputDir=F:\cu-port\src\casualties-together-main\build\mp3-multi\ShipFullgame
E:\tools\dotnet\dotnet.exe build ScavPrototypeMP\CasualtiesTogether.csproj -c PlaytestFullgame -p:SolutionDir=F:\cu-port\src\casualties-together-main\ -p:ModPluginOutputDir=F:\cu-port\src\casualties-together-main\build\mp3-multi\PlaytestFullgame
```

最终构建结果：ShipFullgame **0 error**；PlaytestFullgame **0 error**。完整日志见 `build/mp3-ship-build.log`、`build/mp3-playtest-build.log`；保留编译器现有 warning，不宣称零警告。

DLL：`build/mp3-multi/ShipFullgame/CasualtiesMP.dll`、`build/mp3-multi/PlaytestFullgame/CasualtiesMP.dll`，各目录也包含项目 PostBuild 复制的依赖 DLL。

## 需要用户实机验证（本次未运行）

1. **默认与规则权限**：旧存档/新配置下新规则默认关闭，在“同步 MP3 播放器”旁显示正确中英文名称和提示；普通客机和远程管理员均不能切换该项；主机切换、存档重进与预设恢复正确。
2. **关闭规则的原行为**：只有主机内置/自定义列表，原播放按钮工作，没有玩家栏或快捷播放；客机没有上报清单。关闭 EnableMP3Sync 后每端只使用自己的原版单机曲目。
3. **A/B/C 列表来源**：A 放 A1/A2/A3，B 放 B1/B2，C 放 C1/C2；三端依次切 A/B/C，验证内置曲目只出现在主机栏，数量、名字、0 首置灰、晚加入及掉线刷新正确。
4. **UI 交互与布局**：不同分辨率/UI 缩放、长名字、中文和英文、来源滚动；玩家栏位于原面板右侧且不遮挡原 X/播放按钮。单击只选择、同项 <0.35 秒双击播放、右键播放、展开列表时点来源一次就切换；反复开关菜单没有重复玩家栏和残留遮罩。
5. **跨端音频**：让 A 选 B1、B 选 C1、C 选 A1，三端在范围内都能听到，owner 无网络回声。至少用 mp3/wav/ogg、立体声和不同采样率各确认一次；客机系统主音量为 0 时其他人仍可听到该客机音源。
6. **82 范围**：移动听众和 MP3 物品，验证服务端按物品位置转发；范围外听不到，听众返回可接续。移动音源 owner 出范围，验证整首停止且无缓冲残音。
7. **离开与清理**：音源加载时/播放时掉线、物品失效/销毁、停歌、快速换来源换歌、切层、退世界，确认旧音频和加载请求停止，不出现旧 token 歌曲复活。
8. **联机切规则**：客机曲目播放和菜单展开时关闭新规则，所有端立即回到主机列表、玩家栏消失、客机音源停掉；另一个 MP3 正在播主机歌曲时应继续播放。重新开启后重新出现清单与分栏，不需要重连。
9. **文件容错**：上报后删除/改写文件、同名但不同内容、损坏音频、文件名带空格/中文等，失败只显示提示、不导致断线；其他来源仍能播放。

编译成功只能证明两个配置可构建，上述声音、网络和最终 UI 布局效果仍需真实游戏确认。
