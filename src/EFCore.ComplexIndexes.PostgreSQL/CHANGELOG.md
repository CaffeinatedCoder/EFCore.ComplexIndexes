# EFCore.ComplexIndexes.PostgreSQL — changelog

The changes that affect the PostgreSQL satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages, which share one version number and release together.

## [5.5.0] — 2026-10-20

The last 5.x minor with a change of its own: the line refuses EF Core 11, whose successor is `CodoMetis.EFCore.SchemaKit.PostgreSQL`.

### Changed answers

- Expression indexes: typed expression indexes render `Substring`, `Trim`, `TrimStart` and `TrimEnd` as EF Core's queries do, so those queries can use them. `Trim()` also trims tabs and line breaks, where it trimmed spaces only ([#72](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/72), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#substring-and-trim-render-as-ef-cores-queries)).
- Expression indexes: a typed expression index or filter wraps a concatenated operand that can be null in `COALESCE(…, '')`, as EF Core's queries do. `x.Code + x.Suffix` was NULL for every row without a suffix, so a unique index skipped those rows ([#94](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/94), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#concatenation-with-a-nullable-operand)).
- Filters: a typed filter keeps C#'s answer where an operand is null. `x => x.Kind != "federated"` on a `string?` covers the rows whose `Kind` is null, where it left them out, and a captured null compares with `IS NULL`, where it matched no row ([#68](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/68), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#typed-filters-keep-cs-answer-for-null-operands)).
- Filters: a brace in a string literal of a typed filter is compared as written. `x => x.Code != "{draft}"` renders `'{draft}'`, where it rendered `'{{draft}}'` and kept the rows the filter excludes ([#70](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/70), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#typed-filters-keep-braces-in-string-literals)).

### Breaking

- Registration: `UseNpgsqlComplexIndexes()`, `AddNpgsqlComplexIndexes()` and the design-time registration `dotnet ef` runs throw `InvalidOperationException` on an EF Core major other than 10, naming `CodoMetis.EFCore.SchemaKit` for EF Core 11. EF Core 10 on .NET 11 keeps working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#ef-core-11-is-refused-at-registration)).

### Fixed

- Indexes: a complex index, exclusion constraint or temporal constraint named like a table, view or sequence of its schema, the sequence of an identity or serial column included, fails at `migrations add`, where it failed at apply with 42P07 ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- Expression indexes: a typed expression index or filter refuses a member of a scalar type, such as `CreatedAt.Year`, at the declaration, where it failed at `migrations add`. `.Value` and `.HasValue` on a nullable translate ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- Filters: a typed filter compares a `char` with a char literal, where it failed at apply on the char's code, and `x.RevokedAt.HasValue` renders `IS NOT NULL`, where it failed at `migrations add` ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).
- Constraints: a migration that drops both tables of a temporal foreign key applies, `Down()` included, where it failed with 2BP01 when the principal's table sorted first ([Temporal foreign keys](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-foreign-keys-period--requires-postgresql-18)).
- Constraints: an exclusion constraint filter on a `ToJson()` number, boolean, uuid or enum member casts it as EF Core's queries do and applies, where it failed with 42883 or 42804 ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- Constraints: an exclusion constraint with a scalar element under `gist` gets `btree_gist` whatever the element's operator and however the method is spelled, so `<>` and `UseMethod("GiST")` apply, where they failed with 42704 when the extension was missing ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- Docs: the temporal foreign key samples pass both type arguments to `HasTemporalForeignKey`, and compile ([Temporal foreign keys](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-foreign-keys-period--requires-postgresql-18)).
- Docs: the PostgreSQL index guide says to pass a fractional storage parameter as a string, since Npgsql's generator formats numbers with the current culture ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).

## [5.4.0] — 2026-09-22

Indexes on `ToJson()` members are written the way Npgsql's queries read them, so the first `migrations add` re-creates the affected indexes: [Upgrading to 5.4.0](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-540).

### Breaking

- Indexes: a non-unique index that starts with a `DateTime`, `DateTimeOffset`, `DateOnly` or `TimeOnly` JSON member is refused at `migrations add`, because no query can use it. A unique one is still allowed ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).

### Fixed

- Indexes: indexes on `ToJson()` members render as Npgsql's queries read the member, `#>>` for a nested one and a cast for a typed one, so those queries use them. They applied and served no query. Exclusion constraint filters keep their rendering ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).
- Filters: a filter over a typed JSON member, such as `x => x.Profile.Rank > 5`, applies, where it compared text with an integer and failed at apply ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).
- Constraints: every name this package introduces is checked at `migrations add` against the objects it shares a namespace with: constraints per table, indexes per schema. A clash failed at apply with 42710 or 42P07, or silently dropped an exclusion constraint ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).

## [5.2.0] — 2026-09-05

### Breaking

- Indexes: an index, exclusion constraint, temporal constraint or temporal foreign key name longer than 63 bytes is refused at `migrations add`, where PostgreSQL truncated it silently. Give the declaration a name; a name-only change renames in place ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-520)).

### Added

