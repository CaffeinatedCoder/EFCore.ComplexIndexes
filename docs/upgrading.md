# Upgrading

Read this page when you move to a new version. For each version it says what the first
`dotnet ef migrations add` after the upgrade writes, and which models fail now where they passed
before. Versions with nothing to check are left out. The [changelog](../CHANGELOG.md) lists every change.

From 5.5.0 on, a minor version can change what `migrations add` writes for a model you already
have. That happens when it fixes a migration that ran without an error but left the database
different from the model. Such a minor opens its changelog entry with "Changed answers", and has a
section here, "Changed answers in x.y.0".

The sections run from the oldest version to the newest, so read from the one after yours down.

## Upgrading to 5.1.0

### Check CI gates on `has-pending-model-changes`

`HasDifferences` now notices changes to complex indexes and to exclusion and temporal constraints.
Before, `dotnet ef migrations has-pending-model-changes`, the warning `Migrate()` raises and the
snapshot check in `migrations remove` reported "no changes" when only a declaration from this
package had changed. So a CI gate built on `has-pending-model-changes` may now fail where it passed.
Add the migration it asks for.

### Move declarations off the abstract base of a TPC hierarchy

A complex index, exclusion constraint or temporal constraint declared on an entity type mapped to
no table now fails at `migrations add`. It used to produce nothing, so the index or constraint you
declared never existed. Declare it on each concrete type instead. Declarations on view-mapped and
query-mapped types are still skipped.

## Upgrading to 5.2.0

### Name long indexes and constraints yourself

An index or constraint name longer than the provider's identifier limit fails at
`migrations add`: 63 bytes on PostgreSQL, 128 characters on SQL Server. PostgreSQL used to cut the
name short with a NOTICE and run the migration. The object then had a name that neither your
declaration nor a constraint-violation error showed. On SQL Server the migration failed when it ran.

