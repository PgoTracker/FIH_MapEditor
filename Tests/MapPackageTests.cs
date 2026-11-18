using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace FIHMapEditor.Tests
{
    public class MapPackageTests
    {
        private const string Quad = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n";
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DA8J+BkYHh////DAAe9gT9Ce00PgAAAABJRU5ErkJggg==");
        private static readonly byte[] TinyWav = Encoding.ASCII.GetBytes("RIFF----WAVEfmt ");

        [Fact]
        public void RoundTripPairsFilesAndPreservesMapReference()
        {
            string path = TempPath();
            try
            {
                var map = new MapFile { Name = "Test package" };
                map.Objects.Add(new MapObjectData { Source = "package://Ramp", SourceName = "Ramp" });
                map.PackageAssets.Add(new MapPackageAsset
                {
                    Name = "Ramp", Geometry = Encoding.UTF8.GetBytes(Quad),
                    Script = "public class Ramp { }", Texture = TinyPng
                });
                MapPackage.Write(path, map);
                var loaded = MapPackage.Read(path);
                Assert.Equal("Test package", loaded.Name);
                Assert.Equal("package://Ramp", loaded.Objects[0].Source);
                Assert.Equal("Ramp", loaded.PackageAssets.Single().Name);
                Assert.Equal(Quad, Encoding.UTF8.GetString(loaded.PackageAssets.Single().Geometry));
                Assert.Equal(TinyPng, loaded.PackageAssets.Single().Texture);
                Assert.NotNull(loaded.PackageCodeHash);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RejectsTraversalAndMissingGeometry()
        {
            string path = TempPath();
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Write(zip, MapPackage.MapEntry, "{}");
                    Write(zip, "scripts/Ramp.cs", "class Ramp { }");
                }
                Assert.Throws<InvalidDataException>(() => MapPackage.Read(path));
                File.Delete(path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Write(zip, MapPackage.MapEntry, "{}");
                    Write(zip, "geometry/../Ramp.obj", Quad);
                }
                Assert.Throws<InvalidDataException>(() => MapPackage.Read(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ObjTriangulatesQuadAndResolvesNegativeIndices()
        {
            var mesh = ObjGeometry.Parse(Encoding.UTF8.GetBytes(Quad + "f -4 -3 -2\n"));
            Assert.Equal(9, mesh.Triangles.Length);
            Assert.Equal(4, mesh.Vertices.Length / 3);
            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, mesh.Triangles.Take(6));
        }

        [Fact]
        public void RejectsTextureWithoutGeometryAndInvalidPng()
        {
            string path = TempPath();
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Write(zip, MapPackage.MapEntry, "{}");
                    var entry = zip.CreateEntry("textures/Ramp.png");
                    using var stream = entry.Open();
                    stream.Write(TinyPng, 0, TinyPng.Length);
                }
                Assert.Throws<InvalidDataException>(() => MapPackage.Read(path));
                var map = new MapFile { Name = "Bad texture" };
                map.PackageAssets.Add(new MapPackageAsset
                {
                    Name = "Ramp", Geometry = Encoding.UTF8.GetBytes(Quad),
                    Texture = Encoding.UTF8.GetBytes("not a png")
                });
                Assert.Throws<InvalidDataException>(() => MapPackage.Write(path, map));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RoundTripsMatchedImpactSoundAndRejectsInvalidAudio()
        {
            string path = TempPath();
            try
            {
                var map = new MapFile { Name = "Impact test" };
                map.Objects.Add(new MapObjectData { Source = "package://Ramp" });
                map.PackageAssets.Add(new MapPackageAsset
                {
                    Name = "Ramp", Geometry = Encoding.UTF8.GetBytes(Quad),
                    ImpactSound = TinyWav, ImpactSoundExtension = ".wav"
                });
                MapPackage.Write(path, map);
                var loadedMap = MapPackage.Read(path);
                var loaded = loadedMap.PackageAssets.Single();
                Assert.Equal(TinyWav, loaded.ImpactSound);
                Assert.Equal(".wav", loaded.ImpactSoundExtension);
                loaded.ImpactSound = Encoding.UTF8.GetBytes("not audio data");
                Assert.Throws<InvalidDataException>(() => MapPackage.Write(path, loadedMap));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DecodesPngToUnityRgbaRows()
        {
            var image = PngTexture.Decode(TinyPng);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(new byte[]
            {
                0, 0, 255, 255, 255, 255, 255, 255,
                255, 0, 0, 255, 0, 255, 0, 255
            }, image.Rgba);
        }

        private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + MapPackage.Extension);

        private static void Write(ZipArchive zip, string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
    }
}
