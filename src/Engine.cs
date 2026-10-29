// Reading Automation .engine exports and estimating a torque curve from the design.
//
// The .engine file only stores the design (bore, stroke, cams, boost, fuel ...), not the dyno
// results Automation shows in-game, so the curve comes from a simplified physics model. The
// user can type Automation's peak torque/power to reshape it (see EngineModel.Calibrate).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    static class Num
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string F(double v, string fmt) { return v.ToString(fmt, Inv); }
        public static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }
        public const double KWPerHP = 0.7457;
    }

    // Minimal Lua table reader (enough for Automation exports)
    class LuaReader
    {
        static readonly Regex KeyRe = new Regex(@"\G(?:([A-Za-z_][A-Za-z0-9_]*)|\[""([^""]*)""\]|\[(\d+)\])\s*=");
        readonly string s;
        int p;

        LuaReader(string text) { s = text; }

        public static Dictionary<string, object> ParseFile(string path)
        {
            var r = new LuaReader(File.ReadAllText(path));
            r.p = r.s.IndexOf('{');
            if (r.p < 0) throw new InvalidDataException("Not an Automation export: " + Path.GetFileName(path));
            return r.Table();
        }

        void SkipWs()
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (char.IsWhiteSpace(c) || c == ',' || c == ';') { p++; continue; }
                if (c == '-' && p + 1 < s.Length && s[p + 1] == '-') { while (p < s.Length && s[p] != '\n') p++; continue; }
                break;
            }
        }

        object Value()
        {
            SkipWs();
            if (p >= s.Length) throw new InvalidDataException("Unexpected end of file");
            char c = s[p];
            if (c == '{') return Table();
            if (c == '"' || c == '\'')
            {
                p++;
                var sb = new StringBuilder();
                while (p < s.Length && s[p] != c) { if (s[p] == '\\') p++; sb.Append(s[p]); p++; }
                p++;
                return sb.ToString();
            }
            int start = p;
            while (p < s.Length && s[p] != ',' && s[p] != ';' && s[p] != '}' && !char.IsWhiteSpace(s[p])) p++;
            string tok = s.Substring(start, p - start);
            if (tok == "true") return true;
            if (tok == "false") return false;
            if (tok == "nil") return null;
            double d;
            if (double.TryParse(tok, NumberStyles.Float, Num.Inv, out d)) return d;
            return tok;
        }

        Dictionary<string, object> Table()
        {
            p++;  // '{'
            var t = new Dictionary<string, object>();
            int i = 1;
            while (true)
            {
                SkipWs();
                if (p >= s.Length) throw new InvalidDataException("Unclosed table");
                if (s[p] == '}') { p++; return t; }
                var m = KeyRe.Match(s, p);
                if (m.Success)
                {
                    string key = m.Groups[1].Length > 0 ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                    p += m.Length;
                    t[key] = Value();
                }
                else
                {
                    t[(i++).ToString(Num.Inv)] = Value();
                }
            }
        }
    }

    class EngineSpec
    {
        const RegexOptions I = RegexOptions.IgnoreCase;

        public string File, Name, Layout, Head, FuelType, Aspiration, ChargerKind, Intake, IntakeMani, Cat, Muffler, Crank, VariantUID, Setup, Charger2;
        public int Cylinders, Valves, ExhaustCount;
        public double Bore, Stroke, DisplacementL, RPMLimit, Compression, Cam, FuelMap, MaxBoost, TurbineSize, Intercooler, ExhaustDiaIn;
        public bool VVT, VVL;

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k)
        {
            object o;
            return (d.TryGetValue(k, out o) ? o as Dictionary<string, object> : null) ?? new Dictionary<string, object>();
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, Num.Inv) : "";
        }

        static double Dbl(Dictionary<string, object> d, string k, double def)
        {
            object o;
            return d.TryGetValue(k, out o) && o is double ? (double)o : def;
        }

        public static EngineSpec Load(string path)
        {
            var root = LuaReader.ParseFile(path);
            var v = Sub(root, "Variant");
            var f = Sub(root, "Family");
            var e = new EngineSpec();
            e.File = path;

            string cfg = Str(f, "BlockConfig");
            var cm = Regex.Match(cfg, @"(\d+)_Name$");
            e.Cylinders = cm.Success ? int.Parse(cm.Groups[1].Value, Num.Inv) : 4;
            e.Layout = Regex.IsMatch(cfg, @"_V\d", I) ? "V" : Regex.IsMatch(cfg, "Box|Flat", I) ? "boxer" : "inline";

            e.Bore = Dbl(v, "Bore", Dbl(f, "Bore", 80));
            e.Stroke = Dbl(v, "Stroke", Dbl(f, "Stroke", 80));
            e.DisplacementL = Math.PI / 4 * Math.Pow(e.Bore / 10, 2) * (e.Stroke / 10) * e.Cylinders / 1000;

            var vm = Regex.Match(Str(f, "Valves"), @"_(\d+)_Name");
            e.Valves = vm.Success ? int.Parse(vm.Groups[1].Value, Num.Inv) : 4;

            e.Name = (Str(f, "Name") + " " + Str(v, "Name")).Trim();
            if (e.Name.Length == 0) e.Name = Path.GetFileNameWithoutExtension(path);
            e.Head = Str(f, "Head");
            e.RPMLimit = Dbl(v, "RPMLimit", 6500);
            e.Compression = Dbl(v, "Compression", 10);
            e.Cam = Dbl(v, "CamProfileSetting", 0.5);
            e.VVT = Str(v, "VVT").Length > 0 && !Regex.IsMatch(Str(v, "VVT"), "None", I);
            e.VVL = Str(v, "VVL").Length > 0 && !Regex.IsMatch(Str(v, "VVL"), "None", I);
            e.FuelMap = Dbl(v, "FuelMap", 0.5);
            e.FuelType = Str(v, "FuelType");
            e.Aspiration = Str(v, "AspirationType");
            e.ChargerKind = Str(v, "AspirationItemOption_1");
            e.MaxBoost = Dbl(v, "ChargerMaxBoost_1", 0);
            e.TurbineSize = Dbl(v, "TurbineSize_1", 40);
            e.Intercooler = Dbl(v, "IntercoolerSetting", 0);
            e.Intake = Str(v, "Intake");
            e.IntakeMani = Str(v, "IntakeManifold");
            e.ExhaustDiaIn = Dbl(v, "ExhaustDiameter", 2);
            e.ExhaustCount = Str(v, "ExhaustCount").Contains("_2_") ? 2 : 1;
            e.Cat = Str(v, "Cat");
            e.Muffler = Str(v, "Muffler1");
            e.Crank = Str(v, "Crank");
            e.VariantUID = Str(v, "UID");
            e.Setup = Str(v, "AspirationSetup");
            e.Charger2 = Str(v, "AspirationItemOption_2");
            return e;
        }

        public bool IsNatural { get { return Regex.IsMatch(Aspiration, "Natural", I) || MaxBoost <= 0; } }
        public bool IsTwinCharged { get { return !IsNatural && Regex.IsMatch(Aspiration, "Twin_?Charged", I); } }
        // twin-charged engines count as turbo: the primary charger (option 1) is the turbo
        public bool IsTurbo { get { return !IsNatural && (Regex.IsMatch(Aspiration, "Turbo", I) || Regex.IsMatch(ChargerKind, "^Turbo", I)); } }

        // ---- display text ----
        public string LayoutShort { get { return (Layout == "V" ? "V" : Layout == "boxer" ? "F" : "I") + Cylinders; } }

        public string LayoutLong
        {
            get { return (Layout == "V" ? "V" : Layout == "boxer" ? "Flat-" : "Inline-") + Cylinders + ", " + Valves + " valves/cyl"; }
        }

        public string SizeText
        {
            get { return DisplacementL < 1 ? Num.F(DisplacementL * 1000, "0") + " cc" : Num.F(DisplacementL, "0.0") + " L"; }
        }

        public string InductionText
        {
            get
            {
                if (IsNatural) return "Naturally aspirated";
                string sub = Regex.Replace(ChargerKind, @"^(Supercharger|Turbo(charger)?)_|_Name$", "", I).Replace('_', ' ').ToLowerInvariant();
                string kind;
                if (IsTwinCharged) kind = "Twin-charged (turbo + " + Regex.Replace(Charger2, @"^Supercharger_|_Name$", "", I).Replace('_', ' ').ToLowerInvariant() + " supercharger)";
                else if (IsTurbo)
                {
                    kind = Regex.IsMatch(Setup, "Quad", I) ? "Quad-turbo" : Regex.IsMatch(Setup, "Twin", I) ? "Twin-turbo" : "Turbo";
                    if (sub.Length > 0) kind += " (" + sub + ")";
                }
                else kind = "Supercharged" + (sub.Length > 0 ? " (" + sub + ")" : "");
                return kind + ", " + Num.F(MaxBoost, "0.##") + " bar";
            }
        }

        public string FuelText
        {
            get
            {
                string f = Regex.Replace(FuelType, @"^FuelType_|_Name$", "");
                f = Regex.Replace(f, "([a-z])([A-Z])", "$1 $2");
                return f.Length > 0 ? f : "Gasoline";
            }
        }

        public string BoreStrokeText { get { return Num.F(Bore, "0.0") + " × " + Num.F(Stroke, "0.0") + " mm"; } }
    }

    struct Peaks
    {
        public double TorqueNm, TorqueRPM, PowerKW, PowerRPM;
        public double PowerHP { get { return PowerKW / Num.KWPerHP; } }
    }

    static class EngineModel
    {
        const RegexOptions I = RegexOptions.IgnoreCase;

        // Energy released per kg of air relative to gasoline at stoichiometric.
        public static double FuelFactor(string fuel)
        {
            if (Regex.IsMatch(fuel, "Nitro", I))
            {
                double frac = 1.0;
                var nm = Regex.Match(fuel, @"NM(\d+)", I);
                if (nm.Success) frac = int.Parse(nm.Groups[1].Value, Num.Inv) / 100.0;
                return 1.04 + (2.2 - 1.04) * frac;   // remainder is methanol
            }
            if (Regex.IsMatch(fuel, "Methanol", I)) return 1.04;
            if (Regex.IsMatch(fuel, "Ethanol|E85|E100", I)) return 1.02;
            return 1.0;
        }

        // Boost (bar, gauge) delivered at a given rpm.
        public static double Boost(EngineSpec s, double rpm)
        {
            if (s.IsNatural) return 0;
            double x = rpm / s.RPMLimit;
            if (s.IsTurbo)
            {
                double spool = Num.Clamp(0.2 + s.TurbineSize / 100, 0.25, 0.8);
                return s.MaxBoost / (1 + Math.Exp(-12 * (x - spool)));
            }
            if (Regex.IsMatch(s.ChargerKind, "Centrifugal", I)) return s.MaxBoost * Math.Pow(Num.Clamp(x, 0, 1), 2);
            return s.MaxBoost * (1 - Math.Exp(-6 * x));   // Roots / twin-screw: near full boost from low rpm
        }

        public static double TorqueAt(EngineSpec s, double rpm)
        {
            if (rpm <= 0) return 0;
            // Volumetric efficiency: asymmetric bell around a cam-dependent peak.
            double vePeak = 0.86 + new[] { 0.0, 0.03, 0.07, 0.08 }[(int)Num.Clamp(s.Valves, 2, 5) - 2];
            if (Regex.IsMatch(s.Intake, "Race|Sport|Performance", I)) vePeak += 0.02;
            if (Regex.IsMatch(s.IntakeMani, "PerCyl|ITB|Individual", I)) vePeak += 0.03;
            if (s.VVL) vePeak += 0.02;
            if (Regex.IsMatch(s.Head, "OHV|Pushrod", I)) vePeak -= 0.04;
            double rpmPeak = s.RPMLimit * (0.5 + 0.35 * s.Cam);
            double wLow = rpmPeak * (0.7 + (s.VVT ? 0.25 : 0) - 0.2 * s.Cam);
            double wHigh = rpmPeak * 0.45;
            double w = rpm < rpmPeak ? wLow : wHigh;
            double ve = Math.Max(0.3, vePeak * Math.Exp(-Math.Pow((rpm - rpmPeak) / w, 2)));

            // Charge density from boost, with compressor heating and intercooling.
            double pr = 1 + Boost(s, rpm) / 1.013;
            const double t1 = 298.0;
            double t2 = t1 * (1 + (Math.Pow(pr, 0.286) - 1) / 0.70);
            double tc = t2 - Num.Clamp(s.Intercooler, 0, 1) * (t2 - t1);
            double density = pr * t1 / tc;

            // Indicated MEP from air mass, fuel energy and Otto-cycle efficiency.
            double etaRel = (1 - Math.Pow(s.Compression, -0.3)) / (1 - Math.Pow(10, -0.3));
            double fuelMap = 1 + 0.06 * (s.FuelMap - 0.5);
            double imepBar = 13.4 * ve * density * etaRel * FuelFactor(s.FuelType) * fuelMap;

            // Friction MEP (Heywood-style), grows with rpm.
            double k = rpm / 1000;
            double fmepBar = 0.97 + 0.15 * k + 0.05 * k * k;

            // Exhaust restriction scales with air mass flow vs. pipe capacity.
            double airKgS = s.DisplacementL / 1000 * rpm / 120 * 1.18 * ve * density;
            double capKgS = 0.12 * Math.Pow(s.ExhaustDiaIn, 2) * s.ExhaustCount;
            double restr = 0.15;
            if (!Regex.IsMatch(s.Cat, "None", I)) restr += 0.05;
            if (!Regex.IsMatch(s.Muffler, "None|Straight", I)) restr += 0.05;
            double exh = 1 / (1 + restr * Math.Pow(airKgS / capKgS, 2));

            double bmepPa = Math.Max(0.0, imepBar * exh - fmepBar) * 1e5;
            return bmepPa * (s.DisplacementL / 1000) / (4 * Math.PI);
        }

        // Torque table as [rpm, Nm] rows, 250 rpm apart, falling off past the physical limit.
        public static List<double[]> Curve(EngineSpec s)
        {
            double maxRpm = Math.Ceiling((s.RPMLimit + 1500) / 500) * 500;
            var pts = new List<double[]>();
            for (double r = 0; r <= maxRpm; r += 250)
            {
                double tq = TorqueAt(s, r);
                if (r > s.RPMLimit + 500) tq *= Math.Max(0.1, 1 - (r - s.RPMLimit - 500) / 1500);
                pts.Add(new[] { r, tq });
            }
            return pts;
        }

        public static Peaks GetPeaks(List<double[]> curve, double limit)
        {
            var pk = new Peaks();
            foreach (var p in curve)
            {
                if (p[0] > limit) break;
                double kw = p[1] * p[0] * 2 * Math.PI / 60 / 1000;
                if (p[1] > pk.TorqueNm) { pk.TorqueNm = p[1]; pk.TorqueRPM = p[0]; }
                if (kw > pk.PowerKW) { pk.PowerKW = kw; pk.PowerRPM = p[0]; }
            }
            return pk;
        }

        static List<double[]> Tilt(List<double[]> c, double a, double limit)
        {
            var r = new List<double[]>(c.Count);
            foreach (var p in c) r.Add(new[] { p[0], Math.Max(0.0, p[1] * (1 + a * (p[0] / limit - 0.5))) });
            return r;
        }

        static List<double[]> Scale(List<double[]> c, double k)
        {
            var r = new List<double[]>(c.Count);
            foreach (var p in c) r.Add(new[] { p[0], p[1] * k });
            return r;
        }

        // Reshape the estimated curve to hit the numbers Automation reported (0 = not given).
        public static List<double[]> Calibrate(List<double[]> curve, EngineSpec s, double targetT, double targetKW, out string warning)
        {
            warning = null;
            if (targetT > 0 && targetKW > 0)
            {
                double want = targetKW / targetT, lo = -1.5, hi = 1.5;
                for (int i = 0; i < 40; i++)
                {
                    double mid = (lo + hi) / 2;
                    var pk = GetPeaks(Tilt(curve, mid, s.RPMLimit), s.RPMLimit);
                    if (pk.PowerKW / pk.TorqueNm < want) lo = mid; else hi = mid;
                }
                curve = Tilt(curve, (lo + hi) / 2, s.RPMLimit);
            }
            var peaks = GetPeaks(curve, s.RPMLimit);
            double scale = 1;
            if (targetT > 0) scale = targetT / peaks.TorqueNm;
            else if (targetKW > 0) scale = targetKW / peaks.PowerKW;
            var result = Scale(curve, scale);
            if (targetT > 0 && targetKW > 0)
            {
                var chk = GetPeaks(result, s.RPMLimit);
                if (Math.Abs(chk.PowerKW - targetKW) / targetKW > 0.03)
                    warning = "Both numbers can't be reached below the " + Num.F(s.RPMLimit, "0") + " rpm limit. Torque matches; power comes out at "
                        + Num.F(chk.PowerHP, "0") + " hp.";
            }
            return result;
        }
    }
}
