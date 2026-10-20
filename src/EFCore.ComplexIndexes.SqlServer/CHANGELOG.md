# EFCore.ComplexIndexes.SqlServer — changelog

Changes to the SQL Server satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

## 5.5.0

### Changed answers

- [#88](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/88): on SQL Server, a unique complex index over nullable columns gets the `WHERE [c] IS NOT NULL` filter EF Core's convention gives its own unique indexes. SQL Server treats NULLs as equal in a unique index, so `HasComplexIndex(x => x.Registration!.Vin, isUnique: true)` on an optional complex property admitted one row without a registration and refused the second with a duplicate key. The filter covers the nullable key columns only, and is not added to a clustered index or one declared with a filter. **Upgrading:** the first `migrations add` drops and re-creates each such index with the filter. On every provider the model's `CustomIndex:RenderingVersion` becomes 3, so that migration also rewrites the snapshot; no other index changes.

### Changed

- **Changed:** `UseSqlServerComplexIndexes()`, `AddSqlServerComplexIndexes()` and
  the design-time registration throw `InvalidOperationException` when the EF Core
  they run on is not version 10, naming `CodoMetis.EFCore.SchemaKit` as the package for EF Core 11.
  The `[10.0.0, 11.0.0)` dependency ranges did not keep EF Core 11 out: an EF Core 11 prerelease
  sorts below 11.0.0 and falls inside them, and a direct reference to a release overrides them with
  only warning NU1608. The check reads EF Core's version, not the runtime's, so
  EF Core 10 on .NET 11 keeps working.
- **Fixed:** changing the type, length or nullability of a column a complex index reads no longer
  fails at apply with Msg 5074: the index is dropped before the `ALTER COLUMN` and created after it.

## 5.4.1

- **Changed:** the differ declares SQL Server's index-name scope, per table, now that the core
  checks names across the whole database by default. Two tables may still carry indexes of the same
  name, complex or native; a name reused on one table is rejected as before.

## 5.2.0

- **New:** filters resolve `{Property.Path}` placeholders to the mapped column, bracket-quoted, at
  `migrations add` — `filter: "{DeletedAt} IS NULL"` becomes `[deleted_at] IS NULL` in the migration.
- **Changed:** an index name longer than 128 characters is rejected at `migrations add` instead of
  failing when the migration is applied.

## 5.1.0

- **New:** `UseSqlServerComplexIndexes()` / `AddSqlServerComplexIndexes()` — optional runtime
  registration of the differ, so `EnsureCreated()` and `GenerateCreateScript()` include the complex
  indexes and `Migrate()`'s pending-model-changes check sees one that was never scaffolded. Migrations
  still need no wiring.
- **Fixed:** a PostgreSQL option (`UseGin`, `HasOperators`, …) on a *property-level* complex index is
  rejected at `migrations add` like an entity-level one, instead of being dropped by the forwarding
  whitelist — the index scaffolded as a plain B-tree without a word.

## 5.0.3

- **Changed:** the `Microsoft.EntityFrameworkCore.SqlServer` dependency is now `[10.0.0, 11.0.0)`.
  This differ extends EF internals that carry no cross-major compatibility promise. Nothing changes
  if you are on EF Core 10: NuGet resolves the lowest version in a range.
- **New:** the public API is fully documented.

## 5.0.2

- **Fixed:** clustered-index combinations SQL Server rejects are caught at `migrations add`
  instead of at apply time — clustered + `INCLUDE`, clustered + filter, and a second clustered
  index on a table, which by default is any clustered complex index, since the primary key
  holds the clustered slot unless declared otherwise.
- **New:** `UseDataCompression(DataCompressionType)`. The annotation was already forwarded but had
  no way to set it.
- **Fixed:** the data-compression value survives the model-snapshot round trip. Stored as JSON the
  enum flattened to a number, which SQL Server's generator reads back as null through
  `DataCompressionType?`, dropping the option from the generated DDL.
- **Fixed:** the design-time differ is scoped to the SQL Server provider. Previously, in a solution
  that also referenced the PostgreSQL satellite, NuGet's restore order decided which differ ran — and
  the wrong one silently dropped every `SqlServer:*` index option.
- **Fixed:** validation no longer inspects index operations this package did not create, so a plain
  native `HasIndex` carrying provider options is left alone.
- **Fixed:** duplicate index names are rejected at the declaration or during `migrations add`
  instead of producing a migration that fails when applied.

## 5.0.0

- **New:** the package — clustered, covering (`INCLUDE`), online-built, fill-factor, and
  sort-in-tempdb options on complex-property indexes, plus clear errors for expression parts and
  `NULLS FIRST`/`LAST`.
