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

Tests run in parallel at the method level (`Scope = ExecutionScope.MethodLevel`). The
`PostgresIntegrationTests` class (`[TestCategory("Integration")]`, `[DoNotParallelize]`) spins up a
PostgreSQL 18 Testcontainer and applies generated DDL for real; without Docker it needs excluding
via `--filter "TestCategory!=Integration"`.

## Architecture

This library fills a gap in EF Core 10.0 migrations: EF Core can model complex properties (value objects) but does not generate migration SQL for indexes on their nested columns. This library hooks into EF Core's design-time pipeline to produce correct `CREATE INDEX` / `DROP INDEX` SQL.

### Solution layout

| Project | Purpose |
|---------|---------|
| `EFCore.ComplexIndexes` | Core library — provider-agnostic fluent API and migration differ |
| `EFCore.ComplexIndexes.PostgreSQL` | Satellite package — Npgsql index methods (GIN, GiST, BRIN, Hash, SP-GiST), expression/JSON/LINQ indexes, temporal + exclusion constraints |
| `EFCore.ComplexIndexes.SqlServer` | Satellite package — clustered/covering/online/fill-factor options; rejects expression parts and NULLS ordering with clear errors |
| `EFCore.ComplexIndexes.Tests` | MSTest suite covering path extraction, serialization, and migration diffing |

Shared NuGet metadata and the package version live in `Directory.Build.props`.

### How it works end-to-end

1. **Fluent API** (`ComplexIndexExtensions.cs`) — User calls `.HasComplexIndex(...)` or `.HasComplexCompositeIndex(x => new { x.Prop, x.Complex.Nested })` in `OnModelCreating`. These methods store all index metadata as EF Core annotations on the property or entity.

2. **Annotation storage** — `ComplexIndexAnnotations.cs` defines the annotation key constants. Composite index definitions are JSON-serialized via `CompositeIndexSerializer` and stored as a single annotation on the entity type.

3. **Design-time service injection** — Each project ships a `.targets` file (under `build/`) that injects a `DesignTimeServicesReferenceAttribute` into the consuming assembly at compile time. EF Core's design-time host discovers this attribute and instantiates the custom `IDesignTimeServices`, which replaces the default `IMigrationsModelDiffer`.

4. **Migration differ** (`CustomMigrationsModelDiffer.cs`) — Extends `MigrationsModelDiffer`. During `dotnet ef migrations add`, it recursively walks entity type annotations and complex type properties to find index annotations, resolves the actual database column names (respecting both convention-based naming like `Origin_Source` and explicit `HasColumnName` overrides), and emits `CreateIndexOperation` / `DropIndexOperation`. **Operation ordering is load-bearing**: custom drops go *before* the base operations, creates after — an index moving between native `HasIndex` and a complex-index declaration produces a base create plus our same-named drop, and only this order keeps the scaffold from colliding at apply time (the temporal/exclusion differs follow the same rule).

5. **PostgreSQL satellite** (`NpgsqlComplexIndexMigrationsModelDiffer.cs`) — Extends the core differ, validates Npgsql-specific annotations, and normalizes JSON element annotations before passing operations upstream.

### Annotation forwarding is a whitelist

Property-level annotations reach the `CreateIndexOperation` only through
`IsForwardedIndexAnnotation` (virtual on the core differ, default **nothing**; the Npgsql differ
whitelists exactly its seven `Npgsql:*` index-option keys). Never revert to sweeping "everything
except known keys": column facets (`Relational:ColumnName`, `Relational:ColumnType`, …) leaked into
scaffolded migrations that way, and snapshot/code-model asymmetries caused phantom drop/create
churn (see `PhantomIndexChurnTests`).

### Diff polish: renames and the wiring sentinel

The differ recognizes two churn cases: a drop/create pair identical except for the name becomes a
`RenameIndexOperation` when `CanRenameIndexes` is true (Npgsql and SqlServer satellites; core
default false — SQLite's generator can't rename annotation-only indexes), and source indexes on
tables the base operations rename are compared under their new table identity (drops still target
the old name, since they run before the rename). Indexes that need the custom generator
(`RequiresPartsAnnotation`) get `CustomMigrationsModelDiffer.RuntimeWiringSentinel` appended to
`Columns`: the custom generator renders from the parts annotation and ignores `Columns`, while the
stock generator fails loudly at apply time — deliberate, because a column-only NULLS-ordered index
would otherwise apply silently minus its NULLS clause. INCLUDE lists are transformed via
`TransformIndexAnnotation` → `ResolveIncludeList`: entries resolve as property paths with verbatim
column-name fallback.

