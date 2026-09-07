SOLIDWORKS placement interface migration macro
================================================

Macro:
  MigratePlacementToConnectionInterface.swb

Purpose:
  Copy the legacy file-level custom property:

      placement_interface

  to the new file-level property:

      connection_interface

  for Asset roots where is_placement_required=true/yes/1.

Safety:
  - Scans the complete reachable assembly before writing.
  - Asset roots remain hard boundaries.
  - Reads and writes only file-level Custom properties.
  - Copies only when the legacy value is non-blank and the new value is blank.
  - Stops without changing anything if a non-blank new value conflicts.
  - Does not delete placement_interface or is_placement_required.
  - Does not change asset_version, assembly_version, or configuration properties.
  - Refuses read-only, missing, virtual, or already-dirty targets.
  - Backs up every changed SLDASM/SLDPRT to the Desktop before writing.

Run:
  1. Save the top-level assembly and all referenced models.
  2. In SOLIDWORKS choose Tools > Macro > Run.
  3. Choose MigratePlacementToConnectionInterface.swb (select All files if needed).
  4. Review the detected count and choose Yes.
  5. Check the backup/report folder shown by the completion dialog.

After migration, reopen the Custom Properties task pane. The new template
control connection_interface will display the copied values.
