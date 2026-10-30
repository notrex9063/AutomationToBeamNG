// Automation's engine parts read straight from its game files (see AutomationPak.cs): static
// meshes with their sections, materials and sockets, textures as .dds, and a simplified
// BeamNG material for each of Automation's layered engine materials. A part (or several placed
// parts) is written as a BeamNG-ready .dae + .materials.json + textures.
//
// Units and axes: Unreal is left-handed, Z up, centimetres. Everything here is converted on
// load to right-handed, Z up, metres by mirroring Y, so x stays forward along the crank.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AutomationToBeamNG
{
    class MeshSection { public int Material, FirstIndex, NumTriangles; }
    class MeshSocket { public string Name; public double[] Location = new double[3], Rotation = new double[3], Scale = { 1, 1, 1 }; }

    class UMesh
    {
        public string Name, Path;
        public int NumVertices;
        public float[] Pos;         // metres, BeamNG axes
        public float[] Normal;
        public float[] UV;          // first UV channel, V flipped for Collada
        public int[] Indices;       // counter-clockwise in BeamNG axes
        public List<MeshSection> Sections = new List<MeshSection>();
        public List<string> Materials = new List<string>();      // object paths, by material slot
        public List<string> SlotNames = new List<string>();
        public List<MeshSocket> Sockets = new List<MeshSocket>();

        public double[] Bounds()
        {
            double[] b = { 1e9, 1e9, 1e9, -1e9, -1e9, -1e9 };
            for (int i = 0; i < NumVertices; i++)
                for (int k = 0; k < 3; k++) { b[k] = Math.Min(b[k], Pos[i * 3 + k]); b[k + 3] = Math.Max(b[k + 3], Pos[i * 3 + k]); }
            return b;
        }
    }

    // What a BeamNG material needs from one of Automation's.
    class PartMaterial
    {
        public string Path, Name;
        public double[] Color = { 0.5, 0.5, 0.5, 1 };
        public double Roughness = 0.6, Metallic = 0.5;
        public string NormalTexture, ColorTexture;      // object paths
        public bool Paintable, Translucent;
        public bool Skip;       // Automation leaves it out of its own exports (hidden faces, placeholders)
    }

    static class AutomationParts
    {
        const float CmToM = 0.01f;

        // ---------------------------------------------------------------- static meshes

        public static UMesh LoadMesh(PakArchive pak, string objectPath)
        {
            var pkg = UPackage.Load(pak, objectPath);
            if (pkg == null) throw new FileNotFoundException("no such part: " + objectPath);
            var exp = pkg.FirstExport("StaticMesh");
            if (exp == null)
            {
                string cls = pkg.Exports.Count > 0 ? pkg.ClassOf(pkg.Exports.Last()) : "nothing";
                throw new NotSupportedException(objectPath + " is a " + cls + ", not a static mesh");
            }
            var mesh = new UMesh { Name = exp.Name, Path = objectPath };
            var props = pkg.Properties(exp);

            object o;
            if (props.TryGetValue("StaticMaterials", out o) && o is List<object>)
                foreach (var sm in ((List<object>)o).OfType<Dictionary<string, object>>())
                {
                    object mi, slot;
                    sm.TryGetValue("MaterialInterface", out mi);
                    sm.TryGetValue("MaterialSlotName", out slot);
                    mesh.Materials.Add(mi is UObjectRef ? pkg.ImportPath(((UObjectRef)mi).Index) : null);
                    mesh.SlotNames.Add(slot as string ?? ("slot" + mesh.SlotNames.Count));
                }
            if (props.TryGetValue("Sockets", out o) && o is List<object>)
                foreach (var sr in ((List<object>)o).OfType<UObjectRef>())
                {
                    if (sr.Index <= 0) continue;
                    var sp = pkg.Properties(pkg.Exports[sr.Index - 1]);
                    var s = new MeshSocket { Name = sp.ContainsKey("SocketName") ? sp["SocketName"] as string : pkg.Exports[sr.Index - 1].Name };
                    object v;
                    if (sp.TryGetValue("RelativeLocation", out v) && v is float[]) { var f = (float[])v; s.Location = new double[] { f[0] * CmToM, -f[1] * CmToM, f[2] * CmToM }; }
                    if (sp.TryGetValue("RelativeRotation", out v) && v is float[]) { var f = (float[])v; s.Rotation = new double[] { f[0], f[1], f[2] }; }   // pitch, yaw, roll in Unreal's degrees
                    if (sp.TryGetValue("RelativeScale", out v) && v is float[]) { var f = (float[])v; s.Scale = new double[] { f[0], f[1], f[2] }; }
                    mesh.Sockets.Add(s);
                }

            ReadRenderData(pkg, exp, mesh);
            return mesh;
        }

        // The cooked render data follows the tagged properties. Rather than walking every field of
        // UStaticMesh::Serialize, find LOD 0's position buffer (stride 12, count N, element size 12,
        // count N) and read the buffers forward from there; the LOD's sections sit just in front of it.
        static void ReadRenderData(UPackage pkg, UPackage.Export exp, UMesh mesh)
        {
            var d = pkg.Data;
            long end = Math.Min(d.Length, exp.SerialOffset + exp.SerialSize);
            int at = -1, nv = 0;
            for (long i = exp.SerialOffset; i + 16 <= end; i++)
            {
                if (BitConverter.ToInt32(d, (int)i) != 12 || BitConverter.ToInt32(d, (int)i + 8) != 12) continue;
                int n = BitConverter.ToInt32(d, (int)i + 4);
                if (n < 3 || n > 4000000 || BitConverter.ToInt32(d, (int)i + 12) != n || i + 16 + 12L * n > end) continue;
                if (FindSections(d, (int)i, n, mesh)) { at = (int)i; nv = n; break; }
            }
            if (at < 0) throw new InvalidDataException(mesh.Name + ": no geometry found (the part may be streamed or a skeletal mesh)");

            var r = new BinaryReader(new MemoryStream(d));
            r.BaseStream.Position = at + 16;
            mesh.NumVertices = nv;
            mesh.Pos = new float[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                mesh.Pos[i * 3] = r.ReadSingle() * CmToM;
                mesh.Pos[i * 3 + 1] = -r.ReadSingle() * CmToM;
                mesh.Pos[i * 3 + 2] = r.ReadSingle() * CmToM;
            }

            // FStaticMeshVertexBuffer
            r.ReadUInt16();     // strip flags
            int numUV = r.ReadInt32();
            if (r.ReadInt32() != nv || numUV < 0 || numUV > 8) throw new InvalidDataException(mesh.Name + ": unexpected vertex buffer layout");
            bool fullUV = r.ReadInt32() != 0, highTangents = r.ReadInt32() != 0;
            int tSize = r.ReadInt32(), tCount = r.ReadInt32();
            long tStart = r.BaseStream.Position;
            mesh.Normal = new float[nv * 3];
            for (int i = 0; i < nv && i < tCount; i++)
            {
                r.BaseStream.Position = tStart + (long)i * tSize + tSize / 2;     // TangentZ, the normal, is the second half
                float x, y, z;
                if (highTangents) { x = r.ReadInt16() / 32767f; y = r.ReadInt16() / 32767f; z = r.ReadInt16() / 32767f; }
                else { x = (sbyte)r.ReadByte() / 127f; y = (sbyte)r.ReadByte() / 127f; z = (sbyte)r.ReadByte() / 127f; }
                float len = (float)Math.Sqrt(x * x + y * y + z * z);
                if (len < 1e-4f) { x = 0; y = 0; z = 1; len = 1; }
                mesh.Normal[i * 3] = x / len; mesh.Normal[i * 3 + 1] = -y / len; mesh.Normal[i * 3 + 2] = z / len;
            }
            r.BaseStream.Position = tStart + (long)tSize * tCount;
            int uSize = r.ReadInt32(), uCount = r.ReadInt32();
            long uStart = r.BaseStream.Position;
            mesh.UV = new float[nv * 2];
            if (numUV > 0)
                for (int i = 0; i < nv; i++)
                {
                    r.BaseStream.Position = uStart + (long)i * numUV * (fullUV ? 8 : 4);
                    float u, v;
                    if (fullUV) { u = r.ReadSingle(); v = r.ReadSingle(); }
                    else { u = Half(r.ReadUInt16()); v = Half(r.ReadUInt16()); }
                    mesh.UV[i * 2] = u; mesh.UV[i * 2 + 1] = 1 - v;
                }
            r.BaseStream.Position = uStart + (long)uSize * uCount;

            // FColorVertexBuffer
            r.ReadUInt16();
            r.ReadInt32();      // stride
            if (r.ReadInt32() > 0) { int cs = r.ReadInt32(), cc = r.ReadInt32(); r.BaseStream.Position += (long)cs * cc; }

            // FRawStaticIndexBuffer
            bool is32 = r.ReadInt32() != 0;
            r.ReadInt32();      // element size (1)
            int bytes = r.ReadInt32();
            int ni = bytes / (is32 ? 4 : 2);
            mesh.Indices = new int[ni];
            for (int i = 0; i < ni; i++) mesh.Indices[i] = is32 ? r.ReadInt32() : r.ReadUInt16();
            for (int i = 0; i + 2 < ni; i += 3) { int t = mesh.Indices[i + 1]; mesh.Indices[i + 1] = mesh.Indices[i + 2]; mesh.Indices[i + 2] = t; }   // mirroring Y flips the winding
            foreach (var s in mesh.Sections)
                if (s.FirstIndex + s.NumTriangles * 3 > ni) throw new InvalidDataException(mesh.Name + ": sections don't match the index buffer");
        }

        // In front of the position buffer: sections array, max deviation (float), "cooked out" and
        // "inlined" flags (int32 each) and the buffers' strip flags (2 bytes). The size of one section
        // record has changed between engine versions, so try the plausible ones.
        static bool FindSections(byte[] d, int posAt, int nv, UMesh mesh)
        {
            int sectEnd = posAt - 2 - 4 - 4 - 4;
            if (sectEnd < 8 || BitConverter.ToInt32(d, posAt - 6) != 1) return false;     // must be inlined
            foreach (int size in new[] { 36, 32, 40, 28, 44 })
                for (int n = 1; n <= 64; n++)
                {
                    int start = sectEnd - n * size;
                    if (start < 4) break;
                    if (BitConverter.ToInt32(d, start - 4) != n) continue;
                    var list = new List<MeshSection>();
                    bool ok = true;
                    int next = 0;     // sections cover the index buffer in order, starting at 0
                    for (int i = 0; i < n && ok; i++)
                    {
                        int p = start + i * size;
                        var s = new MeshSection { Material = BitConverter.ToInt32(d, p), FirstIndex = BitConverter.ToInt32(d, p + 4), NumTriangles = BitConverter.ToInt32(d, p + 8) };
                        int minV = BitConverter.ToInt32(d, p + 12), maxV = BitConverter.ToInt32(d, p + 16);
                        ok = s.Material >= 0 && s.Material < 256 && s.FirstIndex == next && s.NumTriangles >= 0 && minV >= 0 && maxV < nv && minV <= maxV + 1;
                        next = s.FirstIndex + s.NumTriangles * 3;
                        list.Add(s);
                    }
                    if (!ok) continue;
                    mesh.Sections = list;
                    return true;
                }
            return false;
        }

        static float Half(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 0x1f, m = h & 0x3ff;
            float v = e == 0 ? m / 1024f * (float)Math.Pow(2, -14) : e == 31 ? float.NaN : (1 + m / 1024f) * (float)Math.Pow(2, e - 15);
            return s != 0 ? -v : v;
        }

        // ---------------------------------------------------------------- materials

        // Automation's engine materials are layered shaders (paint, dirt, heat glow, wear) that
        // BeamNG can't run, so each is reduced to a base colour, roughness, metalness and a normal
        // map, read from the instance's parameters (most specific first) and named defaults.
        public static PartMaterial LoadMaterial(PakArchive pak, string objectPath)
        {
            var m = new PartMaterial { Path = objectPath, Name = ShortName(objectPath) };
            var scalars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var referenced = new List<string>();
            var chain = new List<string>();
            Dictionary<string, object> recipe = null;      // Camshaft's ExportMaterialUserData, from the nearest material that has one
            string path = objectPath;
            for (int depth = 0; path != null && depth < 8; depth++)
            {
                chain.Add(ShortName(path));
                var pkg = UPackage.Load(pak, path);
                if (pkg == null) break;
                var exp = pkg.Exports.FirstOrDefault(x => pkg.ClassOf(x).StartsWith("Material"));
                if (exp == null) break;
                Dictionary<string, object> props;
                try { props = pkg.Properties(exp); } catch (Exception) { break; }
                // plain parameters override the layer stack's cached values; the instance overrides its parent
                CollectParams(pkg, props, scalars, vectors, textures);
                var ud = pkg.FirstExport("ExportMaterialUserData");
                if (ud != null && recipe == null) { try { recipe = pkg.Properties(ud); } catch (Exception) { } }
                object o;
                if (props.TryGetValue("BasePropertyOverrides", out o) && o is Dictionary<string, object>)
                {
                    object bm;
                    if (((Dictionary<string, object>)o).TryGetValue("BlendMode", out bm) && (bm as string ?? "").Contains("Translucent")) m.Translucent = true;
                }
                if (props.TryGetValue("BlendMode", out o) && (o as string ?? "").Contains("Translucent")) m.Translucent = true;
                if (props.TryGetValue("CachedReferencedTextures", out o) && o is List<object>)
                    foreach (var tr in ((List<object>)o).OfType<UObjectRef>()) { string tp = pkg.ImportPath(tr.Index); if (tp != null) referenced.Add(tp); }
                if (props.TryGetValue("ReferencedTextures", out o) && o is List<object>)
                    foreach (var tr in ((List<object>)o).OfType<UObjectRef>()) { string tp = pkg.ImportPath(tr.Index); if (tp != null) referenced.Add(tp); }
                if (props.TryGetValue("CachedExpressionData", out o) && o is Dictionary<string, object>)
                {
                    object rt;
                    if (((Dictionary<string, object>)o).TryGetValue("ReferencedTextures", out rt) && rt is List<object>)
                        foreach (var tr in ((List<object>)rt).OfType<UObjectRef>()) { string tp = pkg.ImportPath(tr.Index); if (tp != null) referenced.Add(tp); }
                }
                path = props.TryGetValue("Parent", out o) && o is UObjectRef ? pkg.ImportPath(((UObjectRef)o).Index) : null;
            }

            string all = string.Join(" ", chain).ToLowerInvariant();
            ApplyDefaults(m, all);
            double v;
            if (scalars.TryGetValue("IsEnginePaintable", out v) && v > 0.5) m.Paintable = true;

            if (recipe != null) ApplyRecipe(m, recipe, scalars, vectors, textures);
            else
            {
                var colour = vectors.Where(kv => IsColourParam(kv.Key)).Select(kv => kv.Value).FirstOrDefault();
                if (colour != null) m.Color = new double[] { colour[0], colour[1], colour[2], 1 };
                if (scalars.TryGetValue("Roughness", out v)) m.Roughness = v;
                else if (scalars.ContainsKey("Roughness Min") && scalars.ContainsKey("Roughness Max")) m.Roughness = (scalars["Roughness Min"] + scalars["Roughness Max"]) / 2;
                if (scalars.TryGetValue("Metallic", out v)) m.Metallic = v;
                else if (scalars.ContainsKey("Metallic Min") && scalars.ContainsKey("Metallic Max") && scalars["Metallic Max"] > 0) m.Metallic = (scalars["Metallic Min"] + scalars["Metallic Max"]) / 2;
                m.NormalTexture = textures.Where(kv => kv.Key.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 && kv.Key.IndexOf("grunge", StringComparison.OrdinalIgnoreCase) < 0).Select(kv => kv.Value).FirstOrDefault();
                m.ColorTexture = textures.Where(kv => Regex.IsMatch(kv.Key, "^texture$|base ?colou?r|albedo|diffuse", RegexOptions.IgnoreCase)).Select(kv => kv.Value).FirstOrDefault(t => !Regex.IsMatch(t, "/Engine/"));
            }
            // textures wired straight into the material graph rather than exposed as parameters
            if (m.NormalTexture == null)
                m.NormalTexture = referenced.FirstOrDefault(t => Regex.IsMatch(ShortName(t), "normal$|_n$", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, "/Engine/|8K|Default|Grunge", RegexOptions.IgnoreCase));
            if (m.ColorTexture == null && !m.Skip)
            {
                m.ColorTexture = referenced.FirstOrDefault(t => Regex.IsMatch(ShortName(t), "(basecolou?r|diffuse|albedo)$", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, "/Engine/"));
                if (m.ColorTexture != null && recipe == null) m.Color = new double[] { 1, 1, 1, 1 };
            }
            return m;
        }

        // Camshaft's own conversion recipe (what Automation's "Export to BeamNG" uses). Each entry
        // is { Override, ValueOverride, ParamName, ParamName2, ParamNameLerp }: a fixed value, one
        // parameter, or a blend of two parameters.
        static void ApplyRecipe(PartMaterial m, Dictionary<string, object> recipe, Dictionary<string, double> scalars, Dictionary<string, float[]> vectors, Dictionary<string, string> textures)
        {
            object o;
            if (recipe.TryGetValue("MaterialType", out o) && (o as string ?? "").Contains("DoNotExport")) m.Skip = true;

            Func<string, Dictionary<string, object>> entry = k => recipe.TryGetValue(k, out o) ? o as Dictionary<string, object> : null;
            Func<Dictionary<string, object>, string, string> name = (e, k) => { object n; return e.TryGetValue(k, out n) && n is string && (string)n != "None" ? Regex.Replace((string)n, "^editable_", "") : null; };
            Func<Dictionary<string, object>, bool> overridden = e => { object b; return e.TryGetValue("Override", out b) && b is bool && (bool)b; };

            var ce = entry("ColourParam");
            if (ce != null)
            {
                float[] c = null;
                object ov;
                if (overridden(ce)) c = ce.TryGetValue("ValueOverride", out ov) && ov is float[] ? (float[])ov : new float[] { 0, 0, 0, 1 };
                else
                {
                    string p1 = name(ce, "ParamName"), p2 = name(ce, "ParamName2"), pl = name(ce, "ParamNameLerp");
                    float[] a = p1 != null && vectors.ContainsKey(p1) ? vectors[p1] : null, b = p2 != null && vectors.ContainsKey(p2) ? vectors[p2] : null;
                    double t = pl != null && scalars.ContainsKey(pl) ? scalars[pl] : 0.5;
                    if (a != null && b != null) c = new[] { (float)(a[0] + (b[0] - a[0]) * t), (float)(a[1] + (b[1] - a[1]) * t), (float)(a[2] + (b[2] - a[2]) * t), 1f };
                    else c = a ?? b;
                    if (c == null && ce.TryGetValue("ValueOverride", out ov) && ov is float[]) c = (float[])ov;
                    if (p1 == "CarBaseColor") m.Paintable = true;
                }
                if (c != null)
                {
                    double mult = recipe.TryGetValue("MagicalMysteryMultiplier", out o) && o is float ? (float)o : 1;
                    m.Color = new[] { Math.Min(1, c[0] * mult), Math.Min(1, c[1] * mult), Math.Min(1, c[2] * mult), 1.0 };
                }
            }
            double val;
            if (Scalar(entry("RoughnessParam"), scalars, name, overridden, out val)) m.Roughness = val;
            if (Scalar(entry("MetallicParam"), scalars, name, overridden, out val)) m.Metallic = val;

            var ne = entry("NormalTextureParam");
            string np = ne != null ? name(ne, "ParamName") : null;
            if (np != null && textures.ContainsKey(np)) m.NormalTexture = textures[np];
            var de = entry("DiffuseTextureParam");
            string dp = de != null ? name(de, "ParamName") : null;
            if (dp != null && textures.ContainsKey(dp)) { m.ColorTexture = textures[dp]; if (ce == null || !overridden(ce)) m.Color = new double[] { 1, 1, 1, 1 }; }
        }

        static bool Scalar(Dictionary<string, object> e, Dictionary<string, double> scalars, Func<Dictionary<string, object>, string, string> name, Func<Dictionary<string, object>, bool> overridden, out double value)
        {
            value = 0;
            if (e == null) return false;
            object ov;
            if (overridden(e)) { value = e.TryGetValue("ValueOverride", out ov) && ov is float ? (float)ov : 0; return true; }
            string p1 = name(e, "ParamName"), p2 = name(e, "ParamName2"), pl = name(e, "ParamNameLerp");
            bool h1 = p1 != null && scalars.ContainsKey(p1), h2 = p2 != null && scalars.ContainsKey(p2);
            double t = pl != null && scalars.ContainsKey(pl) ? scalars[pl] : 0.5;
            if (h1 && h2) { value = scalars[p1] + (scalars[p2] - scalars[p1]) * t; return true; }
            if (h1) { value = scalars[p1]; return true; }
            if (h2) { value = scalars[p2]; return true; }
            if (e.TryGetValue("ValueOverride", out ov) && ov is float) { value = (float)ov; return true; }
            return false;
        }

        static bool IsColourParam(string name)
        {
            string n = name.ToLowerInvariant();
            if (!(n.Contains("colour") || n.Contains("color") || n.Contains("tint"))) return false;
            foreach (var bad in new[] { "dirt", "carbase", "speckle", "emissive", "glow", "heat", "rust", "grunge", "wear", "mask" })
                if (n.Contains(bad)) return false;
            return true;
        }

        static void CollectParams(UPackage pkg, Dictionary<string, object> props, Dictionary<string, double> scalars, Dictionary<string, float[]> vectors, Dictionary<string, string> textures)
        {
            object o;
            foreach (var key in new[] { "ScalarParameterValues", "VectorParameterValues", "TextureParameterValues" })
                if (props.TryGetValue(key, out o) && o is List<object>)
                    foreach (var pv in ((List<object>)o).OfType<Dictionary<string, object>>())
                    {
                        object info, val;
                        if (!pv.TryGetValue("ParameterInfo", out info) || !pv.TryGetValue("ParameterValue", out val) || !(info is Dictionary<string, object>)) continue;
                        string name = ((Dictionary<string, object>)info).ContainsKey("Name") ? ((Dictionary<string, object>)info)["Name"] as string : null;
                        if (name == null) continue;
                        AddParam(pkg, name, val, scalars, vectors, textures);
                    }
            // cached parameter tables: the layer stack's resolved values (instances) and a master
            // material's defaults (CachedExpressionData.Parameters)
            var tables = new List<Dictionary<string, object>>();
            if (props.TryGetValue("CachedLayerParameters", out o) && o is Dictionary<string, object>) tables.Add((Dictionary<string, object>)o);
            if (props.TryGetValue("CachedExpressionData", out o) && o is Dictionary<string, object>)
            {
                object pr;
                if (((Dictionary<string, object>)o).TryGetValue("Parameters", out pr) && pr is Dictionary<string, object>) tables.Add((Dictionary<string, object>)pr);
            }
            foreach (var c in tables)
            {
                string[] entries = { "RuntimeEntries", "RuntimeEntries[1]", "RuntimeEntries[2]" };
                string[] values = { "ScalarValues", "VectorValues", "TextureValues" };
                for (int k = 0; k < 3; k++)
                {
                    object e, vs;
                    if (!c.TryGetValue(entries[k], out e) || !c.TryGetValue(values[k], out vs) || !(e is Dictionary<string, object>) || !(vs is List<object>)) continue;
                    object infos;
                    if (!((Dictionary<string, object>)e).TryGetValue("ParameterInfos", out infos) || !(infos is List<object>)) continue;
                    var il = (List<object>)infos; var vl = (List<object>)vs;
                    for (int i = 0; i < il.Count && i < vl.Count; i++)
                    {
                        var info = il[i] as Dictionary<string, object>;
                        if (info == null || !(info.ContainsKey("Name"))) continue;
                        AddParam(pkg, info["Name"] as string, vl[i], scalars, vectors, textures);
                    }
                }
            }
        }

        static void AddParam(UPackage pkg, string name, object val, Dictionary<string, double> scalars, Dictionary<string, float[]> vectors, Dictionary<string, string> textures)
        {
            if (name == null) return;
            name = Regex.Replace(name, "^editable_", "");
            if (val is float && !scalars.ContainsKey(name)) scalars[name] = (float)val;
            else if (val is float[] && ((float[])val).Length >= 3 && !vectors.ContainsKey(name)) vectors[name] = (float[])val;
            else if (val is UObjectRef && !textures.ContainsKey(name))
            {
                string tp = pkg.ImportPath(((UObjectRef)val).Index);
                if (tp != null) textures[name] = tp;
            }
        }

        // Starting point from the material's name, for materials whose parameters say little.
        static void ApplyDefaults(PartMaterial m, string names)
        {
            Func<string, bool> has = s => names.Contains(s);
            if (has("chrome")) { m.Color = new[] { 0.9, 0.9, 0.9, 1.0 }; m.Metallic = 1; m.Roughness = 0.08; }
            else if (has("machined") || has("billet")) { m.Color = new[] { 0.62, 0.62, 0.64, 1.0 }; m.Metallic = 1; m.Roughness = 0.3; }
            else if (has("alloy") || has("aluminium") || has("aluminum")) { m.Color = new[] { 0.55, 0.56, 0.58, 1.0 }; m.Metallic = 1; m.Roughness = 0.55; }
            else if (has("castiron") || has("cast_iron") || has("iron")) { m.Color = new[] { 0.09, 0.085, 0.08, 1.0 }; m.Metallic = 0.6; m.Roughness = 0.8; }
            else if (has("steel")) { m.Color = new[] { 0.45, 0.45, 0.46, 1.0 }; m.Metallic = 1; m.Roughness = 0.45; }
            else if (has("rubber") || has("belt") || has("hose")) { m.Color = new[] { 0.03, 0.03, 0.03, 1.0 }; m.Metallic = 0; m.Roughness = 0.9; }
            else if (has("blackhole") || has("enginehole") || has("hole")) { m.Color = new[] { 0.0, 0.0, 0.0, 1.0 }; m.Metallic = 0; m.Roughness = 1; }
            else if (has("copper")) { m.Color = new[] { 0.72, 0.42, 0.28, 1.0 }; m.Metallic = 1; m.Roughness = 0.35; }
            else if (has("brass")) { m.Color = new[] { 0.78, 0.62, 0.32, 1.0 }; m.Metallic = 1; m.Roughness = 0.35; }
            else if (has("bolt") || has("thread")) { m.Color = new[] { 0.35, 0.35, 0.36, 1.0 }; m.Metallic = 1; m.Roughness = 0.5; }
            else if (has("plastic")) { m.Color = new[] { 0.05, 0.05, 0.05, 1.0 }; m.Metallic = 0; m.Roughness = 0.6; }
            else if (has("exhaust") || has("weld")) { m.Color = new[] { 0.3, 0.28, 0.26, 1.0 }; m.Metallic = 0.8; m.Roughness = 0.6; }
            if (has("translucen")) m.Translucent = true;
        }

        public static string ShortName(string objectPath)
        {
            string s = objectPath ?? "";
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(dot + 1);
            int slash = s.LastIndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        // ---------------------------------------------------------------- textures

        // A Texture2D as a .dds (the largest mip that is present, plus the smaller ones).
        public static byte[] LoadTextureDds(PakArchive pak, string objectPath)
        {
            var pkg = UPackage.Load(pak, objectPath);
            if (pkg == null) return null;
            var exp = pkg.FirstExport("Texture2D");
            if (exp == null) return null;
            var r = pkg.ExportReader(exp);
            new PropReader(pkg, r).ReadProps();
            if (r.ReadInt32() != 0) r.ReadBytes(16);     // object guid
            r.ReadUInt16();                              // UTexture strip flags
            r.ReadUInt16();                              // UTexture2D strip flags
            if (r.ReadInt32() == 0) return null;         // not cooked
            string fmtName = pkg.ReadName(r);
            if (fmtName == "None") return null;
            r.ReadInt64();                               // skip offset
            int sizeX = r.ReadInt32(), sizeY = r.ReadInt32();
            uint packed = r.ReadUInt32();
            string format = UPackage.ReadFString(r);
            if ((packed & (1u << 30)) != 0) { r.ReadUInt32(); r.ReadUInt32(); }    // optional data: ext data, mips in tail
            r.ReadInt32();                               // first mip to serialise
            int mipCount = r.ReadInt32();
            var mips = new List<byte[]>();
            int w0 = 0, h0 = 0;
            for (int i = 0; i < mipCount; i++)
            {
                r.ReadInt32();                           // cooked
                uint flags = r.ReadUInt32();
                long count = (flags & 0x2000) != 0 ? r.ReadInt64() : r.ReadInt32();
                long onDisk = (flags & 0x2000) != 0 ? r.ReadInt64() : r.ReadInt32();
                long offset = r.ReadInt64();
                byte[] data = null;
                bool inline = (flags & (0x1 | 0x100 | 0x20)) == 0;
                if (inline) data = r.ReadBytes((int)onDisk);
                else if ((flags & 0x20) == 0 && (flags & 0x2) == 0) data = pkg.Bulk(flags, offset, onDisk);
                int w = r.ReadInt32(), h = r.ReadInt32(); r.ReadInt32();
                if (data == null || data.Length == 0) { if (mips.Count > 0) break; continue; }
                if (mips.Count == 0) { w0 = w; h0 = h; }
                mips.Add(data);
            }
            if (mips.Count == 0) return null;
            return Dds(format, w0, h0, mips);
        }

        static byte[] Dds(string format, int w, int h, List<byte[]> mips)
        {
            string fourCC = null; int dxgi = 0, bpp = 0;
            switch (format)
            {
                case "PF_DXT1": fourCC = "DXT1"; break;
                case "PF_DXT3": fourCC = "DXT3"; break;
                case "PF_DXT5": fourCC = "DXT5"; break;
                case "PF_BC4": fourCC = "ATI1"; break;
                case "PF_BC5": fourCC = "ATI2"; break;
                case "PF_BC7": fourCC = "DX10"; dxgi = 98; break;
                case "PF_B8G8R8A8": bpp = 32; break;
                case "PF_G8": bpp = 8; break;
                default: return null;
            }
            var ms = new MemoryStream();
            var wr = new BinaryWriter(ms);
            wr.Write(0x20534444);      // "DDS "
            wr.Write(124);
            wr.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | (fourCC != null ? 0x80000 : 0x8));
            wr.Write(h); wr.Write(w);
            wr.Write(fourCC != null ? mips[0].Length : w * bpp / 8);
            wr.Write(0); wr.Write(mips.Count);
            for (int i = 0; i < 11; i++) wr.Write(0);
            wr.Write(32);
            if (fourCC != null) { wr.Write(0x4); wr.Write(Encoding.ASCII.GetBytes(fourCC)); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0); }
            else if (bpp == 32) { wr.Write(0x41); wr.Write(0); wr.Write(32); wr.Write(0x00ff0000); wr.Write(0x0000ff00); wr.Write(0x000000ff); wr.Write(unchecked((int)0xff000000)); }
            else { wr.Write(0x20000); wr.Write(0); wr.Write(8); wr.Write(0xff); wr.Write(0); wr.Write(0); wr.Write(0); }
            wr.Write(0x1000 | 0x8 | 0x400000); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0);
            if (dxgi != 0) { wr.Write(dxgi); wr.Write(3); wr.Write(0); wr.Write(1); wr.Write(0); }
            foreach (var m in mips) wr.Write(m);
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- export

        // One mesh placed in the output, with a 4x3 transform (rotation rows + translation, metres).
        public class Placed
        {
            public UMesh Mesh;
            public double[] M = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
            public Dictionary<int, string> MaterialOverride;     // slot -> material path
        }

        public class ExportResult
        {
            public string MeshName;
            public List<KeyValuePair<string, byte[]>> Files = new List<KeyValuePair<string, byte[]>>();
            public List<string> Warnings = new List<string>();
            public int Triangles;
            public double[] Bounds;
        }

        // Writes the placed meshes as one BeamNG mesh "meshName" in "folder" (inside the mod),
        // with a .materials.json and the textures it uses. paint: the engine's paint colour for
        // materials Automation lets you paint (null keeps their own colour).
        public static ExportResult Export(PakArchive pak, IList<Placed> parts, string meshName, string folder, double[] paint)
        {
            var res = new ExportResult { MeshName = meshName };
            var matCache = new Dictionary<string, PartMaterial>(StringComparer.OrdinalIgnoreCase);
            var byMat = new Dictionary<string, List<int>>();      // material path -> triangle corners (global vertex ids)
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>();
            double[] b = { 1e9, 1e9, 1e9, -1e9, -1e9, -1e9 };

            foreach (var p in parts)
            {
                var mesh = p.Mesh;
                int baseV = pos.Count / 3;
                var m = p.M;
                for (int i = 0; i < mesh.NumVertices; i++)
                {
                    double x = mesh.Pos[i * 3], y = mesh.Pos[i * 3 + 1], z = mesh.Pos[i * 3 + 2];
                    double[] w = { m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11] };
                    for (int k = 0; k < 3; k++) { pos.Add((float)w[k]); b[k] = Math.Min(b[k], w[k]); b[k + 3] = Math.Max(b[k + 3], w[k]); }
                    double nx = mesh.Normal[i * 3], ny = mesh.Normal[i * 3 + 1], nz = mesh.Normal[i * 3 + 2];
                    double ax = m[0] * nx + m[1] * ny + m[2] * nz, ay = m[4] * nx + m[5] * ny + m[6] * nz, az = m[8] * nx + m[9] * ny + m[10] * nz;
                    double len = Math.Sqrt(ax * ax + ay * ay + az * az); if (len < 1e-9) len = 1;
                    nrm.Add((float)(ax / len)); nrm.Add((float)(ay / len)); nrm.Add((float)(az / len));
                    uv.Add(mesh.UV[i * 2]); uv.Add(mesh.UV[i * 2 + 1]);
                }
                bool mirrored = Det(m) < 0;
                foreach (var s in mesh.Sections)
                {
                    string mp = null;
                    if (p.MaterialOverride != null) p.MaterialOverride.TryGetValue(s.Material, out mp);
                    if (mp == null) mp = s.Material < mesh.Materials.Count ? mesh.Materials[s.Material] : null;
                    if (mp == null) mp = "/Game/default";
                    if (Material(pak, mp, matCache, res).Skip) continue;
                    List<int> list;
                    if (!byMat.TryGetValue(mp, out list)) byMat[mp] = list = new List<int>();
                    for (int t = 0; t < s.NumTriangles; t++)
                    {
                        int i0 = mesh.Indices[s.FirstIndex + t * 3], i1 = mesh.Indices[s.FirstIndex + t * 3 + 1], i2 = mesh.Indices[s.FirstIndex + t * 3 + 2];
                        if (mirrored) { int tmp = i1; i1 = i2; i2 = tmp; }
                        list.Add(baseV + i0); list.Add(baseV + i1); list.Add(baseV + i2);
                    }
                    res.Triangles += s.NumTriangles;
                }
            }
            res.Bounds = b;

            Func<string, string> matName = mp => meshName + "_" + Regex.Replace(ShortName(mp), "[^A-Za-z0-9]+", "_");
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n");
            sb.Append("<asset><contributor><authoring_tool>AutomationToBeamNG (built from Automation's engine parts)</authoring_tool></contributor><unit name=\"meter\" meter=\"1\"/><up_axis>Z_UP</up_axis></asset>\n");
            sb.Append("<library_effects>");
            foreach (var mp in byMat.Keys) sb.Append("<effect id=\"").Append(matName(mp)).Append("-fx\"><profile_COMMON><technique sid=\"common\"><lambert/></technique></profile_COMMON></effect>");
            sb.Append("</library_effects>\n<library_materials>");
            foreach (var mp in byMat.Keys) sb.Append("<material id=\"").Append(matName(mp)).Append("\" name=\"").Append(matName(mp)).Append("\"><instance_effect url=\"#").Append(matName(mp)).Append("-fx\"/></material>");
            sb.Append("</library_materials>\n<library_geometries><geometry id=\"").Append(meshName).Append("-lib\" name=\"").Append(meshName).Append("Mesh\"><mesh>\n");
            Action<string, List<float>, int, string> source = (id, data, stride, prm) =>
            {
                sb.Append("<source id=\"").Append(meshName).Append(id).Append("\"><float_array id=\"").Append(meshName).Append(id).Append("-array\" count=\"").Append(data.Count).Append("\">");
                for (int i = 0; i < data.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("0.######", inv)); }
                sb.Append("</float_array><technique_common><accessor source=\"#").Append(meshName).Append(id).Append("-array\" count=\"").Append(data.Count / stride).Append("\" stride=\"").Append(stride).Append("\">");
                foreach (var c in prm) sb.Append("<param name=\"").Append(c).Append("\" type=\"float\"/>");
                sb.Append("</accessor></technique_common></source>\n");
            };
            source("-POSITION", pos, 3, "XYZ");
            source("-NORMAL", nrm, 3, "XYZ");
            source("-UV0", uv, 2, "ST");
            sb.Append("<vertices id=\"").Append(meshName).Append("-VERTEX\"><input semantic=\"POSITION\" source=\"#").Append(meshName).Append("-POSITION\"/></vertices>\n");
            foreach (var kv in byMat)
            {
                sb.Append("<triangles material=\"").Append(matName(kv.Key)).Append("\" count=\"").Append(kv.Value.Count / 3).Append("\">");
                sb.Append("<input semantic=\"VERTEX\" source=\"#").Append(meshName).Append("-VERTEX\" offset=\"0\"/>");
                sb.Append("<input semantic=\"NORMAL\" source=\"#").Append(meshName).Append("-NORMAL\" offset=\"0\"/>");
                sb.Append("<input semantic=\"TEXCOORD\" source=\"#").Append(meshName).Append("-UV0\" offset=\"0\" set=\"0\"/><p>");
                for (int i = 0; i < kv.Value.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(kv.Value[i]); }
                sb.Append("</p></triangles>\n");
            }
            sb.Append("</mesh></geometry></library_geometries>\n<library_visual_scenes><visual_scene id=\"Scene\" name=\"Scene\"><node id=\"").Append(meshName)
              .Append("\" name=\"").Append(meshName).Append("\"><instance_geometry url=\"#").Append(meshName).Append("-lib\"><bind_material><technique_common>");
            foreach (var mp in byMat.Keys) sb.Append("<instance_material symbol=\"").Append(matName(mp)).Append("\" target=\"#").Append(matName(mp)).Append("\"/>");
            sb.Append("</technique_common></bind_material></instance_geometry></node></visual_scene></library_visual_scenes>\n<scene><instance_visual_scene url=\"#Scene\"/></scene>\n</COLLADA>\n");
            res.Files.Add(new KeyValuePair<string, byte[]>(folder + meshName + ".dae", new UTF8Encoding(false).GetBytes(sb.ToString())));

            // materials and textures
            var mats = new Dictionary<string, object>();
            var texFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // object path -> file name
            Func<string, string> texture = tp =>
            {
                if (tp == null) return null;
                string f;
                if (texFiles.TryGetValue(tp, out f)) return f;
                byte[] dds = null;
                try { dds = LoadTextureDds(pak, tp); }
                catch (Exception ex) { res.Warnings.Add("texture " + ShortName(tp) + ": " + ex.Message); }
                f = dds == null ? null : "a2b_" + Regex.Replace(ShortName(tp), "[^A-Za-z0-9]+", "_").ToLowerInvariant() + ".dds";
                texFiles[tp] = f;
                if (f != null) res.Files.Add(new KeyValuePair<string, byte[]>(folder + f, dds));
                return f;
            };
            foreach (var mp in byMat.Keys)
            {
                var pm = Material(pak, mp, matCache, res);
                var colour = pm.Paintable && paint != null ? paint : pm.Color;
                var stage = new Dictionary<string, object>
                {
                    { "diffuseColor", new[] { colour[0], colour[1], colour[2], pm.Translucent ? 0.3 : 1.0 } },
                    { "roughnessFactor", Math.Round(pm.Roughness, 3) },
                    { "metallicFactor", Math.Round(pm.Metallic, 3) },
                    { "useAnisotropic", true },
                };
                string n = texture(pm.NormalTexture);
                if (n != null) stage["normalMap"] = n;
                string c = texture(pm.ColorTexture);
                if (c != null) stage["baseColorMap"] = c;
                var mat = new Dictionary<string, object>
                {
                    { "name", matName(mp) }, { "mapTo", matName(mp) }, { "class", "Material" },
                    { "Stages", new object[] { stage, new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>() } },
                    { "dynamicCubemap", true }, { "materialTag0", "beamng" }, { "materialTag1", "vehicle" }, { "version", 1.5 },
                };
                if (pm.Translucent) { mat["translucent"] = true; mat["translucentBlendOp"] = "PreMulAlpha"; mat["translucentZWrite"] = false; mat["castShadows"] = false; }
                mats[matName(mp)] = mat;
            }
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(mats);
            res.Files.Add(new KeyValuePair<string, byte[]>(folder + meshName + ".materials.json", Encoding.UTF8.GetBytes(json)));
            return res;
        }

        static PartMaterial Material(PakArchive pak, string mp, Dictionary<string, PartMaterial> cache, ExportResult res)
        {
            PartMaterial pm;
            if (cache.TryGetValue(mp, out pm)) return pm;
            try { pm = LoadMaterial(pak, mp); }
            catch (Exception ex) { pm = new PartMaterial { Path = mp, Name = ShortName(mp) }; res.Warnings.Add("material " + ShortName(mp) + ": " + ex.Message); }
            cache[mp] = pm;
            return pm;
        }

        static double Det(double[] m)
        {
            return m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
        }

        // The static meshes inside a blueprint actor (or a lone StaticMeshComponent asset), with
        // each component's transform relative to the actor, following AttachParent chains.
        // Returns (mesh object path, 3x4 matrix in BeamNG axes and metres).
        public static List<KeyValuePair<string, double[]>> ActorMeshes(PakArchive pak, string objectPath)
        {
            var res = new List<KeyValuePair<string, double[]>>();
            var pkg = UPackage.Load(pak, objectPath);
            if (pkg == null) return res;
            var props = new Dictionary<int, Dictionary<string, object>>();
            for (int i = 0; i < pkg.Exports.Count; i++)
            {
                string cls = pkg.ClassOf(pkg.Exports[i]);
                if (!cls.Contains("Component")) continue;
                try { props[i + 1] = pkg.Properties(pkg.Exports[i]); } catch (Exception) { }
            }
            Func<int, double[]> local = null;
            local = idx =>
            {
                Dictionary<string, object> p;
                var m = Identity();
                if (!props.TryGetValue(idx, out p)) return m;
                object o;
                double[] loc = { 0, 0, 0 }, rot = { 0, 0, 0 }, scl = { 1, 1, 1 };
                if (p.TryGetValue("RelativeLocation", out o) && o is float[]) { var f = (float[])o; loc = new double[] { f[0] * CmToM, -f[1] * CmToM, f[2] * CmToM }; }
                if (p.TryGetValue("RelativeRotation", out o) && o is float[]) { var f = (float[])o; rot = new double[] { f[0], f[1], f[2] }; }
                if (p.TryGetValue("RelativeScale3D", out o) && o is float[]) { var f = (float[])o; scl = new double[] { f[0], f[1], f[2] }; }
                m = Compose(Transform(loc, rot, scl), Identity());
                if (p.TryGetValue("AttachParent", out o) && o is UObjectRef && ((UObjectRef)o).Index > 0 && ((UObjectRef)o).Index != idx)
                    m = Compose(local(((UObjectRef)o).Index), m);
                return m;
            };
            foreach (var kv in props)
            {
                object sm;
                if (!kv.Value.TryGetValue("StaticMesh", out sm) || !(sm is UObjectRef)) continue;
                string mp = pkg.ImportPath(((UObjectRef)sm).Index);
                if (mp == null) continue;
                object vis;
                if (kv.Value.TryGetValue("bVisible", out vis) && vis is bool && !(bool)vis) continue;
                res.Add(new KeyValuePair<string, double[]>(mp, local(kv.Key)));
            }
            return res;
        }

        public static double[] Identity() { return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }; }

        // a * b for 3x4 affine matrices
        public static double[] Compose(double[] a, double[] b)
        {
            var r = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
                r[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
            }
            return r;
        }

        // Unreal's rotator (pitch about Y, yaw about Z, roll about X, degrees; applied roll, pitch,
        // yaw) converted to our mirrored-Y axes, with translation and scale.
        public static double[] Transform(double[] loc, double[] rot, double[] scale)
        {
            double p = rot[0] * Math.PI / 180, y = rot[1] * Math.PI / 180, r = rot[2] * Math.PI / 180;
            double sp = Math.Sin(p), cp = Math.Cos(p), sy = Math.Sin(y), cy = Math.Cos(y), sr = Math.Sin(r), cr = Math.Cos(r);
            // FRotationMatrix rows are the rotated X, Y, Z axes; columns of the world matrix
            double[,] u = {
                { cp * cy, sr * sp * cy - cr * sy, -(cr * sp * cy + sr * sy) },
                { cp * sy, sr * sp * sy + cr * cy, cy * sr - cr * sp * sy },
                { sp, -sr * cp, cr * cp } };
            // mirror Y on both sides: M' = S M S with S = diag(1,-1,1)
            double[] sgn = { 1, -1, 1 };
            var m = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) m[i * 4 + j] = u[i, j] * sgn[i] * sgn[j] * scale[j];
                m[i * 4 + 3] = loc[i];
            }
            return m;
        }

        // Object path of an engine part by its asset name (e.g. "INL_OHC_BLOCK_FRONT"), or null.
        public static string FindPart(PakArchive pak, string name)
        {
            string suffix = "/" + name + ".uasset";
            var f = pak.Files.FirstOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && x.StartsWith("AutomationGame/Content/Engines/", StringComparison.OrdinalIgnoreCase));
            return f == null ? null : "/Game/" + f.Substring("AutomationGame/Content/".Length, f.Length - "AutomationGame/Content/".Length - ".uasset".Length);
        }

        // Every static mesh under Automation's engine content, as object paths.
        public static List<string> EngineParts(PakArchive pak)
        {
            return pak.Files
                .Where(f => f.StartsWith("AutomationGame/Content/Engines/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    && f.IndexOf("/Blueprints/", StringComparison.OrdinalIgnoreCase) < 0 && f.IndexOf("/Materials/", StringComparison.OrdinalIgnoreCase) < 0
                    && f.IndexOf("/Textures", StringComparison.OrdinalIgnoreCase) < 0 && f.IndexOf("/LOD", StringComparison.OrdinalIgnoreCase) < 0)
                .Select(f => "/Game/" + f.Substring("AutomationGame/Content/".Length, f.Length - "AutomationGame/Content/".Length - ".uasset".Length))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
