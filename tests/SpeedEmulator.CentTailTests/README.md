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

Closing adjustments use the shared timeline ordering for every bank: available
months take turns, with evenly spaced dates selected within each month. Existing
precision and required-rule priorities stay in force. Monthly budget windows
remain isolated, and fixed transaction dates and existing identifiers do not move.
BOC, CCB and WeChat closing paths use the same ordering; CCB retains its amount
bands. Expense increases use a live suffix-balance index so a repair earlier in
the statement cannot invalidate a later balance. Tests cover both directions,
exact closing, month-local closing, 19-month coverage and 100,000 candidates.

The final shared exit also schedules two-decimal tails in chronological rounds,
covering all 81 legal suffixes when ranges and budgets permit. Reuse inside a
round and recent suffixes at round boundaries are avoided. A budget's unavoidable
cent checksum is closed in its own window, with any exception placed away from
nearby equal tails; yuan transfers preserve the exact direction total. Same-day
and individually verified paired fallbacks retain original interest values.
Every accepted result preserves dates, counts, source ranges, CCB bands, budgets,
interest/tax fields and balance safety. Fixed/imported rows stay untouched. A
signature prevents repeated styling from drifting the same result, and time-only
corrections invalidate it so newly adjacent repeats are checked again.
Time-only corrections use existing-amount permutations within the same source
and budget window, preserving the original amount multiset and CCB distribution.

An explicitly equal, positive integer MinMoney/MaxMoney overrides a positive
fractional precision at runtime (100..100 with precision 2 generates 100.00).
Effective bounds and row metadata both use unit 1 / precision 0; the user's
configuration is unchanged. This covers references and fixed-date projects in
all 34 bank categories and all correction exits. Variable ranges, fixed decimals,
forbidden .01..09 tails and negative rounding precisions keep their rules.
