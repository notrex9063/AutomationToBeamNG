<#
.SYNOPSIS
  Converts an Automation (Car Company Tycoon) .engine export into a BeamNG.drive engine mod.

.DESCRIPTION
  The .engine file only stores the *design* (bore, stroke, cams, boost, fuel ...), not the
  dyno results Automation shows in-game. This tool therefore estimates a torque curve from a
  simplified physics model. For an accurate result, pass the peak torque / power numbers that
  Automation's engine designer shows (-PeakTorqueNm, -PeakPowerKW or -PeakPowerHP) and the
  estimated curve is reshaped to match them.

  For every car you pick, the engine is built on one of that car's stock BeamNG engines (read
  from your own game install), so it keeps the car's mounts, crash structure, intake/exhaust and
  gearbox slots. It shows up as a new choice in that car's Engine slot.

  Run without -Vehicles to get the window (pick the BeamNG folder, tick cars, convert+install).

.EXAMPLE
  .\AutomationToBeamNG.ps1
.EXAMPLE
  .\AutomationToBeamNG.ps1 -EngineFile "..\Ruffe_-_Javelin.engine" -Vehicles covet,pickup -PeakTorqueNm 85 -PeakPowerHP 140
#>
param(
    [string]$EngineFile = "",
    [string[]]$Vehicles = @(),
    [double]$PeakTorqueNm = 0,
    [double]$PeakPowerKW = 0,
    [double]$PeakPowerHP = 0,
    [string]$BeamNGPath = "",
    [string]$ModsPath = "",
    [switch]$ListVehicles
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inv = [Globalization.CultureInfo]::InvariantCulture
$AppDir = Join-Path $env:LOCALAPPDATA "AutomationToBeamNG"
New-Item -ItemType Directory -Force $AppDir | Out-Null
$SettingsFile = Join-Path $AppDir "settings.json"
$IndexFile = Join-Path $AppDir "vehicle_index.json"

function Fmt([double]$v, [string]$f = "0.###") { $v.ToString($f, $inv) }
function Clamp([double]$v, [double]$lo, [double]$hi) { [Math]::Max($lo, [Math]::Min($hi, $v)) }

# Parts that add their own forced induction or torque changes on top of our baked-in curve.
$PowerAdderPattern = '"turbocharger"\s*:\s*\{|"supercharger"\s*:\s*\{|"torqueModIntake"|"torqueModMult"'
$BoostPattern = '"turbocharger"\s*:\s*\{|"supercharger"\s*:\s*\{'

# ---------------------------------------------------------------------------
# Minimal Lua table parser (enough for Automation exports)
# ---------------------------------------------------------------------------
$script:src = ""
$script:pos = 0

function Skip-Ws {
    while ($script:pos -lt $script:src.Length) {
        $c = $script:src[$script:pos]
        if ([char]::IsWhiteSpace($c) -or $c -eq ',' -or $c -eq ';') { $script:pos++; continue }
        if ($c -eq '-' -and $script:pos + 1 -lt $script:src.Length -and $script:src[$script:pos + 1] -eq '-') {
            while ($script:pos -lt $script:src.Length -and $script:src[$script:pos] -ne "`n") { $script:pos++ }
            continue
        }
        break
    }
}

function Read-LuaValue {
    Skip-Ws
    $c = $script:src[$script:pos]
    if ($c -eq '{') { return (Read-LuaTable) }
    if ($c -eq '"' -or $c -eq "'") {
        $q = $c; $script:pos++; $sb = New-Object Text.StringBuilder
        while ($script:src[$script:pos] -ne $q) {
            if ($script:src[$script:pos] -eq '\') { $script:pos++ }
            [void]$sb.Append($script:src[$script:pos]); $script:pos++
        }
        $script:pos++
        return $sb.ToString()
    }
    $m = [regex]::Match($script:src.Substring($script:pos), '^[^,;}\s]+')
    $script:pos += $m.Length
    switch ($m.Value) {
        "true" { return $true }
        "false" { return $false }
        "nil" { return $null }
    }
    return [double]::Parse($m.Value, $inv)
}

function Read-LuaTable {
    $script:pos++  # '{'
    $t = [ordered]@{}; $i = 1
    while ($true) {
        Skip-Ws
        if ($script:src[$script:pos] -eq '}') { $script:pos++; return $t }
        $rest = $script:src.Substring($script:pos, [Math]::Min(200, $script:src.Length - $script:pos))
        $km = [regex]::Match($rest, '^(?:([A-Za-z_][A-Za-z0-9_]*)|\["([^"]*)"\]|\[(\d+)\])\s*=')
        if ($km.Success) {
            $key = @($km.Groups[1].Value, $km.Groups[2].Value, $km.Groups[3].Value) | Where-Object { $_ } | Select-Object -First 1
            $script:pos += $km.Length
            $t[$key] = Read-LuaValue
        } else {
            $t["$i"] = Read-LuaValue; $i++
        }
    }
}

function Read-EngineFile([string]$path) {
    $script:src = [IO.File]::ReadAllText($path)
    $script:pos = $script:src.IndexOf('{')
    if ($script:pos -lt 0) { throw "Not an Automation export: no table found in $path" }
    return Read-LuaTable
}

# ---------------------------------------------------------------------------
# Torque estimation
# ---------------------------------------------------------------------------
function Get-Num($tbl, $key, [double]$default) {
    if ($tbl.Contains($key) -and $tbl[$key] -is [double]) { return [double]$tbl[$key] }
    return $default
}

function Get-EngineSpec($data) {
    $v = $data.Variant; $f = $data.Family
    $cfg = "$($f.BlockConfig)"
    $cyl = 4
    $cm = [regex]::Match($cfg, '(\d+)_Name$')
    if ($cm.Success) { $cyl = [int]$cm.Groups[1].Value }
    $layout = "inline"
    if ($cfg -match '_V\d') { $layout = "V" } elseif ($cfg -match 'Box|Flat') { $layout = "boxer" }

    $bore = Get-Num $v "Bore" (Get-Num $f "Bore" 80)
    $stroke = Get-Num $v "Stroke" (Get-Num $f "Stroke" 80)
    $dispL = [Math]::PI / 4 * [Math]::Pow($bore / 10, 2) * ($stroke / 10) * $cyl / 1000

    $valves = 4
    $vm = [regex]::Match("$($f.Valves)", '_(\d+)_Name')
    if ($vm.Success) { $valves = [int]$vm.Groups[1].Value }

    [pscustomobject]@{
        Name          = "$($f.Name) $($v.Name)".Trim()
        Cylinders     = $cyl
        Layout        = $layout
        Bore          = $bore
        Stroke        = $stroke
        DisplacementL = $dispL
        Valves        = $valves
        Head          = "$($f.Head)"
        RPMLimit      = Get-Num $v "RPMLimit" 6500
        Compression   = Get-Num $v "Compression" 10
        Cam           = Get-Num $v "CamProfileSetting" 0.5
        VVT           = "$($v.VVT)" -notmatch 'None'
        VVL           = "$($v.VVL)" -notmatch 'None'
        FuelMap       = Get-Num $v "FuelMap" 0.5
        FuelType      = "$($v.FuelType)"
        Aspiration    = "$($v.AspirationType)"
        ChargerKind   = "$($v.AspirationItemOption_1)"
        MaxBoost      = Get-Num $v "ChargerMaxBoost_1" 0
        TurbineSize   = Get-Num $v "TurbineSize_1" 40
        Intercooler   = Get-Num $v "IntercoolerSetting" 0
        Intake        = "$($v.Intake)"
        IntakeMani    = "$($v.IntakeManifold)"
        ExhaustDiaIn  = Get-Num $v "ExhaustDiameter" 2
        ExhaustCount  = $(if ("$($v.ExhaustCount)" -match '_2_') { 2 } else { 1 })
        Cat           = "$($v.Cat)"
        Muffler       = "$($v.Muffler1)"
        Crank         = "$($v.Crank)"
    }
}

# Energy released per kg of air relative to gasoline at stoichiometric.
function Get-FuelFactor([string]$fuel) {
    if ($fuel -match 'Nitro') {
        $frac = 1.0
        $nm = [regex]::Match($fuel, 'NM(\d+)')
        if ($nm.Success) { $frac = [int]$nm.Groups[1].Value / 100 }
        return 1.04 + (2.2 - 1.04) * $frac   # remainder is methanol
    }
    if ($fuel -match 'Methanol') { return 1.04 }
    if ($fuel -match 'Ethanol|E85|E100') { return 1.02 }
    return 1.0
}

# Boost (bar, gauge) delivered at a given rpm.
function Get-Boost($s, [double]$rpm) {
    if ($s.Aspiration -match 'Natural' -or $s.MaxBoost -le 0) { return 0 }
    $x = $rpm / $s.RPMLimit
    if ($s.Aspiration -match 'Turbo') {
        $spool = Clamp (0.2 + $s.TurbineSize / 100) 0.25 0.8
        return $s.MaxBoost / (1 + [Math]::Exp(-12 * ($x - $spool)))
    }
    if ($s.ChargerKind -match 'Centrifugal') { return $s.MaxBoost * [Math]::Pow((Clamp $x 0 1), 2) }
    # Roots / twin-screw: near full boost from low rpm
    return $s.MaxBoost * (1 - [Math]::Exp(-6 * $x))
}

function Get-TorqueAt($s, [double]$rpm) {
    if ($rpm -le 0) { return 0 }
    # Volumetric efficiency: asymmetric bell around a cam-dependent peak.
    $vePeak = 0.86 + @{2 = 0.0; 3 = 0.03; 4 = 0.07; 5 = 0.08}[[int](Clamp $s.Valves 2 5)]
    if ($s.Intake -match 'Race|Sport|Performance') { $vePeak += 0.02 }
    if ($s.IntakeMani -match 'PerCyl|ITB|Individual') { $vePeak += 0.03 }
    if ($s.VVL) { $vePeak += 0.02 }
    if ($s.Head -match 'OHV|Pushrod') { $vePeak -= 0.04 }
    $rpmPeak = $s.RPMLimit * (0.5 + 0.35 * $s.Cam)
    $wLow = $rpmPeak * (0.7 + $(if ($s.VVT) { 0.25 } else { 0 }) - 0.2 * $s.Cam)
    $wHigh = $rpmPeak * 0.45
    $w = $(if ($rpm -lt $rpmPeak) { $wLow } else { $wHigh })
    $ve = [Math]::Max(0.3, $vePeak * [Math]::Exp(-[Math]::Pow(($rpm - $rpmPeak) / $w, 2)))

    # Charge density from boost, with compressor heating and intercooling.
    $pr = 1 + (Get-Boost $s $rpm) / 1.013
    $t1 = 298.0
    $t2 = $t1 * (1 + ([Math]::Pow($pr, 0.286) - 1) / 0.70)
    $tc = $t2 - (Clamp $s.Intercooler 0 1) * ($t2 - $t1)
    $density = $pr * $t1 / $tc

    # Indicated MEP from air mass, fuel energy and Otto-cycle efficiency.
    $etaRel = (1 - [Math]::Pow($s.Compression, -0.3)) / (1 - [Math]::Pow(10, -0.3))
    $fuelMap = 1 + 0.06 * ($s.FuelMap - 0.5)
    $imepBar = 13.4 * $ve * $density * $etaRel * (Get-FuelFactor $s.FuelType) * $fuelMap

    # Friction MEP (Heywood-style), grows with rpm.
    $k = $rpm / 1000
    $fmepBar = 0.97 + 0.15 * $k + 0.05 * $k * $k

    # Exhaust restriction scales with air mass flow vs. pipe capacity.
    $airKgS = $s.DisplacementL / 1000 * $rpm / 120 * 1.18 * $ve * $density
    $capKgS = 0.12 * [Math]::Pow($s.ExhaustDiaIn, 2) * $s.ExhaustCount
    $restr = 0.15
    if ($s.Cat -notmatch 'None') { $restr += 0.05 }
    if ($s.Muffler -notmatch 'None|Straight') { $restr += 0.05 }
    $exh = 1 / (1 + $restr * [Math]::Pow($airKgS / $capKgS, 2))

    $bmepPa = [Math]::Max(0.0, ($imepBar * $exh - $fmepBar)) * 1e5
    return $bmepPa * ($s.DisplacementL / 1000) / (4 * [Math]::PI)
}

function Get-Curve($s) {
    $maxRpm = [Math]::Ceiling(($s.RPMLimit + 1500) / 500) * 500
    $pts = @()
    for ($r = 0; $r -le $maxRpm; $r += 250) {
        $tq = Get-TorqueAt $s $r
        if ($r -gt $s.RPMLimit + 500) {   # fall off past the physical limit
            $tq *= [Math]::Max(0.1, 1 - ($r - $s.RPMLimit - 500) / 1500)
        }
        $pts += , @($r, $tq)
    }
    return , $pts
}

function Get-Peaks($curve, [double]$limit) {
    $pt = 0; $pp = 0; $ptr = 0; $ppr = 0
    foreach ($p in $curve) {
        if ($p[0] -gt $limit) { break }
        $kw = $p[1] * $p[0] * 2 * [Math]::PI / 60 / 1000
        if ($p[1] -gt $pt) { $pt = $p[1]; $ptr = $p[0] }
        if ($kw -gt $pp) { $pp = $kw; $ppr = $p[0] }
    }
    [pscustomobject]@{ TorqueNm = $pt; TorqueRPM = $ptr; PowerKW = $pp; PowerRPM = $ppr }
}

# Reshape the estimated curve to hit the numbers Automation reported.
function Set-CurveCalibration($curve, $s, [double]$targetT, [double]$targetP) {
    $tilt = {
        param($c, $a)
        , @($c | ForEach-Object { , @($_[0], [Math]::Max(0.0, $_[1] * (1 + $a * ($_[0] / $s.RPMLimit - 0.5)))) })
    }
    if ($targetT -gt 0 -and $targetP -gt 0) {
        $want = $targetP / $targetT
        $lo = -1.5; $hi = 1.5
        for ($i = 0; $i -lt 40; $i++) {
            $mid = ($lo + $hi) / 2
            $pk = Get-Peaks (& $tilt $curve $mid) $s.RPMLimit
            if ($pk.PowerKW / $pk.TorqueNm -lt $want) { $lo = $mid } else { $hi = $mid }
        }
        $curve = & $tilt $curve (($lo + $hi) / 2)
    }
    $pk = Get-Peaks $curve $s.RPMLimit
    $scale = 1
    if ($targetT -gt 0) { $scale = $targetT / $pk.TorqueNm }
    elseif ($targetP -gt 0) { $scale = $targetP / $pk.PowerKW }
    if ($targetT -gt 0 -and $targetP -gt 0) {
        $chk = Get-Peaks @($curve | ForEach-Object { , @($_[0], ($_[1] * $scale)) }) $s.RPMLimit
        if ([Math]::Abs($chk.PowerKW - $targetP) / $targetP -gt 0.03) {
            $script:CalWarning = ("Could not match both numbers below the {0} rpm limit; torque matched, power is {1} kW." -f $s.RPMLimit, (Fmt $chk.PowerKW '0.0'))
        }
    }
    return , @($curve | ForEach-Object { , @($_[0], ($_[1] * $scale)) })
}

# ---------------------------------------------------------------------------
# jbeam text helpers (jbeam is relaxed JSON: comments, optional commas)
# ---------------------------------------------------------------------------
if (-not ('JBeamText' -as [type])) {
    # Bracket matching in C#: scanning every car's jbeam in PowerShell would take minutes.
    Add-Type -TypeDefinition @"
public static class JBeamText {
    public static int FindClose(string t, int open) {
        char o = t[open]; char c = o == '{' ? '}' : ']';
        int depth = 0;
        for (int i = open; i < t.Length; i++) {
            char ch = t[i];
            if (ch == '"') { i++; while (i < t.Length && t[i] != '"') { if (t[i] == '\\') i++; i++; } }
            else if (ch == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
            else if (ch == '/' && i + 1 < t.Length && t[i + 1] == '*') { int e = t.IndexOf("*/", i + 2); if (e < 0) return -1; i = e + 1; }
            else if (ch == o) depth++;
            else if (ch == c) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
"@
}

function Find-Close([string]$t, [int]$open) {
    $i = [JBeamText]::FindClose($t, $open)
    if ($i -lt 0) { throw "Unbalanced '$($t[$open])' in jbeam" }
    return $i
}

# Returns @(start, end) of the value following "key": (an object or array).
function Find-Block([string]$t, [string]$key, [int]$from = 0) {
    $m = [regex]::new('"' + [regex]::Escape($key) + '"\s*:\s*([\[{])').Match($t, $from)
    if (-not $m.Success) { return $null }
    $open = $m.Groups[1].Index
    return @($open, (Find-Close $t $open))
}

function Read-ZipText([string]$zipPath, [string]$entryName) {
    $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $e = $z.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
        if (-not $e) { throw "$entryName not found in $zipPath" }
        $r = New-Object IO.StreamReader($e.Open())
        try { return $r.ReadToEnd() } finally { $r.Close() }
    } finally { $z.Dispose() }
}

# Top-level parts of a jbeam file: @{ Key; Text (the part's {...}) }
function Get-TopLevelParts([string]$t) {
    $parts = @()
    $i = $t.IndexOf('{')
    if ($i -lt 0) { return $parts }
    $end = [JBeamText]::FindClose($t, $i)
    if ($end -lt 0) { return $parts }
    $re = [regex]'"((?:[^"\\]|\\.)*)"\s*:\s*\{'
    $pos = $i + 1
    while ($true) {
        $m = $re.Match($t, $pos)
        if (-not $m.Success -or $m.Index -ge $end) { break }
        $open = $m.Index + $m.Length - 1
        $close = [JBeamText]::FindClose($t, $open)
        if ($close -lt 0) { break }
        $parts += [pscustomobject]@{ Key = $m.Groups[1].Value; Text = $t.Substring($open, $close - $open + 1) }
        $pos = $close + 1
    }
    return $parts
}

# All parts a car can see for its engine: its own folder, plus the shared engine family
# folders in common.zip that its engines come from.
function Get-ModelParts([string]$beamng, $veh) {
    $sources = @(, @($veh.Zip, "vehicles/$($veh.Model)/"))
    foreach ($e in $veh.Engines) {
        if ($e.Zip -eq $veh.Zip) { continue }
        $d = $e.File.Substring(0, $e.File.LastIndexOf('/') + 1)
        if (-not ($sources | Where-Object { $_[0] -eq $e.Zip -and $_[1] -eq $d })) { $sources += , @($e.Zip, $d) }
    }
    $byKey = @{}; $byType = @{}; $fileOf = @{}
    foreach ($src in $sources) {
    $z = [IO.Compression.ZipFile]::OpenRead((Join-Path $beamng "content\vehicles\$($src[0])"))
    try {
        foreach ($e in $z.Entries) {
            if (-not ($e.FullName.StartsWith($src[1]) -and $e.FullName.EndsWith(".jbeam"))) { continue }
            $r = New-Object IO.StreamReader($e.Open()); $t = $r.ReadToEnd(); $r.Close()
            foreach ($p in (Get-TopLevelParts $t)) {
                $byKey[$p.Key] = $p.Text; $fileOf[$p.Key] = $e.FullName
                $st = [regex]::Match($p.Text, '"slotType"\s*:\s*"([^"]+)"')
                if ($st.Success) {
                    if (-not $byType.ContainsKey($st.Groups[1].Value)) { $byType[$st.Groups[1].Value] = New-Object Collections.ArrayList }
                    [void]$byType[$st.Groups[1].Value].Add($p.Key)
                }
            }
        }
    } finally { $z.Dispose() }
    }
    return @{ ByKey = $byKey; ByType = $byType; FileOf = $fileOf }
}

# A stock part we can build on: a combustion engine with its own torque table.
function Get-EngineCandidate([string]$key, [string]$text) {
    if ($text -notmatch '"combustionEngine"') { return $null }
    $st = [regex]::Match($text, '"slotType"\s*:\s*"([^"]+)"')
    if (-not $st.Success) { return $null }
    $b = Find-Block $text "mainEngine"
    if (-not $b) { return $null }
    $me = $text.Substring($b[0], $b[1] - $b[0] + 1)
    $tb = Find-Block $me "torque"
    if (-not $tb) { return $null }
    $peak = 0.0
    foreach ($m in [regex]::Matches($me.Substring($tb[0], $tb[1] - $tb[0] + 1), '\[\s*[\d.]+\s*,\s*([\d.]+)\s*\]')) {
        $peak = [Math]::Max($peak, [double]::Parse($m.Groups[1].Value, $inv))
    }
    $cyl = [regex]::Match($text, '"fundamentalFrequencyCylinderCount"\s*:\s*(\d+)')
    $nm = [regex]::Match($text, '"name"\s*:\s*"([^"]*)"')
    [pscustomobject]@{
        PartKey = $key; SlotType = $st.Groups[1].Value; Name = $nm.Groups[1].Value
        PeakTorque = $peak; Cylinders = $(if ($cyl.Success) { [int]$cyl.Groups[1].Value } else { 0 })
        Diesel = $text -match '"requiredEnergyType"\s*:\s*"diesel"'
    }
}

# Engine candidates in one zip, plus every jbeam text (for slot lookups). Entries outside
# vehicles/<model>/ are ignored; for common.zip <model> is "common".
function Read-ZipEngines([string]$zipPath) {
    $engines = @(); $texts = @{}; $names = @{}
    $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($e in $z.Entries) {
            $mm = [regex]::Match($e.FullName, '^vehicles/([^/]+)/(.*)$')
            if (-not $mm.Success) { continue }
            $model = $mm.Groups[1].Value
            if ($mm.Groups[2].Value -eq "info.json") {
                $r = New-Object IO.StreamReader($e.Open()); $j = $r.ReadToEnd(); $r.Close()
                $names[$model] = @(
                    [regex]::Match($j, '"Brand"\s*:\s*"([^"]+)"').Groups[1].Value,
                    [regex]::Match($j, '"Name"\s*:\s*"([^"]+)"').Groups[1].Value)
                continue
            }
            if ($e.FullName -notlike "*.jbeam") { continue }
            $r = New-Object IO.StreamReader($e.Open()); $t = $r.ReadToEnd(); $r.Close()
            if (-not $texts.ContainsKey($model)) { $texts[$model] = New-Object Text.StringBuilder }
            [void]$texts[$model].Append($t)
            if ($t.IndexOf('"combustionEngine"') -lt 0) { continue }
            foreach ($p in (Get-TopLevelParts $t)) {
                $c = Get-EngineCandidate $p.Key $p.Text
                if (-not $c) { continue }
                $c | Add-Member Model $model; $c | Add-Member Zip (Split-Path $zipPath -Leaf); $c | Add-Member File $e.FullName
                $engines += $c
            }
        }
    } finally { $z.Dispose() }
    return @{ Engines = $engines; Texts = $texts; Names = $names }
}

function Get-Translations([string]$beamng) {
    $map = @{}
    $f = Join-Path $beamng "locales\translations\en-US\vehiclesGenerated.translation.json"
    if (Test-Path $f) {
        foreach ($m in [regex]::Matches([IO.File]::ReadAllText($f), '"(vehiclesData\.[^"]+\.Name)"\s*:\s*"([^"]*)"')) { $map[$m.Groups[1].Value] = $m.Groups[2].Value }
    }
    return $map
}

# Index of every car in the install that has a combustion engine (its own, or a shared one
# from common.zip that fits one of its slots). Cached until the game files change.
function Get-VehicleIndex([string]$beamng, [scriptblock]$progress) {
    $dir = Join-Path $beamng "content\vehicles"
    $zips = @(Get-ChildItem $dir -Filter *.zip)
    $stamp = ($zips | ForEach-Object { "$($_.Name):$($_.Length):$($_.LastWriteTimeUtc.Ticks)" }) -join "|"
    $stamp = [BitConverter]::ToString([Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("index-v2|$beamng|$stamp")))
    if (Test-Path $IndexFile) {
        try {
            $c = Get-Content $IndexFile -Raw | ConvertFrom-Json
            if ($c.Stamp -eq $stamp) { return @($c.Vehicles) }
        } catch {}
    }

    $tr = Get-Translations $beamng
    if ($progress) { & $progress "Scanning shared engines (common.zip)..." }
    $shared = @()
    $common = $zips | Where-Object { $_.BaseName -eq "common" }
    if ($common) { $shared = @((Read-ZipEngines $common.FullName).Engines) }
    $sharedTypes = @($shared | ForEach-Object { $_.SlotType } | Select-Object -Unique)

    $result = @(); $n = 0
    foreach ($f in ($zips | Where-Object { $_.BaseName -ne "common" })) {
        $n++
        if ($progress) { & $progress "Scanning $($f.BaseName) ($n/$($zips.Count - 1))..." }
        try { $r = Read-ZipEngines $f.FullName } catch { continue }
        foreach ($model in $r.Texts.Keys) {
            if ($model -in @("common", "engine_props", "large_roller", "simple_traffic")) { continue }   # not drivable cars
            $all = $r.Texts[$model].ToString()
            $engines = @($r.Engines | Where-Object { $_.Model -eq $model })
            foreach ($st in $sharedTypes) {
                if ($all.Contains('"' + $st + '"')) { $engines += @($shared | Where-Object { $_.SlotType -eq $st }) }
            }
            if ($engines.Count -eq 0) { continue }
            $name = $model
            if ($r.Names.ContainsKey($model)) {
                $brand, $key = $r.Names[$model]
                $nm = $(if ($tr.ContainsKey($key)) { $tr[$key] } elseif ($key -and $key -notlike "vehiclesData.*") { $key } else { $model })
                $name = "$brand $nm".Trim()
            }
            $result += [pscustomobject]@{ Model = $model; Name = $name; Zip = $f.Name; Engines = $engines }
        }
    }
    $result = @($result | Sort-Object Name)
    ConvertTo-Json @{ Stamp = $stamp; Vehicles = $result } -Depth 6 -Compress | Set-Content -Path $IndexFile -Encoding UTF8
    return $result
}

# Pick the stock engine closest to ours, from the car's main engine slot.
# Petrol bases are preferred (a diesel base would burn diesel and smoke), as are bases whose
# stock sub-parts can all be swapped for ones that add no boost of their own.
function Select-BaseEngine($veh, $s, [double]$peakTorque, $parts) {
    $groups = $veh.Engines | Group-Object SlotType | Sort-Object Count -Descending
    $pool = @($groups[0].Group | Where-Object { $parts.ByKey.ContainsKey($_.PartKey) })
    if (@($pool | Where-Object { -not $_.Diesel }).Count -gt 0) { $pool = @($pool | Where-Object { -not $_.Diesel }) }
    $best = $null; $bestScore = [double]::MaxValue
    foreach ($e in $pool) {
        $score = [Math]::Abs([Math]::Log([Math]::Max(1.0, $e.PeakTorque) / [Math]::Max(1.0, $peakTorque)))
        if ($e.Cylinders -gt 0) { $score += 0.3 * [Math]::Abs($e.Cylinders - $s.Cylinders) }
        $dry = New-Object Collections.ArrayList
        [void](Update-SubSlots $parts.ByKey[$e.PartKey] $parts $dry)
        if ($dry -match '^WARNING') { $score += 3 }
        if ($score -lt $bestScore) { $bestScore = $score; $best = $e }
    }
    return $best
}

# Rework the engine's sub-slots: drop the ECU (it would impose the stock rev limiter) and
# swap defaults that bolt on a turbo/supercharger or torque mods for a neutral sibling part.
function Update-SubSlots([string]$part, $parts, [Collections.ArrayList]$notes) {
    $kind = "slots2"; $b = Find-Block $part "slots2"
    if (-not $b) { $kind = "slots"; $b = Find-Block $part "slots" }
    if (-not $b) { return $part }
    $sl = $part.Substring($b[0], $b[1] - $b[0] + 1)
    $re2 = '^(\s*\[\s*"([^"]*)"\s*,\s*\[([^\]]*)\]\s*,\s*\[[^\]]*\]\s*,\s*")([^"]*)(".*)$'
    $re1 = '^(\s*\[\s*"([^"]*)"\s*,\s*")([^"]*)(".*)$'
    $out = foreach ($ln in ($sl -split "`n")) {
        $m = [regex]::Match($ln, $(if ($kind -eq "slots2") { $re2 } else { $re1 }))
        if (-not $m.Success) { $ln; continue }
        $slot = $m.Groups[2].Value
        if ($slot -eq "type" -or $slot -eq "name") { $ln; continue }
        if ($kind -eq "slots2") {
            $types = @([regex]::Matches($m.Groups[3].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
            $pre = $m.Groups[1].Value; $def = $m.Groups[4].Value; $post = $m.Groups[5].Value
        } else {
            $types = @($slot); $pre = $m.Groups[1].Value; $def = $m.Groups[3].Value; $post = $m.Groups[4].Value
        }
        if ($slot -match 'ecu') { [void]$notes.Add("removed stock ECU slot '$slot'"); continue }
        if ($def -and $parts.ByKey.ContainsKey($def) -and $parts.ByKey[$def] -match $PowerAdderPattern) {
            $boosted = $parts.ByKey[$def] -match $BoostPattern
            $cands = @(foreach ($ty in $types) { if ($parts.ByType.ContainsKey($ty)) { $parts.ByType[$ty] } })
            $alt = $cands | Where-Object { $parts.ByKey[$_] -notmatch $PowerAdderPattern } | Select-Object -First 1
            if (-not $alt -and $boosted) { $alt = $cands | Where-Object { $parts.ByKey[$_] -notmatch $BoostPattern } | Select-Object -First 1 }
            if ($alt) { [void]$notes.Add("'$slot' default $def -> $alt (no extra boost/torque mods)"); "$pre$alt$post"; continue }
            if ($boosted) { [void]$notes.Add("WARNING: '$slot' must keep stock $def, which adds its own turbo/supercharger - the engine will make more power than shown") }
            else { [void]$notes.Add("'$slot' keeps stock $def (small stock torque adjustment)") }
        }
        $ln
    }
    $sl = $out -join "`n"
    return $part.Substring(0, $b[0]) + $sl + $part.Substring($b[1] + 1)
}

function Get-SoundSamples($s) {
    $c = $s.Cylinders
    if ($s.Layout -eq "boxer") { return @("B4_2_engine_rear", "B4_2_exhaust") }
    if ($s.Layout -eq "V") {
        if ($c -le 6) { return @("V6_2_engine", "V6_2_exhaust") }
        if ($c -le 8) { return @("V8_cross_engine", "V8_cross_exhaust") }
        return @("V10_2_engine_rear", "V10_2_exhaust")
    }
    switch ($c) {
        { $_ -le 3 } { return @("I3_engine", "I3_exhaust") }
        4 { return @("I4_2_engine", "I4_2_exhaust") }
        5 { return @("i5_engine", "i5pt_1983_02000cc_motorsport_01_exhaust") }
        default { return @("I6_2_engine", "I6_2_exhaust") }
    }
}

function Build-Part($parts, [string]$baseKey, $s, $curve, $peaks, [string]$partKey, [Collections.ArrayList]$notes) {
    $part = $parts.ByKey[$baseKey]

    # --- mainEngine: drop the stock curve and the values we replace, insert ours
    $b = Find-Block $part "mainEngine"
    $me = $part.Substring($b[0], $b[1] - $b[0] + 1)
    $tb = Find-Block $me "torque"
    $tm = [regex]::Match($me.Substring($tb[1] + 1), '^[ \t]*,?')
    $tStart = $me.LastIndexOf('"torque"', $tb[0])
    $me = $me.Remove($tStart, $tb[1] + 1 + $tm.Length - $tStart)
    $keys = 'idleRPM|maxRPM|hasRevLimiter|revLimiterRPM|revLimiterType|revLimiterCutTime|inertia|friction|dynamicFriction|engineBrakeTorque|maxTorqueRating|maxOverTorqueDamage'
    $me = [regex]::Replace($me, '(?m)^[ \t]*"(' + $keys + ')"\s*:[^\r\n]*\r?\n', '')

    $d = $s.DisplacementL
    $crankMul = $(if ($s.Crank -match 'Light') { 0.85 } elseif ($s.Crank -match 'Heavy') { 1.15 } else { 1.0 })
    $inertia = [Math]::Max(0.01, (0.015 + 0.035 * $d) * $crankMul)
    $rows = ($curve | ForEach-Object { "            [$(Fmt $_[0] '0'), $(Fmt $_[1] '0.0')]," }) -join "`r`n"
    $ins = @"

        // ---- generated from Automation: $($s.Name) ----
        "torque":[
            ["rpm", "torque"],
$rows
        ],
        "idleRPM":$(Fmt (700 + 900 * $s.Cam) '0'),
        "maxRPM":$(Fmt ($s.RPMLimit + 500) '0'),
        "hasRevLimiter":true,
        "revLimiterRPM":$(Fmt $s.RPMLimit '0'),
        "revLimiterType":"timeBased",
        "revLimiterCutTime":0.1,
        "inertia":$(Fmt $inertia '0.0000'),
        "friction":$(Fmt (6 + 3 * $d) '0.0'),
        "dynamicFriction":$(Fmt (0.012 + 0.005 * $d) '0.0000'),
        "engineBrakeTorque":$(Fmt (12 + 10 * $d + 0.1 * $peaks.TorqueNm) '0.0'),
        "maxTorqueRating":$(Fmt ($peaks.TorqueNm * 1.5) '0'),
        "maxOverTorqueDamage":$(Fmt ($peaks.TorqueNm * 2.0) '0'),
        // ---- stock values kept below ----
"@
    $me = $me.Insert(1, $ins)
    $part = $part.Substring(0, $b[0]) + $me + $part.Substring($b[1] + 1)

    # --- sub-slots
    $part = Update-SubSlots $part $parts $notes

    # --- information, sound, shift points
    $label = "$($s.Name) $(Fmt ($d * 1000) '0')cc $($s.Cylinders)cyl (Automation)"
    $ib = Find-Block $part "information"
    $info = "{`r`n        `"authors`":`"Automation export (AutomationToBeamNG)`",`r`n        `"name`":`"$($label -replace '"', "'")`",`r`n        `"value`":8000,`r`n    }"
    $part = $part.Substring(0, $ib[0]) + $info + $part.Substring($ib[1] + 1)

    $samples = Get-SoundSamples $s
    $script:n = 0
    $part = [regex]::Replace($part, '"sampleName"\s*:\s*"[^"]*"', {
            param($m) $v = $samples[[Math]::Min($script:n, 1)]; $script:n++; "`"sampleName`": `"$v`"" })
    $part = [regex]::Replace($part, '"fundamentalFrequencyCylinderCount"\s*:\s*\d+', "`"fundamentalFrequencyCylinderCount`":$($s.Cylinders)")
    $part = [regex]::Replace($part, '"highShiftUpRPM"\s*:\s*[\d.]+', "`"highShiftUpRPM`":$(Fmt ($s.RPMLimit - 300) '0')")

    return "// Generated by AutomationToBeamNG from an Automation .engine export.`r`n// Base: $($parts.FileOf[$baseKey]) / $baseKey`r`n{`r`n`"$partKey`": $part`r`n}`r`n"
}

# ---------------------------------------------------------------------------
# Paths and settings
# ---------------------------------------------------------------------------
function Test-BeamNGPath([string]$p) { $p -and (Test-Path (Join-Path $p "content\vehicles")) }

function Find-BeamNGPath {
    $ini = Join-Path $env:LOCALAPPDATA "BeamNG\BeamNG.drive.ini"
    if (Test-Path $ini) {
        $m = Select-String -Path $ini -Pattern '^\s*installPath\s*=\s*(.+)$' | Select-Object -First 1
        if ($m -and (Test-BeamNGPath $m.Matches[0].Groups[1].Value.Trim())) { return $m.Matches[0].Groups[1].Value.Trim() }
    }
    $vdf = "${env:ProgramFiles(x86)}\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"')) {
            $c = Join-Path ($m.Matches[0].Groups[1].Value -replace '\\\\', '\') "steamapps\common\BeamNG.drive"
            if (Test-BeamNGPath $c) { return $c }
        }
    }
    return ""
}

function Find-ModsPath {
    foreach ($c in @((Join-Path $env:LOCALAPPDATA "BeamNG\BeamNG.drive\current"), (Join-Path $env:LOCALAPPDATA "BeamNG.drive\latest"))) {
        if (Test-Path $c) { return (Join-Path $c "mods") }
    }
    return (Join-Path $env:LOCALAPPDATA "BeamNG\BeamNG.drive\current\mods")
}

function Get-Settings {
    $s = @{ BeamNGPath = ""; ModsPath = ""; AutomationPath = ""; Checked = @("covet"); LastEngine = "" }
    if (Test-Path $SettingsFile) {
        try {
            $j = Get-Content $SettingsFile -Raw | ConvertFrom-Json
            foreach ($k in @($s.Keys)) { if ($null -ne $j.$k) { $s[$k] = $j.$k } }
        } catch {}
    }
    return $s
}

function Save-Settings($s) { ConvertTo-Json $s | Set-Content -Path $SettingsFile -Encoding UTF8 }

# ---------------------------------------------------------------------------
# Conversion: one mod zip with an engine part for every selected car
# ---------------------------------------------------------------------------
function Convert-Engine([string]$engineFile, $vehicles, [string]$beamng, [string]$mods, [double]$torqueNm, [double]$powerKW, [scriptblock]$log) {
    $spec = Get-EngineSpec (Read-EngineFile $engineFile)
    $curve = Get-Curve $spec
    $est = Get-Peaks $curve $spec.RPMLimit
    $script:CalWarning = $null
    if ($torqueNm -gt 0 -or $powerKW -gt 0) { $curve = Set-CurveCalibration $curve $spec $torqueNm $powerKW }
    $peaks = Get-Peaks $curve $spec.RPMLimit

    & $log "Engine     : $($spec.Name)  ($($spec.Cylinders)-cyl $($spec.Layout), $(Fmt ($spec.DisplacementL*1000) '0') cc, bore $($spec.Bore) x stroke $($spec.Stroke) mm)"
    & $log "Induction  : $($spec.Aspiration -replace '^Aspiration_|_Name$','') $($spec.ChargerKind -replace '_Name$','') max $($spec.MaxBoost) bar | fuel $($spec.FuelType -replace '^FuelType_|_Name$','')"
    & $log "Estimated  : $(Fmt $est.TorqueNm '0.0') Nm @ $($est.TorqueRPM) rpm, $(Fmt $est.PowerKW '0.0') kW ($(Fmt ($est.PowerKW/0.7457) '0') hp) @ $($est.PowerRPM) rpm"
    if ($torqueNm -gt 0 -or $powerKW -gt 0) {
        & $log "Calibrated : $(Fmt $peaks.TorqueNm '0.0') Nm @ $($peaks.TorqueRPM) rpm, $(Fmt $peaks.PowerKW '0.0') kW ($(Fmt ($peaks.PowerKW/0.7457) '0') hp) @ $($peaks.PowerRPM) rpm"
        if ($script:CalWarning) { & $log "WARNING    : $script:CalWarning" }
    } else {
        & $log "             (estimate only - enter Automation's peak torque/power for accurate numbers)"
    }
    & $log "Rev limit  : $($spec.RPMLimit) rpm"

    $safe = ($spec.Name -replace '[^A-Za-z0-9]+', '_').Trim('_').ToLower()
    $entries = @()
    foreach ($veh in $vehicles) {
        try {
            $parts = Get-ModelParts $beamng $veh
            $base = Select-BaseEngine $veh $spec $peaks.TorqueNm $parts
            $notes = New-Object Collections.ArrayList
            $jb = Build-Part $parts $base.PartKey $spec $curve $peaks "$($veh.Model)_engine_automation_$safe" $notes
            $entries += @{ Path = "vehicles/$($veh.Model)/automation_$safe.jbeam"; Text = $jb }
            & $log "  + $($veh.Name): built on stock '$(if ($base.Name -and $base.Name.Length -gt 6) { $base.Name } else { $base.PartKey })'"
            if ($base.Diesel) { [void]$notes.Add("this car only has diesel engines, so it will run on diesel fuel") }
            foreach ($n in $notes) { & $log "      $n" }
        } catch {
            & $log "  ! $($veh.Name): failed - $($_.Exception.Message)"
        }
    }
    if ($entries.Count -eq 0) { throw "No engine parts could be built." }

    New-Item -ItemType Directory -Force $mods | Out-Null
    $modZip = Join-Path $mods "automation_$safe.zip"
    $tmp = "$modZip.tmp"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    $z = [IO.Compression.ZipFile]::Open($tmp, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $entries) {
            $w = New-Object IO.StreamWriter(($z.CreateEntry($e.Path)).Open(), (New-Object Text.UTF8Encoding $false))
            $w.Write($e.Text); $w.Close()
        }
    } finally { $z.Dispose() }
    try { Move-Item $tmp $modZip -Force }
    catch { throw "Could not write $modZip - is BeamNG.drive running? Close it and try again." }

    & $log "Installed  : $modZip"
    & $log "In BeamNG: spawn one of those cars, open Vehicle Config > Parts > Engine, and pick '$($spec.Name) ... (Automation)'."
    return $modZip
}

# ---------------------------------------------------------------------------
# Command line mode
# ---------------------------------------------------------------------------
$settings = Get-Settings
if (-not $BeamNGPath) { $BeamNGPath = $(if (Test-BeamNGPath $settings.BeamNGPath) { $settings.BeamNGPath } else { Find-BeamNGPath }) }
if (-not $ModsPath) { $ModsPath = $(if ($settings.ModsPath) { $settings.ModsPath } else { Find-ModsPath }) }
$Vehicles = @($Vehicles | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })   # -File passes "a,b" as one string
$powerArg = $(if ($PeakPowerKW -gt 0) { $PeakPowerKW } elseif ($PeakPowerHP -gt 0) { $PeakPowerHP * 0.7457 } else { 0 })

if ($ListVehicles -or $Vehicles.Count -gt 0) {
    if (-not (Test-BeamNGPath $BeamNGPath)) { throw "BeamNG.drive not found. Pass -BeamNGPath <game folder>." }
    $index = Get-VehicleIndex $BeamNGPath { param($m) Write-Host $m }
    if ($ListVehicles) { $index | ForEach-Object { "{0,-16} {1}" -f $_.Model, $_.Name }; return }
    if (-not $EngineFile) { throw "Pass -EngineFile <file.engine>." }
    $sel = @($index | Where-Object { $Vehicles -contains $_.Model })
    $missing = @($Vehicles | Where-Object { $index.Model -notcontains $_ })
    if ($missing) { throw "Unknown car(s): $($missing -join ', '). Use -ListVehicles to see the names." }
    [void](Convert-Engine (Resolve-Path $EngineFile).Path $sel $BeamNGPath $ModsPath $PeakTorqueNm $powerArg { param($m) Write-Host $m })
    return
}

# ---------------------------------------------------------------------------
# Window
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

# One row of the engine table: what the design is and what this tool estimates it makes.
function Get-EngineSummary([string]$path) {
    $spec = Get-EngineSpec (Read-EngineFile $path)
    $pk = Get-Peaks (Get-Curve $spec) $spec.RPMLimit
    $layout = "$(@{ inline = 'I'; V = 'V'; boxer = 'F' }[$spec.Layout])$($spec.Cylinders)"
    $ind = $(if ($spec.Aspiration -match 'Turbo') { "Turbo $($spec.MaxBoost) bar" }
             elseif ($spec.Aspiration -match 'Super') { "Supercharged $($spec.MaxBoost) bar" }
             else { "Naturally aspirated" })
    [pscustomobject]@{
        Path = $path; Name = $spec.Name; Layout = $layout
        Size = $(if ($spec.DisplacementL -lt 1) { "$(Fmt ($spec.DisplacementL * 1000) '0') cc" } else { "$(Fmt $spec.DisplacementL '0.0') L" })
        Induction = $ind; Fuel = ($spec.FuelType -replace '^FuelType_|_Name$', '')
        RPM = $spec.RPMLimit; HP = $pk.PowerKW / 0.7457; Nm = $pk.TorqueNm
    }
}

function Test-AutomationPath([string]$p) { $p -and (Test-Path $p -PathType Container) }
$DefaultAutomationPath = Join-Path $env:LOCALAPPDATA "AutomationGame\Saved\UserData\CarSaveExport"

$form = New-Object Windows.Forms.Form
$form.Text = "Automation -> BeamNG.drive engine converter"
$form.Size = New-Object Drawing.Size(820, 880)
$form.MinimumSize = New-Object Drawing.Size(700, 760)
$form.StartPosition = "CenterScreen"
$form.Font = New-Object Drawing.Font("Segoe UI", 9)

$AT = [Windows.Forms.AnchorStyles]
function Add-Ctl($ctl, $x, $y, $w, $h, $anchor) {
    $ctl.Location = New-Object Drawing.Point($x, $y); $ctl.Size = New-Object Drawing.Size($w, $h)
    if ($anchor) { $ctl.Anchor = $anchor }
    $form.Controls.Add($ctl); return $ctl
}
function New-Label($text, $x, $y, $w = 200) { $l = New-Object Windows.Forms.Label; $l.Text = $text; Add-Ctl $l $x $y $w 20 $null }
$TLR = $AT::Top -bor $AT::Left -bor $AT::Right
$TR = $AT::Top -bor $AT::Right
$W = 780; $BX = $W - 88   # content width, button column

[void](New-Label "Automation export folder (where your .engine files are)" 12 12 400)
$txtAuto = Add-Ctl (New-Object Windows.Forms.TextBox) 12 32 ($BX - 20) 24 $TLR
$btnAuto = Add-Ctl (New-Object Windows.Forms.Button) $BX 31 92 26 $TR; $btnAuto.Text = "Browse..."

$engines = Add-Ctl (New-Object Windows.Forms.ListView) 12 62 ($W) 170 $TLR
$engines.View = "Details"; $engines.FullRowSelect = $true; $engines.MultiSelect = $false; $engines.HideSelection = $false
foreach ($c in @(@("Engine", 170), @("Layout", 55), @("Size", 65), @("Induction", 135), @("Fuel", 120), @("Rev limit", 65), @("Power*", 70), @("Torque*", 70), @("File", 130))) {
    [void]$engines.Columns.Add($c[0], $c[1])
}
$engHint = New-Label "* this tool's estimate from the design. Automation's own dyno numbers can differ - type them below to match exactly." 12 236 ($W)
$engHint.ForeColor = [Drawing.Color]::DimGray; $engHint.Anchor = $TLR

[void](New-Label "BeamNG.drive install folder" 12 262 300)
$txtGame = Add-Ctl (New-Object Windows.Forms.TextBox) 12 282 ($BX - 20) 24 $TLR
$btnGame = Add-Ctl (New-Object Windows.Forms.Button) $BX 281 92 26 $TR; $btnGame.Text = "Browse..."

[void](New-Label "BeamNG mods folder (the engine is installed here)" 12 312 400)
$txtMods = Add-Ctl (New-Object Windows.Forms.TextBox) 12 332 ($BX - 20) 24 $TLR
$btnMods = Add-Ctl (New-Object Windows.Forms.Button) $BX 331 92 26 $TR; $btnMods.Text = "Browse..."

[void](New-Label "Peak torque (Nm)" 12 368 110)
$numTq = Add-Ctl (New-Object Windows.Forms.NumericUpDown) 124 366 90 24 $null
$numTq.Maximum = 20000; $numTq.DecimalPlaces = 1
[void](New-Label "Peak power (hp)" 232 368 100)
$numHp = Add-Ctl (New-Object Windows.Forms.NumericUpDown) 334 366 90 24 $null
$numHp.Maximum = 20000; $numHp.DecimalPlaces = 1
$hint = New-Label "Optional, from Automation. 0 = use estimate." 436 368 330
$hint.ForeColor = [Drawing.Color]::DimGray; $hint.Anchor = $TLR

[void](New-Label "Cars to add this engine to" 12 400 300)
$list = Add-Ctl (New-Object Windows.Forms.CheckedListBox) 12 420 ($BX - 20) 200 ($TLR -bor $AT::Bottom)
$list.CheckOnClick = $true; $list.IntegralHeight = $false
$btnAll = Add-Ctl (New-Object Windows.Forms.Button) $BX 420 92 26 $TR; $btnAll.Text = "All"
$btnNone = Add-Ctl (New-Object Windows.Forms.Button) $BX 452 92 26 $TR; $btnNone.Text = "None"
$btnScan = Add-Ctl (New-Object Windows.Forms.Button) $BX 484 92 26 $TR; $btnScan.Text = "Rescan"

$btnGo = Add-Ctl (New-Object Windows.Forms.Button) 12 628 ($W) 34 ($AT::Bottom -bor $AT::Left -bor $AT::Right)
$btnGo.Text = "Convert and install into BeamNG"; $btnGo.Font = New-Object Drawing.Font("Segoe UI", 10, [Drawing.FontStyle]::Bold)
$txtLog = Add-Ctl (New-Object Windows.Forms.TextBox) 12 670 ($W) 160 ($AT::Bottom -bor $AT::Left -bor $AT::Right)
$txtLog.Multiline = $true; $txtLog.ReadOnly = $true; $txtLog.ScrollBars = "Vertical"; $txtLog.Font = New-Object Drawing.Font("Consolas", 9)

$script:index = @()
$logFn = { param($m) $txtLog.AppendText("$m`r`n"); [Windows.Forms.Application]::DoEvents() }

$startEngine = $(if ($EngineFile) { (Resolve-Path $EngineFile).Path } else { "$($settings.LastEngine)" })
$txtAuto.Text = $(if ($EngineFile) { Split-Path $startEngine } elseif (Test-AutomationPath $settings.AutomationPath) { $settings.AutomationPath } else { $DefaultAutomationPath })
$txtGame.Text = $BeamNGPath
$txtMods.Text = $ModsPath

function Update-EngineList([string]$select) {
    $engines.Items.Clear()
    if (-not (Test-AutomationPath $txtAuto.Text)) { & $logFn "Pick the folder where Automation saves exported .engine files."; return }
    $files = @(Get-ChildItem $txtAuto.Text -Filter *.engine -Recurse -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($f in $files) {
        try {
            $e = Get-EngineSummary $f.FullName
            $it = New-Object Windows.Forms.ListViewItem($e.Name)
            foreach ($v in @($e.Layout, $e.Size, $e.Induction, $e.Fuel, "$($e.RPM)", "$(Fmt $e.HP '0') hp", "$(Fmt $e.Nm '0') Nm", $f.Name)) { [void]$it.SubItems.Add($v) }
        } catch {
            $it = New-Object Windows.Forms.ListViewItem("(unreadable)")
            foreach ($v in @("", "", "", "", "", "", "", $f.Name)) { [void]$it.SubItems.Add($v) }
            $it.ForeColor = [Drawing.Color]::Gray
        }
        $it.Tag = $f.FullName
        [void]$engines.Items.Add($it)
        if ($select -and $f.FullName -eq $select) { $it.Selected = $true; $it.EnsureVisible() }
    }
    if ($engines.SelectedItems.Count -eq 0 -and $engines.Items.Count -gt 0) { $engines.Items[0].Selected = $true }
    & $logFn "Found $($files.Count) Automation engine file(s) in $($txtAuto.Text)."
}

function Update-CarList {
    $list.Items.Clear(); $script:index = @()
    if (-not (Test-BeamNGPath $txtGame.Text)) { & $logFn "Pick your BeamNG.drive install folder (it contains BeamNG.drive.exe and a 'content' folder)."; return }
    $form.Cursor = "WaitCursor"
    try {
        $script:index = @(Get-VehicleIndex $txtGame.Text $logFn)
        $checked = @($settings.Checked)
        foreach ($v in $script:index) {
            $i = $list.Items.Add("$($v.Name)   ($($v.Model))")
            if ($checked -contains $v.Model) { $list.SetItemChecked($i, $true) }
        }
        & $logFn "Found $($script:index.Count) cars with combustion engines."
    } catch { & $logFn "Scan failed: $($_.Exception.Message)" }
    finally { $form.Cursor = "Default" }
}

function Select-Folder([string]$description, [string]$start, [scriptblock]$valid, [string]$invalidMsg) {
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = $description
    if ($start -and (Test-Path $start)) { $dlg.SelectedPath = $start }
    while ($dlg.ShowDialog($form) -eq "OK") {
        if (& $valid $dlg.SelectedPath) { return $dlg.SelectedPath }
        [void][Windows.Forms.MessageBox]::Show($form, $invalidMsg, "Wrong folder")
    }
    return $null
}

function Select-GameFolder {
    $p = Select-Folder "Select your BeamNG.drive install folder (the one containing BeamNG.drive.exe)" $txtGame.Text { param($x) Test-BeamNGPath $x } "That folder doesn't look like a BeamNG.drive install (no content\vehicles folder inside)."
    if ($p) { $txtGame.Text = $p; return $true }
    return $false
}

function Select-AutomationFolder {
    $p = Select-Folder "Select the folder where Automation exports .engine files (usually ...\AutomationGame\Saved\UserData\CarSaveExport)" $txtAuto.Text { param($x) Test-AutomationPath $x } "Pick a folder."
    if ($p) { $txtAuto.Text = $p; return $true }
    return $false
}

# First run: confirm a detected folder, or ask for one.
function Confirm-Folder([string]$what, [string]$detected, [scriptblock]$valid, [scriptblock]$browse) {
    if ((& $valid $detected) -and [Windows.Forms.MessageBox]::Show($form, "Found $what at:`n$detected`n`nUse this folder?", $what, "YesNo", "Question") -eq "Yes") { return }
    [void](& $browse)
}

$btnAuto.Add_Click({ if (Select-AutomationFolder) { Update-EngineList "" } })
$btnGame.Add_Click({ if (Select-GameFolder) { Update-CarList } })
$btnMods.Add_Click({
    $p = Select-Folder "Select your BeamNG.drive mods folder" $txtMods.Text { param($x) $true } ""
    if ($p) { $txtMods.Text = $p }
})
$btnAll.Add_Click({ for ($i = 0; $i -lt $list.Items.Count; $i++) { $list.SetItemChecked($i, $true) } })
$btnNone.Add_Click({ for ($i = 0; $i -lt $list.Items.Count; $i++) { $list.SetItemChecked($i, $false) } })
$btnScan.Add_Click({ if (Test-Path $IndexFile) { Remove-Item $IndexFile -Force }; Update-CarList })

$btnGo.Add_Click({
    if ($engines.SelectedItems.Count -eq 0) { [void][Windows.Forms.MessageBox]::Show($form, "Pick an engine in the list first.", "No engine"); return }
    $engineFile = $engines.SelectedItems[0].Tag
    if (-not (Test-BeamNGPath $txtGame.Text)) { if (-not (Select-GameFolder)) { return }; Update-CarList }
    $sel = @(foreach ($i in $list.CheckedIndices) { $script:index[$i] })
    if ($sel.Count -eq 0) { [void][Windows.Forms.MessageBox]::Show($form, "Tick at least one car.", "No cars selected"); return }

    $settings.BeamNGPath = $txtGame.Text; $settings.ModsPath = $txtMods.Text; $settings.AutomationPath = $txtAuto.Text
    $settings.Checked = @($sel | ForEach-Object { $_.Model }); $settings.LastEngine = $engineFile
    Save-Settings $settings

    $txtLog.Clear(); $btnGo.Enabled = $false; $form.Cursor = "WaitCursor"
    try {
        $zip = Convert-Engine $engineFile $sel $txtGame.Text $txtMods.Text ([double]$numTq.Value) ([double]$numHp.Value * 0.7457) $logFn
        [void][Windows.Forms.MessageBox]::Show($form, "Installed:`n$zip`n`nIn BeamNG open Vehicle Config > Parts > Engine on one of the selected cars.", "Done")
    } catch {
        & $logFn "ERROR: $($_.Exception.Message)"
        [void][Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, "Conversion failed", "OK", "Error")
    } finally { $btnGo.Enabled = $true; $form.Cursor = "Default" }
})

$form.Add_Shown({
    $changed = $false
    if (-not $EngineFile -and -not (Test-AutomationPath $settings.AutomationPath)) {
        Confirm-Folder "your Automation exports" $txtAuto.Text { param($x) Test-AutomationPath $x } { Select-AutomationFolder }
        $settings.AutomationPath = $txtAuto.Text; $changed = $true
    }
    if (-not (Test-BeamNGPath $settings.BeamNGPath)) {
        Confirm-Folder "BeamNG.drive" $txtGame.Text { param($x) Test-BeamNGPath $x } { Select-GameFolder }
        if (Test-BeamNGPath $txtGame.Text) { $settings.BeamNGPath = $txtGame.Text; $settings.ModsPath = $txtMods.Text; $changed = $true }
    }
    if ($changed) { Save-Settings $settings }
    Update-EngineList $startEngine
    Update-CarList
})

[void]$form.ShowDialog()
