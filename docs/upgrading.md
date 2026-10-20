# Upgrading

This page says what to check when you move to a new version: what the first
`dotnet ef migrations add` after the upgrade scaffolds, and which models now fail where they passed.
Versions with nothing to check are left out. The [changelog](../CHANGELOG.md) lists every change.

From 5.5.0 on, a minor can change what an existing model scaffolds: a fix for a migration that
applied cleanly while the database ended up different from the model. Such a minor opens its
changelog entry with "Changed answers", and has a section here, "Changed answers in x.y.0".

The sections run from the oldest version to the newest, so read from the one after yours down.

## Upgrading to 5.1.0

### Check CI gates on `has-pending-model-changes`

`HasDifferences` now sees changes to complex indexes and to exclusion and temporal constraints.
`dotnet ef migrations has-pending-model-changes`, the warning `Migrate()` raises and the snapshot
check in `migrations remove` reported "no changes" when only a declaration from this package had
changed. A CI gate built on `has-pending-model-changes` may now fail where it passed. Scaffold the
migration it asks for.

### Move declarations off the abstract base of a TPC hierarchy

A complex index, exclusion constraint or temporal constraint declared on an entity type mapped to
no table now fails at `migrations add`. It used to produce nothing, so the index or constraint you
declared never existed. Declare it on each concrete type instead. Declarations on view-mapped and
query-mapped types are still skipped.

## Upgrading to 5.2.0

### Name long indexes and constraints yourself

An index or constraint name longer than the provider's identifier limit fails at
`migrations add`: 63 bytes on PostgreSQL, 128 characters on SQL Server. PostgreSQL used to
truncate the name with a NOTICE and apply the migration, so the object existed under a name that
neither the declaration nor a constraint-violation error reported. SQL Server failed at apply.

The names this package derives are never truncated, so a long table name plus a long column path
reaches the limit. A default temporal foreign key name, built from two table names, is the first to.
Give the declaration a `name`. A change of name alone renames the index or constraint in place.

## Upgrading to 5.4.0

### Expect one re-create of each index on a typed or nested JSON member

On PostgreSQL, indexes on members of a `ToJson()` complex property are written the way Npgsql's
queries read the member. Every member used to be extracted as `"doc" -> 'A' ->> 'B'` text with no
cast, which matched a query only for a top-level string. An index on a nested or typed member
applied, enforced uniqueness, and served no query.

The first `migrations add` drops and re-creates each affected index under its existing name, in
index parts, typed expression indexes and index filters. Indexes on top-level string members are
untouched, and exclusion constraint filters keep their rendering. Until that migration exists,
`has-pending-model-changes` reports changes and `Migrate()` raises EF Core's pending-model-changes
error. The re-create blocks writes while it builds, so on a large table declare the index with
`IsCreatedConcurrently()` first.

The snapshot gains one `CustomIndex:RenderingVersion` annotation. It records the rules the model
was declared under, which is how a rendering change reaches databases built by earlier versions.

### Drop non-unique indexes that start with a date or time JSON member

A non-unique index whose first part is a `DateTime`, `DateTimeOffset`, `DateOnly` or `TimeOnly`
JSON member fails at `migrations add`. EF Core's queries cast such a member to `timestamptz`,
`date` or `time`, and PostgreSQL can't index those casts, so no query could use the index. A unique
one is still allowed and enforces uniqueness on the stored text. A date member in a later position
is fine.

## Upgrading to 5.4.1

### Rename a complex index name you reuse across tables on SQLite

On SQLite, a complex index name is checked across the whole database at `migrations add`. SQLite
refuses a second `CREATE INDEX` under a name used on any other table, and ignores the schemas a
model configures, so such a model used to fail when the migration was applied. Rename one of the
two indexes.

On a provider without a satellite that keeps index names per table, such as MySQL, the same check
asks for a rename the database would not have needed. With the differ registered at runtime
(`UseComplexIndexes()`), `Migrate()` raises the same error until you rename. A snapshot that holds
such a name stays diffable.

