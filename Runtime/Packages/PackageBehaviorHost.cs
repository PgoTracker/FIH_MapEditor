using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

namespace FIHMapEditor
{
    // A packaged script defines a public class with the same name as its OBJ and
    // implements this interface. No custom MonoBehaviour registration is required.
    public interface IPackagedGeometryBehavior
    {
        void OnSpawn(PackagedGeometryContext context);
        void Update(PackagedGeometryContext context, float deltaTime);
        void OnDespawn(PackagedGeometryContext context);
    }

    public sealed class PackagedGeometryContext
    {
        private readonly GameObjectFinder _finder;
        public string Uid { get; }
        public string Name { get; }
        public GameObject Root { get; }
        public GameObject LocalPlayer => _finder?.FindPlayer();
        public PackagedGeometryContext(PlacedObject placed, string name, GameObjectFinder finder)
        {
            Uid = placed.Uid; Name = name; Root = placed.Root; _finder = finder;
        }
    }

    public sealed class PackageBehaviorHost
    {
        private readonly GameObjectFinder _finder;
        private sealed class Instance
        {
            public IPackagedGeometryBehavior Behavior;
            public PackagedGeometryContext Context;
        }

        private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Instance> _instances = new Dictionary<string, Instance>();
        private List<MapPackageAsset> _assets = new List<MapPackageAsset>();
        private string _codeHash;
        public bool HasCode => _codeHash != null;
        public bool CodeEnabled => _types.Count > 0;

        public PackageBehaviorHost(GameObjectFinder finder)
        {
            _finder = finder;
        }

        private static string TrustPath => Path.Combine(MapSerializer.MapsDir, "trusted-code-hashes.txt");

        public void SetAssets(List<MapPackageAsset> assets, string codeHash)
        {
            ClearInstances();
            _types.Clear();
            _assets = assets ?? new List<MapPackageAsset>();
            _codeHash = codeHash;
            if (IsTrusted())
                try { Compile(); }
                catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[PACKAGE] Code compilation failed: {ex}"); }
        }

        public bool IsTrusted()
        {
            if (_codeHash == null || !File.Exists(TrustPath)) return false;
            return File.ReadLines(TrustPath).Any(line =>
                string.Equals(line.Trim(), _codeHash, StringComparison.OrdinalIgnoreCase));
        }

        public void TrustAndEnable(IEnumerable<PlacedObject> placed)
        {
            if (_codeHash == null) return;
            Directory.CreateDirectory(MapSerializer.MapsDir);
            if (!Compile()) return;
            if (!IsTrusted()) File.AppendAllText(TrustPath, _codeHash + Environment.NewLine);
            foreach (var obj in placed) OnPlaced(obj);
        }

        private bool Compile()
        {
            var scripts = _assets.Where(a => a.Script != null).ToList();
            if (scripts.Count == 0) return false;
            var trees = scripts.Select(a => CSharpSyntaxTree.ParseText(a.Script, path: a.Name + ".cs")).ToArray();
            var references = new List<MetadataReference>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                try
                {
                    string location = assembly.Location;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location) && paths.Add(location))
                        references.Add(MetadataReference.CreateFromFile(location));
                }
                catch { }
            }
            var compilation = CSharpCompilation.Create("FIHMapCode_" + _codeHash.Substring(0, 16),
                trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            if (!result.Success)
            {
                foreach (var error in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(12))
                    MapEditorPlugin.Logger.LogError("[PACKAGE] " + error);
                return false;
            }
            var assemblyLoaded = Assembly.Load(output.ToArray());
            foreach (var asset in scripts)
            {
                var type = assemblyLoaded.GetTypes().FirstOrDefault(t =>
                    t.Name == asset.Name && typeof(IPackagedGeometryBehavior).IsAssignableFrom(t)
                    && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null);
                if (type == null)
                    MapEditorPlugin.Logger.LogWarning($"[PACKAGE] {asset.Name}.cs needs a public {asset.Name} class implementing IPackagedGeometryBehavior.");
                else _types[asset.Name] = type;
            }
            MapEditorPlugin.Logger.LogInfo($"[PACKAGE] Compiled {_types.Count} behavior(s) from {scripts.Count} C# file(s).");
            return _types.Count > 0;
        }

        public void OnPlaced(PlacedObject placed)
        {
            if (placed == null || !MapPackage.TryNameFromSource(placed.SourcePath, out string name)
                || !_types.TryGetValue(name, out var type)) return;
            try
            {
                var instance = new Instance
                {
                    Behavior = (IPackagedGeometryBehavior)type.GetConstructor(Type.EmptyTypes).Invoke(null),
                    Context = new PackagedGeometryContext(placed, name, _finder),
                };
                _instances[placed.Uid] = instance;
                instance.Behavior.OnSpawn(instance.Context);
                MapEditorPlugin.Logger.LogInfo($"[PACKAGE] Started '{name}' behavior on object {placed.Uid}.");
            }
            catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[PACKAGE:{name}] OnSpawn failed: {ex}"); }
        }

        public void OnRemoved(PlacedObject placed)
        {
            if (placed == null || !_instances.TryGetValue(placed.Uid, out var instance)) return;
            _instances.Remove(placed.Uid);
            try { instance.Behavior.OnDespawn(instance.Context); }
            catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[PACKAGE:{instance.Context.Name}] OnDespawn failed: {ex}"); }
        }

        public void Update(float deltaTime)
        {
            foreach (var instance in _instances.Values.ToArray())
            {
                if (instance.Context.Root == null) continue;
                try { instance.Behavior.Update(instance.Context, deltaTime); }
                catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[PACKAGE:{instance.Context.Name}] Update failed: {ex}"); }
            }
        }

        private void ClearInstances()
        {
            foreach (var instance in _instances.Values)
                try { instance.Behavior.OnDespawn(instance.Context); } catch { }
            _instances.Clear();
        }
    }
}
