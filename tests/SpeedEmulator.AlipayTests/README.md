# Alipay generated number regression checks

Run `dotnet run --project tests/SpeedEmulator.AlipayTests -c Release` from the repository root.

All cases use synthetic records and an in-memory save repository. They do not read or modify customer data.
Coverage includes all observed business format families, independent blank-field ownership,
formula/manual/import preservation, final-date refresh through generation and corrections,
explicit refund and deferred-payment relations, amount-in-cents updates, stable utility profiles,
batch uniqueness including cloned metadata, and bypass checks for all 33 other bank categories.

Only personal Alipay uses these simulation formats. Missing original-order context keeps an
independent simulation number; matching amounts or counterparties alone never establishes a relation.
