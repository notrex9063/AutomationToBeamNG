// Builds an engine's 3D model from Automation's own parts (see AutomationParts.cs), driven by
// the .engine file. Automation assembles engines at runtime: a block is a front piece, one
// middle piece per inner cylinder and a rear piece; the head, sump and intake follow the same
// pattern, and the front cover sits ahead of cylinder 1. All parts are authored for a 0.10 m
// cylinder pitch with cylinder 1 at x = 0; the builder places them per cylinder and scales them
// to the engine's bore and stroke.
//
// First cut (phase 2): inline engines; block, head, sump, front cover, water pump and
// per-cylinder intake. The sizes are estimates until checked against Automation's own export.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    class BuiltEngine
    {
        public List<AutomationParts.Placed> Parts = new List<AutomationParts.Placed>();
        public List<string> Notes = new List<string>();
        public List<string> Missing = new List<string>();
    }

    static class EngineBuilder
    {
        const double Pitch = 0.10;          // the parts' authored cylinder spacing (m)
        const double RefBore = 80, RefStroke = 80;

        public static BuiltEngine Build(PakArchive pak, EngineSpec s)
        {
            var res = new BuiltEngine();
            if (s.Layout != "inline") { res.Notes.Add(s.LayoutLong + " engines aren't supported by the builder yet (inline only)"); return res; }
            int n = Math.Max(1, s.Cylinders);

            // bore sets the cylinder spacing and the block's width, stroke its height
            double sx = (s.Bore + 20) / (RefBore + 20);
            double sy = (s.Bore + 60) / (RefBore + 60);
            double sz = (s.Stroke + 100) / (RefStroke + 100);
            double pitch = Pitch * sx;
            Func<int, double> cyl = i => i * pitch;

            var cache = new Dictionary<string, UMesh>();
            Func<string, UMesh> mesh = name =>
            {
                UMesh m;
                if (cache.TryGetValue(name, out m)) return m;
                string p = AutomationParts.FindPart(pak, name);
                if (p != null) { try { m = AutomationParts.LoadMesh(pak, p); } catch (Exception) { m = null; } }
                if (m == null && !res.Missing.Contains(name)) res.Missing.Add(name);
                cache[name] = m;
                return m;
            };
            // place a part authored at cylinder 1 so that its origin sits at (x, y, z), scaled
            Action<string, double, double, double, double, double, double> put = (name, x, y, z, kx, ky, kz) =>
            {
                var m = mesh(name);
                if (m == null) return;
                var pl = new AutomationParts.Placed { Mesh = m };
                pl.M = new[] { kx, 0, 0, x, 0, ky, 0, y, 0, 0, kz, z };
                res.Parts.Add(pl);
            };
            Action<string, int> perCyl = (name, i) => put(name, cyl(i), 0, 0, sx, sy, sz);

            // ---- block
            bool ohv = Regex.IsMatch(s.Head ?? "", "OHV|Pushrod", RegexOptions.IgnoreCase);
            string bf = ohv ? "INL_BLOCK_FRONT_OHV" : "INL_OHC_BLOCK_FRONT";
            string bm = ohv ? "INL_BLOCK_MIDDLE_OHV" : "INL_OHC_BLOCK_MIDDLE";
            string br = ohv ? "INL_BLOCK_REAR_OHV" : "INL_OHC_BLOCK_REAR";
            perCyl(bf, 0);
            for (int i = 1; i < n - 1; i++) perCyl(bm, i);
            if (n > 1) perCyl(br, n - 1);
            var top = mesh(bm) ?? mesh(bf);
            double deck = top != null ? top.Bounds()[5] * sz : 0.18 * sz;

            // ---- head
            string head = HeadPrefix(s);
            if (head != null)
            {
                put(head + "_FRONT", cyl(0), 0, deck, sx, sy, sz);
                for (int i = 0; i < n; i++) put(head + "_MID", cyl(i), 0, deck, sx, sy, sz);
                put(head + "_REAR", cyl(n - 1), 0, deck, sx, sy, sz);
            }
            else res.Notes.Add("head type " + s.Head + " isn't mapped yet");

            // ---- sump: the front piece covers cylinders 1-2, middles the rest, the rear closes it
            perCyl("INL_SUMP_FRONT", 0);
            for (int i = 2; i < n; i++) perCyl("INL_SUMP_MID", i);
            perCyl("INL_SUMP_REAR", n - 1);

            // ---- timing cover and water pump ahead of cylinder 1
            put(FrontCover(s, ohv), cyl(0), 0, 0, sx, sy, sz);
            put(ohv ? "INL_OHV_WATERPUMP" : "INL_OHC_WATERPUMP", cyl(0), 0, 0, sx, sy, sz);

            // ---- cam / rocker cover: the set chosen in Automation, by its mesh ID
            var cover = RockerCover(pak, s);
            if (cover != null)
            {
                Action<string, int> coverPiece = (key, i) =>
                {
                    string bp;
                    if (!cover.TryGetValue(key, out bp)) return;
                    foreach (var am in AutomationParts.ActorMeshes(pak, bp))
                    {
                        UMesh m;
                        try { m = AutomationParts.LoadMesh(pak, am.Key); } catch (Exception) { continue; }
                        var pl = new AutomationParts.Placed { Mesh = m };
                        pl.M = AutomationParts.Compose(new[] { sx, 0, 0, cyl(i), 0, sy, 0, 0, 0, 0, sz, deck }, am.Value);
                        res.Parts.Add(pl);
                    }
                };
                coverPiece("FrontPiece", 0);
                coverPiece("FrontMidPiece", 0);
                for (int i = 1; i < n - 1; i++) coverPiece("MidPiece", i);
                coverPiece(n > 1 ? "RearMidPiece" : "MidPiece", n - 1);
                coverPiece("RearPiece", n - 1);
            }
            else res.Notes.Add("rocker cover " + (s.RockerCoverUID ?? "?") + " not found");

            // ---- exhaust headers, one mesh for the bank, hung off the head's header position
            string hdr = HeaderAsset(pak, s);
            if (hdr != null && head != null)
            {
                var hm = mesh(head + "_MID");
                var sock = hm == null ? null : hm.Sockets.FirstOrDefault(k => k.Name == "HeaderPositionLeft");
                double hy = (sock != null ? sock.Location[1] : -0.078) * sy, hz = deck + (sock != null ? sock.Location[2] : 0.035) * sz;
                double centre = cyl(n - 1) / 2;
                foreach (var am in AutomationParts.ActorMeshes(pak, hdr))
                {
                    UMesh m;
                    try { m = AutomationParts.LoadMesh(pak, am.Key); } catch (Exception) { continue; }
                    var pl = new AutomationParts.Placed { Mesh = m };
                    var at = AutomationParts.Compose(new[] { sx, 0, 0, centre, 0, sy, 0, hy, 0, 0, sz, hz },
                        AutomationParts.Transform(new double[3], sock != null ? sock.Rotation : new double[] { 0, 0, 90 }, new double[] { 1, 1, 1 }));
                    pl.M = AutomationParts.Compose(at, am.Value);
                    res.Parts.Add(pl);
                }
                res.Notes.Add("headers: " + AutomationParts.ShortName(hdr));
            }

            // ---- intake on the head's intake side (pieces are authored at their cylinder's offset)
            string intake = IntakePrefix(s);
            if (intake != null && head != null && IncludeIntake)
            {
                var hm = mesh(head + "_MID");
                var sock = hm == null ? null : hm.Sockets.FirstOrDefault(k => k.Name == "IntakePosition");
                double iy = sock != null ? sock.Location[1] * sy : 0.081 * sy, iz = deck + (sock != null ? sock.Location[2] : 0.039) * sz;
                put(intake + "_FRONT", cyl(0), iy, iz, sx, sy, sz);
                for (int i = 1; i < n - 1; i++) put(intake + "_MID", cyl(i) - Pitch * sx, iy, iz, sx, sy, sz);
                if (n > 1) put(intake + "_REAR", cyl(n - 1) - 2 * Pitch * sx, iy, iz, sx, sy, sz);
            }

            res.Notes.Add(string.Format("{0} cylinders, pitch {1:0.000} m, scale {2:0.00}/{3:0.00}/{4:0.00}", n, pitch, sx, sy, sz));
            return res;
        }

        // The built engine as BeamNG files the converter fits like a model cut from an export:
        // centred on its bounding box, crankshaft along x.
        public static EngineMeshFiles ToMeshFiles(EngineSpec s, string safe, List<string> notes)
        {
            string game = PakArchive.FindGame();
            if (game.Length == 0) throw new InvalidOperationException("Automation's game files weren't found");
            using (var pak = new PakArchive(game))
            {
                var b = Build(pak, s);
                notes.AddRange(b.Notes);
                if (b.Missing.Count > 0) notes.Add("missing parts: " + string.Join(", ", b.Missing));
                if (b.Parts.Count == 0) throw new InvalidOperationException(b.Notes.Count > 0 ? b.Notes[0] : "no parts");
                double[] bb = { 1e9, 1e9, 1e9, -1e9, -1e9, -1e9 };
                foreach (var p in b.Parts)
                {
                    var mb = p.Mesh.Bounds();
                    for (int c = 0; c < 8; c++)
                    {
                        double x = mb[(c & 1) != 0 ? 3 : 0], y = mb[(c & 2) != 0 ? 4 : 1], z = mb[(c & 4) != 0 ? 5 : 2];
                        double[] w = { p.M[0] * x + p.M[1] * y + p.M[2] * z + p.M[3], p.M[4] * x + p.M[5] * y + p.M[6] * z + p.M[7], p.M[8] * x + p.M[9] * y + p.M[10] * z + p.M[11] };
                        for (int k = 0; k < 3; k++) { bb[k] = Math.Min(bb[k], w[k]); bb[k + 3] = Math.Max(bb[k + 3], w[k]); }
                    }
                }
                foreach (var p in b.Parts) for (int k = 0; k < 3; k++) p.M[k * 4 + 3] -= (bb[k] + bb[k + 3]) / 2;
                var res = new EngineMeshFiles { MeshName = "aeng_" + safe, Folder = "vehicles/common/automation_engines/" + safe + "/", CrankAlongY = false };
                var ex = AutomationParts.Export(pak, b.Parts, res.MeshName, res.Folder, null);
                res.Files.AddRange(ex.Files);
                res.Triangles = ex.Triangles;
                for (int k = 0; k < 3; k++) res.Size[k] = ex.Bounds[k + 3] - ex.Bounds[k];
                foreach (var f in ex.Files.Where(f => f.Key.EndsWith(".materials.json")))
                    foreach (System.Text.RegularExpressions.Match m in Regex.Matches(System.Text.Encoding.UTF8.GetString(f.Value), "\"name\":\"([^\"]+)\""))
                        res.Materials.Add(m.Groups[1].Value);
                notes.AddRange(ex.Warnings);
                return res;
            }
        }

        // the intake runners' orientation isn't worked out yet, so they're off for now
        public static bool IncludeIntake = false;

        static Dictionary<string, byte[]> coverIndex;       // rocker cover set object path -> its export data

        // The rocker cover set whose GUID matches the .engine's valve cover MeshUID: piece name -> actor path.
        static Dictionary<string, string> RockerCover(PakArchive pak, EngineSpec s)
        {
            if (string.IsNullOrEmpty(s.RockerCoverUID) || s.RockerCoverUID.Length != 32) return null;
            if (coverIndex == null)
            {
                var idx = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in pak.Files.Where(x => x.IndexOf("/Engines/Blueprints/Heads/RockerCovers/", StringComparison.OrdinalIgnoreCase) >= 0 && x.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)).ToList())
                    idx["/Game/" + f.Substring("AutomationGame/Content/".Length, f.Length - "AutomationGame/Content/".Length - 5)] = pak.Read(f);
                coverIndex = idx;
            }
            // the set's GUID property is four little-endian uint32s
            var bin = new byte[16];
            for (int k = 0; k < 4; k++) BitConverter.GetBytes(Convert.ToUInt32(s.RockerCoverUID.Substring(k * 8, 8), 16)).CopyTo(bin, k * 4);
            foreach (var kv in coverIndex)
            {
                if (IndexOf(kv.Value, bin) < 0) continue;
                var pkg = UPackage.Load(pak, kv.Key);
                var e = pkg == null ? null : pkg.FirstExport("RockerCoverSet");
                if (e == null) continue;
                var res = new Dictionary<string, string>();
                foreach (var p in pkg.Properties(e))
                {
                    var path = p.Value as string;
                    if (path == null || !p.Key.EndsWith("Piece")) continue;
                    if (path.EndsWith("_C")) path = path.Substring(0, path.LastIndexOf('.'));
                    res[p.Key] = path;
                }
                return res;
            }
            return null;
        }

        static int IndexOf(byte[] h, byte[] n)
        {
            for (int i = 0; i + n.Length <= h.Length; i++)
            {
                int j = 0;
                while (j < n.Length && h[i + j] == n[j]) j++;
                if (j == n.Length) return i;
            }
            return -1;
        }

        // Header mesh for the engine's header choice and cylinder count (first match wins).
        static string HeaderAsset(PakArchive pak, EngineSpec s)
        {
            string h = s.Headers ?? "";
            bool turbo = s.IsTurbo;
            string kind = Regex.IsMatch(h, "Race|Tubular", RegexOptions.IgnoreCase) ? "RACE" : Regex.IsMatch(h, "Long", RegexOptions.IgnoreCase) ? "LONG"
                : Regex.IsMatch(h, "Short", RegexOptions.IgnoreCase) ? "SHORTCAST" : Regex.IsMatch(h, "Low", RegexOptions.IgnoreCase) ? "LOWCAST" : "CASTLOG";
            string pre = "INL" + s.Cylinders + "_HEADERS_";
            var tries = turbo
                ? new[] { pre + "TURBO_" + kind + "1", pre + "TURBO_" + kind, pre + "TURBO_" + kind + "_L", pre + "TURBO_SHORTCAST_L", pre + "TURBO_CASTLOG_L" }
                : new[] { pre + kind + "_L", pre + kind + "_T", pre + "CASTLOG_L" };
            foreach (var t in tries)
            {
                string p = AutomationParts.FindPart(pak, t);
                if (p != null) return p;
            }
            return null;
        }

        static string HeadPrefix(EngineSpec s)
        {
            string h = s.Head ?? "";
            int v = s.Valves <= 2 ? 2 : s.Valves >= 5 ? 5 : 4;
            if (Regex.IsMatch(h, "Duel|Dual|DOHC", RegexOptions.IgnoreCase)) return "INL_DOHC_" + v + "_HEAD";
            if (Regex.IsMatch(h, "DAOHC", RegexOptions.IgnoreCase)) return "INL_DAOHC_2_HEAD";
            return null;
        }

        static string FrontCover(EngineSpec s, bool ohv)
        {
            if (ohv) return "INL_OHV_FRONTCOVER";
            string h = s.Head ?? "";
            if (Regex.IsMatch(h, "DAOHC", RegexOptions.IgnoreCase)) return "INL_DAOHC_FRONT_COVER";
            if (Regex.IsMatch(h, "Duel|Dual|DOHC", RegexOptions.IgnoreCase)) return "INL_DOHC_4_5_FRONT_COVER";
            return "INL_SOHC_FRONT_COVER";
        }

        static string IntakePrefix(EngineSpec s)
        {
            string fs = (s.FuelSystem ?? "") + " " + (s.IntakeMani ?? "");
            if (!Regex.IsMatch(fs, "Inj", RegexOptions.IgnoreCase)) return null;     // carburettors: later
            bool high = Regex.IsMatch((s.IntakeMani ?? "") + (s.Intake ?? ""), "PerCyl|Perf|High|Race", RegexOptions.IgnoreCase);
            return "INL_INTAKE_MPFI_4_" + (high ? "HIGHEND" : "MIDRANGE");
        }
    }
}