### Index identity and dedup

`ComplexIndexStorage.AddOrReplace` is the single store for entity-level definitions: same ordered
parts (direction ignored) + same filter → replace (re-declaring updates); same parts +
*different* filter → coexisting partial indexes, both of which must be explicitly named (default
names would collide). Single-column indexes exist in two forms: property-level (one per property,
stored as property annotations) and entity-level `HasComplexIndex(x => x.Complex.Prop, …)`
(stored as a one-part composite definition — use this for multiple filtered indexes per column).

### Two integration seams: design-time vs. runtime

There are two distinct hook points, and it matters which one a feature uses:

- **Design-time** (`IDesignTimeServices` via the `.targets`-injected attribute) replaces `IMigrationsModelDiffer`. This runs during `dotnet ef migrations add` and is auto-wired — consumers do nothing.
- **Runtime** (`IMigrationsSqlGenerator`) converts operations to SQL when migrations are *applied*. This is **not** auto-wired; consumers opt in with `optionsBuilder.UseNpgsqlComplexIndexes()` (a `ReplaceService` helper).

Most index metadata (GIN/operators/include/etc.) flows as *real Npgsql annotation keys* (`Npgsql:IndexMethod`, …) on the `CreateIndexOperation`, so Npgsql's own runtime SQL generator renders it — this package never touches SQL generation for those. Expression indexes are the exception (see below).

### Expression indexes (`HasExpressionIndex`)

Expression indexes are **provider-specific** and deliberately live in the satellite, not core: PostgreSQL/SQLite render `CREATE INDEX … ((expr))` natively, but SQL Server has no functional-index DDL (it models the same intent via persisted computed columns). Exposing the API in provider-agnostic core would be a false promise — a SQL Server consumer could call it and get a `CreateIndexOperation` with empty `Columns` that the stock generator can't render. So:

- The **entry point** `HasExpressionIndex` (on `EntityTypeBuilder<TEntity>`) lives in `EFCore.ComplexIndexes.PostgreSQL` (`NpgsqlExpressionIndexExtensions.cs`), as does its `ExpressionIndexBuilder`.
- Core owns only the inert **plumbing**: the `IIndexExpression` seam (`SqlIndexExpression` ships today; a future LINQ add-on plugs in here), `IndexPartDefinition`/`ResolvedIndexPart`/`IndexPartsSerializer`, `CompositeIndexDefinition.Parts`, the differ's part-handling, and the `ComplexIndexStorage` helper satellites call to dedup-and-store definitions. None of it activates unless a satellite populates it.

Each column-list entry is a "part"; an index is an ordered list of parts. Verbatim string parts are emitted as-is (no property→column resolution).

An `IndexPartDefinition` is exactly one of three kinds: a **column** (`PropertyPath`, resolved to a
column name), a **verbatim expression** (`Expression`, emitted as-is), or a **template**
(`Template`, produced by `NpgsqlLinqIndexTranslator` from a typed lambda — SQL with
`{Property.Path}` placeholders, literal braces escaped `{{`/`}}`). Three virtuals on the core
differ let satellites resolve what the core cannot:

- `IsForwardedIndexAnnotation` — the annotation whitelist (see below).
- `ResolveUnmappedPart` — a path with no table column; the Npgsql differ builds a JSON extraction
  (`"col" -> 'A' ->> 'B'`) when the path traverses a `ToJson()` complex property, honoring
  `HasJsonPropertyName`. Members extract as text — no automatic casts (text→timestamptz casts are
  not IMMUTABLE and would blow up `CREATE INDEX`).
- `ResolveTemplatePart` — substitutes template placeholders with quoted columns or parenthesized
  JSON extractions; core throws (identifier quoting is provider-specific).

`NULLS FIRST/LAST` (`DbOrder.NullsFirst/NullsLast`, `ExpressionIndexBuilder.NullsFirst()/NullsLast()`)
rides on the parts as `NullSort`. EF's native `CreateIndexOperation` has no slot for it, so any
index containing a nulls-ordered part is routed through the `IndexParts` annotation and the custom
Npgsql generator — i.e. it needs `UseNpgsqlComplexIndexes()` even when column-only. The SQL Server
differ rejects nulls-ordered and expression parts with targeted errors.