## Upgrading to 5.5.0

### EF Core 11 is refused at registration

Every registration entry point throws `InvalidOperationException` when the EF Core it runs on is
not version 10: `UseComplexIndexes()`, `UseNpgsqlComplexIndexes()`, `UseSqlServerComplexIndexes()`,
their `Add…` counterparts, and the design-time registration `dotnet ef` runs. The differs and
the PostgreSQL SQL generator check too when an application registers them itself with
`ReplaceService`. The message names `CodoMetis.EFCore.SchemaKit`, the package for EF Core 11.

Nothing changes on EF Core 10, and EF Core 10 on .NET 11 keeps working: the check reads EF Core's
version, not the runtime's. An application on EF Core 11 now fails at startup or at
`migrations add`. During the EF Core 11 release candidates its restore shows no warning, because a
prerelease sorts below 11.0.0 and falls inside the `[10.0.0, 11.0.0)` ranges, so this message may
be the first sign it was running on a version this package never supported.

### Rename complex index names that differ only in case

Complex index names that differ only in case, such as `ix_code` and `IX_CODE`, are one name at
`migrations add` on SQLite and SQL Server, from each other and from a native `HasIndex` name. Both
databases compare index names without regard to case, so such a model used to fail when the
migration was applied. PostgreSQL keeps the case of the quoted names EF Core writes, and nothing
changes there.

On a SQL Server database with a case-sensitive collation, and on a provider without a satellite
whose index names keep their case, the check asks for a rename the database would not have needed.
Rename one of the two indexes. A snapshot that holds such a pair stays diffable.

## Changed answers in 5.5.0

Most of these make the first `migrations add` after the upgrade drop and re-create some indexes or
constraints. On every provider the model's `CustomIndex:RenderingVersion` becomes 3, and a model
that declares only PostgreSQL constraints gets the key for the first time, so that migration also
rewrites the snapshot; no other index changes.

### Unique indexes over nullable columns on SQL Server

A unique complex index over nullable columns gets the `WHERE [c] IS NOT NULL` filter EF Core's
convention gives its own unique indexes. SQL Server treats NULLs as equal in a unique index, so
`HasComplexIndex(x => x.Registration!.Vin, isUnique: true)` on an optional complex property
admitted one row without a registration and refused the second with a duplicate key.

The filter covers the nullable key columns only. It is not added to a clustered index or to one
declared with a filter. The first `migrations add` drops and re-creates each such index with the
filter.

### Declarations on a TPC type cover every table of the hierarchy

A complex index, exclusion constraint, temporal constraint or temporal foreign key declared on an
entity type mapped with TPC is created on the table of every type derived from it, as EF Core
creates its own `HasIndex` there. It used to land on the declaring type's table only. On a concrete
base, a unique index or exclusion constraint held among the base's rows and not among a derived
type's: PostgreSQL 18 refused an overlapping booking in `bookings` and accepted it in
`special_bookings`. Each table enforces its own rows, as EF Core's indexes do; nothing spans the
tables.

The first `migrations add` creates the copies on the derived tables, each under the default name
for its table, such as `IX_special_bookings_Code`, and changes nothing on the declaring type's
table. Before you apply it, remove the rows in the derived tables that violate a unique index or
an exclusion constraint. The migration fails on them, with 23505 or 23P01 on PostgreSQL and
Msg 1505 on SQL Server, and in its transaction applies nothing.

Some of these declarations fail at `migrations add` now:

- One with an explicit name, where index names are unique per schema or database: on PostgreSQL
  for every kind but the temporal foreign key, and on SQLite. The name would repeat on each table.
  Leave it out. SQL Server keeps index names per table and takes the name on each, as EF Core does.
- One that reads a member of a complex property. EF Core 10 keeps a TPC type's complex properties
  in that type's own table, so a derived table has no column for it. Map the member as a regular
  property, or remove the declaration.
