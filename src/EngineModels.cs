// Automation's own 3D engine models. Automation's "Export to BeamNG" writes a car mod whose
// engine is a separate mesh in the car's .dae, placed with a flexbody pos/rot/scale. We find those
// exports in the mods folder, match them to engines by variant UID, cut the engine (and gearbox)
// away from the driveline that shares the mesh, re-centre it, and write it as a small standalone
// .dae with its materials and textures that any car can place in its engine bay.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace AutomationToBeamNG
{
    // Read access to a mod that is either a .zip or an unpacked folder.
    abstract class ModSource : IDisposable
    {
        public string Path;
        public abstract IEnumerable<string> Files { get; }
        public abstract Stream Open(string file);
        public abstract DateTime Modified(string file);
        public virtual void Dispose() { }

        public string ReadText(string file) { using (var r = new StreamReader(Open(file))) return r.ReadToEnd(); }
        public byte[] ReadBytes(string file) { using (var s = Open(file)) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }

        public static ModSource Create(string path)
        {
            if (Directory.Exists(path)) return new DirSource(path);
            return new ZipSource(path);
        }
    }

    class ZipSource : ModSource
    {
        readonly ZipArchive zip;
        readonly Dictionary<string, ZipArchiveEntry> entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        public ZipSource(string path)
        {
            Path = path;
            zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries) if (e.Length > 0 || e.Name.Length > 0) entries[e.FullName.Replace('\\', '/')] = e;
        }
        public override IEnumerable<string> Files { get { return entries.Keys; } }
        public override Stream Open(string file) { return entries[file].Open(); }
        public override DateTime Modified(string file) { return entries[file].LastWriteTime.DateTime; }
        public override void Dispose() { zip.Dispose(); }
    }

    class DirSource : ModSource
    {
        public DirSource(string path) { Path = path; }
        public override IEnumerable<string> Files
        {
            get
            {
                string v = System.IO.Path.Combine(Path, "vehicles");
                if (!Directory.Exists(v)) return new string[0];
                return Directory.EnumerateFiles(v, "*", SearchOption.AllDirectories).Select(f => f.Substring(Path.Length).TrimStart('\\', '/').Replace('\\', '/'));
            }
        }
        public override Stream Open(string file) { return File.OpenRead(System.IO.Path.Combine(Path, file)); }
        public override DateTime Modified(string file) { return File.GetLastWriteTime(System.IO.Path.Combine(Path, file)); }
    }

    // One engine found in an Automation BeamNG export.
    class ExportedEngine
    {
        public string Uid, CarName, Source, VehicleDir, DaeFile, MeshName;
        public double[] Pos = new double[3], Rot = new double[3], Scale = { 1, 1, 1 };
        public double[] NodeBox;   // min x,y,z, max x,y,z of the engine block nodes
        public DateTime Exported;

        public string Describe { get { return CarName + " (" + System.IO.Path.GetFileName(Source) + ")"; } }
    }

    // The engine mesh cut out of an export, ready to drop into a mod.
    class EngineMeshFiles
    {
        public string MeshName, Folder;               // folder inside the mod, e.g. vehicles/common/automation_engines/x/
        public bool CrankAlongY;                      // in the vehicle's axes, after the export's placement
        public double[] Size = new double[3];         // bounding box size, metres
        public int Triangles, DroppedPieces;
        public List<KeyValuePair<string, byte[]>> Files = new List<KeyValuePair<string, byte[]>>();
        public HashSet<string> Materials = new HashSet<string>();
    }

    static class EngineExports
    {
        static readonly Regex EngineJbeam = new Regex("^vehicles/([^/]+)/eng_[^/]+/camso_engine_[^/]+\\.jbeam$", RegexOptions.IgnoreCase);
        static readonly Regex StructureJbeam = new Regex("^vehicles/([^/]+)/([^/]+)/camso_engine_structure_[^/]+\\.jbeam$", RegexOptions.IgnoreCase);
        static readonly Regex UidRe = new Regex("\"sampleName\"\\s*:\\s*\"([0-9A-Fa-f]{32})\"");

        static string CacheFile { get { return System.IO.Path.Combine(AppPaths.Dir, "export_index.txt"); } }

        // All engines found in Automation exports in the mods folder, newest export per UID.
        public static Dictionary<string, ExportedEngine> Scan(string modsDir)
        {
            var result = new Dictionary<string, ExportedEngine>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return result;
            var cache = ReadCache();
            var newCache = new Dictionary<string, List<ExportedEngine>>();
            var sources = new List<string>();
            sources.AddRange(Directory.EnumerateFiles(modsDir, "*.zip", SearchOption.AllDirectories)
                .Where(f => !System.IO.Path.GetFileName(f).StartsWith("automation_", StringComparison.OrdinalIgnoreCase)));
            string unpacked = System.IO.Path.Combine(modsDir, "unpacked");
            if (Directory.Exists(unpacked)) sources.AddRange(Directory.GetDirectories(unpacked));
            foreach (var src in sources)
            {
                string stamp;
                try
                {
                    var fi = new FileInfo(src);
                    stamp = src + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "dir|" + Directory.GetLastWriteTimeUtc(src).Ticks);
                }
                catch (Exception) { continue; }
                List<ExportedEngine> found;
                if (!cache.TryGetValue(stamp, out found))
                {
                    try { found = ScanSource(src); } catch (Exception) { found = new List<ExportedEngine>(); }
                }
                newCache[stamp] = found;
                foreach (var e in found)
                {
                    ExportedEngine cur;
                    if (!result.TryGetValue(e.Uid, out cur) || e.Exported > cur.Exported) result[e.Uid] = e;
                }
            }
            WriteCache(newCache);
            return result;
        }

        static List<ExportedEngine> ScanSource(string path)
        {
            var list = new List<ExportedEngine>();
            using (var src = ModSource.Create(path))
            {
                var files = src.Files.ToList();
                foreach (var f in files)
                {
                    var m = EngineJbeam.Match(f);
                    if (!m.Success) continue;
                    var um = UidRe.Match(src.ReadText(f));
                    if (!um.Success) continue;
                    string car = m.Groups[1].Value;
                    var e = new ExportedEngine { Uid = um.Groups[1].Value.ToUpperInvariant(), Source = path, VehicleDir = "vehicles/" + car + "/", CarName = car, Exported = src.Modified(f) };
                    string info = files.FirstOrDefault(x => x.Equals(e.VehicleDir + "info.json", StringComparison.OrdinalIgnoreCase));
                    if (info != null)
                    {
                        var nm = Regex.Match(src.ReadText(info), "\"Name\"\\s*:\\s*\"([^\"]+)\"");
                        if (nm.Success) e.CarName = nm.Groups[1].Value;
                    }
                    foreach (var sf in files.Where(x => StructureJbeam.IsMatch(x) && x.StartsWith(e.VehicleDir, StringComparison.OrdinalIgnoreCase)))
                        if (ReadStructure(src.ReadText(sf), e))
                        {
                            string dir = sf.Substring(0, sf.LastIndexOf('/') + 1);
                            e.DaeFile = files.FirstOrDefault(x => x.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && x.EndsWith(".dae", StringComparison.OrdinalIgnoreCase));
                            break;
                        }
                    if (e.MeshName != null && e.NodeBox != null && e.DaeFile != null) list.Add(e);
                }
            }
            return list;
        }

        static double D(Dictionary<string, object> d, string k, double def)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) && o is double ? (double)o : def;
        }

        // Engine mesh placement (flexbody of the "Camso_engine_mesh" part) and the engine block nodes.
        static bool ReadStructure(string text, ExportedEngine e)
        {
            var root = JBeamParser.Parse(text) as Dictionary<string, object>;
            if (root == null) return false;
            foreach (var part in root.Values.OfType<Dictionary<string, object>>())
            {
                object st;
                part.TryGetValue("slotType", out st);
                object fb;
                if ("Camso_engine_mesh".Equals(st as string, StringComparison.OrdinalIgnoreCase) && part.TryGetValue("flexbodies", out fb))
                {
                    var row = ((List<object>)fb).OfType<List<object>>().Skip(1).FirstOrDefault(r => r.Count >= 1 && r[0] is string);
                    if (row == null) continue;
                    e.MeshName = (string)row[0];
                    var opt = row.OfType<Dictionary<string, object>>().FirstOrDefault();
                    if (opt != null)
                    {
                        Func<string, double, double[]> vec = (k, def) =>
                        {
                            object o;
                            var d = opt.TryGetValue(k, out o) ? o as Dictionary<string, object> : null;
                            return new[] { D(d, "x", def), D(d, "y", def), D(d, "z", def) };
                        };
                        e.Pos = vec("pos", 0); e.Rot = vec("rot", 0); e.Scale = vec("scale", 1);
                    }
                }
                object nodesObj;
                if (part.TryGetValue("nodes", out nodesObj))
                {
                    var box = EngineBox(nodesObj as List<object>);
                    if (box != null) e.NodeBox = box;
                }
            }
            return e.MeshName != null && e.NodeBox != null;
        }

        // "engine_block", or a vehicle-specific name like "us_semi_engine_block".
        public static bool IsBlockGroup(object g)
        {
            Func<string, bool> ok = s => s != null && s.EndsWith("engine_block", StringComparison.OrdinalIgnoreCase);
            return (g is string && ok((string)g)) || (g is List<object> && ((List<object>)g).OfType<string>().Any(ok));
        }

        // Box around the nodes in the "engine_block" engine group (jbeam modifier rows included).
        public static double[] EngineBox(List<object> nodes)
        {
            if (nodes == null) return null;
            object curGroup = null;
            double[] mn = { 1e9, 1e9, 1e9 }, mx = { -1e9, -1e9, -1e9 };
            int n = 0;
            foreach (var row in nodes)
            {
                var mod = row as Dictionary<string, object>;
                if (mod != null) { if (mod.ContainsKey("engineGroup")) curGroup = mod["engineGroup"]; continue; }
                var l = row as List<object>;
                if (l == null || l.Count < 4 || !(l[1] is double) || !(l[2] is double) || !(l[3] is double)) continue;
                object g = curGroup;
                var opt = l.OfType<Dictionary<string, object>>().FirstOrDefault();
                if (opt != null && opt.ContainsKey("engineGroup")) g = opt["engineGroup"];
                if (!IsBlockGroup(g)) continue;
                for (int k = 0; k < 3; k++) { mn[k] = Math.Min(mn[k], (double)l[k + 1]); mx[k] = Math.Max(mx[k], (double)l[k + 1]); }
                n++;
            }
            return n >= 2 ? new[] { mn[0], mn[1], mn[2], mx[0], mx[1], mx[2] } : null;
        }

        // ---- cache ----
        static string Esc(string s) { return (s ?? "").Replace('\t', ' '); }
        static string Nums(double[] v) { return string.Join(",", v.Select(x => x.ToString("R", Num.Inv))); }
        static double[] ParseNums(string s) { return s.Split(',').Select(x => double.Parse(x, Num.Inv)).ToArray(); }

        static Dictionary<string, List<ExportedEngine>> ReadCache()
        {
            var d = new Dictionary<string, List<ExportedEngine>>();
            try
            {
                if (!File.Exists(CacheFile)) return d;
                string key = null;
                foreach (var ln in File.ReadAllLines(CacheFile, Encoding.UTF8))
                {
                    var c = ln.Split('\t');
                    if (c[0] == "S" && c.Length >= 2) { key = c[1]; d[key] = new List<ExportedEngine>(); }
                    else if (c[0] == "E" && c.Length >= 12 && key != null)
                        d[key].Add(new ExportedEngine
                        {
                            Uid = c[1], CarName = c[2], Source = c[3], VehicleDir = c[4], DaeFile = c[5], MeshName = c[6],
                            Pos = ParseNums(c[7]), Rot = ParseNums(c[8]), Scale = ParseNums(c[9]), NodeBox = ParseNums(c[10]),
                            Exported = new DateTime(long.Parse(c[11], Num.Inv)),
                        });
                }
            }
            catch (Exception) { d.Clear(); }
            return d;
        }

        static void WriteCache(Dictionary<string, List<ExportedEngine>> d)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in d)
                {
                    sb.Append("S\t").Append(Esc(kv.Key)).Append('\n');
                    foreach (var e in kv.Value)
                        sb.Append("E\t").Append(e.Uid).Append('\t').Append(Esc(e.CarName)).Append('\t').Append(Esc(e.Source)).Append('\t').Append(Esc(e.VehicleDir)).Append('\t')
                          .Append(Esc(e.DaeFile)).Append('\t').Append(Esc(e.MeshName)).Append('\t').Append(Nums(e.Pos)).Append('\t').Append(Nums(e.Rot)).Append('\t')
                          .Append(Nums(e.Scale)).Append('\t').Append(Nums(e.NodeBox)).Append('\t').Append(e.Exported.Ticks.ToString(Num.Inv)).Append('\n');
                }
                Directory.CreateDirectory(AppPaths.Dir);
                File.WriteAllText(CacheFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // ================================================================== extraction

        class Src { public string Id; public double[] Data; public int Stride; public string ParamXml; public bool Rotate, IsPos; }
        class Prim { public string Material; public List<string[]> Inputs = new List<string[]>(); public int[] P; public int Stride; }   // input: semantic, source, offset, set

        static double[] Floats(string s)
        {
            var parts = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++) r[i] = double.Parse(parts[i], NumberStyles.Float, Num.Inv);
            return r;
        }

        static int[] Ints(string s)
        {
            var parts = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) r[i] = int.Parse(parts[i], Num.Inv);
            return r;
        }

        // BeamNG's flexbody rotation: yaw (z) first, then x, then y  (R = Ry * Rx * Rz).
        static double[,] Rotation(double[] rot)
        {
            Func<double[,], double[,], double[,]> mul = (a, b) =>
            {
                var r = new double[3, 3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
                return r;
            };
            double x = rot[0] * Math.PI / 180, y = rot[1] * Math.PI / 180, z = rot[2] * Math.PI / 180;
            var rx = new[,] { { 1, 0, 0 }, { 0, Math.Cos(x), -Math.Sin(x) }, { 0, Math.Sin(x), Math.Cos(x) } };
            var ry = new[,] { { Math.Cos(y), 0, Math.Sin(y) }, { 0, 1, 0 }, { -Math.Sin(y), 0, Math.Cos(y) } };
            var rz = new[,] { { Math.Cos(z), -Math.Sin(z), 0 }, { Math.Sin(z), Math.Cos(z), 0 }, { 0, 0, 1 } };
            return mul(mul(ry, rx), rz);
        }

        static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }

        // Cuts the engine out of the export's .dae and writes the mesh, materials and textures.
        public static EngineMeshFiles Extract(ExportedEngine e, string safe)
        {
            var res = new EngineMeshFiles { MeshName = "aeng_" + safe, Folder = "vehicles/common/automation_engines/" + safe + "/" };
            double unit = 1;
            double[] nodeMatrix = null;
            var sources = new Dictionary<string, Src>();
            string posSourceId = null;
            var prims = new List<Prim>();

            using (var src = ModSource.Create(e.Source))
            {
                // ---- read the engine geometry
                using (var x = XmlReader.Create(src.Open(e.DaeFile), new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
                {
                    bool inGeo = false;
                    Src curSrc = null;
                    Prim curPrim = null;
                    while (x.Read())
                    {
                        if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "geometry") inGeo = false;
                        if (x.NodeType != XmlNodeType.Element) continue;
                        string n = x.LocalName;
                        if (n == "unit") { double u; if (double.TryParse(x.GetAttribute("meter"), NumberStyles.Float, Num.Inv, out u)) unit = u; continue; }
                        if (n == "geometry") { inGeo = x.GetAttribute("id") == e.MeshName + "-lib" || x.GetAttribute("name") == e.MeshName + "Mesh"; continue; }
                        if (n == "node" && x.GetAttribute("name") == e.MeshName)
                        {
                            using (var sub = x.ReadSubtree())
                                while (sub.Read())
                                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "matrix") { nodeMatrix = Floats(sub.ReadElementContentAsString()); break; }
                            continue;
                        }
                        if (!inGeo) continue;
                        switch (n)
                        {
                            case "source": curSrc = new Src { Id = x.GetAttribute("id") }; sources[curSrc.Id] = curSrc; break;
                            case "float_array": if (curSrc != null) curSrc.Data = Floats(x.ReadElementContentAsString()); break;
                            case "accessor":
                                if (curSrc != null)
                                {
                                    curSrc.Stride = int.Parse(x.GetAttribute("stride") ?? "1", Num.Inv);
                                    using (var sub = x.ReadSubtree()) { sub.Read(); var inner = new StringBuilder(); while (sub.Read()) if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "param") inner.Append("<param name=\"" + sub.GetAttribute("name") + "\" type=\"" + (sub.GetAttribute("type") ?? "float") + "\"/>"); curSrc.ParamXml = inner.ToString(); }
                                }
                                break;
                            case "vertices": curSrc = null; break;
                            case "triangles": curPrim = new Prim { Material = x.GetAttribute("material") }; prims.Add(curPrim); curSrc = null; break;
                            case "polylist": case "polygons": throw new InvalidDataException("the export's engine mesh uses polygons, which aren't supported yet");
                            case "input":
                                if (curPrim == null && x.GetAttribute("semantic") == "POSITION") posSourceId = x.GetAttribute("source").TrimStart('#');
                                else if (curPrim != null)
                                {
                                    curPrim.Inputs.Add(new[] { x.GetAttribute("semantic"), x.GetAttribute("source").TrimStart('#'), x.GetAttribute("offset") ?? "0", x.GetAttribute("set") });
                                    curPrim.Stride = Math.Max(curPrim.Stride, int.Parse(x.GetAttribute("offset") ?? "0", Num.Inv) + 1);
                                }
                                break;
                            case "p": if (curPrim != null) curPrim.P = Ints(x.ReadElementContentAsString()); break;
                        }
                    }
                }
                if (posSourceId == null || !sources.ContainsKey(posSourceId) || prims.Count == 0) throw new InvalidDataException("engine mesh " + e.MeshName + " not found in " + e.DaeFile);

                // ---- place it like the export does (node matrix, units, flexbody scale/rotation/position)
                var R = Rotation(e.Rot);
                var pos = sources[posSourceId];
                pos.IsPos = true;
                int nv = pos.Data.Length / 3;
                var world = new double[nv * 3];
                Func<double[], int, double[]> applyNode = (v, i) =>
                {
                    if (nodeMatrix == null || nodeMatrix.Length != 16) return new[] { v[i], v[i + 1], v[i + 2] };
                    var m = nodeMatrix;
                    return new[] { m[0] * v[i] + m[1] * v[i + 1] + m[2] * v[i + 2] + m[3], m[4] * v[i] + m[5] * v[i + 1] + m[6] * v[i + 2] + m[7], m[8] * v[i] + m[9] * v[i + 1] + m[10] * v[i + 2] + m[11] };
                };
                for (int i = 0; i < nv; i++)
                {
                    var v = applyNode(pos.Data, i * 3);
                    double vx = v[0] * unit * e.Scale[0], vy = v[1] * unit * e.Scale[1], vz = v[2] * unit * e.Scale[2];
                    for (int k = 0; k < 3; k++) world[i * 3 + k] = R[k, 0] * vx + R[k, 1] * vy + R[k, 2] * vz + e.Pos[k];
                }

                // ---- keep only the pieces around the engine block (drops driveshaft / axle)
                var par = Enumerable.Range(0, nv).ToArray();
                foreach (var pr in prims)
                {
                    int vo = int.Parse(pr.Inputs.First(i => i[0] == "VERTEX")[2], Num.Inv);
                    for (int t = 0; t + 3 * pr.Stride - 1 < pr.P.Length; t += 3 * pr.Stride)
                    {
                        int a = Find(par, pr.P[t + vo]), b = Find(par, pr.P[t + pr.Stride + vo]), c = Find(par, pr.P[t + 2 * pr.Stride + vo]);
                        par[b] = a; par[Find(par, c)] = a;
                    }
                }
                var sums = new Dictionary<int, double[]>();
                for (int i = 0; i < nv; i++)
                {
                    int r = Find(par, i);
                    double[] s;
                    if (!sums.TryGetValue(r, out s)) sums[r] = s = new double[4];
                    s[0] += world[3 * i]; s[1] += world[3 * i + 1]; s[2] += world[3 * i + 2]; s[3]++;
                }
                var box = e.NodeBox;
                const double margin = 0.35;
                var keep = new HashSet<int>();
                foreach (var kv in sums)
                {
                    var s = kv.Value;
                    double cx = s[0] / s[3], cy = s[1] / s[3], cz = s[2] / s[3];
                    if (cx >= box[0] - margin && cx <= box[3] + margin && cy >= box[1] - margin && cy <= box[4] + margin && cz >= box[2] - margin && cz <= box[5] + margin) keep.Add(kv.Key);
                    else res.DroppedPieces++;
                }
                if (keep.Count == 0) throw new InvalidDataException("couldn't find the engine inside the export's mesh");

                // ---- centre on the engine block, rotate normals/tangents, compact, write
                double[] centre = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                var crank = new[] { R[0, 0], R[1, 0] };   // mesh x axis (crankshaft) in vehicle axes
                res.CrankAlongY = Math.Abs(crank[1]) > Math.Abs(crank[0]);

                var used = new Dictionary<string, Dictionary<int, int>>();   // source id -> old index -> new index
                var outPrims = new List<Prim>();
                double[] mn = { 1e9, 1e9, 1e9 }, mx = { -1e9, -1e9, -1e9 };
                foreach (var pr in prims)
                {
                    int vo = int.Parse(pr.Inputs.First(i => i[0] == "VERTEX")[2], Num.Inv);
                    var outP = new List<int>();
                    for (int t = 0; t + 3 * pr.Stride - 1 < pr.P.Length; t += 3 * pr.Stride)
                    {
                        if (!keep.Contains(Find(par, pr.P[t + vo]))) continue;
                        for (int c = 0; c < 3; c++)
                            foreach (var inp in pr.Inputs)
                            {
                                string sid = inp[0] == "VERTEX" ? posSourceId : inp[1];
                                int old = pr.P[t + c * pr.Stride + int.Parse(inp[2], Num.Inv)];
                                Dictionary<int, int> map;
                                if (!used.TryGetValue(sid, out map)) used[sid] = map = new Dictionary<int, int>();
                                int nw;
                                if (!map.TryGetValue(old, out nw)) map[old] = nw = map.Count;
                                outP.Add(nw);
                            }
                    }
                    if (outP.Count == 0) continue;
                    // inputs are rewritten with consecutive offsets in the order listed
                    outPrims.Add(new Prim { Material = pr.Material, Inputs = pr.Inputs, P = outP.ToArray(), Stride = pr.Inputs.Count });
                    res.Triangles += outP.Count / (3 * pr.Inputs.Count);
                }

                foreach (var pr in prims)
                    foreach (var inp in pr.Inputs)
                    {
                        string sem = inp[0];
                        string sid = sem == "VERTEX" ? posSourceId : inp[1];
                        if (sources.ContainsKey(sid) && (sem == "NORMAL" || sem.Contains("TANGENT") || sem.Contains("BINORMAL"))) sources[sid].Rotate = true;
                    }

                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n");
                sb.Append("<asset><contributor><authoring_tool>AutomationToBeamNG (from an Automation export)</authoring_tool></contributor><unit name=\"meter\" meter=\"1\"/><up_axis>Z_UP</up_axis></asset>\n");
                var matNames = outPrims.Select(p => p.Material).Distinct().ToList();
                Func<string, string> newMat = m => res.MeshName + "_" + Regex.Replace(m, "[^A-Za-z0-9]+", "_");
                sb.Append("<library_effects>");
                foreach (var m in matNames) sb.Append("<effect id=\"").Append(newMat(m)).Append("-fx\"><profile_COMMON><technique sid=\"common\"><lambert/></technique></profile_COMMON></effect>");
                sb.Append("</library_effects>\n<library_materials>");
                foreach (var m in matNames) sb.Append("<material id=\"").Append(newMat(m)).Append("\" name=\"").Append(newMat(m)).Append("\"><instance_effect url=\"#").Append(newMat(m)).Append("-fx\"/></material>");
                sb.Append("</library_materials>\n<library_geometries><geometry id=\"").Append(res.MeshName).Append("-lib\" name=\"").Append(res.MeshName).Append("Mesh\"><mesh>\n");
                Func<string, string> newId = id => res.MeshName + "-" + Regex.Replace(id.StartsWith(e.MeshName) ? id.Substring(e.MeshName.Length).TrimStart('-') : id, "[^A-Za-z0-9_]+", "_");
                foreach (var kv in used)
                {
                    var s = sources[kv.Key];
                    int stride = s.IsPos ? 3 : s.Stride;
                    var data = new double[kv.Value.Count * stride];
                    foreach (var m in kv.Value)
                    {
                        if (s.IsPos)
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                double v = world[m.Key * 3 + k] - centre[k];
                                data[m.Value * 3 + k] = v;
                                mn[k] = Math.Min(mn[k], v); mx[k] = Math.Max(mx[k], v);
                            }
                        }
                        else if (s.Rotate && stride >= 3)
                        {
                            double ax = s.Data[m.Key * stride], ay = s.Data[m.Key * stride + 1], az = s.Data[m.Key * stride + 2];
                            if (nodeMatrix != null && nodeMatrix.Length == 16)
                            {
                                var nm = nodeMatrix;
                                double bx = nm[0] * ax + nm[1] * ay + nm[2] * az, by = nm[4] * ax + nm[5] * ay + nm[6] * az, bz = nm[8] * ax + nm[9] * ay + nm[10] * az;
                                ax = bx; ay = by; az = bz;
                            }
                            for (int k = 0; k < 3; k++) data[m.Value * stride + k] = R[k, 0] * ax + R[k, 1] * ay + R[k, 2] * az;
                            for (int k = 3; k < stride; k++) data[m.Value * stride + k] = s.Data[m.Key * stride + k];
                        }
                        else Array.Copy(s.Data, m.Key * stride, data, m.Value * stride, stride);
                    }
                    string id = s.IsPos ? res.MeshName + "-POSITION" : newId(s.Id);
                    sb.Append("<source id=\"").Append(id).Append("\"><float_array id=\"").Append(id).Append("-array\" count=\"").Append(data.Length).Append("\">");
                    for (int i = 0; i < data.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("0.######", Num.Inv)); }
                    sb.Append("</float_array><technique_common><accessor source=\"#").Append(id).Append("-array\" count=\"").Append(kv.Value.Count).Append("\" stride=\"").Append(stride).Append("\">")
                      .Append(s.IsPos ? "<param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/>" : s.ParamXml).Append("</accessor></technique_common></source>\n");
                }
                sb.Append("<vertices id=\"").Append(res.MeshName).Append("-VERTEX\"><input semantic=\"POSITION\" source=\"#").Append(res.MeshName).Append("-POSITION\"/></vertices>\n");
                foreach (var pr in outPrims)
                {
                    sb.Append("<triangles material=\"").Append(newMat(pr.Material)).Append("\" count=\"").Append(pr.P.Length / (3 * pr.Inputs.Count)).Append("\">");
                    for (int i = 0; i < pr.Inputs.Count; i++)
                    {
                        var inp = pr.Inputs[i];
                        string srcRef = inp[0] == "VERTEX" ? res.MeshName + "-VERTEX" : newId(inp[1]);
                        sb.Append("<input semantic=\"").Append(inp[0]).Append("\" source=\"#").Append(srcRef).Append("\" offset=\"").Append(i).Append("\"");
                        if (!string.IsNullOrEmpty(inp[3])) sb.Append(" set=\"").Append(inp[3]).Append("\"");
                        sb.Append("/>");
                    }
                    sb.Append("<p>");
                    for (int i = 0; i < pr.P.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(pr.P[i]); }
                    sb.Append("</p></triangles>\n");
                }
                sb.Append("</mesh></geometry></library_geometries>\n<library_visual_scenes><visual_scene id=\"Scene\" name=\"Scene\"><node id=\"").Append(res.MeshName)
                  .Append("\" name=\"").Append(res.MeshName).Append("\"><instance_geometry url=\"#").Append(res.MeshName).Append("-lib\"><bind_material><technique_common>");
                foreach (var m in matNames) sb.Append("<instance_material symbol=\"").Append(newMat(m)).Append("\" target=\"#").Append(newMat(m)).Append("\"/>");
                sb.Append("</technique_common></bind_material></instance_geometry></node></visual_scene></library_visual_scenes>\n<scene><instance_visual_scene url=\"#Scene\"/></scene>\n</COLLADA>\n");
                res.Files.Add(new KeyValuePair<string, byte[]>(res.Folder + res.MeshName + ".dae", new UTF8Encoding(false).GetBytes(sb.ToString())));
                for (int k = 0; k < 3; k++) res.Size[k] = mx[k] - mn[k];

                // ---- materials (renamed so they can't clash with the export's own) and their textures
                var wanted = new HashSet<string>(matNames);
                var outMats = new Dictionary<string, object>();
                var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = src.Files.ToList();
                foreach (var mf in files.Where(f => f.StartsWith(e.VehicleDir, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".materials.json", StringComparison.OrdinalIgnoreCase)))
                {
                    var mats = JBeamParser.Parse(src.ReadText(mf)) as Dictionary<string, object>;
                    if (mats == null) continue;
                    foreach (var kv in mats)
                    {
                        var mat = kv.Value as Dictionary<string, object>;
                        if (mat == null) continue;
                        string mapTo = (mat.ContainsKey("mapTo") ? mat["mapTo"] as string : null) ?? kv.Key;
                        if (!wanted.Contains(mapTo) && !wanted.Contains(kv.Key)) continue;
                        string nm = newMat(wanted.Contains(mapTo) ? mapTo : kv.Key);
                        mat["name"] = nm;
                        mat["mapTo"] = nm;
                        mat.Remove("persistentId");
                        CollectTextures(mat, textures);
                        outMats[nm] = mat;
                    }
                }
                foreach (var m in matNames) res.Materials.Add(newMat(m));
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(outMats);
                res.Files.Add(new KeyValuePair<string, byte[]>(res.Folder + res.MeshName + ".materials.json", Encoding.UTF8.GetBytes(json)));
                foreach (var t in textures)
                {
                    string name = t.Replace('\\', '/');
                    name = name.Substring(name.LastIndexOf('/') + 1);
                    string f = files.FirstOrDefault(x => x.StartsWith(e.VehicleDir, StringComparison.OrdinalIgnoreCase) && x.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
                    if (f != null) res.Files.Add(new KeyValuePair<string, byte[]>(res.Folder + name, src.ReadBytes(f)));
                }
            }
            return res;
        }

        // Texture file names are the string values in the material's stages that look like images;
        // they are made relative (bare file names) since we copy them next to the materials file.
        static void CollectTextures(Dictionary<string, object> mat, HashSet<string> textures)
        {
            object stages;
            if (!mat.TryGetValue("Stages", out stages) || !(stages is List<object>)) return;
            foreach (var st in ((List<object>)stages).OfType<Dictionary<string, object>>())
                foreach (var k in st.Keys.ToList())
                {
                    var v = st[k] as string;
                    if (v == null || !Regex.IsMatch(v, "\\.(dds|png|jpg|jpeg|tga)$", RegexOptions.IgnoreCase)) continue;
                    textures.Add(v);
                    string bare = v.Replace('\\', '/');
                    st[k] = bare.Substring(bare.LastIndexOf('/') + 1);
                }
        }
    }
}
