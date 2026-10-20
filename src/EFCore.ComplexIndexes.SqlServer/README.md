# EFCore.ComplexIndexes.SqlServer

> **On EF Core 11 or later?** This package supports EF Core 10 only. From 5.5.0 it throws when you
> register it on any other major version. Its successor for EF Core 11 is a new package,
> `CodoMetis.EFCore.SchemaKit`, tracked in
> [#37](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/37).

SQL Server index options for
[EFCore.ComplexIndexes](https://www.nuget.org/packages/EFCore.ComplexIndexes/). This package brings
the core package with it.

You get SQL Server's index options on indexes over complex properties:

- **Clustered / nonclustered** control
- **Covering indexes** (`INCLUDE`)
- **Online index builds** (`ONLINE = ON`)
- **Fill factor**
- **Sort in tempdb**
- **Data compression** (`ROW` / `PAGE`)

---

## Setup

Migrations need no setup. Every option goes into the migration as a SQL Server annotation that EF
Core's own SQL Server generator writes. Install the package, declare your indexes, and run
`dotnet ef migrations add`.

There is one optional call. `Database.EnsureCreated()`, `GenerateCreateScript()` and the
pending-model-changes check in `Migrate()` use your context's own services, which the automatic
design-time registration never reaches. Without a registration, `EnsureCreated()` creates the tables
and none of the indexes, with no error. Register the package once so these see your complex indexes
too:

```csharp
options.UseSqlServer(connectionString).UseSqlServerComplexIndexes();
```

If you build your own internal service provider, call `AddSqlServerComplexIndexes()` on your
`IServiceCollection` instead.

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

The builder also has `IsClustered()`, `SortInTempDb()` and
`UseDataCompression(DataCompressionType.Page)`.

Filtered indexes (`filter:`) and `DbOrder.Desc` need nothing from this package, because EF Core's own
index operation carries both.

A filter can name properties instead of columns: `migrations add` turns
`filter: "{DeletedAt} IS NULL"` into `[deleted_at] IS NULL`.

### Deliberate rejections

If you declare something SQL Server can't express, `dotnet ef migrations add` fails and names the
problem, instead of writing a migration that would fail when it runs:

- **Expression parts.** SQL Server has no DDL for an index on an expression. Store the expression in
  a persisted computed column and index that column instead.
- **`DbOrder.NullsFirst` / `NullsLast`.** T-SQL has no `NULLS FIRST`/`NULLS LAST`.
- **A clustered index with `INCLUDE` columns.** Included columns belong to nonclustered indexes. A
  clustered index already stores every column.
- **A clustered filtered index.** A filtered index must be nonclustered.
- **A second clustered index on a table.** Usually the primary key already holds the clustered slot:
  SQL Server clusters the primary key unless you declare `HasKey(...).IsClustered(false)`. So that is
  normally what a clustered complex index collides with.

---

## Documentation

- [SQL Server](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/sqlserver.md)
  — every index option and the declarations SQL Server can't express
- [Full documentation](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes)

## Changelog

[This package's changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/src/EFCore.ComplexIndexes.SqlServer/CHANGELOG.md),
or the [root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covering all three packages.

MIT licensed.