- A temporal foreign key to a TPC type with derived types. Its rows live in several tables and a
  foreign key references one, so it refused every row of a derived type. EF Core creates no
  foreign key for such a relationship either. Reference a type with no derived types.

A declaration on the abstract base of a TPC hierarchy, refused since 5.1.0, is created on every
concrete table now, unless it reads a member of a complex property.

### Substring and Trim render as EF Core's queries

On PostgreSQL, typed expression indexes render `Substring`, `Trim`, `TrimStart` and `TrimEnd` the
way EF Core's queries do, so a query over the same lambda can use the index:

| Lambda | Was | Is |
|---|---|---|
| `x.Code.Substring(1, 3)` | `substr("Code", 2, 3)` | `substring("Code", 2, 3)` |
| `x.Code.Trim()` | `btrim("Code")` | `btrim("Code", E' \t\n\r')` |

PostgreSQL treats `substr` and `substring` as different functions, so those indexes applied and
served no query. `btrim("Code")` trims spaces only, so a unique index on `Trim()` accepted values
that differ only in a tab or line break at the ends.

The first `migrations add` re-creates each typed expression index over these methods. A unique
trimmed index may then refuse rows that differ only in tabs or line breaks at the ends. Those are
the duplicates it was declared to refuse: clean them up before you apply the migration.

### Concatenation with a nullable operand

On PostgreSQL, a typed expression index or typed filter that concatenates an operand that can be
null wraps it in a `COALESCE` with an empty string, as EF Core's queries do. C# concatenates null as an empty
string, while PostgreSQL's `||` returns NULL: `x => x.Code + x.Suffix` on a `string? Suffix`
scaffolded `("Code" || "Suffix")`, which is NULL for every row without a suffix. The index served
no query over the lambda, and a unique one checked none of those rows. It is now
`("Code" || coalesce("Suffix", ''))`.

The first `migrations add` re-creates each typed expression index, and each index and exclusion
constraint with a typed filter, whose lambda concatenates an operand that can be null. A unique
index may then refuse rows it let in, such as two rows with code `A` and no suffix. Those are the
duplicates it was declared to refuse: clean them up before you apply the migration.

### Typed filters keep C#'s answer for null operands

On PostgreSQL, a typed filter whose comparison has an operand that can be null keeps C#'s answer
for the rows where it is null, written the way EF Core writes the same lambda in a query.
`x => x.Kind != "federated"` on a `string?` scaffolded `"Kind" <> 'federated'`, which is NULL for a
null `Kind`, so those rows fell out of the partial index and a unique index did not enforce on
them. It is now `("Kind" <> 'federated' OR "Kind" IS NULL)`.

A negation over such a comparison is pushed down to it: `!(x.Maybe > 5)` is
`CASE WHEN "Maybe" > 5 THEN FALSE ELSE TRUE END`, true where `Maybe` is null, as in EF Core's query.
A computed operand is checked through its columns: `x.Name.ToLower() != "a"` adds
`OR "Name" IS NULL`. A captured variable holding null compares with `IS NULL` or
`IS NOT NULL` instead of `= NULL`, which matched no row. Operands that can't be null, a
non-nullable value type or a reference type annotated non-nullable, translate as before.

This covers every typed filter: `HasComplexIndex`, `HasComplexCompositeIndex` and
`HasExpressionIndex` with a predicate, `HasFilter`, `AddComplexIndexFilter`, and exclusion
constraint filters. The first `migrations add` drops and re-creates each index and exclusion
constraint whose typed filter changed. A unique index now covers rows it left out, so re-creating
it fails if those rows hold duplicates. Those are duplicates it should have refused.

### Typed filters keep braces in string literals

On PostgreSQL, a brace in a string literal of a typed filter is compared as written.
`x => x.Code != "{draft}"` scaffolded `"Code" <> '{{draft}}'`, a different value, so rows whose
code is `{draft}` were indexed, and constrained if the index is unique, although the filter excludes
them. It is now `"Code" <> '{draft}'`. Typed expression indexes were not affected.

