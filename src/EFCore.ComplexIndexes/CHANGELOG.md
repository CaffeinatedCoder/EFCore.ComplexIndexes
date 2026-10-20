# EFCore.ComplexIndexes — changelog

The changes that affect the core package, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages, which share one version number and release together.

## [5.5.0] — 2026-10-20

The last 5.x minor with a change of its own: the line refuses EF Core 11, whose successor is `CodoMetis.EFCore.SchemaKit`.

### Changed answers

- Registration: on SQL Server and PostgreSQL, the core differ refuses a complex index carrying that provider's index options and names `UseSqlServerComplexIndexes()` or `UseNpgsqlComplexIndexes()`. With `UseComplexIndexes()`, `EnsureCreated()` created the index without its property-level options ([#92](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/92), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#the-core-differ-refuses-sql-server-and-postgresql-index-options)).
- Indexes: a complex index declared on a TPC type is created on the table of every type derived from it, as EF Core's own indexes are. On a concrete base it was created on the base's table only, so a unique index admitted duplicates among the derived types' rows ([#93](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/93), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Migrations: on SQLite without `UseComplexIndexes()`, `migrations add` refuses a migration that rebuilds a table with a complex index, naming the indexes and the ways out. It scaffolded a migration that applied cleanly and dropped every complex index on the table ([#86](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/86), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-refuses-a-table-rebuild-that-would-drop-complex-indexes)).
- Migrations: on SQLite with `UseComplexIndexes()` after `UseSqlite()`, a migration that rebuilds a table drops its complex indexes before the rebuild and creates them after it. Applied without that registration, the migration fails before the rebuild ([#95](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/95), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild)).

### Breaking

- Registration: `UseComplexIndexes()`, `AddComplexIndexes()` and the design-time registration `dotnet ef` runs throw `InvalidOperationException` on an EF Core major other than 10, naming `CodoMetis.EFCore.SchemaKit` for EF Core 11. EF Core 10 on .NET 11 keeps working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#ef-core-11-is-refused-at-registration)).
- Indexes: on a provider without a satellite whose index names keep their case, complex index names that differ only in case fail at `migrations add`; rename one ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#rename-complex-index-names-that-differ-only-in-case)).
- Indexes: a complex index on a TPC type with derived types fails at `migrations add` when it reads a member of a complex property, which EF Core 10 maps to no column in a derived table; map the member as a regular property ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Indexes: on SQLite, an explicit name on a complex index of a TPC type with derived types fails at `migrations add`, since it would repeat within one schema; leave the name out ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).

### Added

- **`CustomMigrationsModelDiffer.RequiresIndexRecreation`**, a protected hook through which a provider's differ drops an unchanged complex index before a base operation that cannot run while it exists, and creates it after.
- **`CustomMigrationsModelDiffer.ImplicitFilter`**, a protected hook through which a provider's differ gives an index declared without a filter the one its EF Core convention would give it.
- **`CustomMigrationsModelDiffer.IndexNameComparer`**, a protected hook through which a provider's differ says how it compares index names: without regard to case by default and on SQL Server, ordinally on PostgreSQL.
- **`CustomMigrationsModelDiffer(…, ICurrentDbContext)`**, a constructor taking the context the migration is diffed for, and **`ComplexIndexAnnotations.AfterTableRebuild`**, the mark on a create that runs after SQLite's table rebuild ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild)).

### Changed

- Indexes: a complex index declared on the abstract base of a TPC hierarchy is created on every concrete table, as EF Core's own indexes are, where it failed at `migrations add` ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).

### Fixed

- Indexes: on SQLite, complex index names that differ only in case, from each other or from a native index, fail at `migrations add`, where they failed at apply because SQLite compares them without regard to case ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.4.1] — 2026-09-22

### Breaking

- Indexes: on a provider without a satellite that keeps index names per table, such as MySQL, a complex index name reused across tables fails at `migrations add`; rename one ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-541)).

### Added

- **`IndexNameScope`** and the protected `CustomMigrationsModelDiffer.IndexNameScope`, through which a provider's differ says where its index names must be unique: per table, per schema or, the default, per database.

### Fixed

- Indexes: on SQLite, a complex index name is checked across the whole database at `migrations add`, where a name reused on another table failed at apply ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.4.0] — 2026-09-22

### Added

- **`ComplexIndexAnnotations.RenderingVersion`**, which every declaration writes onto the model, so a change in how parts render reaches databases built by earlier versions ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-540)).

### Changed

- Indexes: the default names of expression parts are built from the property path, not the rendered SQL, so a rendering change never renames an index. Every name 5.3.0 produced stays the same.

## [5.3.0] — 2026-09-05

### Fixed

