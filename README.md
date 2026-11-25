> **这是修改版分支，不是原项目官方版本。**
>
> 基于 [Casualties: Together](https://github.com/creaturefeaturelarry/casualties-together) 5.4.0 修改，
> 适配《Casualties: Unknown》正式版 1.0.2。
>
> 修改版作者：bilibili 星云Zarith  
> 许可证：LGPL-2.1

当前正式版移植协议：`V1.0.2_MPv5.5.5-cu-port-fix2`。所有玩家须更新至 `5.5.5-cu-port`，与旧版本不兼容。

游戏运行时，日志线程每 5 秒更新 `logs/crash-report-live_<pid>.zip`，包含 session、BepInEx 和 Unity 日志的末尾及说明。崩溃或强杀后直接把留下的 zip 发给作者；正常退出会自动删除。下次启动会将已退出实例的报告改名为 `crash-report_*.zip`，保留最近 5 份。报告最多缺少最后约 5 秒的日志，不包含崩溃转储，转储位置见 zip 内 README.txt。

## 这个分支包含什么

- 同层存档 / 读档（含未探索区域背景与结构）
- 三结局多人触发规则优化
- 旁观相机与输入修复
- 医疗面板 / 假肢状态同步与权威修复
- 水晶同步（普通水晶颜色、重力、不稳定爆炸表现）
- 多人 MP3 音源
- 沙盒模式、共同出生点、联机稳定性修复
- 游戏内容扩展与优化（GCE）配套：GCE 桥接、规则页「按分队在不同位置降落」、通用模组同步
- 入房「模组不同步」可无视并继续 / 同步主机模组 / 断开连接；同步前先弹免责提示，列表默认不勾选，勾选后下载并用 SHA256 校验，动态加载或 `.new` 重启应用

### 游戏内容扩展与优化（GCE）

本分支是 GCE 0.3.9 的配套 Together 构建（5.5.5 同步线 + 破片/设置同步 fix2）。GCE 单独安装也能运行，但没有 Together 时模块 1 置灰；配套时规则页出现 GCE 管理区和「按分队在不同位置降落」，并可从主机同步缺失/版本不同的插件。GCE 仓库：<https://github.com/Cloudawolf/->（`function` 分支）。

## 安装

解压到《Casualties: Unknown》游戏根目录，主机和客机必须使用**同一版本**。

## 构建

1. 安装正式版 1.0.1
2. 运行 `python scripts/prepare_workspace.py`
3. 用 Visual Studio 2022 打开 `CasualtiesTogether.sln` 编译


<div align="center">
<a href="https://www.nexusmods.com/scavprototype/mods/67">
<img alt="Casualties: Together" src=".github/ct6.png">
</a>
</div>

<div align="center">

[![Stars](https://img.shields.io/github/stars/creaturefeaturelarry/casualties-together?style=flat-square&label=Stars&color=orange&logo=github)](https://github.com/creaturefeaturelarry/casualties-together/releases)
[![Latest Release](https://img.shields.io/github/v/release/creaturefeaturelarry/casualties-together?style=flat-square&include_prereleases&logo=github&label=Version)](https://github.com/creaturefeaturelarry/casualties-together/releases/latest)
[![Github Downloads](https://img.shields.io/github/downloads/creaturefeaturelarry/casualties-together/total?style=flat-square&color=blue&logo=github&label=Downloads)](https://github.com/creaturefeaturelarry/casualties-together/releases)
[![Nexus Downloads](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fjimmyking9999999%2FMetadata-generator%2Fmain%2Fbadges%2Fcasualties-together.json&style=flat-square)](https://www.nexusmods.com/scavprototype/mods/67?tab=files)
[![Game Version](https://img.shields.io/badge/Game_Version-v7.0.1-darkgreen?style=flat-square&logo=steam)](https://store.steampowered.com/app/4576510)
</div>




### Online Multiplayer Co-op mod for game ["Casualties: Unknown"](https://store.steampowered.com/app/4576510)<br/>


[![Casualties: Together](https://img.shields.io/badge/Casualties%3A%20Together%20Mod%20Discord-%235865F2.svg?logo=discord&logoColor=white)](https://discord.gg/SA6H7mA6De)   [![Target Planet](https://img.shields.io/badge/Official%20Casualties%3A%20Unknown%20Discord-%235865F2.svg?logo=discord&logoColor=white)](https://discord.com/channels/955738554129063947/1470206122844618802)


## Support developer:

[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://buymeacoffee.com/casualtiestogether)



# Install Guide:

### Requirements:
Download Game: https://store.steampowered.com/app/4576510<br/>
Download BepInEx: https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip<br/>
Download Co-op Mod: [https://www.nexusmods.com/scavprototype/mods/67](https://www.nexusmods.com/scavprototype/mods/67?tab=files)<br/>

<details><summary>

### Click to show full install guide.
</summary>

1. Download all the prerequisistes ^^^^^^ 

2. Go to the game root directory.
    - In Steam, Right click on the game.
    - Click `Manage > Browse local files`


3. Unzip `BepInEx*.zip` into the game root directory.

4. Unzip `MPMod*.zip`, open `mod` folder and move the contents into the game root directory.

5. Now it should look like this 
   
![alt text](.github/tut_1.png)

7. Go back to steam and click **Play**.

8. In main menu, multiplayer related buttons should appear.
    - (To go back to singleplayer you need to press the `Deactivate MP Mod` button in `Settings > General`!)

9. (optional) Upvote/Share/Support the mod.

<details>
<summary>FOR LINUX USERS</summary>

1. Add this to launch options `WINEDLLOVERRIDES="winhttp=n,b" %command%`
    - It tells proton to load BepInEx.

</details>
</details>
<br>



    
# Warning:
If installation doesn't work, Try again.
 - Or cry at people in "Casualties: Together" discord server: [https://discord.gg/FrBX3srzzG](https://discord.gg/FrBX3srzzG)

Make sure to launch the game through Steam Library if Steam Lobbies don't work.

It's recommended to have good internet connection for optimal experience.


!!!<br>
IF YOU ENCOUNTER ANY GLITCHES, CRASHES, OTHER ISSUES - BLAME THE MOD, NOT THE ORIGINAL DEVELOPER OF THE GAME<br>
!!!

To go back to singleplayer you need to press the `Deactivate MP Mod` button in `Settings > General`!


# Features:
Steam Lobbies<br>
Real voice chat with in-game effects.<br/>
Middle Mouse Button to point at a location.<br>
Right click to interact and heal other players.<br> 
Stay close to others to share body temperature.<br>
Spectator mode when dead.<br>
Sleeping is disabled.<br>
Hunger and thirst is slowed down.<br>
Carrying incapacitated players.<br>
Any dead players respawns at next layer.<br>
Unchipped option is individual.<br>
Most of these features can be tweaked in the in-game console.<br>
Also this mod can run as a dedicated server. (its not real headless)<br>

There's no client prediction.<br>
There's no network ownership.<br>
There's no deterministic RNG.<br>
There's no anticheat. <br>
This mod only adds a simple co-op experience to the game.<br>





# Full feaure list:

Disabled sleeping. (tied to the rule "EnableSleep" )<br>
Disabled time scale buttons. (don't try re-enable this actually)<br>
Player friendly fire can be enabled with the "PVP" rule.<br>
Middle mouse click to highlight a location at your cursor, for the other players to see.<br>
Stand close to other players to share body temperature.<br>

Text chat and Voice chat are dependent on your characters ability to speak, same distortions apply to your messages.<br>
MP3 Player now is a boombox instead of replacing background music.<br>

Right click on a player to open a player interaction context menu.
 - Inspect health panel and perform any actions.
 - Inspect their inventory and take their items. 
 - Use any usable item on them (Feeding).

When a player dies, they are put into spectator mode.
 - All dead players respawn after living players reach the next level.
 - (if rule "Permadeath" is true, respawns are disabled)
 - Player can be revived from traders.
   
To continue to the next layer, half of all players must reach the end. 
 - Straggles will get killed if they take too long.
 - Only the Host gets prompted to continue the run.
 - DrillPod works the same, all players must be in the pod to continue.
 - (tweaked by the rule "LayerFinishConditionPlayerPercent")

Player health regen and decay is tweaked.
 - Death is much faster if there are no comrades nearby. ( tweaked by the "AdditionalHealthDecay" rule )
 - Health regeneration is boosted. ( tweaked by the "AdditionalHealthRegen" rule)

All these rules can be changed only by the Host by running the command "rule [rule name] [new value]"<br>

You can eat your friends after they died. 
 - The rule "CanAmputateHealthyPlayers" allows you to steal limbs of living players.

Traders first impression reputation is calculated from the prettiest of the bunch.<br>



# How to enable all console commands:

The Host must enter this console command:<br>
```rule sv_cheats 1```<br>
**To let Client into server console mode they need "admin" privilege.**<br>
&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;**(Given by "adminpriv [player]" or "adminpriv-password")**



# Building:

Run script `scripts/prepare_workspace.py`

It copies the game and installs BepInEx.

**Visual Studio 2022** is recommended.

Open the project and compile it.

Hit F5

You have to launch multiple instances of the game manually or using scripts.

Uuhh <img src="https://media1.tenor.com/m/qPyvaruhJsYAAAAd/drooling-cat-lord-im-one.gif" width="32" height="32">   


# Credits:

Credits are listed in "About" menu inside the mod.<br>
[Creature](https://github.com/creaturefeaturelarry) Current owner.<br>
[Krokosha666](https://github.com/Krokosha666) Original developer. (Retired)

### Contributors:
[Dune](https://github.com/Jyan-sei) - Sync, Optimizations<br>
[VtLoader](https://github.com/vtloader) - Bug fixes, Locale<br>
[Gary the Cat](https://github.com/Garythecat89), [Dannad](https://github.com/danxnader), [Meow](https://github.com/SprightlyHawk8), [Jimmyking](https://github.com/jimmyking9999999), [Kidihh](https://github.com/Kidihh), [Dawn](https://github.com/hehdawn), [mayzmae](https://github.com/mayzmae), ItsVoidSK<br>
...And a bunch of playtesters

### Supporters:
 
```Bleh :P, EthanTheProtogen, RoyMartini, Jazun, Alexx__, vega, Zmechol1337, plasterjello, Doctor Callico, vivikat, Ariyah, killthebudget, _stuck_, ShaoAZ, JameisonAus, Kidi, BluBerryToaster, Katomou, Nemean_Lion732, Locko2265, Trenchburner, TheBoathead, Diesel, Malbyte, KaliVasquez, Mitirich, Shiro, ChrisTheDragon```



