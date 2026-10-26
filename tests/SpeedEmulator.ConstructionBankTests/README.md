# Personal CCB generation regressions

Run from the repository root:

```powershell
dotnet run --project tests/SpeedEmulator.ConstructionBankTests -c Release
```

The tests use synthetic records without saving user data or restarting the app. They cover the reported 20-reference configuration, separate identities for overlapping reference rows, full-range amount distribution, existing precision rules, monthly budgets, all five fixed-date modes, interest reconciliation, and constrained negative-balance correction.

Only personal `建行` uses this generator. Reference date selections are exclusive: selected weekends and holidays form a union; selecting neither permits ordinary weekdays. Fixed-date additions ignore both date checkboxes. System interest follows the bank's settlement calendar.

Optional counts are planned from each full range's expected value and the available budget. After reserving fixed and required occurrences, positive randomized family weights vary the optional budget mix on each run. Equal ranges remain in one family. With one optional family, neighbouring feasible sample sizes can vary instead. Discrete values rotate evenly, including a random circular phase for leftover strata; larger ranges use equal rank bands with random values inside each band. Reconciliation prefers those bands. If the fixed counts and budget require it, a flexible tail can move within the original configured range; sampling bands are not extra hard restrictions. A small sample need not contain every individual value. Rules or budgets that permit only one count keep that count.

Totals close at the nearest attainable precision, including fixed fractional amounts in the baseline. Expense compensates the income residue to preserve the requested net when its precision permits. Two or three coordinated legal amounts can close a residue even when single-row moves cannot, including prohibited zero precision digits. The existing final-balance tolerance is retained; configured row ranges, precision, fixed dates, and monthly occurrence counts are never bypassed. Truly incompatible configurations still report an error rather than inventing invalid rows.

Negative-balance repair searches around the first cash-flow gap. Income moves backwards from that gap; expenses move forward to the next ordinary income event. That event need not already have a nonnegative balance in the unrepaired ledger. Same-month moves take precedence. Before a more disruptive cross-month move, the generator also considers exchanging occupied times within the same source and configured window. Advancing larger income or delaying larger expense improves the intervening balances without changing amounts or row counts.

Per-source and overall direction/day preferences come from the original schedule and available dates, rather than a universal daily transaction limit. Income keeps its density limit and cannot increase that source's original destination-month count by more than one. Expense density is a soft preference: after normal moves and same-source exchanges are exhausted, expense can use the least-overloaded legal dates after income. An automatic density preference must not reject a feasible fixed-payday configuration. Original schedule metadata survives interest reconciliation and repeated corrections, so income limits cannot grow on every repair pass. Fixed dates and settlement interest do not move. Monthly required occurrences and explicit monthly budgets remain inside their original windows.

The holiday calendar currently contains the official 2024–2026 rest periods. Reference generation for an unsupported year reports that the calendar needs to be supplied.

The optional `--saved-config` diagnostic reads this workstation's reported CCB configuration and creates records only in memory. It does not save accounts, configuration, or generated records:

```powershell
dotnet run --project tests/SpeedEmulator.ConstructionBankTests -c Release -- --saved-config
dotnet run --project tests/SpeedEmulator.ConstructionBankTests -c Release -- --saved-config --vary-counts
```

Set `SPEEDEMULATOR_CCB_STRESS=1` to repeat the original fixed-salary configuration with 100 seeds, the mixed integer/cent-budget configuration with 100 seeds for each interest setting, the larger-income concentration configuration with 100 seeds, and the one-year wide-salary configuration with 100 seeds (500 generations total). These check nonnegative balances, daily and monthly income concentration, required monthly counts, and stable repeated correction. Separate cases force a same-source time exchange after daily capacity is filled, and seven required weekend expenses onto the two legal dates after a fixed payday without changing the opening balance.
