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

None. Every option flows as a native SQL Server annotation that the provider's own migrations SQL
generator renders, so there is **no runtime wiring at all** — install the package, declare your
indexes, and run `dotnet ef migrations add`.

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

## Changelog

### 5.0.2

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

### 5.0.0

- **New:** the package — clustered, covering (`INCLUDE`), online-built, fill-factor, and
  sort-in-tempdb options on complex-property indexes, plus clear errors for expression parts and
  `NULLS FIRST`/`LAST`.

---

Full documentation: **https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes**

MIT licensed.