- **`GetExclusionConstraints()` and `FindExclusionConstraint(name)`**, with `ExclusionPartDefinition` and `NpgsqlExclusionAnnotations.Constraints` now public ([Reading constraints back](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#reading-constraints-back)).
- **`{Property.Path}` placeholders in filters** of complex, composite and expression indexes and of exclusion constraints, resolved at `migrations add`, `ToJson()` members included ([Expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- **Property paths through a value converter** in typed expression indexes and exclusion elements: `x => x.Email.Value` resolves to the converted column ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- **`AddExclusionConstraintFilter(predicate, where)`** on `IMutableEntityType`, which adds a predicate to every selected exclusion constraint's filter. Repeating a call is safe ([Amending constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#amending-constraints)).
- **Typed filter predicates**: every `filter:` string has a lambda form, and the builders take `HasFilter(x => …)`. Enums and values with no portable SQL spelling are refused at the declaration ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).

### Changed

- Expression indexes: a typed expression with a `DateTime`, `Guid` or enum constant throws at the declaration, where it rendered the constant as bare text ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).

## [5.1.0] — 2026-09-05

### Breaking

- Constraints: an exclusion constraint, temporal constraint or temporal foreign key declared on an entity type mapped to no table, typically the abstract base of a TPC hierarchy, fails at `migrations add`, where it produced nothing ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-510)).

### Added

- **`UseNpgsqlComplexIndexes()` registers the PostgreSQL differ at runtime** too, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes and exclusion and temporal constraints ([Runtime wiring](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#ensurecreated-generatecreatescript-and-the-pending-changes-check)).
- **Whole-document JSON indexes**: `HasComplexIndex` on a `ToJson()` complex property or a complex collection indexes its `jsonb` column, for example with `UseGin()` ([Indexing the whole document](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#indexing-the-whole-document)).
- **`HasStorageParameter(name, value)`** on complex and expression indexes, for `WITH (fillfactor=70)` and the like ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).
- **`UseCollation(params string[])`** on complex and expression indexes: per-column index collations, by position, independent of the column's own collation ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).

### Fixed

- Indexes: a SQL Server option on a complex index, property-level or entity-level, is refused at `migrations add`, where Npgsql's generator ignored it.

## [5.0.3] — 2026-08-15

### Added

- **XML documentation for the whole public API**, the differ and the SQL generator included.

### Changed

- Packaging: the `Npgsql.EntityFrameworkCore.PostgreSQL` dependency has an upper bound, `[10.0.0, 11.0.0)`. Restore still picks 10.0.0.

## [5.0.2] — 2026-08-15

### Fixed

- Registration: the design-time differ is scoped to the Npgsql provider, so a solution that also references the SQL Server satellite no longer hands a PostgreSQL model to the wrong differ.
- Indexes: `Npgsql:IndexSortOrder` and `Npgsql:IndexNullSortOrder` are no longer forwarded onto complex indexes, and setting either throws, pointing to `DbOrder`.
- Indexes: validation leaves index operations this package did not create alone, so a native `HasIndex` with provider options no longer fails `migrations add`.
- Constraints: temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys render at design time, without `UseNpgsqlComplexIndexes()`. Without it they scaffolded a plain `UNIQUE (key, period)` ([Temporal constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-unique-constraints-without-overlaps--requires-postgresql-18)).
- Constraints: two exclusion constraints over the same columns with different filters coexist, both named. The second replaced the first ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- Constraints: a reused exclusion constraint name is refused. The migration applied, and the second constraint replaced the first.

## [5.0.1] — 2026-08-10

### Changed

- Constraints: an exclusion constraint's `ADD CONSTRAINT` is preceded by `DROP CONSTRAINT IF EXISTS`, so adopting a hand-written constraint of the same name applies instead of failing with 42P07.
- Constraints: a name-only change to an exclusion constraint, temporal constraint or temporal foreign key emits `RENAME CONSTRAINT` instead of a rebuild, and dependent temporal foreign keys survive it.

### Fixed

- Constraints: renaming a table no longer drops and re-creates its exclusion and temporal constraints.

## [5.0.0] — 2026-08-10

### Added

- **`HasExclusionConstraint`**: `EXCLUDE` constraints with `WHERE` predicates ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- **`HasExpressionIndex(x => x.Email.ToLower())`**, typed LINQ expression indexes ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- **JSON member indexes** for members of `ToJson()` complex properties ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).
- **`DbOrder.NullsFirst` and `DbOrder.NullsLast`**, and `NullsFirst()` and `NullsLast()` on `ExpressionIndexBuilder` ([Per-column null ordering](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#per-column-null-ordering)).

### Changed

- Indexes: `IncludeProperties(…)` entries resolve as property paths, complex members included, with the column name as written as the fallback.
- Indexes: an index that needs this package's SQL generator carries a sentinel column, so a missing `UseNpgsqlComplexIndexes()` fails at apply with a message that says so.

### Fixed

- Expression indexes: descending parts render `DESC`, declared with `ExpressionIndexBuilder.Descending()`.
