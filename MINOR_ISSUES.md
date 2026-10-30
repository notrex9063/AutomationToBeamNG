# Minor issues

Known small problems to come back to. Nothing here stops an engine from working.

## Exhaust doesn't meet the engine model

- **Seen:** 2026-10-29, 1LT Hake in the Bruckell Moonhawk, with Automation's engine model from the Tench Panther export.
- **What happens:** the engine model sits right in the bay, but the car's exhaust doesn't line up with the engine's exhaust outlet.
- **Why:** the exhaust is still the car's stock exhaust model, placed for its stock engine. Only the engine is swapped.
- **Possible fix:** move or stretch the start of the car's exhaust (its first exhaust nodes / flexbody) to the engine model's exhaust outlet. Or use the exhaust from Automation's export (`Camso_exhaust_*` with its `exhaust_*` nodes) up to where it meets the car's pipe.

## Car gearboxes are geared for the stock engine

- **Seen:** 2026-10-29, 1LT Hake (10,000 rpm) in the Ibishu Covet: 1st gear runs out at about 59 km/h.
- **What happens:** the car keeps its own gear ratios, chosen for its stock engine's rev range (the Covet's 6,800 rpm). A high-revving engine runs through each gear quickly.
- **Possible fix:** scale the gear ratios (or the final drive) by stock rev limit / engine rev limit, as an option.
