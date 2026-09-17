# Drain Connection: Y at break

Cases 01, 02, 03 and 05 support an explicit loaded pipe-fitting Family / Type selection. AUTO retains main pipe junction routing preferences. Cases 04 and 06 connect to an open end and do not use this selection.

Select the intended Y under **Y fitting at break (Family / Type)**. The list includes loaded pipe fitting types; geometry is validated during Stage 2. The selected type is isolated on a temporary pipe type when Revit accepts it as a Junction rule. If Revit rejects the rule ("The rule cannot be added to the groupType"), the temporary isolation is rolled back and the same selected family is placed, sized, rotated and connected directly at the break. The original pipe type routing rules remain unchanged and the original type is excluded from temporary-type cleanup. No alternative family is substituted for an explicit selection.

A candidate must have three round piping connectors, an approximately straight run and a measured 35-55 degree branch angle. Unknown connector angles and sanitary Tees around 88-90 degrees are rejected. Existing sizing and connection checks still apply. If Stage 2 fails, its main break and fittings roll back while Stage 1 branch pipes remain.

All Y cases first try native fitting creation from the three pipe connectors when routing isolation succeeds, including both main-run connector orders. Each failed native attempt rolls back before direct placement. After placement and pipe-type restoration, the tool checks the selected type, all three pipe connections, their diameters and the branch angle.

Partial results show an expandable error dialog and append diagnostics to `AppPaths.LogFolder/drain-connection.log` (normally `familymep-data/logs`), including case, selected elements, fitting selection and connection point.

## Revit 2025 manual verification

- Reload the plugin and reopen Drain Connection to refresh the loaded type list.
- In the Snowdon sample with a sanitary Tee routing rule, select a loaded compatible Y and create a connection; verify selected fitting type, three connections, diameters and slope.
- Select a geometrically valid Y that Revit rejects as a Junction routing rule; verify direct placement reaches the break and connects all three pipes without deleting or modifying the original Pipe Type.
- Select the sanitary Tee explicitly; verify Stage 2 reports incompatible geometry, the main remains unbroken and Stage 1 pipes remain.
- Repeat with AUTO, a reducing Y, and Cases 02/03/05; confirm Cases 04/06 disable Y selection.

Build and XAML parsing were checked locally. Live Revit model behavior requires verification in Revit.