The first `migrations add` re-creates each index and exclusion constraint whose typed filter has a
brace in a string literal.

### SQLite refuses a table rebuild that would drop complex indexes

On SQLite without `UseComplexIndexes()`, `dotnet ef migrations add` refuses a migration that
rebuilds a table carrying a complex index. SQLite runs an `ALTER COLUMN`, a `DROP COLUMN`, a change
to a key, foreign key, unique or check constraint, a table comment, a new column with a comment
and a table rename (EF renames the primary key with it) by rebuilding the table, and EF Core
re-creates only its own indexes. Every complex index on the
table vanished, unique ones included, while the migration applied cleanly and the snapshot still
listed them.

No operation in the migration can re-create them, because EF Core's SQLite generator runs every
rebuild last. The error names the indexes and two ways out: register `UseComplexIndexes()` after
`UseSqlite()` (see the next section), or split the change into three migrations: remove the
declarations on the table, make the change, then declare them again.

A SQLite database that went through such a migration before is missing those indexes while its
snapshot lists them. Check with `SELECT name FROM sqlite_master WHERE type = 'index'`, and re-create
any missing one with `migrationBuilder.Sql(…)` in a new migration.

### SQLite re-creates complex indexes after a table rebuild

With `UseComplexIndexes()` called after `UseSqlite()` on the context `dotnet ef` uses, the migration
the previous section refuses is scaffolded instead. It drops each complex index on the rebuilt
table, unchanged ones included, and creates it again, marked with the
`CustomIndex:AfterTableRebuild` annotation and a call to `__requires_UseComplexIndexes__()` ANDed
onto its filter. The SQL generator `UseComplexIndexes()` registers moves those creates behind the
rebuild and removes the marker. TPC copies of a base's index are covered like any other.

The migration needs that generator wherever it is applied: `Migrate()`, `dotnet ef database update`
and `dotnet ef migrations script` use the context's own services. Applied without it, the first
create fails with `no such function: __requires_UseComplexIndexes__`, before the rebuild, and the
transaction leaves the table as it was. Don't edit the marker; the generator refuses a filter it did
not write.

`migrations add` still refuses when `UseComplexIndexes()` is called before `UseSqlite()`, or when
another package replaces `IMigrationsSqlGenerator` with a generator that does not derive from
SQLite's, since EF Core keeps only one replacement. The error names the generator in effect. A
replacement keyed on SQLite's generator
(`ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator, TNew>()`), or a provider
extension registered before `UseComplexIndexes()`, is wrapped and keeps working.

### NULLS ordering and expression parts fail on SQLite

On SQLite and any other provider without a satellite, a complex index with a
`DbOrder.NullsFirst`/`DbOrder.NullsLast` part or an expression part (`HasExpressionIndex` from the
PostgreSQL package, in a model shared with SQLite tests) fails at `migrations add`, `EnsureCreated()`
and `GenerateCreateScript()`. Only the PostgreSQL satellite's generator renders those parts. The
migration used to carry a sentinel column that fails with the SQLite bundled with
Microsoft.Data.Sqlite. Where SQLite accepts double-quoted strings as literals, as a default build does
(a system `libsqlite3`, other SQLitePCLRaw bundles), the sentinel and the expression were taken for
strings, and the index applied with a constant column, without its ordering or expression.

Remove the marker, or the expression index, and add a migration. It drops the index and creates it
from its columns. Check with `SELECT sql FROM sqlite_master WHERE type = 'index'`: an index whose
definition names `__requires_UseNpgsqlComplexIndexes__` came from such a migration.

### The core differ refuses SQL Server and PostgreSQL index options

On SQL Server and PostgreSQL, the provider-agnostic core differ refuses a complex index that carries
one of that provider's index options, such as `IsClustered()`, `HasFillFactor(…)`,
`IncludeProperties(…)` or `UseGin()`. The error names the satellite's registration.

