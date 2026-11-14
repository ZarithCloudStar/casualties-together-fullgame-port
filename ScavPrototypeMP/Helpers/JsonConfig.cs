
using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Together;




// AI GENERATED
public class JsonConfigThingy {
    private string filePath;

    [Serializable]
    private class PrefData {
        public Dictionary<string, string> data = new Dictionary<string, string>();
    }

    private PrefData prefs = new PrefData();

    // ------------------------ 
    // Constructor 
    // ------------------------ 
    public JsonConfigThingy(string fileName) {
        filePath = Path.Combine(Application.persistentDataPath, fileName);
        Load();
    }

    // ------------------------ 
    // Public API (PlayerPrefs-like)
    // ------------------------ 

    public void SetString(string key, string value) {
        prefs.data[key] = value;
    }

    public string GetString(string key, string defaultValue = "") {
        return prefs.data.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public void SetBool(string key, bool value) {
        SetString(key, value ? "1" : "0");
    }

    public bool GetBool(string key, bool defaultValue = false) {
        if (prefs.data.TryGetValue(key, out var str)) {
            str = str.ToLower();
            if (bool.TryParse(str, out var result)) {
                return result;
            }
            if (int.TryParse(str, out var n)) {
                if (n == 0) return false;
                return true;
            }
        }
        return defaultValue;
    }

    public void SetInt(string key, int value) {
        SetString(key, value.ToString());
    }

    public int GetInt(string key, int defaultValue = 0) {
        if (prefs.data.TryGetValue(key, out var value) && int.TryParse(value, out var result)) {
            return result;
        }
        return defaultValue;
    }

    public void SetFloat(string key, float value) {
        SetString(key, value.ToStringInvariant());
    }

    public float GetFloat(string key, float defaultValue = 0f) {
        if (prefs.data.TryGetValue(key, out var value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) {
            return result;
        }
        return defaultValue;
    }

    public bool HasKey(string key) {
        return prefs.data.ContainsKey(key);
    }

    public void DeleteKey(string key) {
        prefs.data.Remove(key);
    }

    public void DeleteAll() {
        prefs.data.Clear();
    }

    // ------------------------
    // File Handling
    // ------------------------

    public void Save() {
        try {
            string json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
            File.WriteAllText(filePath, json);
        } catch (Exception e) {
            log.error($"{this.GetType().Name} Save failed: {e.ToString()}");
        }
    }

    public void Load() {
        try {
            if (File.Exists(filePath)) {
                string json = File.ReadAllText(filePath);
                prefs = JsonConvert.DeserializeObject<PrefData>(json) ?? new PrefData();
            } else {
                prefs = new PrefData();
            }
        } catch (Exception e) {
            log.error($"{this.GetType().Name} Load failed: {e.ToString()}");
            prefs = new PrefData();
        }
    }
}


public static class SettingsJsonThing {
    public static JsonConfigThingy json { 
        get {
            if (thing == null) {
                thing = new("mp_settings.json");
            }
            return thing;
        }
    }
    static JsonConfigThingy thing;

    // Settings live only in memory inside JsonConfigThingy until Save() is called.
    // The classic settings tab saved only when the Apply button was pressed and
    // the modern tab never saved, so values silently reverted on the next launch.
    static bool dirty;
    static float last_change;

    public static void MarkDirty() {
        dirty = true;
        last_change = Time.realtimeSinceStartup;
    }

    public static void Flush(bool force = false) {
        if (thing == null || !dirty) {
            return;
        }
        if (!force && Time.realtimeSinceStartup - last_change < 0.75f) {
            return;
        }
        dirty = false;
        thing.Save();
    }

    public static void FlushNow() {
        if (thing == null) {
            return;
        }
        dirty = false;
        thing.Save();
    }
}

internal class SettingsAutoSaver : MonoBehaviour {
    void Update() {
        SettingsJsonThing.Flush();
    }

    void OnApplicationQuit() {
        SettingsJsonThing.FlushNow();
    }
}













public class JsonPresetsSaver<T> {
    private string filePath;

    [Serializable]
    private class PrefData {
        public Dictionary<string, T> data = new ();
    }

    private PrefData prefs = new PrefData();

    // ------------------------ 
    // Constructor 
    // ------------------------ 
    public JsonPresetsSaver(string filename) {
        filePath = Path.Combine(Application.persistentDataPath, filename);
        Load();
    }

    // ------------------------
    // Public API (PlayerPrefs-like)
    // ------------------------

    public IEnumerable<string> GetPresets() {
        return prefs.data.Keys;
    }

    public void SetData(string key, T value) {
        prefs.data[key] = value;
    }

    public T GetData(string key) => GetData(key, default(T));
    public T GetData(string key, in T defaultValue) {
        return prefs.data.TryGetValue(key, out var value) ? (value) : defaultValue;
    }

    public bool HasKey(string key) {
        return prefs.data.ContainsKey(key);
    }

    public void DeleteKey(string key) {
        prefs.data.Remove(key);
    }

    public void DeleteAll() {
        prefs.data.Clear();
    }

    // ------------------------
    // File Handling
    // ------------------------

    public void Save() {
        try {
            string json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
            File.WriteAllText(filePath, json);
        } catch (Exception e) {
            log.error($"JsonSimpleClassSave \"{typeof(T).Name}\" Save failed: {e.ToString()}");
        }
    }

    public void Load() {
        try {
            if (File.Exists(filePath)) {
                string json = File.ReadAllText(filePath);
                prefs = JsonConvert.DeserializeObject<PrefData>(json) ?? new PrefData();
            } else {
                prefs = new PrefData();
            }
        } catch (Exception e) {
            log.error($"JsonSimpleClassSave \"{typeof(T).Name}\" Load failed: {e.ToString()}");
            prefs = new PrefData();
        }
    }
}














