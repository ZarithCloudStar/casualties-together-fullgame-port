#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

internal static class PlayerPlacementRegression {
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--mp-player-placement-test");
    internal static bool Minimal => Environment.GetCommandLineArgs().Contains("--mp-fix7-minimal");
    static string Folder => SavesystemPatch.mpsavefolder;
    static bool moved, checkedRestore, checkedNext;
    internal static void Tick() {
        if (!Enabled || !Net.IsClient || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        string move = Path.Combine(Folder, "placement-move.json");
        // A load-only run inherits the move request from its source save.
        // Never replay it when that loaded party subsequently changes layer.
        if (!moved && !checkedRestore && File.Exists(move) && !WorldSnapshotTransfer.SnapshotMode) {
            moved = true;
            var point = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(move));
            ScavPlayer.LocalPlayer.body.ResetEntropy();
            ScavPlayer.LocalPlayer.netBody.SetBodyPosition(new Vector2(point[0], point[1]));
            if (ConsoleScript.instance != null) ConsoleScript.instance.noClipPos = new Vector2(point[0], point[1]);
            ScavPlayer.LocalPlayer.body.isRight = false;
            log.l($"[placement-test] guest moved locally to {point[0]:F3},{point[1]:F3}");
        }
        if (!checkedRestore && WorldSnapshotTransfer.SnapshotMode) {
            checkedRestore = true;
            Util.StartCoroutine(ClientRestored());
        }
        if (!checkedNext && checkedRestore && !WorldSnapshotTransfer.SnapshotMode) {
            checkedNext = true;
            Util.StartCoroutine(ClientNextLayer());
        }
    }
    static IEnumerator ClientNextLayer() {
        yield return new WaitForSecondsRealtime(3);
        var at = ScavPlayer.LocalPlayer.netBody.GetPosition();
        File.WriteAllText(Path.Combine(Folder, "placement-client-next-layer.json"), JsonConvert.SerializeObject(new {
            layer = WorldGeneration.world.biomeDepth, x = at.x, y = at.y
        }));
    }
    static IEnumerator ClientRestored() {
        yield return new WaitForSecondsRealtime(3);
        var p = ScavPlayer.LocalPlayer;
        var rules = JObject.Parse(File.ReadAllText(Path.Combine(Folder, SavesystemPatch.mpsaveheaderfile)));
        var expected = rules["PLRPOSE"][p.GetPersistentId()].ToObject<PlayerPlacementCheckpoint.Pose>();
        var at = p.body.transform.position;
        float error = Vector2.Distance(at, new Vector2(expected.x, expected.y));
        File.WriteAllText(Path.Combine(Folder, "placement-client-restored.json"), JsonConvert.SerializeObject(new {
            p.playerName, expected, x = at.x, y = at.y, layer = WorldGeneration.world.biomeDepth, error
        }, Formatting.Indented));
        if (Minimal) log.l($"[placement-test] OBSERVED client restore pass={error <= 1} expected={expected.x:F3},{expected.y:F3} actual={at.x:F3},{at.y:F3} error={error:F3}");
        else Check(error <= 1, $"client restored expected={expected.x:F3},{expected.y:F3} actual={at.x:F3},{at.y:F3} error={error:F3}");
    }
    internal static IEnumerator BeforeSave() {
        if (!Enabled) yield break;
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        var target = (Vector2)host.body.transform.position + new Vector2(77, -31);
        // Make a walkable test pocket. The fixture's noclip move must not save
        // a body embedded in solid terrain and then measure depenetration.
        WorldGeneration.world.GenerateBlockCircle(target, 10, 0, 1f, 0f, autoUpdateChunk: true);
        WorldGeneration.world.UpdateWorld();
        File.WriteAllText(Path.Combine(Folder, "placement-move.json"), JsonConvert.SerializeObject(new[] { target.x, target.y }));
        float until = Time.realtimeSinceStartup + 20;
        while (Vector2.Distance(guest.body.transform.position, target) > .5f && Time.realtimeSinceStartup < until) yield return null;
        Check(Vector2.Distance(guest.body.transform.position, target) <= 1f, $"server received unique guest position from real client expected={target} actual={guest.body.transform.position}");
        File.WriteAllText(Path.Combine(Folder, "placement-before.json"), JsonConvert.SerializeObject(PlayerPlacementCheckpoint.Capture(), Formatting.Indented));
    }
    internal static IEnumerator VerifyRestore() {
        if (!Enabled) yield break;
        float until = Time.realtimeSinceStartup + 50;
        while (!File.Exists(Path.Combine(Folder, "placement-client-restored.json")) && Time.realtimeSinceStartup < until) yield return null;
        Check(File.Exists(Path.Combine(Folder, "placement-client-restored.json")), "client supplied restored position evidence");
        var rules = JObject.Parse(File.ReadAllText(Path.Combine(Folder, SavesystemPatch.mpsaveheaderfile)));
        var poses = rules["PLRPOSE"].ToObject<System.Collections.Generic.Dictionary<string, PlayerPlacementCheckpoint.Pose>>();
        var result = ScavPlayer.ClientIdToPlayerDict.Values.Select(p => {
            var expected = poses[p.GetPersistentId()]; var at = p.body.transform.position;
            return new { p.playerName, expected, x = at.x, y = at.y, error = Vector2.Distance(at, new Vector2(expected.x, expected.y)) };
        }).ToArray();
        foreach (var r in result) {
            if (Minimal) log.l($"[placement-test] OBSERVED server restore player={r.playerName} pass={r.error <= 1} expected={r.expected.x:F3},{r.expected.y:F3} actual={r.x:F3},{r.y:F3} error={r.error:F3}");
            else Check(r.error <= 1, $"server restored player={r.playerName} expected={r.expected.x:F3},{r.expected.y:F3} actual={r.x:F3},{r.y:F3} error={r.error:F3}");
        }
        File.WriteAllText(Path.Combine(Folder, "placement-host-restored.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
    }
    internal static IEnumerator NextLayer() {
        if (!Enabled) yield break;
        int before = WorldGeneration.world.biomeDepth;
        // Deliberately separated X coordinates exercise the former per-player
        // layer_transition_x_offset branch with its rule enabled.
        Multiplayer.rules.LayerFinishKeepXOffset = true;
        var players = ScavPlayer.ClientIdToPlayerDict.Values.ToArray();
        players[0].Server_TeleportCharacter(new Vector2(-90, 0));
        players[1].Server_TeleportCharacter(new Vector2(90, 0));
        WorldGeneration.world.StartCoroutine("RegenerateWorld", false);
        yield return null;
        float until = Time.realtimeSinceStartup + 120;
        while ((!Util.IsWorldGenerated() || WorldGeneration.world.biomeDepth == before ||
            players.Any(p => !p.IsLocal && !p.ServerPlayerState.is_loaded_in)) && Time.realtimeSinceStartup < until) yield return null;
        yield return new WaitForSecondsRealtime(4);
        while (!File.Exists(Path.Combine(Folder, "placement-client-next-layer.json")) && Time.realtimeSinceStartup < until) yield return null;
        var at = players.Select(p => new { p.playerName, x = p.body.transform.position.x, y = p.body.transform.position.y }).ToArray();
        float distance = Vector2.Distance(players[0].body.transform.position, players[1].body.transform.position);
        var client = JObject.Parse(File.ReadAllText(Path.Combine(Folder, "placement-client-next-layer.json")));
        var guest = players.Single(p => !p.IsLocal).netBody.GetPosition();
        float clientError = Vector2.Distance(guest, new Vector2(client.Value<float>("x"), client.Value<float>("y")));
        File.WriteAllText(Path.Combine(Folder, "placement-next-layer.json"), JsonConvert.SerializeObject(new {
            layer = WorldGeneration.world.biomeDepth, players = at, distance, client, clientError, authoritative = Body_PlaceBody_MultiplayerPatch.SpawnLocation.ToString("F3")
        }, Formatting.Indented));
        Check(WorldGeneration.world.biomeDepth != before && distance <= 2, $"next layer={WorldGeneration.world.biomeDepth} host={at[0].x:F3},{at[0].y:F3} guest={at[1].x:F3},{at[1].y:F3} distance={distance:F3}");
        Check(client.Value<int>("layer") == WorldGeneration.world.biomeDepth && clientError <= 1, $"client received next-layer authoritative coordinate error={clientError:F3}");
    }
    static void Check(bool ok, string message) {
        log.l("[placement-test] " + (ok ? "PASS " : "FAIL ") + message);
        if (!ok) throw new Exception(message);
    }
}
#endif
