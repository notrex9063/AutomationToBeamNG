# Automation → BeamNG.drive Engine Converter

Put engines you designed in **Automation – The Car Company Tycoon Game** into the cars of **BeamNG.drive**.
Pick an engine, tick the cars, click Install. The converter builds a BeamNG mod in which each car gets your
engine with Automation's own dyno curve, the correct boost, a fitting sound and, optionally, Automation's 3D
engine model.

![Windows](https://img.shields.io/badge/platform-Windows-blue) ![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-purple) ![License: MIT](https://img.shields.io/badge/license-MIT-green)

## Features

- **Accurate power.** The curve comes straight from Automation's own results (its `Sandbox_*.db`), not from an
  estimate. The converter then recomputes, from the generated files, the torque graph BeamNG will show and checks
  that it matches Automation within 3 %. If Automation's results can't be found, you can type in the dyno numbers.
- **Real boost.** Turbochargers and superchargers (roots, screw, centrifugal, twin-charged) are simulated by
  BeamNG's own turbo/supercharger code, tuned to reproduce Automation's boost curve. You get lag, the boost
  gauge, whine and blow-off.
- **Any car.** Every BeamNG car with an engine slot is supported, including ones with diesel-only or shared
  engines. Each car's best-matching stock engine is used as the base for mounts, wiring and its sub-parts, and you
  can override that choice.
- **Fair intake/exhaust parts.** Exhaust and intake parts change power by the same *percentage* as on the stock
  engine, so removing the exhaust from a 30 Nm engine doesn't add 20 Nm.
- **Automation's 3D engine model.** If you exported a car from Automation to BeamNG, its engine model is cut out and
  placed in the engine bay of every car you pick.
- **Tuning sliders.** BeamNG's Tuning menu gets rev limit, idle, rev-limiter cut, power, boost and backfire
  sliders. Their defaults are Automation's values.
- **Upgrade parts.** Sport and Race ECUs, two-step launch control, forged or indestructible internals, an oil
  cooler, nitrous (on cars that have nitrous kits) and, for engines without boost, three turbo kits.
- **Live updates.** While BeamNG is running, installs go straight into the game, and the car you're driving
  reloads (or gets the engine fitted) without a restart.
- **Reinstall all.** After a BeamNG update or a new version of the converter, a banner offers to rebuild every
  installed engine, either into the same cars or into every car.
- **Ready-made configs.** Each car can also get a "<engine> (Automation)" config in the vehicle selector.
- **Physics-safe.** It never lowers node weights below stock, and inertia and friction are sized to the engine,
  which avoids the instability crashes a naive swap causes.

## Requirements

- Windows 10 or 11 with .NET Framework 4.8 (built into Windows; nothing to install)
- [BeamNG.drive](https://www.beamng.com/)
- [Automation](https://automationgame.com/) and some `.engine` exports. In Automation's engine designer choose
  Export → Engine. They are saved in `%LOCALAPPDATA%\AutomationGame\Saved\UserData\CarSaveExport`.

## Getting started

1. Download `AutomationToBeamNG.exe` from the [Releases](../../releases) page, or build it yourself (see below).
2. Run it. On the first start it asks for (and usually finds on its own):
   - your Automation exports folder
   - your BeamNG.drive install folder
   - your BeamNG mods folder (`%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\mods`)
3. Pick an engine on the left. Its size, power, torque and curve are shown.
4. Tick the cars you want it in, then click **Install**.
5. In BeamNG, either spawn the "<engine> (Automation)" config, or open **Vehicle Config → Parts → Engine** and
   choose the engine. Tune it in **Vehicle Config → Tuning → Engine**.

Your installed engines are listed on the **Installed engines** tab, where you can update, remove or reinstall them.

## Building from source

No Visual Studio or .NET SDK is needed. The C# compiler that ships with Windows is enough:

```bat
build.bat
```

This produces `AutomationToBeamNG.exe` next to `build.bat`. The source is C# 5 / WinForms in `src\`:

| File | What it does |
|---|---|
| `Engine.cs` | Reads Automation `.engine` files (Lua tables) and estimates curves when results are missing |
| `Automation.cs` | Reads Automation's dyno results from its SQLite databases (via Windows' `winsqlite3.dll`) |
| `Tuning.cs` | Torque table, friction, turbo/supercharger design, the in-game torque controller and a BeamNG torque simulator |
| `BeamNG.cs` | Car index, base-engine choice, jbeam generation, configs, checks and writing the mod |
| `Upgrades.cs` | Tuning sliders and upgrade parts, and the resolver for jbeam variables |
| `EngineModels.cs` | Finds Automation's BeamNG exports and cuts out the engine model |
| `Live.cs` | The companion mod and link that update a running game |
| `MainForm.cs`, `Dialogs.cs`, `Ui.cs` | The interface |

`old-powershell\` has the original PowerShell prototype.

## How it works (short version)

BeamNG computes torque as `(table × torqueModMult + intake + exhaust mods) × boost − friction`, and Automation's
curve is already net of friction. So the converter writes a table that, once BeamNG has applied all of that,
lands exactly on Automation's curve. It then proves this by running the same maths on the files it generated. A
small vehicle controller rescales the car's intake and exhaust torque changes to the engine's size after BeamNG
has built the curve. Boost is reproduced by solving BeamNG's turbo and supercharger equations for the pressure
table that gives Automation's boost at each rpm.

## Known limitations

- Windows only.
- Only BeamNG's own cars are indexed. Modded cars may work if they follow the stock engine-slot layout.
- Automation's engine model can be bigger than a small car's engine bay. The converter warns you when that happens.
- BeamNG and Automation change between versions. If something breaks after an update, use **Reinstall all**
  and open an issue.

## Credits

- Created by the project owner, who designed it, tested it in-game and steered every feature.
- Written with **[Claude](https://claude.ai)** (Anthropic's AI, via Claude Code). Claude wrote the code and the
  research behind it: BeamNG's jbeam merging, its engine, turbo and supercharger code, and Automation's export and
  database formats.
- Inspired in part by R4NKING's *Turbo Everything* and *Highly Customizable Engines* BeamNG mods. None of their
  files are included; the tuning and upgrade parts are generated from scratch using BeamNG's own systems.

## Disclaimer

This is a fan-made tool. It is not affiliated with, endorsed by or supported by Camshaft Software (Automation) or
BeamNG GmbH (BeamNG.drive). All trademarks belong to their owners. The generated mods use your own Automation
designs and parts of BeamNG's own vehicles, which stay in your game install and are not redistributed by this
project.

## License

[MIT](LICENSE)
