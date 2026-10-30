// Automation's own engine behaviour, taken from the user's "Export to BeamNG" of the same engine.
// Automation's exports don't use BeamNG's combustionEngine: they ship Camshaft's "camsoEngine"
// powertrain device with a throttle x rpm torque map (part throttle included), a friction table,
// the engine's real inertia and Camshaft's turbo/supercharger model. Using those in every car we
// convert to makes the engine respond exactly as in Automation's own export.
// The exported files are Camshaft Software's, licensed CC BY-NC-SA 4.0; the license travels with
// the generated mod.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    class CamsoEngine
    {
        public string Source;                  // export description, for logs
        public string TorqueBlock, FrictionBlock, BurnBlock;
        public Dictionary<string, string> Values = new Dictionary<string, string>();   // mainEngine key -> raw jbeam value
        public string ChargerKey, ChargerBlock;
        public List<string> VariableRows = new List<string>();
        public double RevLimit, RevLimitDrop = 100;
        public List<KeyValuePair<string, byte[]>> Lua = new List<KeyValuePair<string, byte[]>>();
        public byte[] License;
        public List<double[]> FullThrottleNet = new List<double[]>();   // rpm, torque at 100% minus friction
        public List<double[]> Gross = new List<double[]>(), Friction = new List<double[]>();   // rpm, full-throttle map torque / friction
        // Automation's coolant radiator (area x effectiveness) and fan: its engine makes more heat than BeamNG's,
        // and the car's own radiator part (which BeamNG keeps) is sized for its stock engine
        public double RadiatorCoef, FanAirSpeed;

        public static readonly string[] Keys = { "idleRPM", "idleRPMRoughness", "maxRPM", "inertia", "friction", "dynamicFriction", "engineBrakeTorque",
                                                  "starterTorque", "starterMaxRPM", "maxTorqueRating", "maxOverTorqueDamage", "oilVolume", "coolantVolume",
                                                  // Automation's heat limits and oil cooler: its engine model runs hotter than BeamNG's, and with the
                                                  // car's stock limits (e.g. the Covet's 130 C cylinder walls, no oil cooler) it cooks itself at high rpm
                                                  "cylinderWallTemperatureDamageThreshold", "oilRadiatorArea", "oilRadiatorEffectiveness", "oilThermostatTemperature" };

        static readonly Regex EngineJbeam = new Regex("^vehicles/([^/]+)/eng_[^/]+/camso_engine_[^/]+\\.jbeam$", RegexOptions.IgnoreCase);

        public static CamsoEngine Load(ExportedEngine e)
        {
            using (var src = ModSource.Create(e.Source))
            {
                var files = src.Files.ToList();
                string text = null, dir = null;
                foreach (var f in files.Where(f => EngineJbeam.IsMatch(f)))
                {
                    string t = src.ReadText(f);
                    if (t.IndexOf(e.Uid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    text = t; dir = "vehicles/" + EngineJbeam.Match(f).Groups[1].Value + "/";
                    break;
                }
                if (text == null) throw new InvalidDataException("the export has no engine file for this engine");
                var c = new CamsoEngine { Source = e.Describe };
                var parts = JBeam.TopLevelParts(text);
                Func<string, string> partText = prefix => parts.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();

                string eng = partText("Camso_Engine_");
                if (eng == null) throw new InvalidDataException("the export's engine part wasn't found");
                var mb = JBeam.FindBlock(eng, "mainEngine");
                string me = eng.Substring(mb[0], mb[1] - mb[0] + 1);
                var tb = JBeam.FindBlock(me, "torque");
                var fb = JBeam.FindBlock(me, "frictionTorque");
                if (tb == null || fb == null) throw new InvalidDataException("the export's engine has no torque map");
                c.TorqueBlock = me.Substring(tb[0], tb[1] - tb[0] + 1);
                c.FrictionBlock = me.Substring(fb[0], fb[1] - fb[0] + 1);
                if (!Regex.IsMatch(c.TorqueBlock, "\\[\\s*\"throttle\"\\s*,\\s*\"rpm\"\\s*,\\s*\"torque\"")) throw new InvalidDataException("the export's torque map has an unknown layout");
                var bb = JBeam.FindBlock(me, "burnEfficiency");
                if (bb != null) c.BurnBlock = me.Substring(bb[0], bb[1] - bb[0] + 1);
                // the top-level values (the torque map and friction table are cut out first so their rows can't match)
                string flat = me.Remove(tb[0], tb[1] - tb[0] + 1);
                fb = JBeam.FindBlock(flat, "frictionTorque");
                flat = flat.Remove(fb[0], fb[1] - fb[0] + 1);
                foreach (var k in Keys)
                {
                    var m = Regex.Match(flat, "\"" + k + "\"\\s*:\\s*(-?[0-9.eE+-]+)");
                    if (m.Success) c.Values[k] = m.Groups[1].Value;
                }
                Func<string, double> num = k => { var m = Regex.Match(flat, "\"" + k + "\"\\s*:\\s*(-?[0-9.eE+-]+)"); double x; return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : 0; };
                c.RadiatorCoef = num("radiatorArea") * num("radiatorEffectiveness");
                c.FanAirSpeed = num("radiatorFanMaxAirSpeed");
                foreach (var k in new[] { "inertia", "friction", "idleRPM", "maxRPM" })
                    if (!c.Values.ContainsKey(k)) throw new InvalidDataException("the export's engine has no " + k);

                // stock engine management: rev limiter
                string mg = parts.Where(p => Regex.IsMatch(p.Key, "^Camso_EngineManagement_[0-9a-f]+$", RegexOptions.IgnoreCase)).Select(p => p.Value).FirstOrDefault();
                double v;
                if (mg != null)
                {
                    var m = Regex.Match(mg, "\"revLimiterRPM\"\\s*:\\s*([0-9.]+)");
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) c.RevLimit = v;
                    m = Regex.Match(mg, "\"revLimiterRPMDrop\"\\s*:\\s*([0-9.]+)");
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) c.RevLimitDrop = v;
                }

                // the forced induction the engine comes with (its slot's default part)
                foreach (var kind in new[] { "Turbo", "Supercharger" })
                {
                    var m = Regex.Match(eng, "\\[\\s*\"Camso_" + kind + "_[0-9a-f]+\"\\s*,\\s*\"(Camso_" + kind + "_[0-9a-f]+)\"", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string ch = parts.Where(p => p.Key.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();
                    if (ch == null) continue;
                    c.ChargerKey = kind == "Turbo" ? "turbocharger" : "supercharger";
                    var cb = JBeam.FindBlock(ch, c.ChargerKey);
                    if (cb == null) { c.ChargerKey = null; continue; }
                    c.ChargerBlock = ch.Substring(cb[0], cb[1] - cb[0] + 1);
                    var vb = JBeam.FindBlock(ch, "variables");
                    if (vb != null)
                        foreach (Match row in Regex.Matches(ch.Substring(vb[0], vb[1] - vb[0] + 1), "^\\s*(\\[\\s*\"\\$[^\\r\\n]*\\])\\s*,?\\s*$", RegexOptions.Multiline))
                            c.VariableRows.Add(row.Groups[1].Value.Trim());
                    break;
                }

                foreach (var f in files.Where(f => f.StartsWith(dir + "lua/powertrain/camso", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
                    c.Lua.Add(new KeyValuePair<string, byte[]>(f.Substring(f.LastIndexOf('/') + 1), src.ReadBytes(f)));
                if (!c.Lua.Any(l => l.Key.Equals("camsoEngine.lua", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("the export has no camsoEngine.lua");
                string lic = files.FirstOrDefault(f => f.Equals(dir + "license.txt", StringComparison.OrdinalIgnoreCase));
                if (lic != null) c.License = src.ReadBytes(lic);

                c.FullThrottleNet = NetCurve(c.TorqueBlock, c.FrictionBlock);
                c.Friction = Regex.Matches(c.FrictionBlock, "\\[\\s*([0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*\\]").Cast<Match>()
                    .Select(m => new[] { D(m.Groups[1].Value), D(m.Groups[2].Value) }).OrderBy(p => p[0]).ToList();
                c.Gross = c.FullThrottleNet.Select(p => new[] { p[0], p[1] + BeamNGSim.Lerp(c.Friction, p[0]) }).ToList();
                return c;
            }
        }

        // What the torque-scale controller does to the map at runtime (see TorqueScaleController):
        // the full-throttle torque gets the fitted parts' change relative to the default parts', and
        // the Power trim; the result minus friction is what the engine makes at full throttle.
        public List<double[]> NetWithParts(TorqueMods fitted, TorqueMods defaults, double trim)
        {
            return Gross.Select(p =>
            {
                double r = p[0], g = p[1];
                double k = Factor(r, g, fitted, defaults) * trim;
                return new[] { r, g * k - BeamNGSim.Lerp(Friction, r) };
            }).ToList();
        }

        public static double Factor(double rpm, double gross, TorqueMods fitted, TorqueMods defaults)
        {
            if (gross <= 1) return 1;
            double add = (fitted != null && fitted.Scaled ? fitted.Add(rpm) : 0) - (defaults != null && defaults.Scaled ? defaults.Add(rpm) : 0);
            double mult = (fitted != null ? fitted.Mult(rpm) : 1) / Math.Max(1e-6, defaults != null ? defaults.Mult(rpm) : 1);
            return Math.Max(0, (gross * mult + add) / gross);
        }

        // Full-throttle torque minus friction, what Automation's dyno shows.
        public static List<double[]> NetCurve(string torque, string friction)
        {
            var fr = new SortedDictionary<double, double>();
            foreach (Match m in Regex.Matches(friction, "\\[\\s*([0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*\\]"))
                fr[D(m.Groups[1].Value)] = D(m.Groups[2].Value);
            double maxThrottle = 0;
            foreach (Match m in Regex.Matches(torque, "\\[\\s*([0-9.]+)\\s*,")) maxThrottle = Math.Max(maxThrottle, D(m.Groups[1].Value));
            var res = new List<double[]>();
            foreach (Match m in Regex.Matches(torque, "\\[\\s*([0-9.]+)\\s*,\\s*([0-9.]+)\\s*,\\s*(-?[0-9.]+)"))
            {
                if (D(m.Groups[1].Value) != maxThrottle) continue;
                double rpm = D(m.Groups[2].Value);
                res.Add(new[] { rpm, D(m.Groups[3].Value) - Interp(fr, rpm) });
            }
            return res.OrderBy(p => p[0]).ToList();
        }

        static double Interp(SortedDictionary<double, double> t, double x)
        {
            if (t.Count == 0) return 0;
            double px = double.NaN, py = 0;
            foreach (var kv in t)
            {
                if (kv.Key >= x) return double.IsNaN(px) ? kv.Value : py + (kv.Value - py) * (x - px) / Math.Max(1e-9, kv.Key - px);
                px = kv.Key; py = kv.Value;
            }
            return py;
        }

        static double D(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        // The mainEngine lines that replace ours (indentation as BuildPart writes them).
        public string MainEngineText(Func<string, string> label)
        {
            var sb = new StringBuilder();
            sb.Append("        \"torque\":").Append(Reindent(TorqueBlock)).Append(",\r\n");
            sb.Append("        \"frictionTorque\":").Append(Reindent(FrictionBlock)).Append(",\r\n");
            foreach (var k in Keys)
            {
                string val;
                if (Values.TryGetValue(k, out val)) sb.Append("        \"").Append(k).Append("\":").Append(val).Append(",\r\n");
            }
            if (BurnBlock != null) sb.Append("        \"burnEfficiency\":").Append(Reindent(BurnBlock)).Append(",\r\n");
            sb.Append("        \"hasRevLimiter\":true,\r\n");
            sb.Append("        \"revLimiterRPM\":").Append(Num.F(RevLimit, "0")).Append(",\r\n");
            sb.Append("        \"revLimiterType\":\"rpmDrop\",\r\n");
            sb.Append("        \"revLimiterRPMDrop\":").Append(Num.F(RevLimitDrop, "0")).Append(",\r\n");
            sb.Append("        \"revLimiterMaxRPMDrop\":").Append(Num.F(RevLimitDrop, "0")).Append(",\r\n");
            if (ChargerKey != null) sb.Append("        \"").Append(ChargerKey).Append("\":\"").Append(ChargerKey).Append("\",\r\n");
            return sb.ToString();
        }

        static string Reindent(string block)
        {
            // keep the rows compact: one row per line
            var lines = block.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//"));
            return string.Join("\r\n            ", lines).Replace("\r\n            ]", "\r\n        ]").Replace("\r\n            }", "\r\n        }");
        }
    }
}