The package doesn't shorten the default names it builds, so a long table name plus a long column
path can reach the limit. (Since 5.5.0 it shortens the copies of a declaration on a TPC type, as
[described below](#declarations-on-a-tpc-type-cover-every-table-of-the-hierarchy).) A default temporal foreign key name, built from two table names, reaches
it first. Give the declaration a `name`. Changing only the name renames the index or constraint in
place.

## Upgrading to 5.4.0

### Expect one re-create of each index on a typed or nested JSON member

On PostgreSQL, indexes on members of a `ToJson()` complex property are written the way Npgsql's
queries read the member. Every member used to be extracted as `"doc" -> 'A' ->> 'B'` text with no
cast, which matched a query only for a top-level string. An index on a nested or typed member was
created and kept values unique, but no query used it.

The first `migrations add` drops and re-creates each affected index under its existing name, in
index parts, typed expression indexes and index filters. Indexes on top-level string members are
untouched, and exclusion constraint filters keep their rendering. Until that migration exists,
`has-pending-model-changes` reports changes and `Migrate()` raises EF Core's pending-model-changes
error. The re-create blocks writes while it builds, so on a large table declare the index with
`IsCreatedConcurrently()` first.

The snapshot gains one `CustomIndex:RenderingVersion` annotation. It records which rules the model
was declared under. That is how a change in the SQL the package writes reaches databases built by
earlier versions.

### Drop non-unique indexes that start with a date or time JSON member

A non-unique index whose first part is a `DateTime`, `DateTimeOffset`, `DateOnly` or `TimeOnly`
JSON member fails at `migrations add`. EF Core's queries cast such a member to `timestamptz`,
`date` or `time`, and PostgreSQL can't index those casts, so no query could use the index. A unique
one is still allowed and enforces uniqueness on the stored text. A date member in a later position
is fine.

## Upgrading to 5.4.1

### Rename a complex index name you reuse across tables on SQLite

On SQLite, `migrations add` checks a complex index name across the whole database. SQLite rejects
a second `CREATE INDEX` under a name used on any other table, and ignores the schemas your model
configures. So such a model used to fail when the migration ran. Rename one of the two indexes.

Some providers, such as MySQL, keep index names per table and have no package of their own here. On
those, the same check makes you rename an index the database would have accepted. With the package
registered on your context (`UseComplexIndexes()`), `Migrate()` fails with the same error until you
rename. A snapshot that holds such a name still works, since the check covers your current model
only.

## Upgrading to 5.5.0

### EF Core 11 is refused at registration

Every registration entry point throws `InvalidOperationException` when the EF Core it runs on is
not version 10: `UseComplexIndexes()`, `UseNpgsqlComplexIndexes()`, `UseSqlServerComplexIndexes()`,
their `Add…` counterparts, and the design-time registration `dotnet ef` runs. The package's
differs (the classes that build migration operations) and its PostgreSQL SQL generator check too, when your application registers them
itself with `ReplaceService`. The message names `CodoMetis.EFCore.SchemaKit`, the package for EF
Core 11.

Nothing changes on EF Core 10, and EF Core 10 on .NET 11 keeps working: the check reads EF Core's
version, not the runtime's. An application on EF Core 11 now fails at startup or at
`migrations add`. During the EF Core 11 release candidates, restore shows no warning: a prerelease
sorts below 11.0.0, so it falls inside the `[10.0.0, 11.0.0)` ranges. This error may be the first
sign that your application ran on a version this package never supported.

### Rename complex index names that differ only in case

On SQLite and SQL Server, `migrations add` treats complex index names that differ only in case,
such as `ix_code` and `IX_CODE`, as one name. That holds between two complex indexes, and between a
complex index and a native `HasIndex` name. Both databases compare index names without regard to
case, so such a model used to fail when the migration ran. PostgreSQL keeps the case of the quoted
names EF Core writes, and nothing changes there.

Rename one of the two indexes. On a SQL Server database with a case-sensitive collation, and on a
provider with no package of its own here whose index names keep their case, that is a rename the
database would not have needed. A snapshot that holds such a pair still works, since the check
covers your current model only.

## Changed answers in 5.5.0

Most of these make the first `migrations add` after the upgrade drop and re-create some indexes or
constraints. Until that migration exists, `has-pending-model-changes` reports changes and
`Migrate()` raises EF Core's pending-model-changes error. On every provider, the model's
`CustomIndex:RenderingVersion` becomes 3, and a model that declares only PostgreSQL constraints
gets the key for the first time. So that migration also rewrites the snapshot. No other index
changes. The new version alone is not a pending change.

### Unique indexes over nullable columns on SQL Server

A unique complex index over nullable columns gets the `WHERE [c] IS NOT NULL` filter EF Core's
convention gives its own unique indexes. SQL Server treats NULLs as equal in a unique index, so
`HasComplexIndex(x => x.Registration!.Vin, isUnique: true)` on an optional complex property
accepted one row without a registration and rejected the second as a duplicate key.

The filter covers the nullable key columns only. It is not added to a clustered index or to one
declared with a filter. The first `migrations add` drops and re-creates each such index with the
filter.

### Declarations on a TPC type cover every table of the hierarchy

A complex index, exclusion constraint, temporal constraint or temporal foreign key declared on an
entity type mapped with TPC is created on the table of every type derived from it, as EF Core
creates its own `HasIndex` there. It used to land on the declaring type's table only. On a concrete
base, a unique index or exclusion constraint applied to the base's rows and not to a derived
type's. PostgreSQL 18 rejected an overlapping booking in `bookings` and accepted it in
`special_bookings`. Each table enforces its own rows, as EF Core's indexes do; nothing spans the
tables.

The first `migrations add` creates the copies on the derived tables and changes nothing on the
declaring type's table. Each copy is named for its table:

- **Without a name,** a copy takes the default name for its table, such as
  `IX_special_bookings_Code`.
- **With a name,** the declaring type's table keeps it. On PostgreSQL and SQLite, where an index
  name must be unique in the schema or database, each derived table's copy takes your name followed
  by its table: `ix_code_active` on `bookings` becomes `ix_code_active_special_bookings` on
  `special_bookings`. EF Core repeats the name of its own `HasIndex` on every table there, and the
  migration fails when it runs (42P07; SQLite "index … already exists"). Two partial indexes or
  exclusion constraints over the same columns need a name each, and this keeps them apart on every
  table ([#167](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/167)).
- **On SQL Server, and for a temporal foreign key,** a name repeats on every table, as EF Core
  repeats its own. SQL Server keeps index names per table, and PostgreSQL keeps a foreign key's
  name per table.

Where a derived table's long name puts a copy's name past the provider's identifier limit (63 bytes
on PostgreSQL), the name is shortened as EF Core shortens its own: cut and ended with `~`, or with
`~1`, `~2`, … where the shortened name is already taken
([#145](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/145), and for a temporal
foreign key [#168](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/168)). A table
of 57 characters gives `IX_special_bookings_for_corporate_customer_accounts_xyz_1234_C~`. Only
these names are shortened. A name you wrote that is too long, or a default name too long off a TPC
hierarchy, still fails at `migrations add`, and a shorter explicit name fixes it.

Before you apply the migration, remove the rows in the derived tables that violate a unique index or
an exclusion constraint. The migration fails on them, with 23505 or 23P01 on PostgreSQL and
Msg 1505 on SQL Server. The migration runs in a transaction, so nothing of it is applied, with
one exception on PostgreSQL.

The copies of an index you declared with `IsCreatedConcurrently()` are created outside the
migration's transaction, one at a time. When a copy fails, the operations before it stay
applied, and PostgreSQL keeps the failed copy as an invalid index. The migration is not recorded
as applied, so running it again fails on an object it already created, such as an index (42P07).
To recover, remove the rows, then drop what the migration created, the invalid index included, and
apply the migration again. This query lists the invalid indexes:

```sql
SELECT indexrelid::regclass AS invalid_index FROM pg_index WHERE NOT indisvalid;
```

Drop each one with `DROP INDEX CONCURRENTLY`, for example
`DROP INDEX CONCURRENTLY "IX_special_bookings_Code";`.

Some of these declarations fail at `migrations add` now:

- One that reads a member of a complex property. EF Core 10 keeps a TPC type's complex properties
  in that type's own table, so a derived table has no column for it. Map the member as a regular
  property, or remove the declaration.
- A temporal foreign key to a TPC type with derived types. Its rows live in several tables and a
  foreign key references one, so it rejected every row of a derived type. EF Core creates no
  foreign key for such a relationship either. Reference a type with no derived types.

A declaration on the abstract base of a TPC hierarchy, rejected since 5.1.0, is created on every
concrete table now, unless it reads a member of a complex property. The abstract base has no table
to keep an explicit name on, so on PostgreSQL and SQLite every copy takes the name followed by its
table.

### Substring and Trim render as EF Core's queries

On PostgreSQL, typed expression indexes and typed filters render `Substring`, `Trim`, `TrimStart`
and `TrimEnd` the way EF Core's queries do, so a query over the same lambda can use the index. A
typed filter is one you pass as a lambda, to an index or to an exclusion constraint:

| Lambda | Was | Is |
|---|---|---|
| `x.Code.Substring(1, 3)` | `substr("Code", 2, 3)` | `substring("Code", 2, 3)` |
| `x.Code.Trim()` | `btrim("Code")` | `btrim("Code", E' \t\n\r')` |
| `x.Code.TrimStart()` | `ltrim("Code")` | `ltrim("Code", E' \t\n\r')` |
| `x.Code.TrimEnd()` | `rtrim("Code")` | `rtrim("Code", E' \t\n\r')` |

PostgreSQL treats `substr` and `substring` as different functions, so those indexes applied and
served no query. `btrim("Code")` trims spaces only, so a unique index on `Trim()` accepted values
that differ only in a tab or line break at the ends.

Trimming tabs and line breaks also changes which rows a typed filter covers. A value of only tabs
or line breaks now falls outside `x => x.Code.Trim() != ""`, so a unique index or exclusion
constraint with that filter stops checking it. A value such as `'abc\t'` now falls inside
`x => x.Code.Trim() == "abc"`, so it is checked for the first time. A filter over `Substring`
covers the same rows as before.

The first `migrations add` drops and re-creates each typed expression index over these methods, and
each index and exclusion constraint whose typed filter uses one of them. Before you apply the
migration, clean up the rows the new SQL rejects:

- **Values that differ only in tabs or line breaks at the ends.** A unique trimmed index rejects
  them now (23505). Those are the duplicates it was declared to reject.
- **Rows a typed filter now covers.** A unique index fails on their duplicates (23505), and an
  exclusion constraint on their overlaps (23P01). To find them, compare the filter's new SQL in the
  migration's `Up` with the old SQL in its `Down`, for example:

```sql
SELECT * FROM "docs"
WHERE (btrim("Code", E' \t\n\r') = 'abc') AND NOT (btrim("Code") = 'abc');
```

### Concatenation with a nullable operand

On PostgreSQL, a typed expression index or typed filter that concatenates an operand that can be
null wraps it in a `COALESCE` with an empty string, as EF Core's queries do. C# concatenates null as an empty
string, while PostgreSQL's `||` returns NULL: `x => x.Code + x.Suffix` on a `string? Suffix`
used to give `("Code" || "Suffix")`, which is NULL for every row without a suffix. No query over the
lambda used the index, and a unique one checked none of those rows. It is now
`("Code" || coalesce("Suffix", ''))`.

The first `migrations add` re-creates each typed expression index, and each index and exclusion
constraint with a typed filter, whose lambda concatenates an operand that can be null. A unique
index may then reject rows it let in, such as two rows with code `A` and no suffix. Those are the
duplicates it was declared to reject: clean them up before you apply the migration.

### Typed filters keep C#'s answer for null operands

On PostgreSQL, a typed filter whose comparison has an operand that can be null keeps C#'s answer
for the rows where it is null, written the way EF Core writes the same lambda in a query.
`x => x.Kind != "federated"` on a `string?` used to give `"Kind" <> 'federated'`, which is NULL for a
null `Kind`, so those rows fell out of the partial index, and a unique index didn't check
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
it fails if those rows hold duplicates. Those are duplicates it should have rejected.

### Typed filters keep braces in string literals

On PostgreSQL, a brace in a string literal of a typed filter is compared as written.
`x => x.Code != "{draft}"` used to give `"Code" <> '{{draft}}'`, a different value, so rows whose
code is `{draft}` were indexed, and constrained if the index is unique, although the filter excludes
them. It is now `"Code" <> '{draft}'`. Typed expression indexes were not affected.

The first `migrations add` re-creates each index and exclusion constraint whose typed filter has a
brace in a string literal.

### SQLite refuses a table rebuild that would drop complex indexes

On SQLite without `UseComplexIndexes()`, `dotnet ef migrations add` fails on a migration that
rebuilds a table with a complex index. SQLite rebuilds the table for any of these:

- an `ALTER COLUMN` or a `DROP COLUMN`;
- a change to a key, foreign key, unique or check constraint;
- a table comment, or a new column with a comment;
- a table rename, since EF renames the primary key with it.

EF Core then creates only its own indexes again. Every complex index on the table was gone, unique
ones included, while the migration ran without an error and the snapshot still listed them.

No operation in the migration can re-create them, because EF Core's SQLite generator runs every
rebuild last. The error names the indexes and two ways out: register `UseComplexIndexes()` after
`UseSqlite()` (see the next section), or split the change into three migrations: remove the
declarations on the table, make the change, then declare them again.

A SQLite database that went through such a migration before is missing those indexes while its
snapshot lists them. Check with `SELECT name FROM sqlite_master WHERE type = 'index'`, and re-create
any missing one with `migrationBuilder.Sql(…)` in a new migration.

### SQLite re-creates complex indexes after a table rebuild

With `UseComplexIndexes()` called after `UseSqlite()` on the context `dotnet ef` uses,
`migrations add` no longer fails on the migration from the previous section, and writes it. The
migration drops each complex index on the rebuilt
table, unchanged ones included, and creates it again, marked with the
`CustomIndex:AfterTableRebuild` annotation and a call to `__requires_UseComplexIndexes__()` ANDed
onto its filter. The SQL generator `UseComplexIndexes()` registers moves those creates behind the
rebuild and removes the marker. TPC copies of a base's index are covered like any other.

The migration needs that generator wherever it is applied: `Migrate()`, `dotnet ef database update`
and `dotnet ef migrations script` use the context's own services. Applied without it, the first
create fails with `no such function: __requires_UseComplexIndexes__`, before the rebuild, and the
transaction leaves the table as it was. Don't edit the marker: the generator throws on a filter it
didn't write.

`migrations add` still fails when `UseComplexIndexes()` is called before `UseSqlite()`, or when
another package replaces `IMigrationsSqlGenerator` without naming SQLite's generator as the one it
replaces (`ReplaceService<IMigrationsSqlGenerator, TNew>()`), since EF Core keeps only one
replacement. That holds even when `TNew` derives from SQLite's generator. The error names the
generator in effect. A replacement keyed on SQLite's generator
(`ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator, TNew>()`), or a provider
extension registered before `UseComplexIndexes()`, is wrapped and keeps working.

### NULLS ordering and expression parts fail on SQLite

On SQLite and any other provider without a package of its own here, a complex index with a
`DbOrder.NullsFirst`/`DbOrder.NullsLast` part or an expression part (`HasExpressionIndex` from the
PostgreSQL package, in a model shared with SQLite tests) fails at `migrations add`, `EnsureCreated()`
and `GenerateCreateScript()`. Only the PostgreSQL package's generator writes those parts. The
migration used to carry a placeholder column, which fails with the SQLite bundled with
Microsoft.Data.Sqlite. Where SQLite accepts double-quoted strings as literals, as a default build does
(a system `libsqlite3`, other SQLitePCLRaw bundles), the placeholder and the expression were read as
strings. The index was then created on a constant, without its ordering or expression.

Remove the marker, or the expression index, and add a migration. It drops the index and creates it
from its columns. Check with `SELECT sql FROM sqlite_master WHERE type = 'index'`: an index whose
definition names `__requires_UseNpgsqlComplexIndexes__` came from such a migration.

On SQL Server and PostgreSQL, the core differ refuses the same parts when it runs in place of the
provider package's: under `UseComplexIndexes()`, or in a `dotnet ef` run without the provider
package's `.targets`. The migration used to carry the same placeholder column and failed when
applied, naming `UseNpgsqlComplexIndexes()` even on SQL Server. Only one setup applied the index,
and correctly: `UseComplexIndexes()` called after `UseNpgsqlComplexIndexes()`, which runs the core
differ with the PostgreSQL package's generator. That setup now fails too, in
`EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`, whose pending-model-changes check runs
the same differ. Nothing is re-created.

On PostgreSQL, call `UseNpgsqlComplexIndexes()` and not `UseComplexIndexes()`. SQL Server can't
express these parts, and its package refuses them too: remove the marker, or index a computed
column that holds the expression.

### The core differ refuses SQL Server and PostgreSQL index options

On SQL Server and PostgreSQL, the core package's migration code (the core differ) rejects a
complex index that carries one of that provider's index options, such as `IsClustered()`,
`HasFillFactor(…)`, `IncludeProperties(…)` or `UseGin()`. The error names the call the provider's
package needs.

The core differ runs in place of the provider package's when an application calls
`UseComplexIndexes()` instead of `UseSqlServerComplexIndexes()` or `UseNpgsqlComplexIndexes()`, or
when `dotnet ef` runs without the provider package's `.targets`. It passes on no property-level
option, so `EnsureCreated()` created such an index without its options, and without an error.
Entity-level options went through without their checks, and an `INCLUDE` list kept its property
paths as column names.

Nothing is re-created. An application with that registration now fails in `EnsureCreated()`,
`GenerateCreateScript()` and `Migrate()`, whose pending-model-changes check runs the same differ.
Replace `UseComplexIndexes()` with the provider package's method. A database `EnsureCreated()` built that
way has the indexes without their options: drop and re-create them, or rebuild the database. A model
shared with SQLite tests is unaffected, since the check covers only the provider the context uses.

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
Replace `UseComplexIndexes()` with the provider package's method. Nothing is re-created, and migrations
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
`DROP COLUMN`. PostgreSQL drops an index with a column it includes, so a unique index disappeared
while the migration ran without an error. The snapshot still listed it, so no later migration
re-created it. SQL Server rejected the drop (Msg 5074).

With `UseNpgsqlComplexIndexes()` or `UseSqlServerComplexIndexes()`, an application with such a model
also fails in `EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`, whose pending-model-changes
check runs the same differ. Remove the entry, or name a property. An entry naming a column that exists in the database but not
in the model fails too: map the column, as a shadow property if nothing reads it. A snapshot that
holds such an entry still works, since the check covers your current model only. On PostgreSQL, check a database that a migration of that shape
reached with `SELECT indexname FROM pg_indexes WHERE tablename = '<table>'`. The migrations do not
know the index is missing, so re-create it by hand with `migrationBuilder.Sql(…)`.

## Changed answers in 5.6.0

These change only migrations that rename a column, change a generated column, move a table into the
default schema, or change a column's default or comment on SQL Server. A model you have not changed
writes no migration, and the snapshot stays as it is. One of them, on PostgreSQL, also
asks you to check your database for objects an earlier migration dropped.

### A column rename keeps what reads the column

When you rename a column, the migration now renames the column and keeps the complex indexes,
exclusion constraints and temporal constraints that read it. PostgreSQL and SQLite keep them through
the rename, and so does SQL Server unless the index has a filter. Before, `migrations add` dropped
each of them before the rename and created it again after. On a large table, that rebuild locked the
table until the migration finished.

A default name includes the column, so it changes with it. The migration renames that index or
constraint, `IX_members_email` to `IX_members_email_address` for example. On SQLite, which cannot
rename these indexes, the migration drops and re-creates it.

Good to know:

- **On SQL Server, an index whose filter reads the column is still re-created.** SQL Server rejects
  the rename while such an index exists ("The index … is dependent on column …"), so the migration
  drops it before the rename and creates it after. That includes the `IS NOT NULL` filter a unique
  index over a nullable column gets. EF Core does the same for its own filtered indexes.
- **SQL you wrote as a string is compared as written.** If you change `lower(code)` to
  `lower(member_code)` in an expression index or a filter along with the rename, the migration
  drops and re-creates that index. A `{Code}` placeholder or a typed lambda follows the rename
  without a rebuild.
- **An `IncludeProperties(…)` entry written as a column name** has to take the new name too.
  Otherwise `migrations add` fails and names the entry ([INCLUDE entries must name a column](#include-entries-must-name-a-column)).
- **Anything else that changed** in the same migration still drops and re-creates the index. The
  drop runs before the rename and the create after it.

### A changed generated column keeps what reads it

On PostgreSQL, when you change a generated column's expression (`HasComputedColumnSql`), whether it
is stored, or a `HasGeneratedTsVectorColumn` configuration, the migration now drops the complex
indexes, exclusion constraints, temporal constraints and temporal foreign keys on that column before
the change and adds them again after it. PostgreSQL cannot change a generation expression in place,
so Npgsql drops the column and adds it again, which takes everything on the column with it. Npgsql
then re-creates EF Core's own indexes only. Before, the migration ran without an error and left this
package's indexes and constraints out. The snapshot still listed them, so no later migration added
them back. A generated range column used as the period of an exclusion or temporal constraint is
the usual case.

Check a database that ran such a migration. List what the table has:

```sql
SELECT indexname FROM pg_indexes WHERE tablename = '<table>';
SELECT conname FROM pg_constraint WHERE conrelid = '<table>'::regclass;
```

Compare it with what your model declares on the table. To add what is missing, take the statements
from the script `GenerateCreateScript()` writes with `UseNpgsqlComplexIndexes()`, and put them in a
new migration with `migrationBuilder.Sql(…)`. An exclusion or temporal constraint fails to add over
rows that overlap (23P01). Find and fix those rows first.

Good to know:

- **A column that becomes generated, or stops being one,** takes the same path.
- **A temporal foreign key** goes with the constraint it references when that constraint is rebuilt,
  and is added back after it.

### A move into the default schema keeps what is on the table

When you move a table into the default schema, `ToTable("bookings", "north")` to
`ToTable("bookings")` for example, the migration now keeps the complex indexes, exclusion
constraints, temporal constraints and temporal foreign keys on it. Before, `migrations add` dropped
each of them before the move and created it again after. On SQL Server that rebuilt every index for
nothing, since `ALTER SCHEMA … TRANSFER` keeps a table's indexes. Moves between two named schemas,
or out of the default schema, already kept them.

On PostgreSQL the migration failed when it ran (42P01). Npgsql 10 writes no statement for a move
into the default schema, so the table stays where it is, and the indexes were created on a table of
that name in the default schema, which does not exist. The migration now runs without an error, but
the table still stays in its old schema. To move it, name the schema: `ToTable("bookings", "public")`.

### A default or comment change keeps the index on SQL Server

On SQL Server, when you change only a column's default (`HasDefaultValue`, `HasDefaultValueSql`) or
its comment (`HasComment`), the migration now keeps the complex indexes on that column. EF Core
writes a default as `DROP CONSTRAINT` plus `ADD DEFAULT` and a comment as `sp_addextendedproperty`,
neither of which touches an index. Before, `migrations add` dropped each complex index on the column
before the change and created it again after. On every edition but Enterprise that rebuild runs
offline and locks the table.

A change that EF Core writes as `ALTER COLUMN` still rebuilds the index, because SQL Server rejects
it while the index exists (Msg 5074): the type, length, precision, nullability, collation, Unicode
or fixed length, and a computed column's expression.
