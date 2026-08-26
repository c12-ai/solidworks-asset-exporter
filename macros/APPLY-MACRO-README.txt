SOLIDWORKS Custom Property Apply Macro
======================================

ApplyCustomProperties.bas performs the field changes shown by the preview macro.

Rules
-----
1. Preserve the value of every existing field whose name exactly matches the CSV.
2. Add missing fields using DefaultValue from the CSV.
3. Delete file-level custom properties not listed for that document type.
4. Do not edit configuration-specific properties.

Safety
------
Before changing any model, the macro aborts if a component is unresolved/unsaved,
if a document has unsaved changes, or if a source file is read-only.

After confirmation, it copies every source model to a timestamped backup directory
on the Desktop. manifest.csv maps every original path to its backup copy.
Only after all backup copies succeed does the macro start changing and saving models.
Open files that cannot be copied by Windows are backed up through SOLIDWORKS SaveAs
with the Copy option, which does not change the active document path.

Import
------
Create a new SOLIDWORKS VBA macro (*.swp), remove its generated empty module, and
import ApplyCustomProperties.bas. Compile, save, and run Sub main.
