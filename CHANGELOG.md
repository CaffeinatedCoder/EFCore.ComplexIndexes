# Changelog

All notable changes to EFCore.ComplexIndexes and its satellites.

The three packages share one version number (the `Version` property in `Directory.Build.props`) and
are released together, so a version appears here even when a package saw no change in it. Each
package also has its own `CHANGELOG.md` with the entries that affect it:
[core](src/EFCore.ComplexIndexes/CHANGELOG.md),
[PostgreSQL](src/EFCore.ComplexIndexes.PostgreSQL/CHANGELOG.md),
[SQL Server](src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md). Versions follow
[Semantic Versioning](https://semver.org/). Entries are newest first.

A version's section is the GitHub release's notes. Each line is one change: what happens now, what
to do about it, and a link to the guide with the detail. What the first `migrations add` after an
upgrade does is in [docs/upgrading.md](docs/upgrading.md), and
[CONTRIBUTING.md](CONTRIBUTING.md#changelog) has the rules.

## [5.6.0] — 2026-10-23

Migrations keep the indexes and constraints a column change used to drop or rebuild: [Changed answers in 5.6.0](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#changed-answers-in-560).

### Changed answers

- Migrations: on PostgreSQL, changing a generated column's expression, its storage or a `tsvector` column's configuration drops the complex indexes, exclusion constraints, temporal constraints and temporal foreign keys on the column before the change and adds them after it. The migration dropped them with the column and never added them back ([#184](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/184), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#a-changed-generated-column-keeps-what-reads-it)).
- Migrations: moving a table into the default schema keeps the complex indexes and constraints on it. They were dropped and created again, and on PostgreSQL, where Npgsql leaves such a table in its schema, the migration failed (42P01) ([#185](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/185), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#a-move-into-the-default-schema-keeps-what-is-on-the-table)).
- Migrations: on SQL Server, changing only a column's default value, default SQL or comment keeps the complex indexes on it. Each was dropped and created again around the change, which issues no `ALTER COLUMN` ([#186](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/186), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#a-default-or-comment-change-keeps-the-index-on-sql-server)).
- Migrations: renaming a column keeps the complex indexes, exclusion constraints and temporal constraints that read it, and renames a default name that includes the column. They were dropped and created again around the rename. SQL Server still re-creates an index whose filter reads the column ([#182](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/182), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#a-column-rename-keeps-what-reads-the-column)).

### Fixed

- Constraints: on PostgreSQL, a migration that drops a temporal foreign key's dependent table and drops or changes the principal's temporal constraint applies, where it failed at apply with 2BP01 ([#187](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/187), [Temporal constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#change-remove-or-rename-a-constraint)).
- Constraints: on PostgreSQL, changing a temporal foreign key's key column type on both tables applies, where it failed at apply with 42804 when EF Core altered the dependent table first ([#188](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/188), [Temporal constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#change-remove-or-rename-a-constraint)).
- Indexes: on SQL Server, a complex index whose filter or INCLUDE entry names a column in another case, `[note]` for `Note`, is dropped before an `ALTER COLUMN` or rename of that column and created after it, where the migration failed at apply, since SQL Server rejects either while the index exists (Msg 5074 for `ALTER COLUMN`) ([#189](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/189)).

## [5.5.0] — 2026-10-20

The last 5.x minor with a change of its own: the line refuses EF Core 11, whose successor is `CodoMetis.EFCore.SchemaKit`. Some fixes change what an existing model scaffolds or applies: [Changed answers in 5.5.0](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#changed-answers-in-550).

### Changed answers

- Registration: on SQL Server and PostgreSQL, the core differ refuses a complex index carrying that provider's index options and names `UseSqlServerComplexIndexes()` or `UseNpgsqlComplexIndexes()`. With `UseComplexIndexes()`, `EnsureCreated()` created the index without its property-level options ([#92](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/92), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#the-core-differ-refuses-sql-server-and-postgresql-index-options)).
- Registration: on PostgreSQL, the core differ also refuses exclusion constraints, temporal constraints and temporal foreign keys, and on SQL Server a unique complex index over nullable columns. With `UseComplexIndexes()`, `EnsureCreated()` created the tables without those constraints and the index without its `IS NOT NULL` filter ([#150](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/150), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#the-core-differ-refuses-the-satellites-constraints-and-null-filter)).
- Indexes: on SQL Server, a unique complex index over nullable columns gets EF Core's `IS NOT NULL` filter, so rows without a value no longer collide. It admitted one such row and refused the second ([#88](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/88), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#unique-indexes-over-nullable-columns-on-sql-server)).
- Indexes: a complex index declared on a TPC type is created on the table of every type derived from it, as EF Core's own indexes are. On a concrete base it was created on the base's table only, so a unique index admitted duplicates among the derived types' rows ([#93](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/93), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Indexes: a complex index with `DbOrder.NullsFirst`/`NullsLast` ordering or an expression part fails at `migrations add` on SQLite and other providers without a satellite, and under the core differ on SQL Server and PostgreSQL. Where SQLite accepts double-quoted strings, it applied with a constant column in place of its ordering or expression ([#134](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/134), [#172](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/172), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#nulls-ordering-and-expression-parts-fail-on-sqlite)).
- Indexes: on PostgreSQL and SQL Server, an `IncludeProperties(…)` entry that names neither a property nor a column of the table fails at `migrations add`. When its property was removed, the migration dropped only the column: PostgreSQL dropped the index with it and applied cleanly, SQL Server failed at apply ([#142](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/142), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#include-entries-must-name-a-column)).
- Expression indexes: on PostgreSQL, typed expression indexes and typed filters render `Substring`, `Trim`, `TrimStart` and `TrimEnd` as EF Core's queries do, so those queries can use the indexes. `Trim()` also trims tabs and line breaks, where it trimmed spaces only, so a typed filter can cover other rows: `x.Code.Trim() != ""` leaves out a tabs-only value ([#72](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/72), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#substring-and-trim-render-as-ef-cores-queries)).
- Expression indexes: on PostgreSQL, a typed expression index or filter wraps a concatenated operand that can be null in `COALESCE(…, '')`, as EF Core's queries do. `x.Code + x.Suffix` was NULL for every row without a suffix, so a unique index skipped those rows ([#94](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/94), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#concatenation-with-a-nullable-operand)).
- Filters: on PostgreSQL, a typed filter keeps C#'s answer where an operand is null. `x => x.Kind != "federated"` on a `string?` covers the rows whose `Kind` is null, where it left them out, and a captured null compares with `IS NULL`, where it matched no row ([#68](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/68), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#typed-filters-keep-cs-answer-for-null-operands)).
- Filters: on PostgreSQL, a brace in a string literal of a typed filter is compared as written. `x => x.Code != "{draft}"` renders `'{draft}'`, where it rendered `'{{draft}}'` and kept the rows the filter excludes ([#70](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/70), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#typed-filters-keep-braces-in-string-literals)).
- Constraints: on PostgreSQL, an exclusion constraint, temporal constraint or temporal foreign key declared on a TPC type is added to the table of every type derived from it. On a concrete base it was added to the base's table only, and the derived tables accepted the overlapping rows it refuses ([#93](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/93), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Migrations: on SQLite without `UseComplexIndexes()`, `migrations add` refuses a migration that rebuilds a table with a complex index, naming the indexes and the ways out. It scaffolded a migration that applied cleanly and dropped every complex index on the table ([#86](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/86), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-refuses-a-table-rebuild-that-would-drop-complex-indexes)).
- Migrations: on SQLite with `UseComplexIndexes()` after `UseSqlite()`, a migration that rebuilds a table drops its complex indexes before the rebuild and creates them after it. Applied without that registration, the migration fails before the rebuild ([#95](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/95), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild)).

### Breaking

- Registration: every registration entry point, the design-time one `dotnet ef` runs included, throws `InvalidOperationException` on an EF Core major other than 10, naming `CodoMetis.EFCore.SchemaKit` for EF Core 11. EF Core 10 on .NET 11 keeps working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#ef-core-11-is-refused-at-registration)).
- Indexes: on a SQL Server database with a case-sensitive collation, or a provider without a satellite whose index names keep their case, complex index names that differ only in case fail at `migrations add`; rename one ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#rename-complex-index-names-that-differ-only-in-case)).
- Indexes: a complex index, exclusion constraint or temporal constraint on a TPC type with derived types fails at `migrations add` when it reads a member of a complex property, which EF Core 10 maps to no column in a derived table; map the member as a regular property ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Constraints: on PostgreSQL, a temporal foreign key to a TPC type with derived types fails at `migrations add`, since its rows live in several tables; reference a type with no derived types ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).

### Added

- **`CustomMigrationsModelDiffer.RequiresIndexRecreation`**, a protected hook through which a provider's differ drops an unchanged complex index before a base operation that cannot run while it exists, and creates it after.
- **`CustomMigrationsModelDiffer.ImplicitFilter`**, a protected hook through which a provider's differ gives an index declared without a filter the one its EF Core convention would give it.
- **`CustomMigrationsModelDiffer.IndexNameComparer`**, a protected hook through which a provider's differ says how it compares index names: without regard to case by default and on SQL Server, ordinally on PostgreSQL.
- **`CustomMigrationsModelDiffer(…, ICurrentDbContext)`**, a constructor taking the context the migration is diffed for, and **`ComplexIndexAnnotations.AfterTableRebuild`**, the mark on a create that runs after SQLite's table rebuild ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#sqlite-re-creates-complex-indexes-after-a-table-rebuild)).

### Changed

- Indexes: a complex index, exclusion constraint or temporal constraint declared on the abstract base of a TPC hierarchy is created on every concrete table, as EF Core's own indexes are, where it failed at `migrations add` ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Indexes: on PostgreSQL and SQLite, an explicit name on a TPC type's complex index, exclusion constraint or temporal constraint stays on that type's table, and each derived table's copy takes the name followed by its table. A copy's name past the identifier limit is shortened as EF Core's are, a temporal foreign key's too ([#145](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/145), [#167](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/167), [#168](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/168), [Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy)).
- Packaging: each package carries the version's section of its changelog as its NuGet release notes, which nuget.org's Release Notes tab and Visual Studio's Updates tab show.
- Packaging: the release notes, the README on nuget.org and the GitHub release link the documentation at the version's tag, so they keep describing the version they shipped with after `main` moves on to the next line.
- Packaging: the PostgreSQL and SQL Server packages depend on exactly their own version of `EFCore.ComplexIndexes`, and the core differ refuses a satellite of another version, naming both. Update the packages together: a 5.4.x satellite beside core 5.5.0 was refused as the core differ ([#169](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/169)).

### Fixed

- Indexes: on PostgreSQL, a complex index, exclusion constraint or temporal constraint named like a table, view or sequence of its schema, the sequence of an identity or serial column included, fails at `migrations add`, where it failed at apply with 42P07 ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- Indexes: on SQL Server, a clustered complex index added in a later migration beside an existing one, and a complex index named like its table's primary key or a unique constraint, fail at `migrations add`, where they failed at apply ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#deliberate-rejections)).
- Indexes: on SQLite and SQL Server, complex index names that differ only in case, from each other or from a native index, fail at `migrations add`, where they failed at apply because the database compares them without regard to case ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- Expression indexes: on PostgreSQL, a typed expression index or filter refuses a member of a scalar type, such as `CreatedAt.Year`, at the declaration, where it failed at `migrations add`. `.Value` and `.HasValue` on a nullable translate ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- Filters: on PostgreSQL, a typed filter compares a `char` with a char literal, where it failed at apply on the char's code, and `x.RevokedAt.HasValue` renders `IS NOT NULL`, where it failed at `migrations add` ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).
- Filters: on PostgreSQL, a filter of an index or exclusion constraint that compares a date or time `ToJson()` member with a column fails at `migrations add`, naming the member, where it failed at apply comparing text with a timestamp ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).
- Constraints: on PostgreSQL, a migration that drops both tables of a temporal foreign key applies, `Down()` included, where it failed with 2BP01 when the principal's table sorted first ([Temporal foreign keys](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-foreign-keys-period--requires-postgresql-18)).
- Constraints: on PostgreSQL, an exclusion constraint filter on a `ToJson()` number, boolean, uuid or enum member casts it as EF Core's queries do and applies, where it failed with 42883 or 42804 ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- Constraints: on PostgreSQL, an exclusion constraint with a scalar element under `gist` gets `btree_gist` whatever the element's operator and however the method is spelled, so `<>` and `UseMethod("GiST")` apply, where they failed with 42704 when the extension was missing ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- Migrations: on SQL Server, changing the type, length or nullability of a column a complex index reads applies, an index renamed in the same migration included, where it failed with Msg 5074. The index is dropped before the `ALTER COLUMN` and created after it ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)).
- Migrations: on PostgreSQL and SQL Server, a complex index or constraint renamed away from a name the same migration gives to another object applies, where it failed because the renamed one still held the name. It is dropped before EF Core's operations and created after them, with any temporal foreign key referencing it ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- Docs: the temporal foreign key samples pass both type arguments to `HasTemporalForeignKey`, and compile ([Temporal foreign keys](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-foreign-keys-period--requires-postgresql-18)).
- Docs: the PostgreSQL index guide says to pass a fractional storage parameter as a string, since Npgsql's generator formats numbers with the current culture ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).

## [5.4.1] — 2026-09-22

### Breaking

- Indexes: on a provider without a satellite that keeps index names per table, such as MySQL, a complex index name reused across tables fails at `migrations add`; rename one ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-541)).

### Added

- **`IndexNameScope`** and the protected `CustomMigrationsModelDiffer.IndexNameScope`, through which a provider's differ says where its index names must be unique: per table, per schema or, the default, per database.

### Fixed

- Indexes: on SQLite, a complex index name is checked across the whole database at `migrations add`, where a name reused on another table failed at apply. PostgreSQL checks per schema and SQL Server per table ([Index names](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.4.0] — 2026-09-22

Indexes on `ToJson()` members are written the way Npgsql's queries read them, so the first `migrations add` re-creates the affected indexes: [Upgrading to 5.4.0](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-540).

### Breaking

- Indexes: on PostgreSQL, a non-unique index that starts with a `DateTime`, `DateTimeOffset`, `DateOnly` or `TimeOnly` JSON member is refused at `migrations add`, because no query can use it. A unique one is still allowed ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).

### Added

- **`ComplexIndexAnnotations.RenderingVersion`**, which every declaration writes onto the model, so a change in how parts render reaches databases built by earlier versions ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-540)).

### Changed

- Indexes: the default names of expression parts are built from the property path, not the rendered SQL, so a rendering change never renames an index. Every name 5.3.0 produced stays the same.

### Fixed

- Indexes: on PostgreSQL, indexes on `ToJson()` members render as Npgsql's queries read the member, `#>>` for a nested one and a cast for a typed one, so those queries use them. They applied and served no query. Exclusion constraint filters keep their rendering ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).
- Filters: on PostgreSQL, a filter over a typed JSON member, such as `x => x.Profile.Rank > 5`, applies, where it compared text with an integer and failed at apply ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).
- Constraints: on PostgreSQL, every name this package introduces is checked at `migrations add` against the objects it shares a namespace with: constraints per table, indexes per schema. A clash failed at apply with 42710 or 42P07, or silently dropped an exclusion constraint ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).

## [5.3.0] — 2026-09-05

### Fixed

- Indexes: a property path through a value converter, such as `x => x.Email.Value`, resolves against a model snapshot. Every diff after the first `migrations add` threw `Could not resolve property path 'Email.Value'`, `has-pending-model-changes` and `Migrate()` included ([Converter paths](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).

## [5.2.0] — 2026-09-05

### Breaking

- Indexes: an index or constraint name longer than the provider's identifier limit is refused at `migrations add`, default names included. PostgreSQL truncated it silently and SQL Server failed at apply. Give the declaration a name; a name-only change renames in place ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-520)).

### Added

- **`GetComplexIndexes()` and `FindComplexIndex(name)`**, which read back every declaration this package holds, on the mutable model inside `OnModelCreating` too ([Reading declarations back](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#reading-declarations-back)).
- **`GetExclusionConstraints()` and `FindExclusionConstraint(name)`** on PostgreSQL, with `ExclusionPartDefinition` and `NpgsqlExclusionAnnotations.Constraints` now public ([Reading constraints back](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#reading-constraints-back)).
- **`{Property.Path}` placeholders in filters**, resolved to the mapped column at `migrations add`, on every index and on exclusion constraints. A brace pair that holds no dotted identifier path, such as `'{urgent}'`, stays literal ([Filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- **Property paths through a value converter**: `x => x.Email.Value` resolves to the converted column when the member's type is the converter's provider type ([Converter paths](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#single-column-index-on-a-complex-property)).
- **`AddComplexIndexFilter(predicate, where)` and `AddComplexIndex(definition)`** on `IMutableEntityType`, and `AddExclusionConstraintFilter` on PostgreSQL, which amend declarations from a shared convention. Repeating a call is safe ([Amending declarations](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#amending-declarations)).
- **Typed filter predicates** on PostgreSQL: every `filter:` string has a lambda form, and the builders take `HasFilter(x => …)`. Enums and values with no portable SQL spelling are refused at the declaration ([Typed filters](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-filters)).

### Changed

- Expression indexes: on PostgreSQL, a typed expression with a `DateTime`, `Guid` or enum constant throws at the declaration, where it rendered the constant as bare text ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).

## [5.1.0] — 2026-09-05

### Breaking

- Indexes: a complex index, exclusion constraint or temporal constraint declared on an entity type mapped to no table, typically the abstract base of a TPC hierarchy, fails at `migrations add`, where it produced nothing. View-mapped and query-mapped types are still skipped ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-510)).

### Added

- **Runtime registration of the differ**: `UseNpgsqlComplexIndexes()`, `UseSqlServerComplexIndexes()` and, for other providers, `UseComplexIndexes()`, each with an `Add…ComplexIndexes()` counterpart, so `EnsureCreated()` and `GenerateCreateScript()` include complex indexes. Call the satellite's method only ([Runtime wiring](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#ensurecreated-generatecreatescript-and-the-pending-changes-check)).
- **Whole-document JSON indexes** on PostgreSQL: `HasComplexIndex` on a `ToJson()` complex property or a complex collection indexes its `jsonb` column, for example with `UseGin()` ([Indexing the whole document](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#indexing-the-whole-document)).
- **`HasStorageParameter(name, value)`** on PostgreSQL complex and expression indexes, for `WITH (fillfactor=70)` and the like ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).
- **`UseCollation(params string[])`** on PostgreSQL complex and expression indexes: per-column index collations, by position, independent of the column's own collation ([Index methods](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#index-methods-on-a-complex-property)).
- **`HasComplexIndex` on the non-generic `ComplexTypePropertyBuilder`**, for a property configured by name or by type.

### Fixed

- Indexes: a complex index named like a native `HasIndex` on the same table is refused at `migrations add`, where the migration failed at apply with 42P07.
- Indexes: an option of the other provider on a property-level complex index is refused at `migrations add`, where `UseGin()` under the SQL Server differ was dropped and scaffolded a plain B-tree. The PostgreSQL differ refuses SQL Server options the same way.
- Migrations: `HasDifferences` reports changes to complex indexes and constraints, so `has-pending-model-changes`, `Migrate()`'s pending-changes warning and `migrations remove` see them. A CI gate on `has-pending-model-changes` may now fail; that is the gate working ([Upgrading](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/upgrading.md#upgrading-to-510)).

## [5.0.3] — 2026-08-15

A packaging and documentation release. The differ and the SQL it generates are unchanged.

### Added

- **XML documentation for the whole public API**, so IntelliSense shows the fluent API, the annotation keys, `CompositeIndexDefinition` and `IndexPartDefinition`, where the shipped `.xml` had 64 gaps.

### Changed

- Packaging: the EF Core dependency has an upper bound, `[10.0.0, 11.0.0)`, on `Microsoft.EntityFrameworkCore.Abstractions` for the core and on the provider package for each satellite. Restore still picks 10.0.0.

## [5.0.2] — 2026-08-15

Eleven fixes from a review of 5.0.1. The first three were migrations that scaffolded and applied cleanly while silently wrong.

### Added

- **`UseDataCompression(DataCompressionType)`** on SQL Server complex indexes. The option was forwarded but nothing could set it ([Index options](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#index-options)).

### Fixed

- Registration: the design-time differ is chosen per provider. Restore order decided it, so a solution referencing two satellites could hand one provider's model to the other's differ, which dropped its index options.
- Indexes: a reused index or exclusion constraint name is refused at the declaration or at `migrations add`. The migration failed at apply or, for exclusion constraints, applied with only the last one standing.
- Indexes: a selector that reads a captured variable or a static member, such as `x => captured.Name`, throws at the declaration and names the selector. It failed later with an opaque resolution error.
- Indexes: `DbOrder.Asc` marks a column ascending, and combining it with `DbOrder.Desc`, or `NullsFirst` with `NullsLast`, throws.
- Indexes: `CompositeIndexDefinition` compares array-valued provider annotations, such as operator classes and `INCLUDE` lists, by content.
- Indexes: provider validation leaves index operations this package did not create alone, so a native `HasIndex` with provider options no longer fails `migrations add`.
- Indexes: on PostgreSQL, `Npgsql:IndexSortOrder` and `Npgsql:IndexNullSortOrder` are no longer forwarded onto complex indexes, and setting either throws, pointing to `DbOrder`.
- Indexes: on SQL Server, the clustered combinations SQL Server refuses fail at `migrations add`: with `INCLUDE`, with a filter, a second one on a table, and one where the primary key holds the slot ([Deliberate rejections](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md#deliberate-rejections)).
- Indexes: on SQL Server, the data compression option survives the model snapshot, which dropped it from the generated DDL.
- Constraints: on PostgreSQL, temporal `UNIQUE … WITHOUT OVERLAPS` constraints and temporal foreign keys render at design time, without `UseNpgsqlComplexIndexes()`. Without it they scaffolded a plain `UNIQUE (key, period)` ([Temporal constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#temporal-unique-constraints-without-overlaps--requires-postgresql-18)).
- Constraints: on PostgreSQL, two exclusion constraints over the same columns with different filters coexist, both named. The second replaced the first ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).

## [5.0.1] — 2026-08-10

### Changed

- Constraints: on PostgreSQL, an exclusion constraint's `ADD CONSTRAINT` is preceded by `DROP CONSTRAINT IF EXISTS`, so adopting a hand-written constraint of the same name applies instead of failing with 42P07.
- Constraints: on PostgreSQL, a name-only change to an exclusion constraint, temporal constraint or temporal foreign key emits `RENAME CONSTRAINT` instead of a rebuild, and dependent temporal foreign keys survive it.

### Fixed

- Constraints: on PostgreSQL, renaming a table no longer drops and re-creates its exclusion and temporal constraints.

## [5.0.0] — 2026-08-10

### Breaking

- Indexes: an indexed property that resolves to no column throws at `migrations add` instead of dropping the index. On PostgreSQL, a `ToJson()` member resolves to a JSON expression index.

### Added

- **`EFCore.ComplexIndexes.SqlServer`**, the SQL Server satellite: clustered, covering, online, fill-factor and sort-in-tempdb options ([SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)).
- **Entity-level `HasComplexIndex(x => x.Complex.Prop, …)`** for single-column indexes, so a column can carry several filtered indexes.
- **`DbOrder.Asc` and `DbOrder.Desc`**, per-column sort direction on every relational provider ([Per-column sort direction](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/README.md#per-column-sort-direction)).
- **`HasExclusionConstraint`** on PostgreSQL: `EXCLUDE` constraints with `WHERE` predicates ([Exclusion constraints](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-constraints.md#exclusion-constraints-exclude)).
- **`HasExpressionIndex(x => x.Email.ToLower())`** on PostgreSQL, typed LINQ expression indexes ([Typed expression indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#typed-linq-expression-indexes)).
- **JSON member indexes** on PostgreSQL, for members of `ToJson()` complex properties ([JSON member indexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#json-member-indexes)).
- **`DbOrder.NullsFirst` and `DbOrder.NullsLast`** on PostgreSQL, and `NullsFirst()` and `NullsLast()` on `ExpressionIndexBuilder` ([Per-column null ordering](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/postgresql-indexes.md#per-column-null-ordering)).

### Changed

- Indexes: provider annotations reach index operations through a whitelist, so column facets such as `Relational:ColumnName` no longer leak into migrations as drop-and-create churn.
- Indexes: two indexes over the same columns coexist when their filters differ, both named. Declaring one again with the same filter updates it in place.
- Indexes: `IncludeProperties(…)` entries resolve as property paths, complex members included, with the column name as written as the fallback.
- Indexes: on PostgreSQL, an index that needs this package's SQL generator carries a sentinel column, so a missing `UseNpgsqlComplexIndexes()` fails at apply with a message that says so.
- Migrations: a name-only index change emits `RenameIndexOperation` on PostgreSQL and SQL Server, and renaming a table no longer re-creates its complex indexes.

### Fixed

- Indexes: integral provider annotations such as fill factor survive the snapshot as `int`. They became `double`, and the generator dropped them.
- Expression indexes: descending parts render `DESC`, declared with `ExpressionIndexBuilder.Descending()`.
- Migrations: the drops of complex indexes run before the base operations, so moving an index between `HasIndex` and a complex declaration under one name applies.

[5.6.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.6.0
[5.5.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.5.0
[5.4.1]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.4.1
[5.4.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.4.0
[5.3.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.3.0
[5.2.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.2.0
[5.1.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.1.0
[5.0.3]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.0.3
[5.0.2]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.0.2
[5.0.1]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.0.1
[5.0.0]: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/v5.0.0