The core differ runs where the satellite's should when an application calls `UseComplexIndexes()`
instead of `UseSqlServerComplexIndexes()` or `UseNpgsqlComplexIndexes()`, or when `dotnet ef` runs
without the satellite's `.targets`. It forwards no property-level option, so `EnsureCreated()`
created such an index without its options and applied cleanly. Entity-level options went through
without their checks, and an `INCLUDE` list kept its property paths as column names.

Nothing is re-created. An application with that registration now fails in `EnsureCreated()`,
`GenerateCreateScript()` and `Migrate()`, whose pending-model-changes check runs the same differ.
Replace `UseComplexIndexes()` with the satellite's method. A database `EnsureCreated()` built that
way has the indexes without their options: drop and re-create them, or rebuild the database. A model
shared with SQLite tests is unaffected, since the check covers only the provider being diffed.

### The core differ refuses the satellites' constraints and null filter

On PostgreSQL, the core differ also refuses a model with an exclusion constraint, a temporal
constraint or a temporal foreign key. On SQL Server, it refuses a unique complex index over nullable
columns declared without a filter, which the satellite gives EF Core's `IS NOT NULL` filter. The
error names the declaration and the satellite's registration.

This affects an application that calls `UseComplexIndexes()` with `UseNpgsql(…)` or
`UseSqlServer(…)` instead of `UseNpgsqlComplexIndexes()` or `UseSqlServerComplexIndexes()`, and a
`dotnet ef` run without the satellite's `.targets`. The core differ never renders these
declarations, so `EnsureCreated()` created the tables without the constraints and the unique index
without its filter, and applied cleanly. Such an application now fails in `EnsureCreated()`,
`GenerateCreateScript()` and `Migrate()`, whose pending-model-changes check runs the same differ.
Replace `UseComplexIndexes()` with the satellite's method. Nothing is re-created, and migrations
that `dotnet ef` scaffolded with the satellite's `.targets` already hold the right DDL.

A database `EnsureCreated()` built under `UseComplexIndexes()` lacks what was dropped. On
PostgreSQL, list the exclusion and temporal constraints it has and compare them with the model:

```sql
SELECT conrelid::regclass AS table_name, conname, contype
FROM pg_constraint
WHERE contype = 'x' OR conperiod;
```

`conperiod` exists from PostgreSQL 18, which temporal constraints need; on an earlier server, leave
out `OR conperiod`. On SQL Server, list the unique indexes without a filter, and look for complex
indexes over nullable columns among them:

```sql
SELECT OBJECT_NAME(object_id) AS table_name, name
FROM sys.indexes
WHERE is_unique = 1 AND is_primary_key = 0 AND is_unique_constraint = 0 AND has_filter = 0;
```

Add what is missing from the script `GenerateCreateScript()` writes under the satellite's
registration, or rebuild the database. A model shared with SQLite tests is unaffected.

### INCLUDE entries must name a column

On PostgreSQL and SQL Server, an `IncludeProperties(…)` entry that names neither a property path nor
a column of the index's table fails at `migrations add`, naming the entry. An entry that names no
property passes through as a column name, so when the property an entry named was removed and the
entry kept, both sides of the diff read the same text and the migration held only the
`DROP COLUMN`. PostgreSQL drops an index with a column it includes, so a unique index vanished and
the migration applied cleanly; the snapshot still listed it, so no later migration re-created it.
SQL Server refused the drop (Msg 5074).

With `UseNpgsqlComplexIndexes()` or `UseSqlServerComplexIndexes()`, an application with such a model
also fails in `EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`, whose pending-model-changes
check runs the same differ. Remove the entry, or name a property. An entry naming a column that exists in the database but not
in the model fails too: map the column, as a shadow property if nothing reads it. A snapshot that
holds such an entry stays diffable. On PostgreSQL, check a database that a migration of that shape
reached with `SELECT indexname FROM pg_indexes WHERE tablename = '<table>'`. The migrations do not
know the index is missing, so re-create it by hand with `migrationBuilder.Sql(…)`.
