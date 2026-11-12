#if FULLGAME
using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Together;

// Disk IDs belong only to this document, never to EntityManager.
internal sealed class WorldObjectDocument {
    public string magic = "CTWO";
    // v3 active flags describe gameplay state, excluding chunk/bag sleep.
    public int version = 3, biome, saveId;
    public int structureCatalog; // optional v3 extension; 0 is a pre-fix7 capture
    public uint width, height;
    public List<WorldObjectRecord> objects = new();
    public List<WorldBackgroundRecord> backgrounds = new();
    // Optional for v4.0 files: spawn placement only, never the live AI/object graph.
    public List<WorldTraderPlacement> traders;
    public EndingSaveData endingFlow;
}

internal sealed class WorldTraderPlacement {
    public string resource;
    public float[] position, desired;
    public float moveMin, moveMax;
    public bool farEnoughToMove, didMove;
}

internal sealed class WorldBackgroundRecord {
    public int networkKey; // WS1 binding; disk restores assign fresh layer-local keys
    public string template, shape;
    // Row-major world matrix. Already includes the prefab child's origin offset.
    public float[] matrix, color;
    public float health;
    public bool active = true, visible = true, backgroundified;
    public bool? hasPump;
    public List<WorldObjectComponent> components;
}

internal static class WorldBackgroundPose {
    // Explicit 2D TRS decomposition preserves reflections and rejects shear/3D tilt.
    // No tile origin or prefab-local offset is added to a saved world matrix.
    internal static bool TryDecompose(float[] m, out float[] position, out float[] rotation, out float[] scale) {
        position = rotation = scale = null;
        if (m == null || m.Length != 16 || m.Any(x => float.IsNaN(x) || float.IsInfinity(x))) return false;
        float sx = (float)Math.Sqrt(m[0] * m[0] + m[4] * m[4]);
        if (sx < 0.00001f || Math.Abs(m[10]) < 0.00001f) return false;
        float sy = (m[0] * m[5] - m[1] * m[4]) / sx;
        float cos = m[0] / sx, sin = m[4] / sx;
        if (Math.Abs(sy) < 0.00001f || Math.Abs(m[1] + sin * sy) > 0.001f || Math.Abs(m[5] - cos * sy) > 0.001f ||
            new[] { 2, 6, 8, 9, 12, 13, 14 }.Any(i => Math.Abs(m[i]) > 0.001f) || Math.Abs(m[15] - 1f) > 0.001f) return false;
        double halfAngle = Math.Atan2(sin, cos) / 2;
        position = new[] { m[3], m[7], m[11] };
        rotation = new[] { 0f, 0f, (float)Math.Sin(halfAngle), (float)Math.Cos(halfAngle) };
        scale = new[] { sx, sy, m[10] };
        return true;
    }
}

internal sealed class WorldObjectRecord {
    public int id, parent, networkKey;
    public string resource;
    public bool building, container, active = true, background, flipX, flipY;
    public float[] position, rotation, scale;
    public float condition, wetAge;
    public bool favourited;
    public string label;
    public int swatch, sortingOrder, corpseSprite = -1;
    public bool hasRigid, simulated;
    public int rigidType;
    public float[] velocity;
    public float angularVelocity;
    public float[] spriteSize;
    public float[][] climbPoints;
    public float climbVelocity;
    public bool? hasCrystal;
    public CrystalSync.Snapshot crystal;
    public bool? usable;
    public int? noteFont;
    public string noteSprite;
    public int[] seating;
    public List<WorldObjectComponent> components = new();
}

internal sealed class WorldObjectComponent {
    // Child-index path inside the resource prefab (empty means the root).
    public string path, type;
    public JObject fields;
}

internal static class WorldObjectSnapshotData {
    const int MaxBytes = 128 * 1024 * 1024;
    static readonly JsonSerializerSettings Settings = new() { TypeNameHandling = TypeNameHandling.None, MaxDepth = 64 };

