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

    // Optional physics-step callback for packaged moving platforms. Existing map
    // scripts continue to work without implementing this interface.
    public interface IPackagedGeometryFixedBehavior : IPackagedGeometryBehavior
    {
        void FixedUpdate(PackagedGeometryContext context, float fixedDeltaTime);
    }

    public sealed class PackagedGeometryContext
    {
        private readonly GameObjectFinder _finder;
        private readonly MapPackageAsset _asset;
        private readonly MultiplayerSync _impactSync;
        private readonly List<(Collider collider, PhysicsMaterial material)> _originalPhysics =
            new List<(Collider, PhysicsMaterial)>();
        private PhysicsMaterial _physicsMaterial;
        private PackagedImpactSound _impactSound;
        public string Uid { get; }
        public string Name { get; }
        public GameObject Root { get; }
        public GameObject LocalPlayer => _finder?.FindPlayer();
        public PackagedGeometryContext(PlacedObject placed, string name, GameObjectFinder finder,
            MapPackageAsset asset = null, MultiplayerSync impactSync = null)
        {
            Uid = placed.Uid; Name = name; Root = placed.Root; _finder = finder;
            _asset = asset; _impactSync = impactSync;
        }

        // Ground settings also select the game's matching landing/impact sound.
        // The Ground component is attached per packaged placement before registration.
        public void SetGroundSurface(PackagedGroundSurface surface)
            => PackageGroundSettings.Surface(Root, surface);

        public void SetGroundSpinVelocity(PackagedSpinVelocity spin)
            => PackageGroundSettings.Spin(Root, spin);

        public void SetPhoneDestroyMode(PackagedPhoneDestroy mode)
            => PackageGroundSettings.PhoneDestroy(Root, mode);

        // PhysicsMaterial changes collision friction and bounce, separate from the
        // game's Ground surface category. Values follow Unity's 0..1 range.
        public void SetColliderPhysics(float staticFriction, float dynamicFriction, float bounciness)
        {
            if (Root == null) throw new InvalidOperationException("Packaged object was destroyed.");
            foreach (float value in new[] { staticFriction, dynamicFriction, bounciness })
                if (float.IsNaN(value) || value < 0f || value > 1f)
                    throw new ArgumentOutOfRangeException(nameof(staticFriction), "Friction and bounce must be between 0 and 1.");
            var previous = _physicsMaterial;
            var material = new PhysicsMaterial("FIH " + Name + " " + Uid)
            {
                staticFriction = staticFriction,
                dynamicFriction = dynamicFriction,
                bounciness = bounciness,
            };
            foreach (var collider in Root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.isTrigger) continue;
                if (_originalPhysics.TrueForAll(entry => entry.collider != collider))
                    _originalPhysics.Add((collider, collider.sharedMaterial));
                collider.sharedMaterial = material;
            }
            _physicsMaterial = material;
            if (previous != null) UnityEngine.Object.Destroy(previous);
        }

        // The package may contain sounds/<asset>.wav or .ogg. Scripts opt in so a
        // map can use the game's surface sound, its own sound, or both.
        public bool EnablePackagedImpactSound(float minImpulse = 1f, float cooldown = 0.15f,
            float volume = 1f, bool playerOnly = true)
        {
            if (_asset?.ImpactSound == null || Root == null) return false;
            if (minImpulse < 0f || cooldown < 0f || volume < 0f || volume > 1f)
                throw new ArgumentOutOfRangeException(nameof(minImpulse));
            PackagedImpactSound.EnsureRegistered();
            _impactSound = Root.GetComponent<PackagedImpactSound>() ?? Root.AddComponent<PackagedImpactSound>();
            _impactSound.Configure(_asset.ImpactSound, _asset.ImpactSoundExtension,
                                   Uid, _finder, _impactSync, minImpulse, cooldown, volume,
                                   playerOnly);
            return true;
        }

        internal void PlayRemoteImpact(string soundHash, Vector3 point)
            => _impactSound?.PlayRemote(soundHash, point);

        internal void Cleanup()
        {
            if (_impactSound != null) UnityEngine.Object.Destroy(_impactSound);
            _impactSound = null;
            foreach (var (collider, material) in _originalPhysics)
                if (collider != null) collider.sharedMaterial = material;
            _originalPhysics.Clear();
            if (_physicsMaterial != null) UnityEngine.Object.Destroy(_physicsMaterial);
            _physicsMaterial = null;
        }
    }

    public sealed class PackageBehaviorHost
    {
        private readonly GameObjectFinder _finder;
        public MultiplayerSync ImpactSync { get; set; }
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
                    Context = new PackagedGeometryContext(placed, name, _finder,
                        _assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)),
                        ImpactSync),
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
            finally { instance.Context.Cleanup(); }
        }

        public void PlayRemoteImpact(string uid, string soundHash, Vector3 point)
        {
            if (uid != null && _instances.TryGetValue(uid, out var instance))
                instance.Context.PlayRemoteImpact(soundHash, point);
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

        public void FixedUpdate(float fixedDeltaTime)
        {
            foreach (var instance in _instances.Values.ToArray())
            {
                if (instance.Context.Root == null ||
                    instance.Behavior is not IPackagedGeometryFixedBehavior fixedBehavior) continue;
                try { fixedBehavior.FixedUpdate(instance.Context, fixedDeltaTime); }
                catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[PACKAGE:{instance.Context.Name}] FixedUpdate failed: {ex}"); }
            }
        }

        private void ClearInstances()
        {
            foreach (var instance in _instances.Values)
            {
                try { instance.Behavior.OnDespawn(instance.Context); } catch { }
                instance.Context.Cleanup();
            }
            _instances.Clear();
        }
    }
}
