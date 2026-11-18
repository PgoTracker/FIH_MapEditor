using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FIHMapEditor
{
    // A single archive is the unit shared between players. Geometry/<name>.obj,
    // Scripts/<name>.cs and Textures/<name>.png share the same name.
    public sealed class MapPackageAsset
    {
        public string Name { get; set; }
        public byte[] Geometry { get; set; }
        public string Script { get; set; }
        public byte[] Texture { get; set; }
        public byte[] ImpactSound { get; set; }
        public string ImpactSoundExtension { get; set; }
    }

    public static class MapPackage
    {
        public const string Extension = ".fihmap.zip";
        public const string MapEntry = "map.fihmap.json";
        public const string SourcePrefix = "package://";
        public const int MaxAssetCount = 100;
        public const int MaxGeometryBytes = 8 * 1024 * 1024;
        public const int MaxScriptBytes = 256 * 1024;
        public const int MaxTextureBytes = 8 * 1024 * 1024;
        public const int MaxImpactSoundBytes = 4 * 1024 * 1024;
        public const int MaxTextureDimension = 2048;
        public const int MaxPackageBytes = 64 * 1024 * 1024;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string SourceFor(string name) => SourcePrefix + name;

        public static bool TryNameFromSource(string source, out string name)
        {
            name = source != null && source.StartsWith(SourcePrefix, StringComparison.Ordinal)
                ? source.Substring(SourcePrefix.Length) : null;
            return IsValidName(name);
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 80) return false;
            if (!(name[0] >= 'A' && name[0] <= 'Z')
                && !(name[0] >= 'a' && name[0] <= 'z') && name[0] != '_') return false;
            foreach (char c in name)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')
                    && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }

        public static MapFile Read(string path)
        {
            if (new FileInfo(path).Length > MaxPackageBytes)
                throw new InvalidDataException("Map package exceeds 64 MB.");
            using var zip = ZipFile.OpenRead(path);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<string, MapPackageAsset>(StringComparer.OrdinalIgnoreCase);
            string mapJson = null;
            long uncompressedBytes = 0;
            foreach (var entry in zip.Entries)
            {
                uncompressedBytes += entry.Length;
                if (uncompressedBytes > MaxPackageBytes)
                    throw new InvalidDataException("Map package expands beyond 64 MB.");
                if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate package entry: " + entry.FullName);
                if (entry.FullName == MapEntry)
                {
                    mapJson = ReadText(entry, 8 * 1024 * 1024);
                    continue;
                }
                bool geometry = entry.FullName.StartsWith("geometry/", StringComparison.Ordinal);
                bool script = entry.FullName.StartsWith("scripts/", StringComparison.Ordinal);
                bool texture = entry.FullName.StartsWith("textures/", StringComparison.Ordinal);
                bool sound = entry.FullName.StartsWith("sounds/", StringComparison.Ordinal);
                string suffix = geometry ? ".obj" : script ? ".cs" : texture ? ".png"
                    : entry.FullName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".ogg";
                string folder = geometry ? "geometry/" : script ? "scripts/" : texture ? "textures/" : "sounds/";
                if ((!geometry && !script && !texture && !sound) || !entry.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected package entry: " + entry.FullName);
                string name = entry.FullName.Substring(folder.Length, entry.FullName.Length - folder.Length - suffix.Length);
                if (!IsValidName(name)) throw new InvalidDataException("Invalid geometry name: " + name);
                if (!assets.TryGetValue(name, out var asset))
                {
                    if (assets.Count >= MaxAssetCount) throw new InvalidDataException("Too many geometry assets.");
                    asset = new MapPackageAsset { Name = name };
                    assets.Add(name, asset);
                }
                if (geometry)
                {
                    if (asset.Geometry != null) throw new InvalidDataException("Duplicate geometry name: " + name);
                    asset.Geometry = ReadBytes(entry, MaxGeometryBytes);
                }
                else if (script)
                {
                    if (asset.Script != null) throw new InvalidDataException("Duplicate script name: " + name);
                    asset.Script = ReadText(entry, MaxScriptBytes);
                }
                else if (texture)
                {
                    if (asset.Texture != null) throw new InvalidDataException("Duplicate texture name: " + name);
                    asset.Texture = ReadBytes(entry, MaxTextureBytes);
                    ValidateTexture(asset.Texture);
                }
                else
                {
                    if (asset.ImpactSound != null) throw new InvalidDataException("Duplicate impact sound name: " + name);
                    asset.ImpactSound = ReadBytes(entry, MaxImpactSoundBytes);
                    asset.ImpactSoundExtension = suffix;
                    ValidateImpactSound(asset.ImpactSound, suffix);
                }
            }
            if (mapJson == null) throw new InvalidDataException("Missing map.fihmap.json.");
            foreach (var asset in assets.Values)
                if (asset.Geometry == null) throw new InvalidDataException("Script, texture or sound has no matching OBJ: " + asset.Name);
            var map = JsonSerializer.Deserialize<MapFile>(mapJson, Json)
                ?? throw new InvalidDataException("Map JSON deserialized to null.");
            MapMigrations.Normalize(map);
            map.PackageAssets = assets.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            map.PackageCodeHash = CodeHash(map.PackageAssets);
            foreach (var obj in map.Objects)
                ValidateSource(obj.Source, assets.ContainsKey);
            return map;
        }

        public static void Write(string path, MapFile map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            var assets = map.PackageAssets ?? new List<MapPackageAsset>();
            if (assets.Count > MaxAssetCount) throw new InvalidDataException("Too many geometry assets.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long uncompressedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(map, Json));
            foreach (var asset in assets)
            {
                if (asset == null || !IsValidName(asset.Name) || !names.Add(asset.Name) || asset.Geometry == null
                    || asset.Geometry.Length > MaxGeometryBytes || Encoding.UTF8.GetByteCount(asset.Script ?? "") > MaxScriptBytes
                    || (asset.Texture?.Length ?? 0) > MaxTextureBytes
                    || (asset.ImpactSound?.Length ?? 0) > MaxImpactSoundBytes)
                    throw new InvalidDataException("Invalid package asset.");
                if (asset.Texture != null) ValidateTexture(asset.Texture);
                if (asset.ImpactSound != null) ValidateImpactSound(asset.ImpactSound, asset.ImpactSoundExtension);
                uncompressedBytes += asset.Geometry.Length + Encoding.UTF8.GetByteCount(asset.Script ?? "")
                    + (asset.Texture?.Length ?? 0) + (asset.ImpactSound?.Length ?? 0);
            }
            if (uncompressedBytes > MaxPackageBytes) throw new InvalidDataException("Map package expands beyond 64 MB.");
            foreach (var obj in map.Objects ?? new List<MapObjectData>())
                ValidateSource(obj.Source, names.Contains);

            string tmp = path + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    WriteText(zip, MapEntry, JsonSerializer.Serialize(map, Json));
                    foreach (var asset in assets)
                    {
                        var entry = zip.CreateEntry("geometry/" + asset.Name + ".obj", CompressionLevel.Optimal);
                        using (var stream = entry.Open()) stream.Write(asset.Geometry, 0, asset.Geometry.Length);
                        if (asset.Script != null) WriteText(zip, "scripts/" + asset.Name + ".cs", asset.Script);
                        if (asset.Texture != null)
                        {
                            var textureEntry = zip.CreateEntry("textures/" + asset.Name + ".png", CompressionLevel.Optimal);
                            using var stream = textureEntry.Open();
                            stream.Write(asset.Texture, 0, asset.Texture.Length);
                        }
                        if (asset.ImpactSound != null)
                        {
                            var soundEntry = zip.CreateEntry("sounds/" + asset.Name + asset.ImpactSoundExtension, CompressionLevel.Optimal);
                            using var stream = soundEntry.Open();
                            stream.Write(asset.ImpactSound, 0, asset.ImpactSound.Length);
                        }
                    }
                }
                if (new FileInfo(tmp).Length > MaxPackageBytes) throw new InvalidDataException("Map package exceeds 64 MB.");
                File.Move(tmp, path, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        public static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        public static string CodeHash(IEnumerable<MapPackageAsset> assets)
        {
            using var sha = SHA256.Create();
            using var data = new MemoryStream();
            foreach (var asset in assets.Where(a => a.Script != null)
                         .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = Encoding.UTF8.GetBytes(asset.Name.ToLowerInvariant() + "\n" + asset.Script + "\n");
                data.Write(bytes, 0, bytes.Length);
            }
            if (data.Length == 0) return null;
            data.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static void ValidateSource(string source, Func<string, bool> exists)
        {
            if (source == null || !source.StartsWith(SourcePrefix, StringComparison.Ordinal)) return;
            if (!TryNameFromSource(source, out var name))
                throw new InvalidDataException("Invalid packaged geometry source: " + source);
            if (!exists(name)) throw new InvalidDataException("Map references missing geometry: " + name);
        }

        private static byte[] ReadBytes(ZipArchiveEntry entry, int limit)
        {
            if (entry.Length > limit) throw new InvalidDataException("Package entry too large: " + entry.FullName);
            using var source = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + n > limit) throw new InvalidDataException("Package entry too large: " + entry.FullName);
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }

        private static string ReadText(ZipArchiveEntry entry, int limit)
            => new UTF8Encoding(false, true).GetString(ReadBytes(entry, limit));

        private static void ValidateTexture(byte[] png)
            => PngTexture.ValidateHeader(png);

        private static void ValidateImpactSound(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxImpactSoundBytes)
                throw new InvalidDataException("Impact sound must be 12 bytes to 4 MB.");
            bool wav = string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE";
            bool ogg = string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                && Encoding.ASCII.GetString(bytes, 0, 4) == "OggS";
            if (!wav && !ogg) throw new InvalidDataException("Impact sound must be a WAV or Ogg file.");
        }

        private static void WriteText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            stream.Write(content);
        }
    }
}
