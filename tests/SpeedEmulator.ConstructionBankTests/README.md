# Personal CCB generation regressions

Run from the repository root:

```powershell
dotnet run --project tests/SpeedEmulator.ConstructionBankTests -c Release
```

The tests use synthetic records without saving user data or restarting the app. They cover the reported 20-reference configuration, separate identities for overlapping reference rows, full-range amount distribution, existing precision rules, monthly budgets, all five fixed-date modes, interest reconciliation, and constrained negative-balance correction.

Only personal `建行` uses this generator. Reference date selections are exclusive: selected weekends and holidays form a union; selecting neither permits ordinary weekdays. Fixed-date additions ignore both date checkboxes. System interest follows the bank's settlement calendar.

Amount counts are planned from each full range's expected value and the available budget. Discrete values rotate evenly; larger ranges use equal rank bands with random values inside each band. Reconciliation prefers those bands. If the fixed counts and budget require it, a flexible tail can move within the original configured range; sampling bands are not extra hard restrictions. A small sample need not contain every individual value.

Totals close at the nearest attainable precision, including fixed fractional amounts in the baseline. Expense compensates the income residue to preserve the requested net when its precision permits. Coarse/fine row pairs can close a residue even when neither single-row move helps. The existing final-balance tolerance is retained; configured row ranges, precision, fixed dates, and monthly occurrence counts are never bypassed. Truly incompatible configurations still report an error rather than inventing invalid rows.

Negative-balance repair searches around the first cash-flow gap. Income moves backwards from that gap; expenses move forward to the next funded point. Same-month moves take precedence. Before a more disruptive cross-month move, the generator also considers exchanging occupied times within the same source and configured window. Advancing larger income or delaying larger expense improves the intervening balances without changing amounts or row counts.

Per-source and overall direction/day limits come from the original schedule and available dates, rather than a universal daily transaction limit. A cross-month move cannot increase that source's original destination-month count by more than one. Original schedule metadata survives interest reconciliation and repeated corrections, so the limits cannot grow on every repair pass. Fixed dates and settlement interest do not move. Monthly required occurrences and explicit monthly budgets remain inside their original windows.

The holiday calendar currently contains the official 2024–2026 rest periods. Reference generation for an unsupported year reports that the calendar needs to be supplied.

The optional `--saved-config` diagnostic reads this workstation's reported CCB configuration and creates records only in memory. It does not save accounts, configuration, or generated records:

```powershell
dotnet run --project tests/SpeedEmulator.ConstructionBankTests -c Release -- --saved-config
```

Set `SPEEDEMULATOR_CCB_STRESS=1` to repeat the original fixed-salary configuration with 100 seeds, the mixed integer/cent-budget configuration with 100 seeds for each interest setting, and the larger-income concentration configuration with 100 seeds (400 generations total). These check nonnegative balances, daily and monthly income concentration, required monthly counts, and stable repeated correction. A separate fixed-expense case forces a same-source time exchange after daily capacity is filled.
