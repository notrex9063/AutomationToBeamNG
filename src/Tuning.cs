// What actually goes into BeamNG for one engine: the torque curve (Automation's own when it has
// one), the boost curve, a turbo/supercharger set up to reproduce that boost, and the sound.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    class EngineData
    {
        public EngineSpec Spec;
        public AutomationResults Real;           // null when Automation's results weren't found
        public List<double[]> Curve;             // total full-load torque [rpm, Nm]
        public List<double[]> BoostBar;          // [rpm, bar] at full load
        public double RpmLimit, IdleRpm;
        public string CalWarning;

        public bool FromAutomation { get { return Real != null; } }
        public Peaks Peaks { get { return EngineModel.GetPeaks(Curve, RpmLimit); } }
        public double PeakBoost { get { return BoostBar.Count == 0 ? 0 : BoostBar.Max(p => p[1]); } }
        public bool IsBoosted { get { return !Spec.IsNatural && PeakBoost > 0.05; } }

        // manualT / manualKW: user-entered peaks, only used without Automation results (0 = none).
        public static EngineData Build(EngineSpec s, AutomationResults real, double manualT, double manualKW)
        {
            var d = new EngineData { Spec = s, Real = real };
            if (real != null && real.HasCurves)
            {
                d.RpmLimit = real.MaxRPM > 0 ? real.MaxRPM : s.RPMLimit;
                d.IdleRpm = real.IdleRPM > 0 ? real.IdleRPM : real.Rpm[0];
                d.Curve = FromSamples(real.Rpm, real.Torque, d.RpmLimit);
                d.BoostBar = real.Boost != null ? Resample(real.Rpm, real.Boost, d.RpmLimit) : new List<double[]>();
                return d;
            }
            d.RpmLimit = real != null && real.MaxRPM > 0 ? real.MaxRPM : s.RPMLimit;
            d.IdleRpm = real != null && real.IdleRPM > 0 ? real.IdleRPM : 700 + 900 * s.Cam;
            var est = EngineModel.Curve(s);
            if (real != null) { manualT = real.PeakTorqueNm; manualKW = real.PeakPowerKW; }   // peaks known, curve not
            string warn = null;
            d.Curve = manualT > 0 || manualKW > 0 ? EngineModel.Calibrate(est, s, manualT, manualKW, out warn) : est;
            d.CalWarning = warn;
            d.BoostBar = new List<double[]>();
            for (double r = 0; r <= d.RpmLimit; r += 250) d.BoostBar.Add(new[] { r, EngineModel.Boost(s, r) });
            return d;
        }

        static double Interp(double[] x, double[] y, double v)
        {
            if (v <= x[0]) return y[0];
            for (int i = 1; i < x.Length; i++)
                if (v <= x[i]) return y[i - 1] + (y[i] - y[i - 1]) * (v - x[i - 1]) / Math.Max(1e-9, x[i] - x[i - 1]);
            return y[y.Length - 1];
        }

        // BeamNG table from Automation's samples: ramp up from 0 rpm, the real curve every 250 rpm,
        // then a fall-off past the limit like the stock engines have.
        static List<double[]> FromSamples(double[] rpm, double[] tq, double limit)
        {
            var c = new List<double[]>();
            double idle = rpm[0], last = rpm[rpm.Length - 1], tLast = tq[tq.Length - 1];
            c.Add(new[] { 0.0, 0.0 });
            if (idle >= 500) c.Add(new[] { Math.Round(idle * 0.5), tq[0] * 0.6 });
            c.Add(new[] { idle, tq[0] });
            for (double r = Math.Ceiling((idle + 1) / 250) * 250; r < last; r += 250) c.Add(new[] { r, Interp(rpm, tq, r) });
            c.Add(new[] { last, tLast });
            double end = Math.Ceiling((Math.Max(limit, last) + 1500) / 250) * 250;
            for (double r = Math.Ceiling((last + 1) / 250) * 250; r <= end; r += 250)
            {
                double over = r - last;
                double k = over <= 500 ? 1 - 0.15 * over / 500 : Math.Max(0.1, 0.85 * (1 - (over - 500) / 1000));
                c.Add(new[] { r, tLast * k });
            }
            return c;
        }

        static List<double[]> Resample(double[] rpm, double[] v, double limit)
        {
            var c = new List<double[]> { new[] { 0.0, 0.0 } };
            for (int i = 0; i < rpm.Length; i++) c.Add(new[] { rpm[i], Math.Max(0, v[i]) });
            return c;
        }

        public double BoostAt(double rpm)
        {
            if (BoostBar.Count == 0) return 0;
            var x = BoostBar.Select(p => p[0]).ToArray();
            var y = BoostBar.Select(p => p[1]).ToArray();
            return Interp(x, y, rpm);
        }
    }

    // A BeamNG turbocharger/supercharger tuned so that, at full throttle, it gives Automation's
    // boost at each rpm. BeamNG multiplies torque by (1 + 0.0000087 * Pa * efficiency), so the
    // engine's own curve is the total divided by that.
    class ForcedInduction
    {
        public const double PsiPerBar = 14.5038, PaPerPsi = 6894.757293178;
        const double MaxExhaustPower = 11000, BackPressure = 0.000035, Friction = 10.4;

        public bool IsTurbo;
        public string ScType;            // roots / screws / centrifugal
        public double EngineMaxRpm, MaxPsi, PsiPer1k;
        public List<double[]> PressureTable = new List<double[]>();   // turbo: [turbineRPM, psi]
        EngineData data;
        bool kit;                        // an add-on turbo kit rather than Automation's own boost

        static readonly Dictionary<string, double[]> ScEff = new Dictionary<string, double[]>
        {
            { "roots", new[] { -0.55, 0, 1 } }, { "screws", new[] { 0.3, 0, 0.7 } }, { "centrifugal", new[] { 0.6, 0, 0.45 } },
        };

        public static ForcedInduction Design(EngineData d)
        {
            if (!d.IsBoosted) return null;
            var fi = new ForcedInduction { data = d, IsTurbo = d.Spec.IsTurbo, EngineMaxRpm = d.RpmLimit + 500 };
            fi.MaxPsi = d.PeakBoost * PsiPerBar;
            if (fi.IsTurbo)
            {
                fi.PressureTable.Add(new[] { 0.0, 0.0 });
                double lastT = 0;
                for (double r = 500; r <= d.RpmLimit; r += 250)
                {
                    double t = TurbineRpm(r, fi.EngineMaxRpm);
                    if (t <= lastT + 1) continue;
                    fi.PressureTable.Add(new[] { Math.Round(t), Math.Round(d.BoostAt(r) * PsiPerBar, 3) });
                    lastT = t;
                }
                double tMax = TurbineRpm(fi.EngineMaxRpm, fi.EngineMaxRpm) * 1.05;
                fi.PressureTable.Add(new[] { Math.Round(tMax), Math.Round(d.BoostAt(d.RpmLimit) * PsiPerBar, 3) });
            }
            else
            {
                string k = d.Spec.ChargerKind;
                fi.ScType = Regex.IsMatch(k, "Centrifugal", RegexOptions.IgnoreCase) ? "centrifugal" : Regex.IsMatch(k, "Screw", RegexOptions.IgnoreCase) ? "screws" : "roots";
                // choose the pressure slope so BeamNG's curve meets Automation's at peak boost
                var peak = d.BoostBar.OrderByDescending(p => p[1]).First();
                double eff = Eff(fi.ScType, peak[0] / fi.EngineMaxRpm);
                fi.PsiPer1k = peak[0] > 0 && eff > 0 ? peak[1] * PsiPerBar * 1000 / (eff * peak[0]) : 0;
            }
            return fi;
        }

        // An add-on turbo for a naturally aspirated engine: boost builds from ~25% of the rev range and
        // reaches "bar" by ~55%, like a mid-sized street turbo.
        public static ForcedInduction TurboKit(EngineData d, double bar)
        {
            var fi = new ForcedInduction { data = d, IsTurbo = true, EngineMaxRpm = d.RpmLimit + 500, MaxPsi = bar * PsiPerBar, kit = true };
            Func<double, double> boost = r =>
            {
                double x = (r / d.RpmLimit - 0.25) / 0.30;
                x = Math.Max(0, Math.Min(1, x));
                return bar * x * x * (3 - 2 * x);
            };
            fi.PressureTable.Add(new[] { 0.0, 0.0 });
            double lastT = 0;
            for (double r = 500; r <= d.RpmLimit; r += 250)
            {
                double t = TurbineRpm(r, fi.EngineMaxRpm);
                if (t <= lastT + 1) continue;
                fi.PressureTable.Add(new[] { Math.Round(t), Math.Round(boost(r) * PsiPerBar, 3) });
                lastT = t;
            }
            fi.PressureTable.Add(new[] { Math.Round(TurbineRpm(fi.EngineMaxRpm, fi.EngineMaxRpm) * 1.05), Math.Round(bar * PsiPerBar, 3) });
            return fi;
        }

        static double TurbineRpm(double engineRpm, double engineMax)
        {
            // steady-state turbine speed BeamNG's turbocharger.lua uses for torque estimates
            double av = Math.Sqrt(Math.Max(0.9 * engineRpm / engineMax * MaxExhaustPower - Friction, 0) / BackPressure);
            return av * 60 / (2 * Math.PI);
        }

        static double Eff(string type, double rel)
        {
            var c = ScEff[type];
            return c[0] * rel * rel + c[1] * rel + c[2];
        }

        // Pressure (psi) BeamNG will produce at full throttle and this rpm.
        public double PsiAt(double rpm)
        {
            if (IsTurbo)
            {
                double t = TurbineRpm(rpm, EngineMaxRpm), p = 0;
                for (int i = 1; i < PressureTable.Count; i++)
                {
                    var a = PressureTable[i - 1]; var b = PressureTable[i];
                    if (t <= b[0]) { p = a[1] + (b[1] - a[1]) * (t - a[0]) / Math.Max(1, b[0] - a[0]); break; }
                    p = b[1];
                }
                return Math.Min(p, MaxPsi + 0.25);   // wastegate (start + range/2)
            }
            return Math.Max(0, Eff(ScType, rpm / EngineMaxRpm) * PsiPer1k * rpm / 1000);
        }

        public double TorqueCoef(double rpm) { return 1 + 0.0000087 * PsiAt(rpm) * PaPerPsi; }

        // scaleVar: a jbeam slider (in %) that scales the boost pressure, or null for fixed values
        public string JBeamSection(string scaleVar = null)
        {
            Func<double, string, string> psi = (v, fmt) => scaleVar == null ? Num.F(v, fmt) : "\"$=" + Num.F(v, fmt) + " * " + scaleVar + " / 100\"";
            var sb = new StringBuilder();
            var s = data.Spec;
            if (IsTurbo)
            {
                double inertia = Num.Clamp(0.6 + s.TurbineSize / 40, 0.6, 3);
                sb.Append("    \"turbocharger\": {\r\n");
                sb.Append(kit ? "        // add-on turbo kit sized for this engine\r\n" : "        // generated to reproduce Automation's boost curve at full throttle\r\n");
                sb.Append("        \"bovSoundFileName\":\"event:>Vehicle>Forced_Induction>Turbo_02>turbo_bov_tuned\",\r\n");
                sb.Append("        \"hissLoopEvent\":\"event:>Vehicle>Forced_Induction>Turbo_07>turbo_hiss_tuned\",\r\n");
                sb.Append("        \"whineLoopEvent\":\"event:>Vehicle>Forced_Induction>Turbo_05>turbo_spin_tuned\",\r\n");
                sb.Append("        \"turboSizeCoef\": ").Append(Num.F(Num.Clamp(s.TurbineSize / 50, 0.4, 1.5), "0.00")).Append(",\r\n");
                sb.Append("        \"bovSoundVolumeCoef\": 0.3,\r\n        \"hissVolumePerPSI\": 0.028,\r\n");
                sb.Append("        \"whineVolumePer10kRPM\": 0.0065,\r\n        \"whinePitchPer10kRPM\": 0.02,\r\n");
                sb.Append("        \"wastegateStart\":").Append(psi(MaxPsi, "0.00")).Append(",\r\n");
                sb.Append("        \"wastegateLimit\":").Append(psi(MaxPsi + 0.5, "0.00")).Append(",\r\n");
                sb.Append("        \"maxExhaustPower\": ").Append(Num.F(MaxExhaustPower, "0")).Append(",\r\n");
                sb.Append("        \"backPressureCoef\": ").Append(Num.F(BackPressure, "0.000000")).Append(",\r\n");
                sb.Append("        \"frictionCoef\": ").Append(Num.F(Friction, "0.0")).Append(",\r\n");
                sb.Append("        \"inertia\":").Append(Num.F(inertia, "0.00")).Append(",\r\n");
                sb.Append("        \"pressureRatePSI\": 30,\r\n        \"damageThresholdTemperature\": 900,\r\n");
                sb.Append("        \"pressurePSI\":[\r\n            //turbineRPM, pressure(PSI)\r\n");
                foreach (var p in PressureTable) sb.Append("            [").Append(Num.F(p[0], "0")).Append(", ").Append(psi(p[1], "0.###")).Append("],\r\n");
                sb.Append("        ],\r\n");
                sb.Append("        \"engineDef\":[\r\n            //engineRPM, efficiency, exhaustFactor\r\n");
                sb.Append("            [0, 1.0, 1.0],\r\n            [").Append(Num.F(EngineMaxRpm + 2000, "0")).Append(", 1.0, 1.0],\r\n        ],\r\n");
                sb.Append("    },\r\n");
            }
            else
            {
                sb.Append("    \"supercharger\": {\r\n");
                sb.Append("        // generated to reproduce Automation's peak boost at full throttle\r\n");
                sb.Append("        \"whineLoopEvent\": \"event:>Vehicle>Forced_Induction>Supercharger_03>supercharger\",\r\n");
                sb.Append("        \"whineVolumePerPSI\": 0.024,\r\n        \"whinePitchPer10kRPM\": 0.74,\r\n");
                sb.Append("        \"gearRatio\":1.0,\r\n");
                sb.Append("        \"maxRPM\":").Append(Num.F(EngineMaxRpm, "0")).Append(",\r\n");
                sb.Append("        \"clutchEngageRPM\":0,\r\n        \"pressureRatePSI\":250,\r\n");
                sb.Append("        \"pressurePSIPer1kRPM\":").Append(psi(PsiPer1k, "0.0000")).Append(",\r\n");
                sb.Append("        \"crankLossPer1kRPM\":0,\r\n");
                sb.Append("        \"type\":\"").Append(ScType).Append("\",\r\n");
                if (ScType != "centrifugal") sb.Append("        \"lobes\":3,\r\n        \"twistedLobes\":false,\r\n");
                sb.Append("        \"boostController\":[\r\n            //throttle in %, factor\r\n            [0, 0.2],\r\n            [100, 1],\r\n        ],\r\n");
                sb.Append("    },\r\n");
            }
            return sb.ToString();
        }
    }

    // Engine friction for BeamNG. BeamNG subtracts friction + dynamicFriction*AV from the torque
    // table (and multiplies the table by the boost coefficient first), while Automation's curve
    // is already net of friction. So the table is (net + friction) / boost coefficient, and the
    // friction itself is sized like the stock engines: a few percent of the engine's torque.
    class Friction
    {
        public double Static, Dynamic, EngineBrake;

        public static Friction For(EngineData d)
        {
            var pk = d.Peaks;
            double limitAV = d.RpmLimit * 2 * Math.PI / 60;
            return new Friction
            {
                Static = Math.Max(0.3, 0.06 * pk.TorqueNm),              // stock Covet: 10 Nm on 134 Nm
                Dynamic = 0.08 * pk.TorqueNm / Math.Max(1, limitAV),     // ~8% of peak torque at the limiter
                EngineBrake = Math.Max(1.5, 0.18 * pk.TorqueNm),         // stock Covet: 26 Nm on 134 Nm
            };
        }

        // Torque table such that, after BeamNG applies the fitted parts' torque modifiers, the
        // boost coefficient and friction, the result is Automation's net curve.
        public List<double[]> TorqueTable(List<double[]> net, ForcedInduction fi, TorqueMods mods)
        {
            mods = mods ?? new TorqueMods();
            return net.Select(p =>
            {
                double r = p[0];
                double combined = (p[1] + mods.FrictionAt(this, r)) / (fi != null ? fi.TorqueCoef(r) : 1);
                return new[] { r, (combined - mods.Add(r)) / Math.Max(0.05, mods.Mult(r)) };
            }).ToList();
        }
    }

    // A small BeamNG vehicle controller shipped with each engine. Intake/exhaust parts add or remove a
    // fixed number of Nm sized for the car's stock engine ("torqueModIntake"/"torqueModExhaust"), and
    // in jbeam those parts always win over the engine part, so the engine can't switch them off. After
    // the engine has built its curve, this scales those changes to our engine's size: swapping or
    // removing an exhaust then changes power by the same share as on the stock engine. It also applies
    // the ECU's Power slider ("powerTrim", in %).
    static class TorqueScaleController
    {
        public const string FileName = "automationToBeamNG_torqueScale";

        public const string Lua =
@"-- Generated by AutomationToBeamNG. Scales the torque that intake/exhaust parts add or remove
-- (torqueModIntake / torqueModExhaust, sized for the car's stock engine) to this engine's size,
-- and applies the ECU's Power slider.
local M = {}
M.type = ""auxiliary""
M.defaultOrder = 1000

local psToWatt = 735.499

local function modCurve(tbl)
  if type(tbl) ~= ""table"" then return nil, 0 end
  local points = {}
  for _, row in pairs(tableFromHeaderTable(tbl)) do
    if type(row) == ""table"" and row.rpm and row.torque then table.insert(points, {row.rpm, row.torque}) end
  end
  if #points == 0 then return nil, 0 end
  local curve = createCurve(points)
  return curve, curve[#curve] or 0
end

local function pointCurve(rows)
  if type(rows) ~= ""table"" then return nil, 0 end
  local points = {}
  for _, row in ipairs(rows) do
    if type(row) == ""table"" and type(row[1]) == ""number"" and type(row[2]) == ""number"" then table.insert(points, {row[1], row[2]}) end
  end
  if #points == 0 then return nil, 0 end
  local curve = createCurve(points)
  return curve, curve[#curve] or 0
end

local function init(jbeamData)
  local engine = powertrain.getDevice(jbeamData.engineName or ""mainEngine"")
  if not engine or type(engine.torqueCurve) ~= ""table"" then return end
  local trim = (tonumber(jbeamData.powerTrim) or 100) / 100
  local scale, lastScale = pointCurve(jbeamData.scaleCurve)
  local ratio = jbeamData.rpmRatio or 1
  local data = v.data[engine.name] or {}
  local intake, lastIntake = modCurve(data.torqueModIntake)
  local exhaust, lastExhaust = modCurve(data.torqueModExhaust)
  local rescale = scale and (intake or exhaust)
  if not rescale and trim == 1 then return end

  -- the intake/exhaust change BeamNG already put into the curve at an rpm
  local function added(rpm)
    local a = 0
    if intake then a = a + (intake[rpm] or lastIntake) end
    if exhaust then a = a + (exhaust[rpm] or lastExhaust) end
    return a
  end

  local cut = (engine.idleRPM or 0) * 0.3
  local topRPM = data.maxRPM or engine.maxRPM or 0
  for rpm = 0, topRPM do
    local t = engine.torqueCurve[rpm]
    if t then
      if rescale and rpm >= cut then
        t = t - added(rpm) + (scale[rpm] or lastScale) * added(math.floor(rpm * ratio + 0.5))
      end
      -- the ECU's Power slider
      engine.torqueCurve[rpm] = t * trim
    end
  end

  engine.idleTorque = engine.torqueCurve[math.floor(engine.idleRPM or 0)] or 0
  if engine.getTorqueData then
    engine.torqueData = engine:getTorqueData()
    engine.maxPower = engine.torqueData.maxPower
    engine.maxTorque = engine.torqueData.maxTorque
    engine.maxPowerThrottleMap = engine.torqueData.maxPower * psToWatt
  end
end

M.init = init
return M
";
    }

    // Torque changes BeamNG applies from other fitted parts (intake/exhaust/ECU parts on the car):
    // combined = table * torqueModMult + torqueModIntake + torqueModExhaust, plus "$*friction"-style
    // friction modifiers.
    class TorqueMods
    {
        public List<double[]> Intake, Exhaust, MultCurve;
        public double FrictionMul = 1, DynamicMul = 1, FrictionAdd = 0, DynamicAdd = 0;
        // TorqueScaleController: at our rpm r the change is the stock change at r * RpmRatio (same point
        // in the rev range), times ScaleCurve(r) = our torque / stock torque there
        public List<double[]> ScaleCurve;
        public double RpmRatio = 1;
        public List<string> Sources = new List<string>();

        public bool Any { get { return Intake != null || Exhaust != null || MultCurve != null || FrictionMul != 1 || DynamicMul != 1 || FrictionAdd != 0 || DynamicAdd != 0; } }
        public bool Scaled { get { return ScaleCurve != null; } }

        public double RawAdd(double rpm) { return (Intake != null ? BeamNGSim.Lerp(Intake, rpm) : 0) + (Exhaust != null ? BeamNGSim.Lerp(Exhaust, rpm) : 0); }

        // intake + exhaust torque change in BeamNG, after our controller has scaled it
        public double Add(double rpm) { return ScaleCurve == null ? RawAdd(rpm) : BeamNGSim.Lerp(ScaleCurve, rpm) * RawAdd(Math.Floor(rpm * RpmRatio + 0.5)); }

        public TorqueMods Unscaled() { var c = Clone(); c.ScaleCurve = null; c.RpmRatio = 1; return c; }

        public TorqueMods Clone() { return (TorqueMods)MemberwiseClone(); }

        public TorqueMods WithoutExhaust()
        {
            var c = Clone();
            c.Exhaust = null;
            return c;
        }
        public double Mult(double rpm) { return MultCurve != null ? BeamNGSim.Lerp(MultCurve, rpm) : 1; }

        public double FrictionStatic(double f) { return f * FrictionMul + FrictionAdd; }
        public double FrictionDynamic(double f) { return f * DynamicMul + DynamicAdd; }

        public double FrictionAt(Friction fr, double rpm)
        {
            return FrictionStatic(fr.Static) + FrictionDynamic(fr.Dynamic) * rpm * 2 * Math.PI / 60;
        }
    }

    // Recomputes, from a finished engine part, the full-throttle curve BeamNG's own torque graph
    // will show (mirrors combustionEngine.lua getTorqueData + turbocharger/supercharger
    // getTorqueCoefs), so the converter can prove the result matches Automation.
    static class BeamNGSim
    {
        const double PsiToPa = 6894.757293178;

        static double D(Dictionary<string, object> d, string k, double def)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) && o is double ? (double)o : def;
        }

        public static List<double[]> Rows(object table)
        {
            var l = table as List<object>;
            if (l == null) return new List<double[]>();
            return l.OfType<List<object>>().Where(r => r.Count >= 2 && r[0] is double && r[1] is double)
                    .Select(r => new[] { (double)r[0], (double)r[1] }).OrderBy(r => r[0]).ToList();
        }

        public static double Lerp(List<double[]> c, double x)
        {
            if (c.Count == 0) return 0;
            if (x <= c[0][0]) return c[0][1];
            for (int i = 1; i < c.Count; i++)
                if (x <= c[i][0]) return c[i - 1][1] + (c[i][1] - c[i - 1][1]) * (x - c[i - 1][0]) / Math.Max(1e-9, c[i][0] - c[i - 1][0]);
            return c[c.Count - 1][1];
        }

        static readonly Dictionary<string, double[]> ScEff = new Dictionary<string, double[]>
        {
            { "roots", new[] { -0.55, 0, 1 } }, { "screws", new[] { 0.3, 0, 0.7 } }, { "centrifugal", new[] { 0.6, 0, 0.45 } },
        };

        // [rpm, Nm] at full throttle, as BeamNG reports it.
        public static List<double[]> Curve(Dictionary<string, object> part, TorqueMods mods)
        {
            mods = mods ?? new TorqueMods();
            var me = part["mainEngine"] as Dictionary<string, object>;
            var table = Rows(me["torque"]);
            double friction = mods.FrictionStatic(D(me, "friction", 0)), dyn = mods.FrictionDynamic(D(me, "dynamicFriction", 0)), maxRpm = D(me, "maxRPM", 8000);
            Func<double, double> coef = rpm => 1;

            object sec;
            if (part.TryGetValue("turbocharger", out sec) && sec is Dictionary<string, object> && me.ContainsKey("turbocharger"))
            {
                var t = (Dictionary<string, object>)sec;
                var pressure = Rows(t["pressurePSI"]);
                var engineDef = (t["engineDef"] as List<object>).OfType<List<object>>().Where(r => r.Count >= 3 && r[0] is double).ToList();
                var eff = engineDef.Select(r => new[] { (double)r[0], (double)r[1] }).ToList();
                var exh = engineDef.Select(r => new[] { (double)r[0], Math.Min((double)r[2], 1) }).ToList();
                double start = D(t, "wastegateStart", 0), limit = D(t, "wastegateLimit", start + 0.01);
                double estLimit = start + (limit - start) * 0.5;
                double maxExh = D(t, "maxExhaustPower", 1), back = D(t, "backPressureCoef", 0.0005), fric = D(t, "frictionCoef", 0.01);
                double maxTurboAV = Math.Max(1, pressure.Max(p => p[0]) * 2 * Math.PI / 60);
                coef = rpm =>
                {
                    double av = Math.Sqrt(Math.Max(0.9 * rpm / maxRpm * Lerp(exh, rpm) * maxExh - fric, 0) / back);
                    av = Math.Min(av, maxTurboAV);
                    double p = Math.Min(Lerp(pressure, Math.Floor(av * 60 / (2 * Math.PI))), estLimit);
                    return 1 + 0.0000087 * p * PsiToPa * Lerp(eff, rpm);
                };
            }
            else if (part.TryGetValue("supercharger", out sec) && sec is Dictionary<string, object> && me.ContainsKey("supercharger"))
            {
                var sc = (Dictionary<string, object>)sec;
                double ratio = D(sc, "gearRatio", 1), scMax = D(sc, "maxRPM", Math.Ceiling(maxRpm * ratio));
                double perRpm = D(sc, "pressurePSIPer1kRPM", 0) * 0.001, loss = D(sc, "crankLossPer1kRPM", 5) * 0.001;
                var c = ScEff[((string)sc["type"]).ToLowerInvariant()];
                coef = rpm =>
                {
                    double blower = rpm * ratio, rel = blower / scMax;
                    double p = Math.Max((c[0] * rel * rel + c[1] * rel + c[2]) * perRpm * Math.Floor(blower), 0);
                    return 1 + 0.0000087 * p * PsiToPa - loss * blower;
                };
            }
            double trim = PowerTrim(part);
            var outC = new List<double[]>();
            for (double rpm = 0; rpm < maxRpm; rpm += 50)
                outC.Add(new[] { rpm, (Lerp(table, rpm) * mods.Mult(rpm) + mods.Add(rpm)) * trim * coef(rpm) - friction - dyn * rpm * 2 * Math.PI / 60 });
            return outC;
        }

        // the ECU's Power slider, as passed to our torque controller
        static double PowerTrim(Dictionary<string, object> part)
        {
            object c;
            if (!part.TryGetValue("controller", out c) || !(c is List<object>)) return 1;
            foreach (var row in ((List<object>)c).OfType<List<object>>())
                if (row.Count >= 2 && (row[0] as string) == TorqueScaleController.FileName && row[1] is Dictionary<string, object>)
                    return D((Dictionary<string, object>)row[1], "powerTrim", 100) / 100;
            return 1;
        }
    }

    // Engine sound sets that ship with BeamNG (art_sound.zip), chosen by layout and character.
    static class Sounds
    {
        public class Option
        {
            public string Name, Engine, Exhaust;
            public override string ToString() { return Name; }
        }

        public static readonly Option[] All =
        {
            new Option { Name = "Inline-3 classic", Engine = "I3_engine", Exhaust = "I3_exhaust" },
            new Option { Name = "Inline-3 modern", Engine = "I3_2_engine", Exhaust = "I3_2_exhaust" },
            new Option { Name = "Inline-3 sporty", Engine = "I3_3_engine", Exhaust = "I3_3_exhaust" },
            new Option { Name = "Inline-4 classic", Engine = "I4_3_engine", Exhaust = "I4_3_exhaust" },
            new Option { Name = "Inline-4 modern", Engine = "I4_2_engine", Exhaust = "I4_2_exhaust" },
            new Option { Name = "Inline-4 tuned", Engine = "i4p_2000_02000cc_modified_01_engine", Exhaust = "i4p_2000_02000cc_modified_01_exhaust" },
            new Option { Name = "Inline-4 race (1.6)", Engine = "i4p_01600cc_motorsport_02_engine", Exhaust = "i4p_01600cc_motorsport_02_exhaust" },
            new Option { Name = "Inline-4 race (2.0 endurance)", Engine = "i4p_02000cc_endurance_02_engine", Exhaust = "i4p_02000cc_endurance_02_exhaust" },
            new Option { Name = "Inline-4 vintage", Engine = "i4p_fh_1929_03300cc_stock_01_engine", Exhaust = "i4p_fh_1929_03300cc_stock_01_exhaust" },
            new Option { Name = "Inline-5", Engine = "I5_engine", Exhaust = "I5_exhaust" },
            new Option { Name = "Inline-5 rally", Engine = "I5_engine", Exhaust = "i5pt_1983_02000cc_motorsport_01_exhaust" },
            new Option { Name = "Inline-6", Engine = "I6_2_engine", Exhaust = "I6_2_exhaust" },
            new Option { Name = "Flat-4 classic", Engine = "B4_2_engine", Exhaust = "B4_2_exhaust" },
            new Option { Name = "Flat-4 tuned", Engine = "b4p_1995_02000cc_modified_01_engine", Exhaust = "b4p_1995_02000cc_modified_01_exhaust" },
            new Option { Name = "Flat-4 rally", Engine = "b4p_2008_02500cc_modified_01_engine", Exhaust = "b4p_2008_02500cc_modified_01_exhaust" },
            new Option { Name = "Flat-6", Engine = "B6_engine", Exhaust = "B6_exhaust" },
            new Option { Name = "V6", Engine = "V6_2_engine", Exhaust = "V6_2_exhaust" },
            new Option { Name = "V8 muscle (cross-plane)", Engine = "V8_cross_engine", Exhaust = "V8_cross_exhaust" },
            new Option { Name = "V8 muscle 2", Engine = "V8_cross_02_engine", Exhaust = "V8_cross_02_exhaust" },
            new Option { Name = "V8 modern GT", Engine = "v8p_cp_2019_05000cc_gt_01_engine", Exhaust = "v8p_cp_2019_05000cc_gt_01_exhaust" },
            new Option { Name = "V8 vintage", Engine = "v8p_fh_1953_04200cc_stock_01_engine", Exhaust = "v8p_fh_1953_04200cc_stock_01_exhaust" },
            new Option { Name = "V8 race (flat-plane)", Engine = "V8_flat_2_engine", Exhaust = "V8_flat_2_exhaust" },
            new Option { Name = "V10", Engine = "V10_2_engine", Exhaust = "V10_2_exhaust" },
            new Option { Name = "V12", Engine = "V12_engine", Exhaust = "V12_exhaust" },
            new Option { Name = "V16", Engine = "V16_engine", Exhaust = "V16_exhaust" },
        };

        static Option Get(string name) { return All.First(o => o.Name == name); }

        public static Option ByName(string name) { return All.FirstOrDefault(o => o.Name == name); }

        // race: high-revving or aggressive cams; classic: pushrod/flathead or low-revving
        public static string Character(EngineSpec s, double limit)
        {
            if (limit >= 8500 || s.Cam >= 0.75) return "race";
            if (Regex.IsMatch(s.Head, "OHV|Pushrod|Flat", RegexOptions.IgnoreCase) || limit < 5500) return "classic";
            return "modern";
        }

        public static Option Auto(EngineSpec s, double limit)
        {
            string ch = Character(s, limit);
            int c = s.Cylinders;
            if (s.Layout == "boxer") return c >= 6 ? Get("Flat-6") : ch == "race" ? Get("Flat-4 rally") : ch == "classic" ? Get("Flat-4 classic") : Get("Flat-4 tuned");
            if (s.Layout == "V")
            {
                if (c >= 14) return Get("V16");
                if (c >= 11) return Get("V12");
                if (c >= 9) return Get("V10");
                if (c >= 7)
                {
                    if (ch == "race") return Get("V8 race (flat-plane)");
                    if (ch == "classic") return Regex.IsMatch(s.Head, "OHV|Pushrod", RegexOptions.IgnoreCase) ? Get("V8 muscle (cross-plane)") : Get("V8 vintage");
                    return Get("V8 modern GT");
                }
                return Get("V6");
            }
            if (c <= 3) return ch == "race" ? Get("Inline-3 sporty") : ch == "classic" ? Get("Inline-3 classic") : Get("Inline-3 modern");
            if (c == 4)
            {
                if (ch == "race") return s.DisplacementL >= 1.8 ? Get("Inline-4 race (2.0 endurance)") : Get("Inline-4 race (1.6)");
                return ch == "classic" ? Get("Inline-4 classic") : Get("Inline-4 modern");
            }
            if (c == 5) return ch == "race" ? Get("Inline-5 rally") : Get("Inline-5");
            return Get("Inline-6");
        }
    }
}