`CreateIndexOperation.Columns` is a `string[]` of quoted identifiers with no slot for an expression, so:
- The differ stamps the ordered, resolved parts onto the operation as the `CustomIndex:IndexParts` annotation (`ResolvedIndexPart` + `IndexPartsSerializer`), **only when a part is an expression** (column-only indexes are untouched).
- `NpgsqlComplexIndexSqlGenerator` (extends `NpgsqlMigrationsSqlGenerator`) overrides `Generate(CreateIndexOperation, …)`: if that annotation is present it renders the full `CREATE INDEX` itself (column parts quoted, expression parts wrapped in parens, reusing the forwarded Npgsql annotations for `USING`/`INCLUDE`/`NULLS NOT DISTINCT`/etc.); otherwise it delegates to `base`. This requires the runtime `UseNpgsqlComplexIndexes()` wiring.

`CompositeIndexDefinition` carries the ordered parts additively via `Parts` (with `EffectiveParts` falling back to the legacy `PropertyPaths` field) so migration snapshots written before expression support still deserialize.

### Exclusion constraints (`HasExclusionConstraint`)

PostgreSQL-only (`NpgsqlExclusionConstraintExtensions.cs`), the answer to "temporal uniqueness with
a filter": PostgreSQL's `ADD CONSTRAINT UNIQUE/PRIMARY KEY` grammar never accepts `WHERE`, only
EXCLUDE constraints do. Definitions (`ExclusionConstraintDefinition`: ordered parts, each a
property path *or* verbatim expression plus an operator; method defaulting to gist; filter; name;
deferrability) are JSON-stored under `CustomExclusion:Constraints`. Unlike expression indexes, the
differ renders the full `ALTER TABLE … ADD CONSTRAINT … EXCLUDE …` / `DROP CONSTRAINT` DDL as
`SqlOperation`s at **design time** — no runtime `UseNpgsqlComplexIndexes()` wiring involved. The
`btree_gist` auto-injection is shared with temporal constraints (single `CREATE EXTENSION`, same
`UseBtreeGist()` / `SuppressTemporalExtensionAutoInjection()` switches) and triggers when a gist
constraint has an `=` element.

### Key extension points

- **Adding a new provider**: Subclass `CustomMigrationsModelDiffer` (override `IsForwardedIndexAnnotation`, optionally `ResolveUnmappedPart`/`ResolveTemplatePart`), implement `IDesignTimeServices` to replace the differ, and ship a `.targets` file that injects the attribute. The PostgreSQL project is the full-featured reference; the SQL Server project is the minimal one (whitelist + validation, no custom SQL generator).
- **New index options**: Add constants to `ComplexIndexAnnotations.cs` (or `NpgsqlAnnotations.cs`), expose them via `ComplexIndexBuilder`, and read them in the differ when constructing `CreateIndexOperation`.

### Expression path extraction

`ComplexIndexExtensions` parses anonymous-type lambda expressions (`x => new { x.Name, x.Address.City }`) by recursively walking `MemberExpression` chains to produce dotted property paths. These paths are then matched against the EF Core metadata model to resolve column names.

### Per-column sort direction (`DbOrder.Asc`/`DbOrder.Desc`)

`DbOrder.Asc`/`Desc` are identity marker functions; `ExtractSinglePart` peels them (and `Convert` boxing) off the expression in any order to record a `Descending` flag per part. Unlike expression indexes, descending columns are **provider-agnostic and need no satellite work**: the differ maps direction onto the native `CreateIndexOperation.IsDescending` (`bool[]`), which every relational provider renders. The differ leaves `IsDescending` **null** when all parts are ascending, so existing ascending indexes don't churn. To avoid snapshot churn, `HasComplexCompositeIndex` keeps writing the legacy `PropertyPaths` form when every column is ascending and only switches to the ordered `Parts` form when a descending column is present. Note: wrapping a member in `DbOrder.Desc(...)` makes it a method call, so C# requires naming it in the anonymous type (`new { x.A, B = DbOrder.Desc(x.B) }`).
