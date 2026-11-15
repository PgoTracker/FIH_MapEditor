using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;

namespace FIHMapEditor
{
    /// <summary>Lightweight list-row metadata; map contents are loaded only on demand.</summary>
    public class MapFileInfo
    {
        public string FileName;      // file name without extension
        public string MapName;       // Name field inside the JSON
        public int ObjectCount;
        public DateTime LastWrite;
        public bool IsAutosave;
    }

    /// <summary>
    /// JSON and path policy for local maps. Higher-level code should normally use
    /// IMapRepository; online transport uses ToJson/FromJson directly.
    /// </summary>
    public static class MapSerializer
    {
        public const string EXTENSION = ".fihmap.json";
        public const string AUTOSAVE_NAME = "_autosave";
        public const string AUTOSAVE_PREV_NAME = "_autosave_prev";

        public static string MapsDir => Path.Combine(Paths.PluginPath, "FIHMapEditor", "Maps");

        // One shared instance: System.Text.Json caches its reflection metadata PER
        // options object, so recreating it per call would re-derive everything each time.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "unnamed" : name;
        }

        // fileName is a repository slot (already sanitized), never a user-supplied path.
        public static string PathFor(string fileName) => Path.Combine(MapsDir, fileName + EXTENSION);
        public static string PackagePathFor(string fileName) => Path.Combine(MapsDir, fileName + MapPackage.Extension);

        // In-memory (de)serialization for the online map library (upload/download).
        public static string ToJson(MapFile map) => JsonSerializer.Serialize(map, Options);

        public static MapFile FromJson(string json)
        {
            var map = JsonSerializer.Deserialize<MapFile>(json, Options);
            if (map == null) throw new InvalidDataException("Map JSON deserialized to null");
            MapMigrations.Normalize(map);
            // Legacy maps (pre-v6) carry no MapId — mint and keep one so the leaderboard
            // has a stable key from now on.
            if (string.IsNullOrEmpty(map.MapId))
                map.MapId = System.Guid.NewGuid().ToString("N");
            return map;
        }

        public static void Save(MapFile map, string fileName)
        {
            Directory.CreateDirectory(MapsDir);
            if ((map.PackageAssets?.Count ?? 0) > 0 || File.Exists(PackagePathFor(fileName)))
            {
                MapPackage.Write(PackagePathFor(fileName), map);
                if (File.Exists(PathFor(fileName))) File.Delete(PathFor(fileName));
                return;
            }
            string json = JsonSerializer.Serialize(map, Options);
            File.WriteAllText(PathFor(fileName), json);
            if (File.Exists(PackagePathFor(fileName))) File.Delete(PackagePathFor(fileName));
        }

        // Two-slot autosave: the previous autosave is kept as *_prev so a fresh session
        // with one stray edit can never wipe out yesterday's work in a single write.
        public static void SaveAutosave(MapFile map)
        {
            Directory.CreateDirectory(MapsDir);
            try
            {
                string oldSuffix = File.Exists(PackagePathFor(AUTOSAVE_NAME))
                    ? MapPackage.Extension : EXTENSION;
                string oldPath = Path.Combine(MapsDir, AUTOSAVE_NAME + oldSuffix);
                if (File.Exists(oldPath))
                {
                    File.Copy(oldPath, Path.Combine(MapsDir, AUTOSAVE_PREV_NAME + oldSuffix), overwrite: true);
                    string otherSuffix = oldSuffix == EXTENSION ? MapPackage.Extension : EXTENSION;
                    string stalePrev = Path.Combine(MapsDir, AUTOSAVE_PREV_NAME + otherSuffix);
                    if (File.Exists(stalePrev)) File.Delete(stalePrev);
                }
            }
            catch { }
            Save(map, AUTOSAVE_NAME);
        }

        public static MapFile Load(string fileName)
        {
            if (File.Exists(PackagePathFor(fileName))) return MapPackage.Read(PackagePathFor(fileName));
            string json = File.ReadAllText(PathFor(fileName));
            var map = JsonSerializer.Deserialize<MapFile>(json, Options);
            if (map == null) throw new InvalidDataException("Map file deserialized to null");
            // Forward-compatible best effort: System.Text.Json ignores unknown members. Warn
            // instead of refusing so a newer map may still be usable by an older editor.
            if (map.FormatVersion > MapFile.CURRENT_FORMAT_VERSION)
                MapEditorPlugin.Logger.LogWarning(
                    $"Map '{fileName}' has format v{map.FormatVersion} (newer than v{MapFile.CURRENT_FORMAT_VERSION}); loading anyway.");
            return MapMigrations.Normalize(map);
        }

        public static void Delete(string fileName)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) File.Delete(path);
            path = PackagePathFor(fileName);
            if (File.Exists(path)) File.Delete(path);
        }

        public static bool Exists(string fileName) => File.Exists(PathFor(fileName)) || File.Exists(PackagePathFor(fileName));

        public static List<MapFileInfo> ListMaps()
        {
            var result = new List<MapFileInfo>();
            try
            {
                if (!Directory.Exists(MapsDir)) return result;
                var files = new List<string>(Directory.GetFiles(MapsDir, "*" + MapPackage.Extension));
                var packageSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    packageSlots.Add(Path.GetFileName(file).Substring(0, Path.GetFileName(file).Length - MapPackage.Extension.Length));
                foreach (var file in Directory.GetFiles(MapsDir, "*" + EXTENSION))
                    if (!packageSlots.Contains(Path.GetFileName(file).Substring(0, Path.GetFileName(file).Length - EXTENSION.Length))) files.Add(file);
                foreach (var file in files)
                {
                    // List view reads only enough JSON for a label/count. A malformed map stays
                    // visible as a deletable row rather than making the whole hub fail.
                    var info = new MapFileInfo
                    {
                        FileName = Path.GetFileName(file).Substring(0, Path.GetFileName(file).Length
                            - (file.EndsWith(MapPackage.Extension, StringComparison.OrdinalIgnoreCase)
                                ? MapPackage.Extension.Length : EXTENSION.Length)),
                        LastWrite = File.GetLastWriteTime(file),
                    };
                    info.IsAutosave = info.FileName.StartsWith(AUTOSAVE_NAME);
                    try
                    {
                        var map = file.EndsWith(MapPackage.Extension, StringComparison.OrdinalIgnoreCase)
                            ? MapPackage.Read(file)
                            : JsonSerializer.Deserialize<MapFile>(File.ReadAllText(file), Options);
                        info.MapName = map?.Name ?? info.FileName;
                        info.ObjectCount = map?.Objects?.Count ?? 0;
                    }
                    catch
                    {
                        info.MapName = info.FileName + " (corrupt?)";
                        info.ObjectCount = -1;
                    }
                    result.Add(info);
                }
                // Newest first, autosave pinned on top
                result.Sort((a, b) =>
                {
                    if (a.IsAutosave != b.IsAutosave) return a.IsAutosave ? -1 : 1;
                    return b.LastWrite.CompareTo(a.LastWrite);
                });
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error listing maps: {ex.Message}");
            }
            return result;
        }
    }
}
