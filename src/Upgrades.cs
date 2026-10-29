// Tuning sliders and optional upgrade parts generated for every converted engine. Everything uses
// BeamNG's own systems (jbeam variables, twoStepLaunch, turbocharger, nitrous slot, damage and
// thermal values), sized from the Automation engine. The defaults reproduce Automation exactly;
// the upgrades sit in empty slots so the stock engine still matches the dyno.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    static class Upgrades
    {
        // variable names are prefixed so they can't clash with other mods' ECU sliders
        public const string RevLimit = "$a2b_revLimiterRPM", Idle = "$a2b_idleRPM", CutTime = "$a2b_revLimiterCutTime",
                            Power = "$a2b_powerTrim", Boost = "$a2b_boost", Afterfire = "$a2b_afterfire",
                            TwoStep = "$a2b_twoStepRPM", KitBoost = "$a2b_kitBoost";

        public class Slot { public string Type, Default, Description; public bool Core; }

        public class Result
        {
            public List<Slot> Slots = new List<Slot>();
            public List<KeyValuePair<string, string>> Parts = new List<KeyValuePair<string, string>>();
            public string StockEcu;                                         // the default ECU part (its slider defaults are the Automation values)
            public List<KeyValuePair<string, ForcedInduction>> TurboKits = new List<KeyValuePair<string, ForcedInduction>>();
        }

        const string Author = "Automation export (AutomationToBeamNG)";

        public static Result Build(EngineData data, ForcedInduction fi, string partKey, string baseEngineText, ModelParts parts)
        {
            var r = new Result();
            var s = data.Spec;
            double L = data.RpmLimit, I = data.IdleRpm, maxRpm = L + 500, peak = data.Peaks.TorqueNm;
            string name = s.Name.Replace("\"", "'").Replace("\\", "/");

            // ---- ECU: the sliders live here (vanilla ECUs work the same way)
            double baseI = Num0(baseEngineText, "instantAfterFireCoef"), baseS = Num0(baseEngineText, "sustainedAfterFireCoef");
            double refI = baseI > 0 ? baseI : 1, refS = baseS > 0 ? baseS : 1;
            double stockAf = baseI > 0 || baseS > 0 ? 100 : 0;
            string ecuSlot = partKey + "_ecu";
            r.StockEcu = partKey + "_ecu_stock";
            r.Slots.Add(new Slot { Type = ecuSlot, Default = r.StockEcu, Description = "Engine Management (Automation)", Core = true });
            var ecus = new[]
            {
                new { Key = r.StockEcu, Label = "Automation tune", Limit = L, Cut = 0.10, Af = stockAf, TrimMax = 120.0, BoostMax = 150.0, Value = 300 },
                new { Key = partKey + "_ecu_sport", Label = "Sport tune", Limit = Math.Min(maxRpm, L + 250), Cut = 0.07, Af = Math.Max(100, stockAf * 2), TrimMax = 125.0, BoostMax = 175.0, Value = 900 },
                new { Key = partKey + "_ecu_race", Label = "Race tune", Limit = maxRpm, Cut = 0.05, Af = Math.Max(300, stockAf * 4), TrimMax = 130.0, BoostMax = 200.0, Value = 2500 },
            };
            double limMin = Round(Math.Max(I + 1000, L * 0.5), 50), idleMin = Round(Math.Max(300, I * 0.6), 25), idleMax = Round(Math.Max(I * 2, I + 1000), 25);
            foreach (var e in ecus)
            {
                var p = new StringBuilder();
                Head(p, e.Key, name + " ECU - " + e.Label, ecuSlot, e.Value);
                p.Append("    \"variables\": [\r\n        [\"name\", \"type\", \"unit\", \"category\", \"default\", \"min\", \"max\", \"title\", \"description\"],\r\n");
                Var(p, RevLimit, "rpm", e.Limit, limMin, maxRpm, "RPM Limit", "Where the rev limiter cuts in (Automation: " + Num.F(L, "0") + " rpm)", 50);
                Var(p, Idle, "rpm", I, idleMin, idleMax, "Idle RPM", "Idle speed (Automation: " + Num.F(I, "0") + " rpm)", 25);
                Var(p, CutTime, "s", e.Cut, 0.01, 0.5, "RPM Limit Cut Time", "How fast the rev limiter cycles", 0.01);
                Var(p, Power, "%", 100, 50, e.TrimMax, "Power", "Scales the engine's torque (100% = Automation's dyno)", 1);
                if (fi != null) Var(p, Boost, "%", 100, 0, e.BoostMax, "Boost", "Scales the boost pressure (100% = Automation's " + Num.F(fi.MaxPsi / ForcedInduction.PsiPerBar, "0.00") + " bar)", 1);
                Var(p, Afterfire, "%", e.Af, 0, 1000, "Backfire", "How much the exhaust pops and bangs", 5);
                p.Append("    ],\r\n");
                p.Append("    \"mainEngine\": {\r\n");
                p.Append("        \"revLimiterRPM\":\"").Append(RevLimit).Append("\",\r\n");
                p.Append("        \"idleRPM\":\"").Append(Idle).Append("\",\r\n");
                p.Append("        \"revLimiterType\":\"timeBased\",\r\n");
                p.Append("        \"revLimiterCutTime\":\"").Append(CutTime).Append("\",\r\n");
                p.Append("        \"instantAfterFireCoef\":\"$=").Append(Num.F(refI, "0.###")).Append(" * ").Append(Afterfire).Append(" / 100\",\r\n");
                p.Append("        \"sustainedAfterFireCoef\":\"$=").Append(Num.F(refS, "0.###")).Append(" * ").Append(Afterfire).Append(" / 100\",\r\n");
                p.Append("    },\r\n");
                p.Append("    \"vehicleController\": {\r\n        \"highShiftUpRPM\":\"$=").Append(RevLimit).Append(" - 300\",\r\n    },\r\n");
                r.Parts.Add(Close(e.Key, p));
            }

            // ---- two-step launch control
            string tsSlot = partKey + "_twostep";
            r.Slots.Add(new Slot { Type = tsSlot, Default = "", Description = "Launch Control (Two-Step)" });
            {
                string key = partKey + "_twostep_on";
                var p = new StringBuilder();
                Head(p, key, "Two-Step Launch Control", tsSlot, 400);
                p.Append("    \"variables\": [\r\n        [\"name\", \"type\", \"unit\", \"category\", \"default\", \"min\", \"max\", \"title\", \"description\"],\r\n");
                Var(p, TwoStep, "rpm", Round(I + (L - I) * 0.45, 100), Round(I + 500, 50), Round(L - 200, 50), "Launch RPM", "RPM the engine holds while launching", 50);
                p.Append("    ],\r\n");
                p.Append("    \"controller\": [\r\n        [\"fileName\"],\r\n        [\"twoStepLaunch\", {\"rpmLimit\":\"").Append(TwoStep).Append("\"}],\r\n    ],\r\n");
                r.Parts.Add(Close(key, p));
            }

            // ---- internals: how much abuse the engine takes before it breaks
            string inSlot = partKey + "_internals";
            r.Slots.Add(new Slot { Type = inSlot, Default = "", Description = "Engine Internals (empty = Automation's)" });
            {
                string key = partKey + "_internals_forged";
                var p = new StringBuilder();
                Head(p, key, "Forged Internals (3x torque rating)", inSlot, 3500);
                p.Append("    \"mainEngine\": {\r\n");
                p.Append("        \"maxTorqueRating\":").Append(Num.F(peak * 3, "0")).Append(",\r\n");
                p.Append("        \"maxOverTorqueDamage\":").Append(Num.F(peak * 4, "0")).Append(",\r\n");
                p.Append("        \"maxOverRevDamage\":3000,\r\n");
                p.Append("        \"headGasketDamageThreshold\":6000000,\r\n        \"pistonRingDamageThreshold\":6000000,\r\n        \"connectingRodDamageThreshold\":6000000,\r\n");
                p.Append("    },\r\n");
                r.Parts.Add(Close(key, p));
                key = partKey + "_internals_race";
                p = new StringBuilder();
                Head(p, key, "Indestructible Race Internals", inSlot, 9000);
                p.Append("    \"mainEngine\": {\r\n");
                p.Append("        \"maxTorqueRating\":-1,\r\n        \"maxOverRevDamage\":1000000000,\r\n");
                p.Append("        \"headGasketDamageThreshold\":1000000000000,\r\n        \"pistonRingDamageThreshold\":1000000000000,\r\n        \"connectingRodDamageThreshold\":1000000000000,\r\n");
                p.Append("        \"cylinderWallTemperatureDamageThreshold\":1000,\r\n        \"engineBlockTemperatureDamageThreshold\":1000,\r\n");
                p.Append("    },\r\n");
                r.Parts.Add(Close(key, p));
            }

            // ---- cooling (the radiator is a body part, so this is the oil side and the heat limits)
            string coSlot = partKey + "_cooling";
            r.Slots.Add(new Slot { Type = coSlot, Default = "", Description = "Engine Cooling Upgrade" });
            {
                string key = partKey + "_cooling_uprated";
                var p = new StringBuilder();
                Head(p, key, "Uprated Oil Cooler + Heat-Resistant Block", coSlot, 900);
                p.Append("    \"mainEngine\": {\r\n");
                p.Append("        \"oilRadiatorArea\":0.3,\r\n        \"oilRadiatorEffectiveness\":2500,\r\n");
                p.Append("        \"engineBlockTemperatureDamageThreshold\":170,\r\n        \"cylinderWallTemperatureDamageThreshold\":200,\r\n");
                p.Append("    },\r\n");
                r.Parts.Add(Close(key, p));
            }

            // ---- nitrous: the car's own N2O kits, when its stock engine didn't already offer them
            if (!baseEngineText.Contains("\"n2o_system\"") && parts.ByKey.Values.Any(t => Regex.IsMatch(t, "\"slotType\"\\s*:\\s*\"n2o_system\"")))
                r.Slots.Add(new Slot { Type = "n2o_system", Default = "", Description = "Nitrous Oxide System" });

            // ---- turbo kits for naturally aspirated engines
            if (fi == null && !data.IsBoosted)
            {
                string tkSlot = partKey + "_turbokit";
                r.Slots.Add(new Slot { Type = tkSlot, Default = "", Description = "Turbo Kit" });
                var stages = new[] { new { Bar = 0.4, Label = "Stage 1 (0.4 bar)", Value = 4000 }, new { Bar = 0.7, Label = "Stage 2 (0.7 bar, wants forged internals)", Value = 6500 },
                                     new { Bar = 1.0, Label = "Stage 3 (1.0 bar, needs forged internals)", Value = 9000 } };
                for (int i = 0; i < stages.Length; i++)
                {
                    var st = stages[i];
                    var kit = ForcedInduction.TurboKit(data, st.Bar);
                    string key = partKey + "_turbokit_" + (i + 1);
                    var p = new StringBuilder();
                    Head(p, key, "Turbo Kit " + st.Label, tkSlot, st.Value);
                    p.Append("    \"variables\": [\r\n        [\"name\", \"type\", \"unit\", \"category\", \"default\", \"min\", \"max\", \"title\", \"description\"],\r\n");
                    Var(p, KitBoost, "%", 100, 0, 150, "Turbo Kit Boost", "Scales the kit's boost (100% = " + Num.F(st.Bar, "0.0") + " bar)", 1);
                    p.Append("    ],\r\n");
                    p.Append(kit.JBeamSection(KitBoost));
                    p.Append("    \"mainEngine\": {\r\n        \"turbocharger\":\"turbocharger\",\r\n    },\r\n");
                    r.Parts.Add(Close(key, p));
                    r.TurboKits.Add(new KeyValuePair<string, ForcedInduction>(key, kit));
                }
            }
            return r;
        }

        // Adds rows to the part's slot table (old "slots" or newer "slots2" layout).
        public static string AddSlots(string part, IList<Slot> slots)
        {
            if (slots.Count == 0) return part;
            bool v2 = Regex.IsMatch(part, "\"slots2\"\\s*:");
            var rows = new StringBuilder();
            foreach (var sl in slots)
            {
                string opts = sl.Core ? ", {\"coreSlot\":true}" : "";
                if (v2) rows.Append("        [\"").Append(sl.Type).Append("\", [\"").Append(sl.Type).Append("\"], [], \"").Append(sl.Default).Append("\", \"").Append(sl.Description).Append("\"").Append(opts).Append("],\r\n");
                else rows.Append("        [\"").Append(sl.Type).Append("\", \"").Append(sl.Default).Append("\", \"").Append(sl.Description).Append("\"").Append(opts).Append("],\r\n");
            }
            var b = JBeam.FindBlock(part, v2 ? "slots2" : "slots");
            if (b == null || part[b[0]] != '[')
            {
                var mk = Regex.Match(part, "\"mainEngine\"\\s*:");
                int lineStart = part.LastIndexOf('\n', mk.Index) + 1;
                return part.Insert(lineStart, "    \"slots\": [\r\n        [\"type\", \"default\", \"description\"],\r\n" + rows + "    ],\r\n");
            }
            string body = part.Substring(b[0] + 1, b[1] - b[0] - 1).TrimEnd();
            string sep = body.EndsWith(",") || body.EndsWith("[") || body.Length == 0 ? "" : ",";
            return part.Substring(0, b[0] + 1) + body + sep + "\r\n        // ---- Automation tuning and upgrades ----\r\n" + rows + "    " + part.Substring(b[1]);
        }

        static void Head(StringBuilder p, string key, string label, string slotType, int value)
        {
            p.Append("    \"information\": {\r\n        \"authors\":\"").Append(Author).Append("\",\r\n        \"name\":\"").Append(label)
             .Append("\",\r\n        \"value\":").Append(value).Append(",\r\n    },\r\n");
            p.Append("    \"slotType\": \"").Append(slotType).Append("\",\r\n");
        }

        static KeyValuePair<string, string> Close(string key, StringBuilder p)
        {
            return new KeyValuePair<string, string>(key, "{\r\n" + p + "}");
        }

        static void Var(StringBuilder p, string name, string unit, double def, double min, double max, string title, string desc, double step)
        {
            def = Math.Max(min, Math.Min(max, def));
            p.Append("        [\"").Append(name).Append("\", \"range\", \"").Append(unit).Append("\", \"Engine\", ").Append(Num.F(def, "0.###")).Append(", ")
             .Append(Num.F(min, "0.###")).Append(", ").Append(Num.F(max, "0.###")).Append(", \"").Append(title).Append("\", \"").Append(desc.Replace("\"", "'"))
             .Append("\", {\"stepDis\":").Append(Num.F(step, "0.###")).Append("}],\r\n");
        }

        static double Round(double v, double step) { return Math.Round(v / step) * step; }

        static double Num0(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*([\\d.]+)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Num.Inv, out v) ? v : 0;
        }
    }

    // Resolves jbeam variables ("$name" and "$=" expressions) the way BeamNG does, so the converter's
    // checks see the values a part will really have with the sliders at their defaults.
    static class JBeamVars
    {
        public static Dictionary<string, double> Defaults(Dictionary<string, object> part)
        {
            var d = new Dictionary<string, double>();
            object v;
            if (part == null || !part.TryGetValue("variables", out v) || !(v is List<object>)) return d;
            foreach (var row in ((List<object>)v).OfType<List<object>>().Skip(1))
                if (row.Count >= 5 && row[0] is string && row[4] is double) d[(string)row[0]] = (double)row[4];
            return d;
        }

        public static void Resolve(object node, Dictionary<string, double> vars)
        {
            var dict = node as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var k in dict.Keys.ToList())
                {
                    if (k == "variables") continue;
                    object val;
                    if (TryValue(dict[k], vars, out val)) dict[k] = val; else Resolve(dict[k], vars);
                }
                return;
            }
            var list = node as List<object>;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                object val;
                if (TryValue(list[i], vars, out val)) list[i] = val; else Resolve(list[i], vars);
            }
        }

        static bool TryValue(object o, Dictionary<string, double> vars, out object val)
        {
            val = null;
            var s = o as string;
            if (s == null || s.Length < 2 || s[0] != '$') return false;
            if (s[1] == '=')
            {
                // only our own sliders are worked out; the car's expressions (e.g. "$components...") stay as they are
                try { val = Eval(s.Substring(2), vars); return true; }
                catch (FormatException) { return false; }
            }
            if (s[1] == '+' || s[1] == '*' || s[1] == '<' || s[1] == '>') return false;
            double d;
            if (vars.TryGetValue(s, out d)) { val = d; return true; }
            return false;
        }

        // + - * / and parentheses over numbers and $variables
        public static double Eval(string expr, Dictionary<string, double> vars)
        {
            int i = 0;
            Func<double> sum = null, prod = null, atom = null;
            Action ws = () => { while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++; };
            atom = () =>
            {
                ws();
                if (i < expr.Length && expr[i] == '(') { i++; double v = sum(); ws(); if (i < expr.Length && expr[i] == ')') i++; return v; }
                if (i < expr.Length && expr[i] == '-') { i++; return -atom(); }
                int st = i;
                if (i < expr.Length && expr[i] == '$')
                {
                    i++;
                    while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                    double v;
                    if (!vars.TryGetValue(expr.Substring(st, i - st), out v)) throw new FormatException("unknown variable " + expr.Substring(st, i - st));
                    return v;
                }
                while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.' || expr[i] == 'e' || expr[i] == 'E')) i++;
                return double.Parse(expr.Substring(st, i - st), NumberStyles.Float, Num.Inv);
            };
            prod = () =>
            {
                double v = atom();
                while (true)
                {
                    ws();
                    if (i < expr.Length && expr[i] == '*') { i++; v *= atom(); }
                    else if (i < expr.Length && expr[i] == '/') { i++; v /= atom(); }
                    else return v;
                }
            };
            sum = () =>
            {
                double v = prod();
                while (true)
                {
                    ws();
                    if (i < expr.Length && expr[i] == '+') { i++; v += prod(); }
                    else if (i < expr.Length && expr[i] == '-') { i++; v -= prod(); }
                    else return v;
                }
            };
            return sum();
        }
    }
}
