# Manufacturer Ball Valve Catalogs

## Bray / Flow-Tek Series 51

- Product: 2-piece full-port brass ball valve, female NPT.
- Official catalog revision: `F-2207_EN_S51_20260416`.
- Official source:
  <https://www.bray.com/docs/default-source/brochures/product-brochures/threaded-ball-valves-s51-pb-en-us.pdf>
- Catalog-verified fields used by FamilyMEP:
  - `A`: face-to-face length;
  - `B`: full-port bore;
  - `C`: total height;
  - `D`: handle length;
  - `Cv`, operating torque and weight.

| Type | NPS | A / L (mm) | Port B (mm) | C / H (mm) | Handle D (mm) | Cv | Torque (N·m) | Weight (kg) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| DN15 | 1/2" | 51.0 | 14.8 | 48.2 | 89.0 | 16 | 5.65 | 0.23 |
| DN20 | 3/4" | 59.2 | 18.8 | 52.4 | 95.0 | 43 | 7.91 | 0.32 |
| DN25 | 1" | 69.0 | 23.8 | 64.2 | 114.0 | 58 | 10.17 | 0.51 |
| DN32 | 1-1/4" | 82.8 | 31.4 | 74.0 | 114.0 | 105 | 14.12 | 0.74 |
| DN40 | 1-1/2" | 91.9 | 39.4 | 84.1 | 138.0 | 160 | 20.34 | 1.10 |
| DN50 | 2" | 105.0 | 49.4 | 92.4 | 138.0 | 325 | 32.77 | 1.60 |
| DN65 | 2-1/2" | 135.0 | 63.2 | 119.5 | 217.4 | 475 | 47.46 | 3.50 |
| DN80 | 3" | 151.6 | 74.0 | 127.5 | 217.4 | 780 | 80.22 | 4.30 |
| DN100 | 4" | 185.5 | 97.9 | 147.3 | 241.0 | 1350 | 105.08 | 7.80 |

## Apollo 77C-100-A

- Product: 2-piece full-port bronze ball valve, female NPT.
- Official datasheet revision: `04/2025 — SS1414`.
- Official source:
  <https://aalberts-ips.us/wp-content/uploads/2025/04/3.31.25-Draft-DS_77C-100-A_SS1414.pdf>
- Published range: 1/4" through 2-1/2".
- FamilyMEP includes DN15 through DN65 and stores each official part number.
- Catalog-verified fields include bore, `d1/d2`, `l1/l2`, `M1/M2`, `V/Y`
  and product weight.

## NIBCO T-585-70

- Product: 2-piece full-port bronze ball valve, threaded.
- Official product and Revit-family download page:
  <https://catalog.nibco.com/en-ca/viewitems/ball-valves-7/t-585-70-two-piece-bronze-ball-valve---full-port->
- NIBCO publishes manufacturer-authored Revit content. Prefer that official BIM
  geometry when the project requires manufacturer-exact geometry.

## Accuracy and LOD

The FamilyMEP procedural families use the published manufacturer envelope dimensions
and product metadata. Dimensions not published in the catalog—such as casting radii,
thread profile, wall thickness, stem internals and seat geometry—are derived.

Therefore:

- procedural catalog mode: catalog-accurate parametric geometry, suitable for LOD 400;
- true manufacturer-exact LOD 500: requires manufacturer-authored RFA or CAD/STEP/SAT
  geometry plus verified fabrication/as-built product data.

FamilyMEP records this distinction in the `Geometry Accuracy` and `LOD Status`
Family Type parameters.