    internal static byte[] Encode(WorldObjectDocument document) {
        Validate(document, document.biome, document.width, document.height, document.saveId);
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document, Formatting.None, Settings));
        if (bytes.Length > MaxBytes) throw new InvalidDataException("World object snapshot exceeds size limit.");
        return Util.Compress(bytes);
    }

    internal static WorldObjectDocument Decode(byte[] compressed, int biome, uint width, uint height, int saveId) {
        if (compressed == null || compressed.Length == 0 || compressed.Length > MaxBytes)
            throw new InvalidDataException("Empty/oversized world object snapshot.");
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) != 0) {
            if (output.Length + read > MaxBytes) throw new InvalidDataException("Inflated world object snapshot exceeds size limit.");
            output.Write(buffer, 0, read);
        }
        var document = JsonConvert.DeserializeObject<WorldObjectDocument>(Encoding.UTF8.GetString(output.ToArray()), Settings);
        Validate(document, biome, width, height, saveId);
        return document;
    }

    internal static void Validate(WorldObjectDocument document, int biome, uint width, uint height, int saveId) {
        if (document == null || document.magic != "CTWO" || document.structureCatalog < 0 || document.structureCatalog > 1 || (document.version != 1 && document.version != 2 && document.version != 3))
            throw new InvalidDataException("Unknown world object snapshot format/version.");
        if (document.biome != biome || document.width != width || document.height != height || document.saveId != saveId)
            throw new InvalidDataException("World object snapshot belongs to another layer/world/save.");
        if (document.objects == null || document.objects.Count > 60000) throw new InvalidDataException("Invalid object count.");
        if (document.traders != null) {
            if (document.traders.Count > 4096) throw new InvalidDataException("Invalid trader placement count.");
            foreach (var trader in document.traders) {
                if (trader == null || (trader.resource != "trader1" && trader.resource != "trader2" && trader.resource != "trader3") ||
                    !Finite(trader.moveMin) || !Finite(trader.moveMax) || trader.moveMin > trader.moveMax)
                    throw new InvalidDataException("Invalid trader spawn placement.");
                Vector(trader.position, 3); Vector(trader.desired, 2);
            }
        }
        if (document.version >= 2) {
            if (document.backgrounds == null || document.backgrounds.Count > 4096) throw new InvalidDataException("Invalid background count.");
            foreach (var background in document.backgrounds) {
                if (background == null || string.IsNullOrEmpty(background.template) || background.template.Length > 1024 ||
                    string.IsNullOrEmpty(background.shape) || background.shape.Length != 64 || !Finite(background.health) ||
                    !WorldBackgroundPose.TryDecompose(background.matrix, out _, out _, out _))
                    throw new InvalidDataException("Invalid background template/pose/state.");
                Vector(background.color, 4);
            }
        }
        for (int i = 0; i < document.objects.Count; i++) {
            var obj = document.objects[i];
            if (obj == null || obj.id != i + 1 || obj.parent < 0 || obj.parent >= obj.id ||
                string.IsNullOrWhiteSpace(obj.resource) || obj.resource.Length > 512)
                throw new InvalidDataException("Invalid world object ID/resource/parent order.");
            if (obj.parent != 0 && (obj.building || !document.objects[obj.parent - 1].container))
                throw new InvalidDataException("World object parent is not a container.");
            Vector(obj.position, 3); Vector(obj.rotation, 4); Vector(obj.scale, 3); Vector(obj.velocity, 2);
            if (obj.spriteSize != null) Vector(obj.spriteSize, 2);
            if (obj.seating != null && obj.seating.Length != 2) throw new InvalidDataException("Invalid seating coordinate.");
            if (obj.noteSprite != null && obj.noteSprite.Length > 1024) throw new InvalidDataException("Invalid note sprite reference.");
            if (obj.climbPoints != null) {
                if (obj.climbPoints.Length > 4096 || !Finite(obj.climbVelocity)) throw new InvalidDataException("Invalid climbable state.");
                foreach (var point in obj.climbPoints) Vector(point, 2);
            }
            if (obj.crystal != null) {
                var c = obj.crystal;
                Vector(c.color, 4);
                if (!Finite(c.size) || !Finite(c.heaterTemp) || !Finite(c.heaterStrength) || c.effects == null || c.effects.Count > 17)
                    throw new InvalidDataException("Invalid crystal state.");
                foreach (var effect in c.effects) {
                    if (effect.type < 0 || effect.type >= CrystalBehaviour.possibleEffects.Count) throw new InvalidDataException("Invalid crystal effect.");
                    Vector(effect.values, 7);
                }
            }
            if (obj.rotation.Sum(x => x * x) < 0.01f || !Finite(obj.condition) || !Finite(obj.wetAge) ||
                !Finite(obj.angularVelocity) || obj.rigidType < 0 || obj.rigidType > 2 || obj.components == null || obj.components.Count > 256)
                throw new InvalidDataException("Invalid world object state.");
            foreach (var component in obj.components) {
                if (component == null || component.path == null || component.path.Length > 256 ||
                    string.IsNullOrEmpty(component.type) || component.type.Length > 512 || component.fields == null)
                    throw new InvalidDataException("Invalid component state.");
                if (component.path.Length != 0 && component.path.Split('/').Any(x => !int.TryParse(x, out int index) || index < 0))
                    throw new InvalidDataException("Invalid component child path.");
            }
        }
    }

    static void Vector(float[] value, int size) {
        if (value == null || value.Length != size || value.Any(x => !Finite(x))) throw new InvalidDataException("Invalid snapshot vector.");
    }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
#endif
