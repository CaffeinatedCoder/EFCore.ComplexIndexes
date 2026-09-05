# EFCore.ComplexIndexes.SqlServer

SQL Server index options for
[EFCore.ComplexIndexes](https://www.nuget.org/packages/EFCore.ComplexIndexes/). The core package is
included automatically.

Brings the SQL Server option set to complex-property indexes:

- **Clustered / nonclustered** control
- **Covering indexes** (`INCLUDE`)
- **Online index builds** (`ONLINE = ON`)
- **Fill factor**
- **Sort in tempdb**
- **Data compression** (`ROW` / `PAGE`)

---

## Setup

None for migrations. Every option flows as a native SQL Server annotation that the provider's own
migrations SQL generator renders — install the package, declare your indexes, and run
`dotnet ef migrations add`.

One optional call exists. `Database.EnsureCreated()`, `GenerateCreateScript()` and the
pending-model-changes check in `Migrate()` run the *runtime* differ, which the design-time wiring never
reaches; without a registration, `EnsureCreated()` creates the tables and silently none of the
indexes. Register the differ once so those see the complex indexes too:

```csharp
options.UseSqlServer(connectionString).UseSqlServerComplexIndexes();
```

`AddSqlServerComplexIndexes()` is the equivalent for a custom internal service provider.

## Usage

```csharp
builder.ComplexProperty(x => x.Email, c =>
    c.Property(x => x.Value).HasColumnName("email"));

builder.HasComplexIndex(x => x.Email.Value, ix => ix
    .IsUnique()
    .HasName("ux_person_email")
    .IncludeProperties("Name")     // property paths resolve to columns
    .IsCreatedOnline()             // ONLINE = ON
    .HasFillFactor(80));
// CREATE UNIQUE INDEX [ux_person_email] ON [person] ([email])
//   INCLUDE ([name]) WITH (FILLFACTOR = 80, ONLINE = ON);
```

Also available: `IsClustered()`, `SortInTempDb()`, and
`UseDataCompression(DataCompressionType.Page)`.

Filtered indexes (`filter:`) and `DbOrder.Desc` need nothing from this package — both ride on EF
Core's native index operation.

A filter may name properties instead of columns: `filter: "{DeletedAt} IS NULL"` resolves to
`[deleted_at] IS NULL` at `migrations add`.

### Deliberate rejections

Declarations SQL Server cannot express fail at `dotnet ef migrations add` with a targeted error
rather than producing DDL that cannot apply:

- **Expression parts** — SQL Server has no functional-index DDL. Model the expression as a persisted
  computed column and index that column instead.
- **`DbOrder.NullsFirst` / `NullsLast`** — there is no `NULLS FIRST`/`NULLS LAST` in T-SQL.
- **Clustered index with `INCLUDE` columns** — included columns are a nonclustered-index feature; a
  clustered index already stores every column.
- **Clustered filtered index** — filtered indexes must be nonclustered.
- **A second clustered index on a table** — including the usual case, where the primary key already
  holds the clustered slot. SQL Server clusters the primary key unless you declare
  `HasKey(...).IsClustered(false)`, so that is normally what a clustered complex index collides with.

---

## Documentation

- [SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)
  — the full index-option reference and every deliberate rejection
- [Full documentation](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes)

## Changelog

[This package's changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md),
or the [root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covering all three packages.

MIT licensed.
