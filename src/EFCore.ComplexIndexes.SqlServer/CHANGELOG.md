# EFCore.ComplexIndexes.SqlServer — changelog

The changes that affect the SQL Server satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages, which share one version number and release together.

## [5.5.0] — 2026-10-20

The last 5.x minor with a change of its own: the line refuses EF Core 11, whose successor is `CodoMetis.EFCore.SchemaKit.SqlServer`.

### Changed answers

- Indexes: a unique complex index over nullable columns gets EF Core's `IS NOT NULL` filter, so rows without a value no longer collide. It admitted one such row and refused the second ([#88](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/88), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#unique-indexes-over-nullable-columns-on-sql-server)).

### Breaking

- Registration: `UseSqlServerComplexIndexes()`, `AddSqlServerComplexIndexes()` and the design-time registration `dotnet ef` runs throw `InvalidOperationException` on an EF Core major other than 10, naming `CodoMetis.EFCore.SchemaKit` for EF Core 11. EF Core 10 on .NET 11 keeps working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#ef-core-11-is-refused-at-registration)).
- Indexes: on a database with a case-sensitive collation, complex index names that differ only in case fail at `migrations add`; rename one ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#rename-complex-index-names-that-differ-only-in-case)).

### Fixed

- Indexes: a clustered complex index added in a later migration beside an existing one, and a complex index named like its table's primary key or a unique constraint, fail at `migrations add`, where they failed at apply ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#deliberate-rejections)).
- Indexes: complex index names that differ only in case, from each other or from a native index, fail at `migrations add`, where they failed at apply because the default collation compares them without regard to case ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- Migrations: changing the type, length or nullability of a column a complex index reads applies, where it failed with Msg 5074. The index is dropped before the `ALTER COLUMN` and created after it ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)).

## [5.4.1] — 2026-09-22

### Changed

- Indexes: the differ keeps SQL Server's index names per table, now that the core checks them across the whole database by default. Two tables may still carry indexes of the same name, complex or native.

## [5.2.0] — 2026-09-05

### Breaking

- Indexes: an index name longer than 128 characters is refused at `migrations add`, where the migration failed at apply. Give the declaration a name; a name-only change renames in place ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-520)).

### Added

- **`{Property.Path}` placeholders in filters**, resolved to the mapped column in brackets at `migrations add`: `filter: "{DeletedAt} IS NULL"` becomes `[deleted_at] IS NULL` ([Filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.1.0] — 2026-09-05

### Added

- **`UseSqlServerComplexIndexes()` and `AddSqlServerComplexIndexes()`**, which register the differ at runtime, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes. Migrations still need no wiring ([Runtime wiring](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#ensurecreated-generatecreatescript-and-the-pending-changes-check)).

### Fixed

- Indexes: a PostgreSQL option such as `UseGin()` on a property-level complex index is refused at `migrations add`, where it was dropped and the index scaffolded as a plain B-tree.

## [5.0.3] — 2026-08-15

### Added

- **XML documentation for the whole public API.**

### Changed

- Packaging: the `Microsoft.EntityFrameworkCore.SqlServer` dependency has an upper bound, `[10.0.0, 11.0.0)`. Restore still picks 10.0.0.

## [5.0.2] — 2026-08-15

### Added

- **`UseDataCompression(DataCompressionType)`**. The option was forwarded but nothing could set it ([Index options](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#index-options)).

### Fixed

- Registration: the design-time differ is scoped to the SQL Server provider. Restore order decided which differ ran, and the wrong one dropped every `SqlServer:` index option.
- Indexes: the clustered combinations SQL Server refuses fail at `migrations add`: with `INCLUDE`, with a filter, a second one on a table, and one where the primary key holds the slot ([Deliberate rejections](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#deliberate-rejections)).
- Indexes: the data compression option survives the model snapshot, which dropped it from the generated DDL.
- Indexes: validation leaves index operations this package did not create alone, so a native `HasIndex` with provider options no longer fails `migrations add`.
- Indexes: a reused index name is refused at the declaration or at `migrations add`, where the migration failed at apply.

## [5.0.0] — 2026-08-10

### Added

- **The package**: clustered, covering (`INCLUDE`), online, fill-factor and sort-in-tempdb options on complex indexes, and clear errors for expression parts and `NULLS FIRST` or `NULLS LAST` ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)).
