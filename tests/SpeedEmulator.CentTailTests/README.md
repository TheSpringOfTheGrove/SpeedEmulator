# Generated two-decimal cent-tail checks

Run `dotnet run --project tests/SpeedEmulator.CentTailTests -c Release`.

Ordinary generated transactions strictly exclude `.01` through `.09` tails.
The shared two-decimal legal-value predicate excludes them during sampling,
clamping and closing. CCB ranking enumerates 81 legal cent values per yuan and
its budget solver can cross the full forbidden interval. Existing nonzero-last-
digit precision semantics remain in effect.

All successful generation and balance-correction exits check the final records.
There is no successful-return fallback containing forbidden ordinary tails.
Conflicting reference ranges and fixed amounts are identified before generation;
the generator does not change the user's configuration to make them fit.
Automatic settlement interest/tax, document-imported rows and persisted manual
rows are retained at their actual values, as explicitly requested.

Tests use synthetic data only, including all nine forbidden suffixes, income and
expense directions, fixed/reference/synthetic rows, all 34 bank categories with
three seeded public generation runs and every correction exit, exact CCB rank
round trips, budget/interest constraints, immutable inputs, and 10,000-row cleanup.
