# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build EFCore.ComplexIndexes.slnx

# Test
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj

# Run a single test class
dotnet test --filter "ClassName=MigrationModelDifferTests"

# Run a single test method
dotnet test --filter "FullyQualifiedName~MigrationModelDifferTests.SingleIndex_IsCreated"

# Pack NuGet packages (also runs on build due to GeneratePackageOnBuild=true)
dotnet pack src/EFCore.ComplexIndexes/EFCore.ComplexIndexes.csproj
```

Tests run in parallel at the method level (`Scope = ExecutionScope.MethodLevel`). The
`PostgresIntegrationTests` class (`[TestCategory("Integration")]`, `[DoNotParallelize]`) spins up a
PostgreSQL 18 Testcontainer and applies generated DDL for real; without Docker it needs excluding
via `--filter "TestCategory!=Integration"`.

## Quality controls

The signature failure here is a migration that scaffolds *and applies* cleanly while being
semantically wrong — no exception, no failed apply, just a database that does not enforce what the
model declared. Three of the eleven issues found in the 5.0.1 audit were that shape. The controls
below exist because ordinary review does not catch it.

**Convention tests** enforce what is otherwise invisible until a consumer installs the package:

| Test class | Guards |
|---|---|
| `ChangelogConsistencyTests` | The changelog lives in four files (root README + one per package). Asserts the shipped version is documented, no README runs ahead of `Directory.Build.props`, package changelogs are a subset of the root's, and sections are newest-first. |
| `PackagingConventionTests` | Every package ships its own README as `PackageReadmeFile`; `.targets` ship to both `build/` and `buildTransitive/`, reference a real `IDesignTimeServices` in their own assembly, and set `ForProvider` on satellites but not on core. |
| `BuilderApiParityTests` | Every key in a satellite's annotation whitelist is reachable from a builder method. `SqlServer:DataCompression` sat whitelisted with no API for a full release; this catches that class of drift by invoking every builder extension and diffing the keys it sets. |

**`MigrationHarness`** (under `test/…/Harness/`) is the shared rig: build a model from a
`DbContext`, construct any differ, render operations through either the stock provider generator or
this package's. Investigating a suspected differ bug should be three lines, not a re-derived
40-line setup. `NpgsqlSql(operations, complexIndexWiring: false)` is the one that matters most — it
shows what a consumer who forgot `UseNpgsqlComplexIndexes()` actually gets.

**Skills** in `.claude/skills/`:

- `migration-safety-review` — the domain review lens: silent degradation across the design-time and
  runtime seams, definition-store identity keys, name collisions, provider scoping, annotation flow,
  snapshot churn, operation ordering. Use when touching a differ, a SQL generator, a whitelist, a
  definition store, a `.targets`, or before a release.
- `verify-the-guard` — revert the source fix and confirm the new test fails. This caught a test of
  mine during the audit that asserted nothing (the two declarations it set up deduplicated into one,
  so no exception was ever possible) and it passed against broken code.

## Architecture

This library fills a gap in EF Core 10.0 migrations: EF Core can model complex properties (value objects) but does not generate migration SQL for indexes on their nested columns. This library hooks into EF Core's design-time pipeline to produce correct `CREATE INDEX` / `DROP INDEX` SQL.

### Solution layout

| Project | Purpose |
|---------|---------|
| `src/EFCore.ComplexIndexes` | Core library — provider-agnostic fluent API and migration differ |
| `src/EFCore.ComplexIndexes.PostgreSQL` | Satellite package — Npgsql index methods (GIN, GiST, BRIN, Hash, SP-GiST), expression/JSON/LINQ indexes, temporal + exclusion constraints |
| `src/EFCore.ComplexIndexes.SqlServer` | Satellite package — clustered/covering/online/fill-factor options; rejects expression parts and NULLS ordering with clear errors |
| `test/EFCore.ComplexIndexes.Tests` | MSTest suite covering path extraction, serialization, and migration diffing |

Shipping projects live under `src/`, the test project under `test/`; the `.slnx` groups them into
matching solution folders. Shared NuGet metadata and the package version live in the root
`Directory.Build.props`, which still applies to every project beneath it. Each shipping project
carries its own `README.md`, packed as that package's NuGet landing page — keep the per-package
changelogs in sync with the root `README.md` when releasing.

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
the old name, since they run before the rename). The exclusion/temporal differs apply the same two
rules: renamed-table normalization, and name-only changes → `ALTER TABLE … RENAME CONSTRAINT`
(placed *after* the base ops — the rename references the new table name). A constraint rename
keeps its dependents, so `DependsOnChangedTemporalConstraint` compares principal constraints
name-insensitively and the temporal FK doesn't churn. Indexes that need the custom generator
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

Names are validated at **two** levels, because neither alone is sufficient. `AddOrReplace` rejects
an explicit name already used on the same entity (fast feedback at the declaration), and the
differ's `ValidateUniqueIndexNames` rejects duplicate resolved names per table — the only place
that sees across the two stores (property-level annotations vs the entity-level list) and knows the
*default* names, which depend on resolved column names. It validates the **target model only**: a
snapshot that already contains a collision must stay diffable, or the model could never be fixed.
Without these, two same-named declarations scaffolded fine and failed at apply time with 42P07.

`NpgsqlExclusionConstraintExtensions.Store` follows the identical rule — ordered elements
(operators ignored, as direction is for indexes) **plus filter**, with the same
both-must-be-named guard, plus the same two-level name-uniqueness validation
(`ValidateUniqueExclusionNames`). **The filter has to stay in the identity key**: filtered overlap
protection is the entire reason the EXCLUDE API exists, and keying on elements alone silently
discarded all but the last declaration, so "no overlap among active rows" + "no overlap among
revoked rows" collapsed to one constraint. Name collisions matter more here than for indexes:
every ADD is preceded by `DROP CONSTRAINT IF EXISTS`, so a duplicate name does not fail at apply
time — the second constraint silently replaces the first.

### Two integration seams: design-time vs. runtime

There are two distinct hook points, and it matters which one a feature uses:

- **Design-time** (`IDesignTimeServices` via the `.targets`-injected attribute) replaces `IMigrationsModelDiffer`. This runs during `dotnet ef migrations add` and is auto-wired — consumers do nothing.
- **Runtime** (`IMigrationsSqlGenerator`) converts operations to SQL when migrations are *applied*. This is **not** auto-wired; consumers opt in with `optionsBuilder.UseNpgsqlComplexIndexes()` (a `ReplaceService` helper).

Anything that depends on the runtime seam silently degrades when a consumer forgets the wiring, so
**prefer rendering DDL at design time** (a `SqlOperation` baked into the migration) whenever the
statement can be built from resolved column names — that is why exclusion *and* temporal
constraints take that route. The runtime seam is reserved for cases where EF's own operation type
is the only way to express the change (expression indexes, `NULLS FIRST/LAST`), and those carry the
`RuntimeWiringSentinel` so a missing wiring fails loudly instead of applying something wrong.

Selecting the design-time differ is deliberately order-independent (`ComplexIndexDesignTimeRegistration`).
A satellite consumer gets *two* `DesignTimeServicesReferenceAttribute`s — the satellite's, plus the
core package's riding along through `buildTransitive` — and EF simply enumerates them, resolving
last-registration-wins. So the satellites' `.targets` set `ForProvider` (EF skips a satellite
entirely when diffing another provider's context), the satellite registration removes any core
registration, and the core registration backs off when a satellite differ is already present.
Registering with a bare `AddSingleton` on both sides picks a differ by luck of NuGet's restore order.

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
`SqlOperation`s at **design time** — no runtime `UseNpgsqlComplexIndexes()` wiring involved (as of
v5.0.2 temporal constraints and temporal FKs are rendered the same way, for the same reason; the
generator's `AddUniqueConstraintOperation`/`AddForeignKeyOperation` overrides remain only so
migrations scaffolded before that change still render). Every
ADD is preceded by `DROP CONSTRAINT IF EXISTS` in the same `SqlOperation` (and standalone drops use
`IF EXISTS`), so adopting a same-named hand-written constraint applies without 42P07 and a
re-applied migration self-heals. The snapshot round trip is covered end-to-end by
`SnapshotRoundTripTests`, which compile a real generated snapshot with Roslyn and diff against it —
if a constraint nevertheless re-emits on every `migrations add` in a consuming app, the *compiled*
snapshot is stale (`--no-build`, stale migrations assembly), not a differ bug. The
`btree_gist` auto-injection is shared with temporal constraints (single `CREATE EXTENSION`, same
`UseBtreeGist()` / `SuppressTemporalExtensionAutoInjection()` switches) and triggers when a gist
constraint has an `=` element.

### Provider validation is scoped, never a list sweep

Satellites reject what their provider cannot express by overriding
`ValidateCreateIndexOperation(CreateIndexOperation)`, which the core calls for each operation *it*
builds from a complex-index declaration. Never validate by iterating the finished operation list:
it also holds the operations the base EF differ emitted for native `HasIndex` declarations, and
policing those turns any provider index option the satellite doesn't happen to know about into a
hard failure of the consumer's whole `migrations add` — for indexes that never touched this package.
The check has to exist because entity-level provider annotations reach the operation *unfiltered*
(only the property-level path goes through `IsForwardedIndexAnnotation`), so `.UseGin()` on a SQL
Server model is caught, while a native `HasIndex(...).HasMethod("gin")` is left alone.

### Key extension points

- **Adding a new provider**: Subclass `CustomMigrationsModelDiffer` (override `IsForwardedIndexAnnotation`, optionally `ValidateCreateIndexOperation`/`ResolveUnmappedPart`/`ResolveTemplatePart`), implement `IDesignTimeServices` to replace the differ, and ship a `.targets` file that injects the attribute (with `ForProvider` set). The PostgreSQL project is the full-featured reference; the SQL Server project is the minimal one (whitelist + validation, no custom SQL generator).
- **New index options**: Add constants to `ComplexIndexAnnotations.cs` (or `NpgsqlAnnotations.cs`), expose them via `ComplexIndexBuilder`, and read them in the differ when constructing `CreateIndexOperation`.

### Expression path extraction

`ComplexIndexExtensions` parses anonymous-type lambda expressions (`x => new { x.Name, x.Address.City }`) by recursively walking `MemberExpression` chains to produce dotted property paths. These paths are then matched against the EF Core metadata model to resolve column names.

Every extraction entry point threads the lambda's `ParameterExpression` down to `ExtractSinglePart`,
which requires the member chain to bottom out at exactly that parameter. This is not optional
politeness: a captured variable or static member (`x => captured.Name`) produces a perfectly
well-formed path — `"captured.Name"` — that no property lookup can ever match, so without the check
the mistake surfaces much later as an opaque "could not resolve property path" from the differ.
`NpgsqlLinqIndexTranslator.TryGetPath` has always done the same check; core now matches it.

### Per-column sort direction (`DbOrder.Asc`/`DbOrder.Desc`)

`DbOrder.Asc`/`Desc` are identity marker functions; `ExtractSinglePart` peels them (and `Convert` boxing) off the expression in any order to record a `Descending` flag per part. Markers of *different* kinds compose (`NullsLast(Desc(x))`); markers of the *same* kind do not — `Asc(Desc(x))` throws rather than letting one win, since silently picking either sorts the index opposite to what was written. Parts are copied via `IndexPartDefinition.WithSortOptions`, which lives on the type so a new member can't be dropped by a caller rebuilding a part by hand (`Template` was lost that way). Unlike expression indexes, descending columns are **provider-agnostic and need no satellite work**: the differ maps direction onto the native `CreateIndexOperation.IsDescending` (`bool[]`), which every relational provider renders. The differ leaves `IsDescending` **null** when all parts are ascending, so existing ascending indexes don't churn. To avoid snapshot churn, `HasComplexCompositeIndex` keeps writing the legacy `PropertyPaths` form when every column is ascending and only switches to the ordered `Parts` form when a descending column is present. Note: wrapping a member in `DbOrder.Desc(...)` makes it a method call, so C# requires naming it in the anonymous type (`new { x.A, B = DbOrder.Desc(x.B) }`).
