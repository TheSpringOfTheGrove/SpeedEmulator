# Bank print regressions

Read-only synthetic-record tests. They do not save accounts, change bank databases, or restart the running app. The catalog comes from the system-template factory, not the repository method that merges templates into user storage.

From the repository root:

```powershell
dotnet run --project tests/SpeedEmulator.BankPrintTests -c Release -- --scan
dotnet run --project tests/SpeedEmulator.BankPrintTests -c Release -- --boc-fields
dotnet run --project tests/SpeedEmulator.BankPrintTests -c Release -- --abc-fields
dotnet run --project tests/SpeedEmulator.BankPrintTests -c Release -- --restored-fields
dotnet run --project tests/SpeedEmulator.BankPrintTests -c Release -p:UseVendorQuestRuntime=true -- --restored-render
```

`--restored-fields` covers 34 native field bindings across 31 templates and all three converters, including blanks, imported records, and custom column bindings. Other fields carry deliberately different values to detect cross-column substitution. The local renderer is checked separately for postal corporate summary/note and ICBC salary summary.

`--restored-render` exports 32 mixed populated/blank records per affected template and verifies expected text and source immutability. PDFs and the JSON manifest are diagnostic artifacts under `tmp/pdfs/restored-bank-fields-20261015`. Postal corporate paper has no note column: its bridge value is tested, but PDF tests do not demand an extra column.

Run `python tests/SpeedEmulator.BankPrintTests/verify_restored_fields.py` with `pdfplumber` installed to check the actual PDF field cells, including blank cells and page boundaries. Decorative seal/watermark glyphs are excluded by their different fonts; table text remains checked.

The opt-in `UseVendorQuestRuntime` copies the vendor's matching QuestPDF DLL only into the test project's generated output. The legacy MRT helper references 2026.7.3 while the application's package references 2026.7.1; default test output cannot render those MRT templates. This option does not change application dependencies or release packaging. Without it, the six affected MRT export tests report a dependency load error, while the converter tests remain valid.
