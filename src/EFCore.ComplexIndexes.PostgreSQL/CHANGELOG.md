# EFCore.ComplexIndexes.PostgreSQL — changelog

Changes to the PostgreSQL satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

## 5.5.0

### Changed answers

- [#70](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/70): a brace in a string literal of a typed filter is compared as written. `x => x.Code != "{draft}"` scaffolded `"Code" <> '{{draft}}'`, a different value, so rows whose code is `{draft}` were indexed (and constrained, if unique) although the filter excludes them; it is now `"Code" <> '{draft}'`. Typed expression indexes were not affected. **Upgrading:** the first `migrations add` re-creates each index and exclusion constraint whose typed filter has a brace in a string literal.
- [#72](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/72): typed expression indexes render `Substring`, `Trim`, `TrimStart` and `TrimEnd` as EF Core's queries render them, so a query over the same lambda can use the index. `x.Code.Substring(1, 3)` was `substr("Code", 2, 3)` and is now `substring("Code", 2, 3)`; `x.Code.Trim()` was `btrim("Code")`, which trims spaces only, and is now `btrim("Code", E' \t\n\r')`. PostgreSQL treats `substr` and `substring` as different functions, so those indexes applied and served no query, and a unique index on `Trim()` accepted values that differ only in a trailing tab or line break. **Upgrading:** the first `migrations add` re-creates each typed expression index over these methods. A unique trimmed index may then refuse rows that differ only in tabs or line breaks at the ends, which are the duplicates it was declared to refuse.
- [#68](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/68): a typed filter whose comparison has an operand that can be null now keeps C#'s answer for the rows where it is null, written the way EF Core writes the same lambda in a query. `x => x.Kind != "federated"` on a `string?` scaffolded `"Kind" <> 'federated'`, which is NULL for a null `Kind`, so those rows fell out of the partial index and a unique index did not enforce on them; it is now `("Kind" <> 'federated' OR "Kind" IS NULL)`. A negation over such a comparison is pushed down to it (`!(x.Maybe > 5)` is `("Maybe" <= 5 OR "Maybe" IS NULL)`), and a captured variable holding null compares with `IS [NOT] NULL` instead of `= NULL`, which matched no row. This covers every typed filter: `HasComplexIndex`, `HasComplexCompositeIndex` and `HasExpressionIndex` with a predicate, `HasFilter<T>`, `AddComplexIndexFilter<T>`, and exclusion-constraint filters. Operands that cannot be null (a non-nullable value type, or a reference type annotated non-nullable) translate as before. **Upgrading:** the first `migrations add` drops and re-creates each index and exclusion constraint whose typed filter changed. A unique index now covers rows it left out, so re-creating it fails if those rows hold duplicates; those are duplicates it should have refused.

### Changed

- **Changed:** `UseNpgsqlComplexIndexes()`, `AddNpgsqlComplexIndexes()` and the
  design-time registration throw `InvalidOperationException` when the EF Core
  they run on is not version 10, naming `CodoMetis.EFCore.SchemaKit` as the package for EF Core 11.
  The `[10.0.0, 11.0.0)` dependency ranges did not keep EF Core 11 out: a direct reference to it
  overrides them with NU1608, a warning. The check reads EF Core's version, not the runtime's, so
  EF Core 10 on .NET 11 keeps working.

## 5.4.0

- **Fixed:** indexes on `ToJson()` members are written the way Npgsql's queries read the member —
  `->>` or `#>> '{A,B}'`, cast to the member's store type unless it is a string, `decode(…,
  'base64')` for `byte[]`, `jsonb` for a primitive collection or a `json`/`jsonb` member — so
  PostgreSQL can use them. Before,
  every member was `"doc" -> 'A' ->> 'B'` text, which matched a query only for a top-level string:
  indexes on nested or typed members applied, enforced uniqueness, and served no query. Applies to
  index parts, typed expression indexes and index filters; exclusion constraint filters keep their
  rendering. Upgrading rebuilds each affected index once, under its existing name, at the first
  `migrations add`.
- **Changed:** a non-unique index that starts with a `DateTime`, `DateTimeOffset`, `DateOnly` or
  `TimeOnly` JSON member is rejected at `migrations add`: the queries cast the member to a type
  PostgreSQL cannot index from text, so the index could never be used. Unique ones are allowed.
- **Fixed:** names are checked across kinds at `migrations add`: constraint names per table
  (temporal, temporal foreign key and exclusion constraints against each other and against EF's
  keys, foreign keys and check constraints) and index names per schema (complex indexes and the
  index behind every unique, exclusion and temporal constraint, against EF's indexes and keys).
  Such a clash failed at apply time with 42710 or 42P07 — or, against an exclusion constraint,
  whose ADD is preceded by `DROP CONSTRAINT IF EXISTS`, silently dropped the other constraint.

## 5.2.0

- **Changed:** an index, exclusion constraint, temporal constraint or temporal foreign key whose
  name exceeds 63 bytes is rejected at `migrations add`. PostgreSQL would truncate it with a NOTICE
  and apply the migration cleanly, leaving the object under a name that no declaration and no
  constraint-violation error ever reports. Measured in UTF-8 bytes, as PostgreSQL does. Default
  temporal foreign key names, built from two table names, are the first to hit it: give the
  declaration a `name` — a name-only change renames the constraint in place.

- **New:** `GetExclusionConstraints()` / `GetDeclaredExclusionConstraints()` on `IReadOnlyEntityType`,
  `GetExclusionConstraints()` and `FindExclusionConstraint(name)` on `IReadOnlyModel` — exclusion
  constraints read back as `ExclusionConstraintDeclaration`s (elements with operators, method,
  filter, deferrability, explicit name). `ExclusionPartDefinition` and
  `NpgsqlExclusionAnnotations` are public now. The differ reads the model through the same code.

- **New:** filters on complex, composite and expression indexes and on exclusion constraints
  resolve `{Property.Path}` placeholders — columns, `HasColumnName`, `ToJson()` members as
  extractions — at `migrations add`, into the migration itself. Existing filters with array or JSON
  literals are unaffected.
- **New:** `x => x.Email.Value` on a converter-mapped value object resolves to the converted column
  in typed expression indexes and exclusion elements too.

- **New:** `AddExclusionConstraintFilter(predicate, where)` on `IMutableEntityType` — ANDs a
  predicate onto every selected exclusion constraint's filter, idempotently.

- **New:** typed filter predicates — `x => x.RevokedAt == null` in place of every `filter:` string,
  `HasFilter(x => …)` on the builders, `AddComplexIndexFilter<TEntity>` / `AddExclusionConstraintFilter<TEntity>`.
  Translated at the declaration into a placeholder filter: null checks, comparisons, `&&`/`||`/`!`,
  boolean properties, the string functions of typed expression indexes. Enums and values without a
  portable SQL spelling are refused at the declaration.
- **Changed:** the typed expression translator no longer renders a `DateTime`, `Guid` or enum
  constant as bare text; it throws at the declaration.

## 5.1.0

- **Changed:** `UseNpgsqlComplexIndexes()` / `AddNpgsqlComplexIndexes()` also register the PostgreSQL
  differ at runtime, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes,
  expression indexes, and exclusion and temporal constraints, and `Migrate()`'s pending-model-changes
  check sees a declaration that was never scaffolded. Previously `EnsureCreated()` created the tables
  and silently none of them.
- **New:** whole-document JSON indexes. A `HasComplexIndex` selector ending at a `ToJson()` complex
  property or a complex collection indexes the `jsonb` container column — the idiomatic
  `USING gin (payload jsonb_path_ops)` — through the stock generator, no runtime wiring. Previously
  the path failed to resolve, and complex collections could not be indexed at all. A complex property
  nested inside the document resolves to a `->` extraction (an expression index).
- **New:** `HasStorageParameter(name, value)` — PostgreSQL storage parameters (`WITH (fillfactor=70)`)
  on complex and expression indexes, one call per parameter. Forwarded under the per-parameter
  `Npgsql:StorageParameter:` prefix, which the whitelist and the unknown-key rejection now both accept.
- **New:** `UseCollation(params string[])` — per-column index collations, positional (`UseCollation("C", "")`
  collates only the first column). Stored under Npgsql's model key and mapped to `Relational:Collation`
  on the operation, where Npgsql's generator reads it; a column's own collation is never copied onto
  the index.
- **Fixed:** SQL Server index options (`IsClustered`, `HasFillFactor`, …) on a complex index diffed by
  this satellite are rejected at `migrations add` — property-level and entity-level alike — instead of
  reaching Npgsql's generator, which ignored them.
- **Changed:** an exclusion constraint, temporal constraint or temporal foreign key declared on an
  entity type mapped to no table — typically the abstract base of a TPC hierarchy — fails at
  `migrations add` instead of producing nothing.

## 5.0.3

- **Changed:** the `Npgsql.EntityFrameworkCore.PostgreSQL` dependency is now `[10.0.0, 11.0.0)`. This
  differ extends Npgsql's own diff and generator internals, which carry no cross-major compatibility
  promise. Nothing changes if you are on Npgsql 10: NuGet resolves the lowest version in a range.
- **New:** the public API is fully documented, including the differ and the custom SQL generator.
- **Tests:** the consumer smoke test scaffolds a real migration from this package as installed from a
  NuGet feed, which is what verifies that the packaged `.targets` still registers the Npgsql differ.

## 5.0.2

- **Fixed:** temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys are rendered
  at design time and **no longer need `UseNpgsqlComplexIndexes()`**. Without that wiring the stock
  Npgsql generator emitted a plain `UNIQUE (key, period)` — valid DDL that applied cleanly and
  silently dropped the entire non-overlap guarantee. Migrations scaffolded before this change keep
  working.
- **Fixed:** exclusion-constraint identity now includes the filter. Two `EXCLUDE` constraints over
  the same columns with different predicates coexist instead of the second silently replacing the
  first — the filtered-overlap case the API exists for.
- **Fixed:** duplicate exclusion-constraint names are rejected. Because every `ADD CONSTRAINT` is
  preceded by `DROP CONSTRAINT IF EXISTS`, a reused name did not fail — the migration applied and
  the second constraint quietly replaced the first.
- **Fixed:** the design-time differ is scoped to the Npgsql provider, so a solution that also
  references another satellite can no longer hand a PostgreSQL model to the wrong differ.
- **Fixed:** `Npgsql:IndexSortOrder`/`IndexNullSortOrder` are no longer forwarded, and setting
  either now throws with a pointer to `DbOrder`. They duplicated what `DbOrder.Asc`/`Desc`/
  `NullsFirst`/`NullsLast` already express per column, so an index could carry two conflicting
  descriptions of its sort order with the annotation's half silently losing.
- **Fixed:** validation no longer inspects index operations this package did not create, so a plain
  native `HasIndex` carrying provider options is left alone.

## 5.0.1

- **Changed:** exclusion-constraint `ADD CONSTRAINT` DDL is preceded by `DROP CONSTRAINT IF EXISTS`,
  so adopting a pre-existing hand-written constraint of the same name applies cleanly instead of
  failing with `42P07`.
- **Fixed:** renaming a table no longer drops and recreates the exclusion and temporal constraints
  it carries.
- **Changed:** a name-only change to an exclusion constraint, temporal constraint, or temporal
  foreign key emits `ALTER TABLE … RENAME CONSTRAINT` instead of rebuilding. Dependent temporal
  foreign keys survive untouched.

## 5.0.0

- **New:** `HasExclusionConstraint` — `EXCLUDE` constraints with `WHERE` predicates.
- **New:** typed LINQ expression indexes — `HasExpressionIndex(x => x.Email.ToLower())`.
- **New:** JSON member indexes for `ToJson()` complex properties.
- **New:** `NULLS FIRST`/`NULLS LAST` via `DbOrder.NullsFirst/NullsLast` and
  `ExpressionIndexBuilder.NullsFirst()/NullsLast()`.
- **Fixed:** descending parts of expression indexes render `DESC`.
- **Changed:** `IncludeProperties(...)` entries resolve as property paths (complex members included)
  with verbatim column-name fallback.
- **Changed:** indexes requiring the custom generator carry a loud sentinel column, so a missing
  `UseNpgsqlComplexIndexes()` fails at apply time with an actionable error instead of applying a
  silently wrong index.
