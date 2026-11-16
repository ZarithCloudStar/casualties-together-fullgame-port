#if FULLGAME && TESTING
using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using CasualtiesTogetherUtils;
namespace Together;
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class UiPreviewOnly {
    static bool done;
    static double startedAt = -1;
    static void Postfix() {
        if (done) return;
        if (startedAt < 0) startedAt = Time.realtimeSinceStartupAsDouble;
        if (Time.realtimeSinceStartupAsDouble - startedAt < 8.0) return;
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--mp-ui-preview")) {
            if (Time.realtimeSinceStartupAsDouble - startedAt > 40.0) {
                done = true;
                log.l("[ui-preview] missing --mp-ui-preview; args=" + string.Join(" ", args));
            }
            return;
        }
        if (!Util.IsInMainMenu()) return;
        try {
            string text = "房主未强制统一模组列表；以下安装差异不代表全部必需，也不一定是入房失败原因。\n\n你未安装的房主模组（不一定必需）：\n- 示例汉化 1.0 [preview.translation]\n\n客机额外安装（房主没有）：\n- Improved Headlamp 1.1.0 [Sholopie.ImprovedHeadlamp]\n- Upgradable Storage Packs 1.1.1 [Sholopie.UpgradableStoragePacks]\n- No cheat achievements lock 1.0.0 [askerious.casualtiesunknown.nocheatlock]\n- gouxismoreattachment 1.1.0 [com.gouxi.gouxismoreattachment]\n\n新增物品或玩法可能需要双方安装对应模组；此提示不会自动识别必装关系。";
            AccessTools.Field(typeof(JoinModNotice), "notice").SetValue(null, text);
            AccessTools.Method(typeof(JoinModNotice), "BlockCanvas").Invoke(null, null);
            done = true;
            log.l("[ui-preview] Opened mod-difference dialog with demonstration data; no connection started.");
        } catch (Exception e) {
            done = true;
            log.error("[ui-preview] failed to open dialog: " + e);
        }
    }
}
#endif
