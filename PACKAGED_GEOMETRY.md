# Packaged geometry (local maps)

The editor accepts `.fihmap.zip` files in `BepInEx/plugins/FIHMapEditor/Maps/`.
Each archive contains a `map.fihmap.json`, OBJ files under `geometry/`, and optional
C# source files under `scripts/` and PNG textures under `textures/`. Files with the
same stem belong together, for example `geometry/Spinner.obj`,
`scripts/Spinner.cs`, and `textures/Spinner.png`.

## Make a package

To try the included cube, copy
`Examples/PackagedGeometry/PackagedGeometryExample.fihmap.zip` into the game's
`BepInEx/plugins/FIHMapEditor/Maps/` folder. Loading it places one **Spinner**
cube. Additional copies are available in **Custom geometry**. The matching
`map.fihmap.json`, `geometry`, `scripts`, and `textures` files are included for editing.

To make your own package:

1. Save a map normally from the editor to create its `.fihmap.json`.
2. Export meshes as OBJ files. Names must start with a letter or `_` and then use
   letters, digits, or `_`, so a matching C# class can use the same name.
3. Put optional matching `.cs` files in a scripts folder and matching `.png` files
   in a textures folder. Each textured OBJ needs UV coordinates (`vt`).
4. From the repository root, run:

```powershell
.\Tools\Pack-Map.ps1 `
  -MapPath 'C:\path\to\Maps\MyMap.fihmap.json' `
  -GeometryDir 'C:\path\to\my-objs' `
  -ScriptsDir 'C:\path\to\my-scripts' `
  -TexturesDir 'C:\path\to\my-textures' `
  -OutputPath 'C:\path\to\Maps\MyMap.fihmap.zip'
```

Refresh the Maps Hub and load the package. The OBJ files appear in the
**Custom geometry** catalog category. Place them like ordinary objects; saving the
map keeps the geometry, textures, and source code in the same archive. To refer to one in
map JSON directly, set `Source` to `package://Spinner` and `SourceName` to
`Spinner`.

An OBJ can contain vertices (`v`), UVs (`vt`), normals (`vn`), and polygon faces
(`f`). Polygon faces are triangulated. A matching `textures/Name.png` is applied
to the mesh using its OBJ UVs; PNGs must be non-interlaced 8-bit RGB or RGBA
and are limited to 2048 × 2048 pixels and 8 MB.
Material libraries (`.mtl`) are not imported. Meshes use a lit shader and a
MeshCollider. The included Spinner texture is newly generated artwork in the
example package, independent of the game's assets.

## Add C# behavior

The class name must match the OBJ and C# filenames. It implements
`IPackagedGeometryBehavior` and has a public parameterless constructor:

```csharp
using FIHMapEditor;
using UnityEngine;

public class Spinner : IPackagedGeometryBehavior
{
    public void OnSpawn(PackagedGeometryContext context) { }

    public void Update(PackagedGeometryContext context, float deltaTime)
    {
        context.Root.transform.Rotate(0f, 45f * deltaTime, 0f);
    }

    public void OnDespawn(PackagedGeometryContext context) { }
}
```

`context.LocalPlayer` gives a script the local player's GameObject for interactions.
The included Spinner script uses it to carry and turn a player who is standing on
the cube. This behavior runs locally for each player who has the package installed.

The editor compiles matching scripts together when you click **ENABLE MAP C#** in
the local Maps Hub. This trusts the exact script contents and remembers their hash
in `Maps/trusted-code-hashes.txt`. C# runs with the same permissions as the mod,
so enable code only from authors you trust. Geometry loads without enabling code.
Compilation errors are written to the BepInEx log.
`Update` runs during Play and while the map is active outside the editor; it pauses
while editing so moving objects can be positioned without their script fighting the
gizmo.

## Current scope

Local file sharing works by sending the `.fihmap.zip` file. The online map library
currently stores JSON only, so it rejects uploading maps with packaged geometry.
Multiplayer object sync sends object transforms but not package bytes; each player
needs to install and load the same package before co-editing. The mod is for BepInEx 6 IL2CPP, and the
runtime script host uses Roslyn 4.3.1 so its dependencies match the bundled
.NET 6 runtime. Deploy `Microsoft.CodeAnalysis.dll` and
`Microsoft.CodeAnalysis.CSharp.dll` beside `FIHMapEditor.dll`; `build.bat`
copies them.
