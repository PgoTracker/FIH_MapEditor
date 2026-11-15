using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FIHMapEditor
{
    // Unity adapter for the portable OBJ data in MapPackage. Templates are inactive,
    // cloned by the ordinary placement path, and discarded on a map change.
    public sealed class PackageGeometryRuntime
    {
        private readonly Dictionary<string, MapPackageAsset> _assets =
            new Dictionary<string, MapPackageAsset>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GameObject> _templates =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();

        public IEnumerable<string> Names => _assets.Keys;

        public void SetAssets(IEnumerable<MapPackageAsset> assets)
        {
            foreach (var template in _templates.Values)
                if (template != null) UnityEngine.Object.Destroy(template);
            foreach (var mesh in _meshes)
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
            foreach (var material in _materials)
                if (material != null) UnityEngine.Object.Destroy(material);
            foreach (var texture in _textures)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            _templates.Clear(); _meshes.Clear(); _materials.Clear(); _textures.Clear(); _assets.Clear();
            foreach (var asset in assets ?? Array.Empty<MapPackageAsset>())
                _assets[asset.Name] = asset;
        }

        public GameObject Resolve(string source)
        {
            if (!MapPackage.TryNameFromSource(source, out string name)
                || !_assets.TryGetValue(name, out var asset)) return null;
            if (_templates.TryGetValue(name, out var template) && template != null) return template;
            try
            {
                var obj = ObjGeometry.Parse(asset.Geometry);
                var mesh = new Mesh { name = name };
                mesh.indexFormat = IndexFormat.UInt32;
                var verts = new Vector3[obj.Vertices.Length / 3];
                var normals = new Vector3[verts.Length];
                var uv = new Vector2[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    verts[i] = new Vector3(obj.Vertices[i * 3], obj.Vertices[i * 3 + 1], obj.Vertices[i * 3 + 2]);
                    normals[i] = new Vector3(obj.Normals[i * 3], obj.Normals[i * 3 + 1], obj.Normals[i * 3 + 2]);
                    uv[i] = new Vector2(obj.Uvs[i * 2], obj.Uvs[i * 2 + 1]);
                }
                mesh.vertices = verts;
                mesh.triangles = obj.Triangles;
                mesh.uv = uv;
                if (obj.HasNormals) mesh.normals = normals;
                else mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                _meshes.Add(mesh);

                template = new GameObject("Package_" + name);
                template.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(template);
                template.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = template.AddComponent<MeshRenderer>();
                var sourceMaterial = FindVisibleOpaqueMaterial();
                var shader = sourceMaterial != null ? sourceMaterial.shader : FindTextureShader();
                if (shader != null && shader.isSupported)
                {
                    // A shader found by name may be stripped of the variant this
                    // player build uses. Clone a material that is visibly rendering
                    // in the level, then replace every common texture slot.
                    var material = sourceMaterial != null ? new Material(sourceMaterial) : new Material(shader);
                    _materials.Add(material);
                    MapEditorPlugin.Logger.LogInfo($"[PACKAGE] '{name}' uses visible shader '{shader.name}' from '{sourceMaterial?.name ?? "shader fallback"}' (PNG bytes: {asset.Texture?.Length ?? 0}).");
                    if (asset.Texture != null)
                    {
                        var decoded = PngTexture.Decode(asset.Texture);
                        var texture = new Texture2D(decoded.Width, decoded.Height, TextureFormat.RGBA32, false);
                        texture.LoadRawTextureData(
                            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(decoded.Rgba));
                        texture.Apply(false, true);
                        texture.name = name + "_MapTexture";
                        texture.filterMode = FilterMode.Bilinear;
                        texture.wrapMode = TextureWrapMode.Repeat;
                        _textures.Add(texture);
                        foreach (string slot in new[]
                        {
                            "_BumpMap", "_NormalMap", "_MetallicGlossMap", "_OcclusionMap",
                            "_EmissionMap", "_DetailAlbedoMap", "_DetailNormalMap", "_MaskMap", "_HeightMap"
                        })
                            if (material.HasProperty(slot)) material.SetTexture(slot, null);
                        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                        material.mainTextureScale = Vector2.one;
                        material.mainTextureOffset = Vector2.zero;
                        MapEditorPlugin.Logger.LogInfo($"[PACKAGE] Loaded texture '{name}' ({texture.width}x{texture.height}).");
                    }
                    renderer.sharedMaterial = material;
                }
                else MapEditorPlugin.Logger.LogWarning($"[PACKAGE] No compatible shader found for '{name}'; texture cannot render.");
                template.AddComponent<MeshCollider>().sharedMesh = mesh;
                _templates[name] = template;
                MapEditorPlugin.Logger.LogInfo($"[PACKAGE] Loaded OBJ '{name}' ({verts.Length} vertices, {obj.Triangles.Length / 3} triangles).");
                return template;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PACKAGE] Failed to load OBJ '{name}': {ex.Message}");
                return null;
            }
        }

        private static Material FindVisibleOpaqueMaterial()
        {
            Material fallback = null;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Material candidate;
                try { candidate = renderer.sharedMaterial; }
                catch { continue; }
                if (candidate == null || candidate.shader == null || !candidate.shader.isSupported
                    || candidate.renderQueue > 2500
                    || (!candidate.HasProperty("_BaseMap") && !candidate.HasProperty("_MainTex"))) continue;
                string shaderName = candidate.shader.name ?? "";
                if (shaderName.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0
                    && shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) < 0)
                    return candidate;
                fallback ??= candidate;
            }
            return fallback;
        }

        private static Shader FindTextureShader()
        {
            foreach (string name in new[]
            {
                "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit",
                "Standard", "Legacy Shaders/Diffuse", "Unlit/Texture"
            })
            {
                var shader = Shader.Find(name);
                if (shader != null) return shader;
            }

            // Player builds strip shaders that Shader.Find cannot locate by name.
            // A material already loaded by the level still holds a usable shader.
            Shader fallback = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null || !material.shader.isSupported
                    || (!material.HasProperty("_BaseMap") && !material.HasProperty("_MainTex"))) continue;
                string name = material.shader.name ?? "";
                if (name.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0
                    && name.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) < 0)
                    return material.shader;
                if (fallback == null && material.renderQueue <= 2500) fallback = material.shader;
            }
            return fallback;
        }
    }
}
