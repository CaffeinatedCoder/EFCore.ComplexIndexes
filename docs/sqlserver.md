# SQL Server

The **EFCore.ComplexIndexes.SqlServer** package lets you set SQL Server's index options on indexes
over complex properties. It brings the core package with it. Migrations need **no call in your
context's configuration**: every option goes into the migration as a SQL Server annotation that EF Core's own
SQL Server generator writes.

One call is optional. `UseSqlServerComplexIndexes()` registers the package on your context, so
`Database.EnsureCreated()` and `GenerateCreateScript()` create your complex indexes too. The
pending-model-changes check in `Migrate()` then also notices a complex index that no migration has
created yet. See
[the runtime wiring section](../README.md#ensurecreated-generatecreatescript-and-the-pending-changes-check)
in the root README.

## Index options

```csharp
builder.ComplexProperty(x => x.Email, c =>
    c.Property(x => x.Value).HasColumnName("email"));

builder.HasComplexIndex(x => x.Email.Value, ix => ix
    .IsUnique()
    .HasName("ux_person_email")
    .IncludeProperties("name")   // covering index
    .IsCreatedOnline()           // ONLINE = ON
    .HasFillFactor(80));
// CREATE UNIQUE INDEX [ux_person_email] ON [person] ([email])
//   INCLUDE ([name]) WITH (FILLFACTOR = 80, ONLINE = ON);
```

The builder also has `IsClustered()`, `SortInTempDb()` and
`UseDataCompression(DataCompressionType.Page)`.

Good to know:

- **Filters and sort direction need nothing from this package.** Filtered indexes (`filter:`) and
  `DbOrder.Desc` are part of EF Core's own index operation.
- **A unique index over nullable columns gets `WHERE [c] IS NOT NULL`**, as EF Core's own unique
  indexes do, since SQL Server treats two NULLs as duplicates in a unique index. The filter covers
  the nullable key columns. It isn't added to a clustered index, or to one you declared with a
  filter (since 5.5.0).
- **A filter can name properties.** `migrations add` turns `filter: "{DeletedAt} IS NULL"` into
  `[deleted_at] IS NULL`, in brackets, and writes it into the migration.
- **`IncludeProperties(...)` takes property paths**, complex members included, and also accepts a
  column name as written. So `IncludeProperties("Email.Value")` finds the real column. An entry
  that is neither a property path nor a column of the table makes `migrations add` fail.

## Deliberate rejections

If you declare something SQL Server can't express, `dotnet ef migrations add` fails and names the
problem, instead of writing a migration that would fail when it runs:

- **Expression parts.** SQL Server has no DDL for an index on an expression. Store the expression in
  a persisted computed column and index that column instead.
- **`DbOrder.NullsFirst` / `NullsLast`.** T-SQL has no `NULLS FIRST`/`NULLS LAST`.
- **A clustered index with `INCLUDE` columns.** Included columns belong to nonclustered indexes. A
  clustered index already stores every column.
- **A clustered filtered index.** A filtered index must be nonclustered.
- **A second clustered index on a table.** The other one can be a complex index, declared in the
  same migration or an earlier one. It can be an EF Core index, a unique constraint, or, most often,
  the primary key.
  SQL Server clusters the primary key unless you declare `HasKey(...).IsClustered(false)`, so that is
  normally what a clustered complex index collides with.
- **A complex index named like its table's primary key or one of its unique constraints.** SQL
  Server creates an index under each of those names. The comparison ignores case, as the default
  collation does.
