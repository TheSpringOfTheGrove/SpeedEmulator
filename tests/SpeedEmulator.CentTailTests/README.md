# Generated two-decimal cent-tail checks

Run `dotnet run --project tests/SpeedEmulator.CentTailTests -c Release`.

The tests use synthetic records only. They cover all 34 bank categories, separate
income/expense quotas, unchanged totals/counts/times, per-row cumulative limits,
monthly and intraday detail budgets, CCB amount strata, interest rollback, negative
balances, Alipay amount-bearing serials, all correction exits, and a 10,000-row bound.

The policy is a best-effort final pass on variable two-decimal reference records.
It never changes fixed-date items, fixed amounts, imported/manual records or other
precisions, and never relaxes mathematical bounds to satisfy a style quota.

The target is zero `.01` through `.09` tails whenever feasible, including small
groups. Failed batch interest checks retain safe individual pairs. Personal BOC
closing first plans non-low, varied tails on references before interest settles;
an incomplete preferred plan is discarded and the original exact-budget path is
used. Final interest is always verified from the actual ledger.
