# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build EFCore.ComplexIndexes.slnx

# Test
dotnet test EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj

# Run a single test class
dotnet test --filter "ClassName=MigrationModelDifferTests"

# Run a single test method
dotnet test --filter "FullyQualifiedName~MigrationModelDifferTests.SingleIndex_IsCreated"

# Pack NuGet packages (also runs on build due to GeneratePackageOnBuild=true)
dotnet pack EFCore.ComplexIndexes/EFCore.ComplexIndexes.csproj
```

Tests run in parallel at the method level (`Scope = ExecutionScope.MethodLevel`).

## Architecture

This library fills a gap in EF Core 10.0 migrations: EF Core can model complex properties (value objects) but does not generate migration SQL for indexes on their nested columns. This library hooks into EF Core's design-time pipeline to produce correct `CREATE INDEX` / `DROP INDEX` SQL.

### Solution layout

| Project | Purpose |
|---------|---------|
| `EFCore.ComplexIndexes` | Core library — provider-agnostic fluent API and migration differ |
| `EFCore.ComplexIndexes.PostgreSQL` | Satellite package — adds Npgsql-specific index methods (GIN, GiST, BRIN, Hash, SP-GiST) |
| `EFCore.ComplexIndexes.Tests` | MSTest suite covering path extraction, serialization, and migration diffing |

Shared NuGet metadata and the package version live in `Directory.Build.props`.

### How it works end-to-end

1. **Fluent API** (`ComplexIndexExtensions.cs`) — User calls `.HasComplexIndex(...)` or `.HasComplexCompositeIndex(x => new { x.Prop, x.Complex.Nested })` in `OnModelCreating`. These methods store all index metadata as EF Core annotations on the property or entity.

2. **Annotation storage** — `ComplexIndexAnnotations.cs` defines the annotation key constants. Composite index definitions are JSON-serialized via `CompositeIndexSerializer` and stored as a single annotation on the entity type.

3. **Design-time service injection** — Each project ships a `.targets` file (under `build/`) that injects a `DesignTimeServicesReferenceAttribute` into the consuming assembly at compile time. EF Core's design-time host discovers this attribute and instantiates the custom `IDesignTimeServices`, which replaces the default `IMigrationsModelDiffer`.

4. **Migration differ** (`CustomMigrationsModelDiffer.cs`) — Extends `MigrationsModelDiffer`. During `dotnet ef migrations add`, it recursively walks entity type annotations and complex type properties to find index annotations, resolves the actual database column names (respecting both convention-based naming like `Origin_Source` and explicit `HasColumnName` overrides), and emits `CreateIndexOperation` / `DropIndexOperation`.

5. **PostgreSQL satellite** (`NpgsqlComplexIndexMigrationsModelDiffer.cs`) — Extends the core differ, validates Npgsql-specific annotations, and normalizes JSON element annotations before passing operations upstream.

### Key extension points

- **Adding a new provider**: Subclass `CustomMigrationsModelDiffer`, implement `IDesignTimeServices` to replace the differ, and ship a `.targets` file that injects the attribute. See the PostgreSQL project for the exact pattern.
- **New index options**: Add constants to `ComplexIndexAnnotations.cs` (or `NpgsqlAnnotations.cs`), expose them via `ComplexIndexBuilder`, and read them in the differ when constructing `CreateIndexOperation`.

### Expression path extraction

`ComplexIndexExtensions` parses anonymous-type lambda expressions (`x => new { x.Name, x.Address.City }`) by recursively walking `MemberExpression` chains to produce dotted property paths. These paths are then matched against the EF Core metadata model to resolve column names.