- Indexes: a property path through a value converter, such as `x => x.Email.Value`, resolves against a model snapshot. Every diff after the first `migrations add` threw `Could not resolve property path 'Email.Value'`, `has-pending-model-changes` and `Migrate()` included ([Converter paths](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.2.0] — 2026-09-05

### Breaking

- Indexes: a complex index name longer than the provider's identifier limit is refused at `migrations add`, default names included. A provider without a limit is left alone ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-520)).

### Added

- **`GetComplexIndexes()` and `FindComplexIndex(name)`**, which read back every declaration this package holds, on the mutable model inside `OnModelCreating` too ([Reading declarations back](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#reading-declarations-back)).
- **`{Property.Path}` placeholders in filters**, resolved to the mapped column at `migrations add` and quoted through the new protected `QuoteIdentifier` seam. A brace pair that holds no dotted identifier path, such as `'{urgent}'`, stays literal ([Filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- **Property paths through a value converter**: `x => x.Email.Value` resolves to the converted column when the member's type is the converter's provider type ([Converter paths](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- **`AddComplexIndexFilter(predicate, where)` and `AddComplexIndex(definition)`** on `IMutableEntityType`, which amend declarations from a shared convention. Repeating a call is safe ([Amending declarations](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#amending-declarations)).

## [5.1.0] — 2026-09-05

### Breaking

- Indexes: a complex index declared on an entity type mapped to no table, typically the abstract base of a TPC hierarchy, fails at `migrations add`, where it produced nothing. View-mapped and query-mapped types are still skipped ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-510)).

### Added

- **`UseComplexIndexes()` and `AddComplexIndexes()`**, which register the differ at runtime for providers without a satellite, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes ([Runtime wiring](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#ensurecreated-generatecreatescript-and-the-pending-changes-check)).
- **`HasComplexIndex` on the non-generic `ComplexTypePropertyBuilder`**, for a property configured by name or by type.

### Fixed

- Indexes: a complex index named like a native `HasIndex` on the same table is refused at `migrations add`, where the migration failed at apply with 42P07.
- Migrations: `HasDifferences` reports changes to complex indexes, so `has-pending-model-changes`, `Migrate()`'s pending-changes warning and `migrations remove` see them. A CI gate on `has-pending-model-changes` may now fail; that is the gate working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-510)).

## [5.0.3] — 2026-08-15

### Added

- **XML documentation for the whole public API**, so IntelliSense shows the fluent API, the annotation keys, `CompositeIndexDefinition` and `IndexPartDefinition`, where the shipped `.xml` had 64 gaps.

### Changed

- Packaging: the `Microsoft.EntityFrameworkCore.Abstractions` dependency has an upper bound, `[10.0.0, 11.0.0)`. Restore still picks 10.0.0.

## [5.0.2] — 2026-08-15

### Fixed

- Registration: the core package's design-time registration steps back when a satellite is installed. Restore order decided which differ ran, and this one drops every provider-specific option.
- Indexes: a reused index name is refused at the declaration or at `migrations add`, where the migration failed at apply.
- Indexes: a selector that reads a captured variable or a static member, such as `x => captured.Name`, throws at the declaration and names the selector. It failed later with an opaque resolution error.
- Indexes: `DbOrder.Asc` marks a column ascending, and combining it with `DbOrder.Desc`, or `NullsFirst` with `NullsLast`, throws.
- Indexes: `CompositeIndexDefinition` compares array-valued provider annotations, such as operator classes and `INCLUDE` lists, by content.
- Indexes: provider validation runs through a scoped extension point, so a satellite can no longer reject index operations this package did not create.

## [5.0.1] — 2026-08-10

No change in this package.

## [5.0.0] — 2026-08-10

### Breaking

- Indexes: an indexed property that resolves to no column throws at `migrations add` instead of dropping the index.

### Added

- **Entity-level `HasComplexIndex(x => x.Complex.Prop, …)`** for single-column indexes, so a column can carry several filtered indexes.
- **`DbOrder.Asc` and `DbOrder.Desc`**, per-column sort direction on every relational provider ([Per-column sort direction](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#per-column-sort-direction)).

### Changed

- Indexes: provider annotations reach index operations through a whitelist, so column facets such as `Relational:ColumnName` no longer leak into migrations as drop-and-create churn.
- Indexes: two indexes over the same columns coexist when their filters differ, both named. Declaring one again with the same filter updates it in place.
- Migrations: a name-only index change emits `RenameIndexOperation` on providers that can rename an index alone, and renaming a table no longer re-creates its complex indexes.

### Fixed

- Indexes: integral provider annotations such as fill factor survive the snapshot as `int`. They became `double`, and the generator dropped them.
- Migrations: the drops of complex indexes run before the base operations, so moving an index between `HasIndex` and a complex declaration under one name applies.
