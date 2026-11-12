# 普通水晶颜色同步修复

2026-11-12，1.0.1 / 5.5.0-cu-port。

根因：原生 `CrystalBehaviour.SetUpEffects` 对无效果水晶销毁自身组件，保留 GameObject、SpriteRenderer 和 Light2D。旧 `CrystalSync.Update` 在组件消失后立即返回，发送/请求/查找也依赖这个组件，导致普通水晶一直保留客机 prefab 的白色。带效果水晶保留组件，所以能同步。

本轮生产源码只改 `ScavPrototypeMP/Compatibility/CrystalSync.cs`：

- 外观同步持有独立 renderer/light 引用，SetUpEffects 完成后缓存尺寸和初始化标记；不阻止原生移除无效果组件。
- 发送、恢复请求和接收通过持续存在的 CrystalSync / 注册对象 id + version 查找，不再要求 CrystalBehaviour 存活。
- 组件已移除的普通水晶发送空效果列表及真实外观。客机照常保持零效果，不随机生成效果。
- 同步 sprite 颜色、光照颜色和亮度。静态普通水晶仅在首次/变化时广播，新副本通过现有恢复请求获取快照，避免整层普通水晶每两秒反复广播。
- 有效果水晶保留原有状态/表现更新流程。

ShipFullgame、PlaytestFullgame 均使用仓库既有 Roslyn fallback 构建，0 error，errors.log 均 0 字节。

| 产物 | SHA256 |
| --- | --- |
| `build/bugfix-ShipFullgame/CasualtiesMP.dll` | `A6FE82CC36531ED711EA7114F4DD9CFA96C592E1A2B83615994074DFFB97EB37` |
| `build/bugfix-PlaytestFullgame/CasualtiesMP.dll` | `D208E2D1A440671A9C3FD897E925986B3EBAA35DA9AE4CADCEC6C14C6C6D6AC0` |

双实例日志在 `build/comment-bug-tests/crystal-colors-manual-20261112-210429/`（目录亦记录于 `build/comment-bug-tests/last-run.txt`）。两端使用同一新版 Ship DLL。夹具生成真实普通水晶、调用原生 SetUpEffects，通过小于 .4 的 crystalSize 走原生必定无效果分支；另放一颗 CrystalElectric 对照。不是客户端直接涂色。

| 对象 | 主机状态 | 双方一致的 RGBA / 亮度 |
| --- | --- | --- |
| 紫色普通水晶 id 42，出生点左侧约 4 格 | `cb=False`（原生已移除组件） | `(0.9550,0,0.9978,1)` / `.6943` |
| 青色普通水晶 id 43，出生点右侧约 4 格 | `cb=False` | `(0,0.9618,0.9448,1)` / `.4739` |
| 带效果水晶 id 41，右侧约 10 格 | `cb=True, CrystalElectric` | `(0.9730,0.9016,0.0153,1)` / `.5163` |

两端 `[appearance]` 日志中 sprite/light 颜色、亮度逐项一致，无 patch/IL 错误。夹具 `spawnplain` 在原生生成完成后的可选刚体设置阶段产生了两条 NullReference 命令错误（该 prefab 没有 Rigidbody2D）；水晶随后由真实 registry 自动发现，观察到主机组件已移除且两端颜色一致。夹具源码现已补空判断，当前窗口保留给用户测试，没有为重跑夹具而重启。未做无关全量回归；自然水晶层的大量生成和主观观感由用户继续测试。

按用户要求通过 `Start-ColorPair.ps1` 以 Normal 窗口模式启动并保持运行：HOST 主机 PID 76360、FRIEND 客机 PID 62964，127.0.0.1:8003。窗口为 800×600，可移动并排比较。未修改 D:\steam 或用户游戏副本；测试 loader、存档和日志均在仓库隔离目录中。
