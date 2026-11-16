using HarmonyLib;
using UnityEngine;
namespace Together;
[HarmonyPatch(typeof(Body), "HandleBody")]
static class Body_HandleBody_MpSleepSpeed {
        static void Prefix(Body __instance, out float __state) {
            __state = __instance.sleepCycleSpeed;
            if (!Net.IsRunning || __instance == null || !__instance.alive || !__instance.TryGetNetBody(out NetBody nb) || !nb.IsPlayer) {
                return;
            }

            if (nb.piggybacking_on != null && __instance.sleeping) {
                __instance.curSleep = Body.SleepQuality.Okay;
            }

            float mult = 1f;
            if (__instance.charType != 1 && ServerMain.TryGetLivingEnergyMedian(out float median)) {
                float offset = (__instance.energy - median) * 0.01f;
                if (!__instance.conscious) {
                    offset = -offset;
                }
                mult *= Mathf.Clamp(1f + offset, 0.01f, 3f);
            }

            if (!Multiplayer.rules.DisableSleep && __instance.sleeping && !__instance.conscious && !ServerMain.CheckIfEveryoneIsSleeping()) {
                float ramp = 0f;
                if (nb.soloSleepBonusStart >= 0f) {
                    ramp = Mathf.Clamp01((Time.realtimeSinceStartup - nb.soloSleepBonusStart) / ServerMain.SoloSleepBonusRampSeconds);
                }
                mult *= Mathf.Lerp(1f, 3f, ramp);
            }

            if (mult != 1f) {
                __instance.sleepCycleSpeed = __state * mult;
            }
        }

        static void Postfix(Body __instance, float __state) {
            if (__instance != null) {
                __instance.sleepCycleSpeed = __state;
            }
        }
    }
