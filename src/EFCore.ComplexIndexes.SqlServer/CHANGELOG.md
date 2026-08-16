# EFCore.ComplexIndexes.SqlServer — changelog

Changes to the SQL Server satellite, newest first. The
[root changelog](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/CHANGELOG.md)
covers all three packages.

## 5.0.3

- **Changed:** the `Microsoft.EntityFrameworkCore.SqlServer` dependency is now `[10.0.0, 11.0.0)`.
  This differ extends EF internals that carry no cross-major compatibility promise. Nothing changes
  if you are on EF Core 10: NuGet resolves the lowest version in a range.
- **New:** the public API is fully documented.

## 5.0.2

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

## 5.0.0

- **New:** the package — clustered, covering (`INCLUDE`), online-built, fill-factor, and
  sort-in-tempdb options on complex-property indexes, plus clear errors for expression parts and
  `NULLS FIRST`/`LAST`.
