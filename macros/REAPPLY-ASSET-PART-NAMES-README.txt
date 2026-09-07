SOLIDWORKS Asset file-level part-name repair macro
=================================================

Macro:
  ReapplyMissingAssetPartNames.swb

Purpose:
  Traverse the active top-level assembly using the same Asset boundary rule as
  the exporter. When a model has file-level is_asset=true/yes/1, that model is
  treated as an Asset and its descendants are not scanned. If its file-level
  custom property [零件名] is missing or blank, the macro writes:

      $PRP:"SW-File Name"

Safety and scope:
  - Reads and writes only file-level custom properties (the "Custom" tab).
  - Does not access configuration-specific properties.
  - Does not change asset_version or any other property.
  - Does not overwrite an already non-blank 零件名.
  - Completes the scan and preflight before changing any file.
  - Aborts before changing files if any target is read-only, missing, or already
    has unsaved changes.
  - Creates a complete backup of every target file on the Desktop before the
    first property change.
  - Writes manifest.csv and repair-report.txt into the backup folder.

How to run:
  1. Save the active top-level assembly and its referenced models.
  2. In SOLIDWORKS choose Tools > Macro > Run.
  3. In the file dialog select All files if .swb is not shown.
  4. Select ReapplyMissingAssetPartNames.swb.
  5. Review the detected target count and choose Yes to apply.
  6. After the success message, run Asset / Project classification preview again.

The macro source deliberately constructs the Chinese property name with ChrW,
so it is not affected by the text-macro file encoding used by SOLIDWORKS VBA.
