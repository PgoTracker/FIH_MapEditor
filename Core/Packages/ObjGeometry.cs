using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FIHMapEditor
{
    public sealed class ObjGeometry
    {
        public float[] Vertices { get; private set; }
        public float[] Normals { get; private set; }
        public float[] Uvs { get; private set; }
        public int[] Triangles { get; private set; }
        public bool HasNormals { get; private set; }

        public static ObjGeometry Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MapPackage.MaxGeometryBytes)
                throw new InvalidDataException("Invalid OBJ size.");
            var positions = new List<float[]>();
            var normals = new List<float[]>();
            var uvs = new List<float[]>();
            var outputPositions = new List<float>();
            var outputNormals = new List<float>();
            var outputUvs = new List<float>();
            var triangles = new List<int>();
            var unique = new Dictionary<string, int>(StringComparer.Ordinal);
            bool hasNormals = true;
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment).Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                switch (parts[0])
                {
                    case "v":
                        positions.Add(ParseVector(parts, 3));
                        break;
                    case "vn":
                        normals.Add(ParseVector(parts, 3));
                        break;
                    case "vt":
                        uvs.Add(ParseVector(parts, 2));
                        break;
                    case "f":
                        if (parts.Length < 4) throw new InvalidDataException("OBJ face needs at least three vertices.");
                        int[] face = new int[parts.Length - 1];
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var token = parts[i];
                            var fields = token.Split('/');
                            int pi = Index(fields[0], positions.Count);
                            int ti = fields.Length > 1 && fields[1].Length > 0 ? Index(fields[1], uvs.Count) : -1;
                            int ni = fields.Length > 2 && fields[2].Length > 0 ? Index(fields[2], normals.Count) : -1;
                            string key = pi + "/" + ti + "/" + ni;
                            if (!unique.TryGetValue(key, out int index))
                            {
                                index = outputPositions.Count / 3;
                                if (index >= 250000) throw new InvalidDataException("OBJ has too many vertices.");
                                unique.Add(key, index);
                                outputPositions.AddRange(positions[pi]);
                                if (ti >= 0) outputUvs.AddRange(uvs[ti]);
                                else { outputUvs.Add(0); outputUvs.Add(0); }
                                if (ni >= 0) outputNormals.AddRange(normals[ni]);
                                else { outputNormals.Add(0); outputNormals.Add(0); outputNormals.Add(0); hasNormals = false; }
                            }
                            face[i - 1] = index;
                        }
                        for (int i = 1; i < face.Length - 1; i++)
                        {
                            if (triangles.Count >= 1500000) throw new InvalidDataException("OBJ has too many faces.");
                            triangles.Add(face[0]); triangles.Add(face[i]); triangles.Add(face[i + 1]);
                        }
                        break;
                }
            }
            if (triangles.Count == 0) throw new InvalidDataException("OBJ contains no faces.");
            return new ObjGeometry
            {
                Vertices = outputPositions.ToArray(), Normals = outputNormals.ToArray(),
                Uvs = outputUvs.ToArray(), Triangles = triangles.ToArray(), HasNormals = hasNormals
            };
        }

        private static float[] ParseVector(string[] parts, int count)
        {
            if (parts.Length < count + 1) throw new InvalidDataException("Invalid OBJ vector.");
            var result = new float[count];
            for (int i = 0; i < count; i++)
                if (!float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])
                    || float.IsNaN(result[i]) || float.IsInfinity(result[i]))
                    throw new InvalidDataException("Invalid OBJ coordinate.");
            return result;
        }

        private static int Index(string token, int count)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed == 0)
                throw new InvalidDataException("Invalid OBJ index.");
            int index = parsed > 0 ? parsed - 1 : count + parsed;
            if (index < 0 || index >= count) throw new InvalidDataException("OBJ index outside its source array.");
            return index;
        }
    }
}
