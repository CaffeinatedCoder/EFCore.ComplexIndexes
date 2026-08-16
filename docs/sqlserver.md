# SQL Server

Provided by the **EFCore.ComplexIndexes.SqlServer** package. The core package is included
automatically, and there is **no runtime wiring at all** — every option flows as a native SQL Server
annotation that the provider's own migrations SQL generator renders.

## Index options

The **EFCore.ComplexIndexes.SqlServer** package brings the SQL Server option set to complex-property
indexes. Like the PostgreSQL GIN/GiST options, everything flows as native provider annotations that
SQL Server's own migrations SQL generator renders:

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

`IsClustered()`, `SortInTempDb()`, and `UseDataCompression(DataCompressionType.Page)` are also
available. Filtered indexes (`filter:`) and `DbOrder.Desc` work out of the box, since both ride on
EF's native operation.

`IncludeProperties(...)` entries are resolved as property paths — complex members included — with a
verbatim column-name fallback, so `IncludeProperties("Email.Value")` finds the real column.

## Deliberate rejections

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
