SOLIDWORKS Asset version reset macro
====================================

Macro:
  SetAllAssetVersionsTo1.swb

Purpose:
  Traverse the active top-level assembly using the exporter Asset boundary
  rule. For every unique visible, non-suppressed, non-envelope Asset, set the
  file-level custom property asset_version to the literal value 1.

Scope and safeguards:
  - Asset is detected only from file-level is_asset=true/yes/1.
  - An Asset is a hard boundary; its children are never scanned or modified.
  - Only missing asset_version values or values other than literal 1 are changed.
  - assembly_version is not changed.
  - No other custom property is changed.
  - Configuration-specific properties are never accessed.
  - The complete scan and target validation finish before any model is changed.
  - The macro aborts if a target is missing, read-only, or already has unsaved
    changes.
  - Every target file is backed up on the Desktop before the first change.
  - manifest.csv and reset-report.txt are written to the backup folder.

How to run:
  1. Save the active top-level assembly and all referenced models.
  2. In SOLIDWORKS choose Tools > Macro > Run.
  3. Select All files if the .swb file is not displayed.
  4. Select SetAllAssetVersionsTo1.swb.
  5. Review the Asset and target counts, then choose Yes to apply.
  6. Run Asset / Project classification preview again after completion.

Important:
  Resetting asset_version does not remove an existing Wanxiang registration.
  If Wanxiang already contains the same Asset UUID and version 1 with a different
  content fingerprint, classification preview will still require a higher
  version. This macro intentionally does not alter the Wanxiang registry.
