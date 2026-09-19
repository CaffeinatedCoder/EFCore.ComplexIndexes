# Roadmap: 5.4.0, then the 6.0 family

Written 2026-09-19 against 5.3.0. A plan, not a promise: every item is marked **Decided**,
**Proposed** or **Open**, and the line references are as of 5.3.0 and will rot. The plan answers
four questions in order: what to ship next on EF Core 10, what to rename the packages to, how to
split them so the next five features do not each add a copy of the differ, and how EF Core 11
fits in. Sources are collected at the end.

## 1. Where things stand

**EF Core 11 makes the package name wrong.** `Microsoft.EntityFrameworkCore` 11.0.0-rc.1 was
published on 2026-09-08 and GA is scheduled for November 2026. It targets `net11.0` only and will
not run on .NET 10. Native `HasIndex`/`HasKey`/`HasAlternateKey` over complex-type properties,
JSON-mapped paths and complex-collection paths (`Items[].Sku`) shipped in preview.6
(dotnet/efcore#31246, PR #38192). Npgsql 11.0.0-rc.1 (2026-09-14) ships native
`WITHOUT OVERLAPS` and `PERIOD` foreign keys (efcore.pg#3708, #3710). What stays unique to this
package on EF Core 11: expression and JSON-member indexes, typed filter predicates, exclusion
constraints, `NULLS FIRST/LAST`, the SQL Server index options, and everything in section 6.

**EF Core 10 lives until 2028-11-10.** The public commitment in issue #3 is two parallel lines
from November 2026, one per EF major. Anything shipped on 5.x has to be maintainable for two more
years.

**The differ's public shape survives rc.1.** `MigrationsModelDiffer`'s constructor is unchanged
(five parameters), `IMigrationsModelDiffer`, `IMigrationsSqlGenerator`, `CreateIndexOperation`,
`ITrigger` and `DesignTimeServicesReferenceAttribute` have no diff against 10.0. Two things to
watch: the protected `GetDefaultValue(Type)` was removed (not overridden here), and a sixth
constructor parameter, `IDiagnosticsLogger<DbLoggerCategory.Migrations>`, exists on `main` but
not in any published preview. When it lands, the `base(...)` call in `CustomMigrationsModelDiffer`
stops compiling on EF Core 11.

**The name covers half of what ships.** Core and the SQL Server package are indexes only. In the
PostgreSQL package, 2124 source lines are index code and 779 are constraint code, while the
reference documentation is 227 lines on indexes and 256 on constraints. Expression indexes, typed
filters and JSON-member indexes work on any column; the "complex" part is path resolution the
consumer never sees.

**A consumer needs a delete guard.** A consuming application had to write a raw `CREATE TRIGGER`
in a migration to stop rows in a final state from being deleted. Nothing in the model records it,
nothing diffs it, and `EnsureCreated()` never creates it. Trigger packages cannot help: they
replace `IMigrationsModelDiffer` too, and there is exactly one of those per context.

**Demand is modest and does not ask for this shape.** dotnet/efcore#10770 ("managing triggers
with EF migrations") has been in Backlog since 2018 with 26 reactions; the EF team declined it
twice because a trigger body is arbitrary SQL with nothing to diff, and said narrower features
would serve better. The only DDL-generating package, Laraue.EfCoreTriggers, has 139 stars and
about 595k downloads; the app-side trigger packages have 4.4M and 3.4M. No provider generates
trigger DDL from the model; EF's own `HasTrigger` is SQL Server bookkeeping for the `OUTPUT`
clause. So a guard is built because it fits this package's thesis and a consumer needs it, the
same way exclusion constraints were. The EF team's objection is the design constraint: a guard has
a definition (event, predicate, name, message) that diffs, snapshots and reads back; a generic
trigger does not, and is a non-goal.

## 2. Decisions at a glance

| # | Decision | Choice | Status |
|---|---|---|---|
| D0 | Name-uniqueness validation for temporal constraints | Ship first, before 4.1, as its own fix | Decided |
| D1 | Where the delete guard ships first | 5.4.0, PostgreSQL package, design-time `SqlOperation`s | Proposed |
| D2 | What precedes it | One generic descriptor differ replacing the four copies | Proposed |
| D3 | Rename | Yes, at 6.0.0, whole family, new namespaces | Proposed |
| D4 | The name | Working name `CodoMetis.EFCore.Syntagma`; shortlist in 5.1 (SchemaKit, Beyond, Perissos, Sterigma, Eutaxia) | Open, Ricardo's call |
| D5 | Package count | Three: kernel, PostgreSQL, SQL Server; no per-feature packages | Proposed |
| D6 | Old ids | Frozen at 5.x, deprecated on nuget.org with the alternate set; no forwarder packages | Proposed |
| D7 | EF Core 10 and 11 | One codebase, `net10.0;net11.0` multi-target, one version stream from 6.x | Proposed |
| D8 | 6.0.0 timing | On EF Core 10 first, decoupled from EF 11 GA; the `net11.0` target lands in the first 6.x after GA | Proposed |
| D9 | Version numbering | 6.0.0 continues the sequence; majors do not track EF majors under D7 | Proposed |
| D10 | Plugin host | Contributors as multiple DI registrations through an options extension, EF's own plugin pattern | Proposed |
| D11 | Snapshot format | One annotation per declaration from 6.0; the 5.x blob is read forever | Proposed |
| D12 | Non-goals | Generic `HasTrigger(body)` and trigger bodies with subqueries; trigger templates whose semantics the package owns are not a non-goal (7.5, 7.8) | Decided |
| D13 | Horizon items (section 7) | Unscheduled; enter 6.x by demand; partitioning gated by a spike | Proposed |

## 3. Release lines and versioning

**5.x (old ids, EF Core 10, `net10.0`).** Receives 5.4.0 with the items in section 4, then fixes.
After 6.0.0 ships it receives security and data-safety fixes only, until the consumer base has
moved or EF Core 10 leaves support. The dependency ceiling `[10.0.0, 11.0.0)` stays.

**6.x (new ids).** 6.0.0 is the rename and the split on EF Core 10 (D8). The first 6.x after EF
Core 11 GA adds `net11.0` to `TargetFrameworks` with the EF 11 dependency floor `[11.0.0, 12.0.0)`
on that target (D7). Because EF Core 11 requires `net11.0` and EF Core 10 is `net10.0`, the TFM
selects the EF major, and one package serves both. The known edge: an application on `net11.0`
that still runs EF Core 10 gets the `net11.0` asset and cannot use the package; it can stay on 5.x,
whose `net10.0` asset is compatible with a `net11.0` consumer. Document it, do not engineer around
it.

**Why one stream and not two (D7).** Two streams mean a cherry-pick per fix and a backport
decision per feature for two years, for one maintainer. Multi-targeting costs a second test run
and a second SDK on the runner. The differences between the two builds are known and small:
the constructor parameter above, `IEntityType.FindIndex` widening to `IReadOnlyPropertyBase`
(source-compatible), and the native-index handover in section 8. They live behind
`NET11_0_OR_GREATER`.

**Branching.** `main` carries 6.x; `support/5.x` carries the old line. `release.yml` verifies the
tag against `Directory.Build.props` on the tagged commit and discovers packages from the pack
output, so both branches release unchanged. Branch protection and the required `Test
(ubuntu-latest)` check have to be configured on `support/5.x` by hand.

## 4. Phase 1: 5.4.0 on EF Core 10

### 4.0 Fix first: constraint names are not validated for the temporal kinds (D0)

Indexes are checked by `ValidateUniqueIndexNames` and against native `HasIndex` names, exclusion
constraints by `ValidateUniqueExclusionNames`. Temporal unique constraints and temporal foreign keys
get only the identifier-length check (`ApplyTemporalConstraints`, lines 292-296). So two temporal
constraints with the same explicit name on one table, or a temporal constraint named like an
exclusion constraint on the same table, scaffold cleanly and fail at apply time with 42710. Worse,
a `UNIQUE … WITHOUT OVERLAPS` constraint is backed by an index of the same name, and index names
share the schema's relation namespace, so a temporal constraint named like any index in the schema
fails with 42P07. This is the signature failure this repository exists to catch, and it is
independent of everything else in the plan, so it ships first, as 5.3.1 or the first commit of
5.4.0.

The fix: one name registry per target model, filled by all four kinds plus the native indexes and
native constraints of the same table or schema, with the rule per kind: foreign keys are unique per
table; unique, exclusion and temporal constraints are unique per table and, being index-backed on
PostgreSQL, also against index names in the schema. Validate the target model only, so a snapshot
that already contains a collision stays diffable and the model can be fixed. Message names both
declarations and their kinds. Tests: each collision pair throws; a same-named pair on different
tables passes for the per-table kinds; the snapshot side is left alone; `verify-the-guard` by
reverting. Once 4.1 lands the registry becomes the generic differ's `Validate` hook.

### 4.1 One descriptor differ instead of four

Four copies of the same algorithm exist today: complex indexes in
`CustomMigrationsModelDiffer.GetDifferences` (core, lines 66-201), and exclusion constraints
(`ApplyExclusionConstraints`, 468-563), temporal unique constraints and temporal foreign keys
(both inside `ApplyTemporalConstraints`, 274-426) in `NpgsqlComplexIndexMigrationsModelDiffer`.
Each builds descriptors from both models, normalises source descriptors onto renamed tables,
matches by identity, turns a name-only difference into a rename, and emits drops before the base
operations and creates after. A delete guard would be the fifth copy. The refactor is internal,
changes no public member, and the existing suite is its guard: `OperationOrderingTests`,
`IndexRenameTests`, `PhantomIndexChurnTests`, `SnapshotRoundTripTests`, the four differ test
classes and the live PostgreSQL integration test.

What actually differs between the copies, and what the generic differ therefore has to
parameterise:

| | Index | Exclusion | Temporal unique | Temporal FK |
|---|---|---|---|---|
| Identity | table, parts, name, unique, filter, provider annotations | table, name, method, filter, deferrability, parts | table, name, period, key columns | both tables, name, both periods, both column lists |
| Rename op | `RenameIndexOperation`, gated on `CanRenameIndexes` | `ALTER TABLE … RENAME CONSTRAINT` | same | same |
| Drop op | `DropIndexOperation` | `SqlOperation` with `IF EXISTS` | `DropUniqueConstraintOperation` | `DropForeignKeyOperation` |
| Pre-drop inside the add | no | `DROP CONSTRAINT IF EXISTS` | no | no |
| Placement | drops first, creates last | same | drops after FK drops, adds before FK adds | drops first of all, adds last of all |
| Name uniqueness | `ValidateUniqueIndexNames` plus native collision check | `ValidateUniqueExclusionNames` | none | none |
| Renamed-table normalisation | inline dictionary | `BuildRenamedTables` | same | two-sided, dependent and principal |
| Extras | parts-annotation sentinel, `ValidateCreateIndexOperation` | `btree_gist` when gist with `=` | `btree_gist` always | forced rebuild when the principal constraint changed |

Design of the replacement, kept deliberately small:

- `SchemaObjectKind` with an ordinal that fixes placement: drops run in descending order
  (temporal FK, then constraints, then indexes), creates in ascending order. This encodes the
  three placement rules above as one comparison.
- A `DescriptorSet<TDescriptor>` built per model, keyed by the descriptor's identity, with
  `Normalise(renamedTables)` implemented once and a `TwoSided` flag for foreign keys.
- `DescriptorDiffer<TDescriptor>` with four callbacks: `Drop(descriptor)`, `Create(descriptor)`,
  `Rename(old, new)` and `CanRename`. The core index differ passes `RenameIndexOperation` and
  `CanRenameIndexes`; the PostgreSQL kinds pass `SqlOperation`s. The rename guard `a.Name !=
  dropped.Name` exists in the three PostgreSQL copies and not in the index copy (line 122);
  confirm whether the index copy relies on identity including the name, and make the guard
  uniform.
- The name registry from 4.0 becomes the generic differ's `Validate` hook, one implementation
  for all kinds.
- `Quote`/`QuoteQualified`, `BuildRenamedTables`, the token builders and the thrice-computed
  `droppedTables` collapse into the shared code.
- The `btree_gist` injection and the temporal FK's forced rebuild stay where they are, as
  post-processing over the generic result.

Order of work: extract with the index kind first, run the suite, then move each PostgreSQL kind
over one commit at a time, running the suite after each. No new tests are needed for the
refactor itself; one characterisation test that declares all four kinds on one model and asserts
the full operation order is worth adding before starting, since `OperationOrderingTests` covers
pairs.

Riding along: `NpgsqlTemporalAnnotations.WithoutOverlaps`, `ForeignKeyDependentPeriod` and
`ForeignKeyPrincipalPeriod` are dead since 5.0.2 (the differ renders `SqlOperation`s and never
writes them). They are public constants, so removing them is a CP0002 break; mark them
`[Obsolete]` in 5.4.0 and remove them in 6.0.0.

### 4.2 Delete constraints on PostgreSQL

Vocabulary: to the application and in the API this is a **constraint**, because that is how it
surfaces (a check-constraint violation carrying the constraint name); the **row guard** is the trigger that implements it, and the word appears only in
internals and in this document where the mechanism is meant.

**API (proposed names).**

```csharp
builder.Entity<Invoice>()
    .HasDeleteConstraint(x => x.FinalizedAt != null,
        name: "RC_Invoices_Finalized",
        message: "Finalized invoices cannot be deleted.");

builder.Entity<AuditEntry>()
    .HasDeleteConstraint(name: "RC_AuditEntries");            // unconditional

builder.Entity<Invoice>()
    .HasDeleteConstraint("{FinalizedAt} IS NOT NULL", "RC_Invoices_Finalized", "…"); // raw predicate
```

The typed overload runs `NpgsqlLinqIndexTranslator.TranslatePredicate`, exactly as typed filters
do, so the predicate subset and both refusals (enums, values with no portable SQL spelling) are
inherited unchanged. The raw overload takes the same `{Property.Path}` placeholder form as filters.
A name is required when a predicate is given, because the name is what the application's error
mapping dispatches on; the unconditional form defaults to `RC_{table}`.

**Rendering.** Design-time `SqlOperation`s, no runtime wiring, the same route as exclusion
constraints and for the same reason: the statement is buildable from resolved column names, and a
design-time statement cannot degrade when a consumer forgets `UseNpgsqlComplexIndexes()`.

```sql
CREATE OR REPLACE FUNCTION "public"."codometis_row_constraint"() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'check_violation',
        MESSAGE = TG_ARGV[1],
        CONSTRAINT = TG_ARGV[0],
        TABLE = TG_TABLE_NAME,
        SCHEMA = TG_TABLE_SCHEMA;
END $$;

DROP TRIGGER IF EXISTS "RC_Invoices_Finalized" ON "public"."Invoices";
CREATE TRIGGER "RC_Invoices_Finalized"
    BEFORE DELETE ON "public"."Invoices"
    FOR EACH ROW WHEN (OLD."FinalizedAt" IS NOT NULL)
    EXECUTE FUNCTION "public"."codometis_row_constraint"('RC_Invoices_Finalized', 'Finalized invoices cannot be deleted.');
```

- The function is shared, created in the table's schema with `CREATE OR REPLACE` by every
  migration that adds a constraint, and never dropped, the way `btree_gist` is handled. Its name
  is permanent once a consumer's migration carries it, so it is branded with the owner prefix and
  not the product (5.1); the 6.0 family keeps rendering the same name.
- `ERRCODE = 'check_violation'` plus `CONSTRAINT`, `TABLE` and `SCHEMA` makes Npgsql surface a
  `PostgresException` with `SqlState` 23514 and `ConstraintName` set to the guard name. To the
  application it is a violated check constraint, which is the ask.
- The `WHEN` clause is the resolved predicate with an `OLD.` row qualifier. `ResolveFilter` gains
  a qualifier parameter; JSON-member extractions become `OLD."doc" ->> 'A'` through the same
  `ResolveUnmappedPart` path.
- `DROP TRIGGER IF EXISTS` before every `CREATE TRIGGER`, so adopting a same-named hand-written
  trigger applies cleanly and a re-applied migration self-heals, matching the exclusion rule.
  `CREATE OR REPLACE TRIGGER` needs PostgreSQL 14; the drop-and-create form needs nothing.
- A row trigger fires for `ExecuteDelete`, raw SQL, psql and cascaded deletes, which is the point.
  A guarded child row therefore blocks the cascade from its parent; document it as intended.
  `TRUNCATE` bypasses row triggers; a `BEFORE TRUNCATE` statement trigger is section 6.

**Storage and identity.** `CustomRowConstraint:Constraints`, a JSON list on the entity type,
serialised like `ExclusionConstraintDefinition`. `RowConstraintDefinition`: `Event` (delete only in 5.4, the field exists
so update and truncate are additive), `Predicate` (template or null), `Name`, `Message`. Identity
for the store is event plus predicate; a redeclaration replaces, two guards with different
predicates coexist and both need names, an explicit name reused on the entity is rejected.
Descriptor identity for the differ is table, schema, event, resolved predicate, name, message.
Name-only change becomes `ALTER TRIGGER "old" ON "table" RENAME TO "new"` after the base
operations. Message-only change is drop-and-create.

**Validation, all at `migrations add`.** `ThrowIfDeclaredOnUnmappedType`; duplicate resolved name
per table through the generic differ; identifier length (63 bytes); unresolvable placeholder
throws; a predicate that resolves to a JSON container or a table-split complex property is
rejected with a targeted message.

**Read model and mutable API.** `RowConstraintDeclaration` (event, predicate as declared, name,
message), `GetDeclaredRowConstraints()` / `GetRowConstraints()` / `FindRowConstraint(name)` on entity
type and model, `AddRowConstraint` on `IMutableEntityType`, and the differ builds its descriptors from these readers, keeping the rule
that the read model is the differ's reader.

**EF's own trigger metadata.** `HasDeleteConstraint` also calls `IMutableEntityType.AddTrigger(name)`
so EF's model records that the table has a trigger. Npgsql ignores it; SQL Server needs it or
`SaveChanges` fails on the `OUTPUT` clause, so doing it now keeps the SQL Server port in
section 6 a rendering change. Check what `CSharpSnapshotGenerator` emits for it and cover it in
`SnapshotRoundTripTests`.

**Tests.** Differ: create, drop, rename, message change, no churn on identical models, predicate
change detected, ordering relative to the base operations and to exclusion constraints. Snapshot
round trip with a compiled snapshot, including `has-pending-model-changes` reporting nothing.
Integration on PostgreSQL 18: apply, delete a guarded row and assert `SqlState` 23514 and
`ConstraintName`, delete an unguarded row, cascade through a foreign key into a guarded row,
`ExecuteDeleteAsync`, re-apply the same DDL. Read model and mutable API. Every validation with
`verify-the-guard`. Consumer smoke test: assert the trigger DDL appears in the scaffolded
migration; this is a second PostgreSQL-only discriminator next to the exclusion constraint.

**Documentation.** A new `docs/postgresql-guards.md` (the filename token scopes
`DocumentationApiTests` to the PostgreSQL surface), a README feature bullet and a wiring-table row
reading "no", entries in the root and PostgreSQL changelogs, a CLAUDE.md architecture paragraph, and
a `migration-safety-review` checklist item for row constraints.

### 4.3 Small items riding along in 5.4.0

- The `[Obsolete]` markings from 4.1.
- `RuntimeWiringSentinel` is `"__requires_UseNpgsqlComplexIndexes__"` and lives in core. Leave the
  value (it is in consumers' migrations) and move the choice of value behind a provider virtual in
  6.0 (section 5.4).

## 5. Phase 2: 6.0.0, the family

### 5.1 The name (D4)

Three directions were tried. A construction metaphor (`Underpin`, `Buttress`, `Undergird`) was
rejected as non-telling. A literal list (`IndexesAndConstraints`) tells exactly what ships today and
becomes a lie the day any item in section 7 lands, which is the Postgres95 and ELK lesson;
`SchemaObjects` is its honest but bland form. The direction now is a name that says "substantially
more than EF Core" and carries a sense, in one of three flavours: a plain English word (Beyond), a
`…Kit` in the style Apple uses for its frameworks, or a pronounceable Greek word that transports
the meaning. The `.EFCore.` segment carries the platform in every case, the reserved `CodoMetis.`
prefix makes nuget.org collisions impossible and gives the verified badge (the prefix already
carries the `CodoMetis.ValueRanges` family, four packages at 8.0.0, repository
`CaffeinatedCoder/CodoMetis.ValueRanges`), and the tagline carries what the word does not.

Checked on 2026-09-19 (nuget.org search for the word in any id, GitHub repositories by name, and
an EF-ecosystem search for the extension words):

| Candidate | Flavour | Sense | Collisions | Verdict |
|---|---|---|---|---|
| **`CodoMetis.EFCore.Syntagma`** | Greek, σύνταγμα | an ordered arrangement, a composition; in modern Greek, a constitution. Indexes are the ordering, constraints and policies the constitution | none in software; Syntagma Square | **Working name.** Pronounceable (sin-TAG-ma), memorable, true for every item in section 7 |
| **`CodoMetis.EFCore.SchemaKit`** | Kit | the kit of schema objects EF leaves out | three small repositories, none .NET | The telling choice; nothing to explain |
| **`CodoMetis.EFCore.Beyond`** | English | past what EF Core does on its own | none | The memorable choice; fully durable |
| `CodoMetis.EFCore.Perissos` | Greek, περισσός | more than enough, beyond the ordinary: the Greek "Beyond" | a pizzeria and a media app | Runner-up to Syntagma |
| `CodoMetis.EFCore.Sterigma` | Greek, στήριγμα | a support, a prop: what holds EF's schema up | none | Runner-up; the Underpin sense, in Greek |
| `CodoMetis.EFCore.Eutaxia` | Greek, εὐταξία | good order, discipline | two tiny repositories | Runner-up; less familiar |
| `CodoMetis.EFCore.DdlKit` | Kit | the DDL kit | none | Telling to DBAs, opaque to application developers |
| `…Encore`, `…Annex` | English | more after the main act; an added wing | none | Playful and precise respectively; weaker than Beyond |
| `…Extended`, `…Plus`, `…Extras` | English | | `EntityFramework.Extended`, `Z.EntityFramework.Plus`, `EntityFrameworkExtras` | Rejected: owned by other libraries |
| `…Pleroma`, `…Nomos`, `…Kanon`, `…Themis`, `…Tekton`, `…Telos`, `…Ecto`, `…Exo` | Greek | | Pleroma (fediverse server), Nomos (two active AI projects), Kanon/Canon, Themis (crypto library), Tekton (CI/CD), Telos (blockchain), Ecto (Elixir), Exokit | Rejected: taken in software |
| `…Epekeina`, `…Peran`, `…Themelion`, `…Krepis` | Greek | beyond; beyond; foundation; temple base | none | Rejected: hard to say or too obscure |
| `CodoMetis.Underpin`, `…Buttress`, `…Undergird` | metaphor | | `Underpin-WP` (WordPress) | Rejected as non-telling |
| `CodoMetis.EFCore.IndexesAndConstraints`, `…SchemaObjects` | literal | | none | Withdrawn: section 7 outgrows the first; the second is the fallback if a literal name is wanted after all |

The working name is used throughout this document; renaming to any other candidate is mechanical.
Satellites take the provider suffix (`CodoMetis.EFCore.Syntagma.PostgreSQL`, `….SqlServer`), the
repository follows the kernel id (`CaffeinatedCoder/CodoMetis.EFCore.Syntagma`; GitHub redirects
the old URL, so the absolute links in the packed 5.x READMEs keep working), and the wiring is
`o => o.UseSyntagma()` (5.6). Tagline for the package pages: "the schema EF Core cannot declare:
complex-type, expression and JSON indexes, exclusion and temporal constraints, row constraints,
typed filters, and more."

Two identifiers outlive any product name because they land in consumers' snapshots and
migrations: annotation keys and the shared trigger functions. Both are branded with the owner
prefix, never the product: annotations under `CodoMetis:` from 6.0 (5.5), and functions such as
`codometis_row_constraint()` from 5.4.0. That settles the permanence question in 4.2 regardless
of D4.

The declaration API keeps its names (`HasComplexIndex`, `HasExpressionIndex`,
`HasExclusionConstraint`, …); they describe what they do. Row guards are exposed as constraints
(`HasDeleteConstraint`, `HasUpdateConstraint`, `HasImmutableColumn`), matching the error the
application sees.

### 5.2 Package layout and dependency direction (D5)

Three packages, the same count as today, with the boundaries moved:

- **`CodoMetis.EFCore.Syntagma`**, the kernel: the differ host and the generic descriptor differ (4.1), the
  contributor and dialect interfaces, registration, path extraction, `ResolveProperty` with the
  converter unwrap, placeholder resolution, the predicate translator behind the dialect,
  annotation storage, the read-model base, and the provider-agnostic declarations and readers:
  complex indexes, guards, typed check constraints.
- **`CodoMetis.EFCore.Syntagma.PostgreSQL`** and **`CodoMetis.EFCore.Syntagma.SqlServer`**: the dialect, the rendering
  of the agnostic features for that provider, and what only that provider can express. Exclusion,
  temporal and expression indexes stay PostgreSQL; the custom `IMigrationsSqlGenerator` stays
  PostgreSQL.

Providers depend on the kernel; consumers install a provider package; nothing depends on a
provider. Per-feature packages were considered and rejected: the translator and path resolution
that guards need are most of the index machinery, so a guards-without-indexes package would carry
the kernel anyway.

### 5.3 The plugin host (D10)

The single differ becomes a host. This is EF's own pattern: Npgsql's NetTopologySuite plugin
registers `IRelationalTypeMappingSourcePlugin` and friends as multiple registrations through an
options extension, and EF resolves them as `IEnumerable<T>`.

- `ISchemaContributor`: `Kind` (ordinal, see 4.1), `Build(IRelationalModel, ISchemaDialect) →
  IReadOnlyList<SchemaObjectDescriptor>`, `Drop`, `Create`, `Rename`, `CanRename`, and a
  `Validate(target)` hook for the name-uniqueness and length rules.
- `SyntagmaMigrationsModelDiffer` (kernel, the only differ type in the family) takes
  `IEnumerable<ISchemaContributor>` and `ISchemaDialect`, runs base `GetDifferences`, then each
  contributor through the generic differ, then post-processing hooks (`btree_gist`, temporal
  rebuilds). `HasDifferences` routes through `GetDifferences` as today.
- Contributors ship in the kernel (index, guard, check constraint) and in providers (exclusion,
  temporal, temporal FK). Ordering is by `Kind`, never by registration order.
- Provider packages stop subclassing the differ. What the satellites override today
  (`IsForwardedIndexAnnotation`, `ValidateCreateIndexOperation`, `ValidateCreatedIndexes`,
  `CanRenameIndexes`, `ToOperationAnnotationName`, `TransformIndexAnnotation`,
  `ResolveUnmappedPart`, `QuoteIdentifier`, `MeasureIdentifier`) becomes the dialect.
- The SPI is public. `InternalsVisibleTo` between shipping packages goes away, which is what makes
  a third-party provider possible.
- Two capabilities the horizon (section 7) needs and plain appends do not give. A
  `Rewrite(IReadOnlyList<MigrationOperation>)` hook per contributor, run after the base differ and
  before the appends, for the kinds that must change EF's own operations (7.4, 7.7); the temporal
  foreign key's forced rebuild already reaches into the base result and becomes the first user.
  And placement ordinals below the table level, so a kind can be created before the tables that
  use it and dropped after them (7.3), formalising the `CREATE EXTENSION` injection at index 0.
  For 7.7 the differ takes `IMigrationsSqlGenerator` as a dependency; the generator does not depend
  on the differ, so there is no cycle. Type-mapping plugins (7.3) ride on the same options
  extension.

### 5.4 The dialect seam and the translator move

`NpgsqlLinqIndexTranslator` is 252 lines and its provider-specific surface is eight spellings:
`||` concatenation, `length`, `btrim`, `substr`, `TRUE`/`FALSE`, a bare boolean column as a
predicate, identifier quoting, and the row qualifier for guards. The translator moves to the
kernel, producing a small SQL AST or the existing placeholder template, and `ISchemaDialect`
renders: `QuoteIdentifier`, `MeasureIdentifier`, function spellings, boolean literals and
predicates (`= 1` on SQL Server), string concatenation, JSON member extraction,
`RowGuard(descriptor) → string`, and `RuntimeWiringSentinel`. The SQL Server package gets typed
filters for free.

### 5.5 Snapshot format v2 (D11)

Today each kind stores one JSON blob per entity type (`CustomIndex:CompositeIndexes`,
`CustomExclusion:Constraints`, …), which merges badly and forces the whole list to re-serialise on
any change. From 6.0 each declaration is its own annotation, keyed by its explicit name or by a
stable hash of its identity: `CodoMetis:Index:<key>`. The kernel reads both forms forever and writes
only the new one. Effect on consumers: the first `migrations add` after upgrading produces an empty
migration whose only content is the snapshot rewrite; no DDL changes, because identities do not.
Say so in the migration guide, and cover it in `SnapshotRoundTripTests` with a 5.x snapshot checked
in as a fixture.

### 5.6 Registration: runtime and design time

**Runtime.** The idiomatic form for a provider plugin is the provider's own options builder:

```csharp
options.UseNpgsql(connectionString, o => o.UseSyntagma());
options.UseSqlServer(connectionString, o => o.UseSyntagma());
options.UseSyntagma();   // kernel only, providers without a package
```

`UseSyntagma()` adds an `IDbContextOptionsExtension` whose `ApplyServices` registers the
contributors, the dialect, `ReplaceService<IMigrationsModelDiffer>` and, on PostgreSQL,
`ReplaceService<IMigrationsSqlGenerator>`. EF applies every extension's `ApplyServices` in order
and then the replaced services as a final rewrite pass, so both mechanisms coexist. `ReplaceService`
and `UseInternalServiceProvider` are mutually exclusive by EF's own validation, so the
`Add…`-style overloads for a caller-owned service collection stay.

**Design time.** `dotnet ef` seeds the design-time collection from the context
(`AddDbContextDesignTimeServices`, a `TryAdd` factory for `IMigrationsModelDiffer` among others),
then applies every `DesignTimeServicesReferenceAttribute` from the startup assembly and the
context assembly, filtered by `ForProvider`, then the provider's design-time services, EF's own,
and finally the user's `IDesignTimeServices`. `ConfigureDesignTimeServices` receives the raw
`ServiceCollection`, so plain `AddSingleton<ISchemaContributor, …>` from several packages
accumulates. Each package's `.targets` keeps injecting its attribute; the kernel's registers the
differ, the dialect default and the kernel contributors; a provider's registers its dialect and
contributors with `ForProvider` set. Because only one differ type exists, the current
back-off-and-remove dance in `ComplexIndexDesignTimeRegistration` becomes a single `TryAdd`.

### 5.7 Guarding against the old packages

A consumer upgrading one project of a solution, or picking up an old satellite transitively, ends
up with `EFCore.ComplexIndexes.*` and `CodoMetis.EFCore.Syntagma.*` in one context. Both inject a
design-time attribute and both replace the differ; the loser goes silent. The kernel therefore
scans the design-time and runtime collections for a differ whose type name starts with
`EFCore.ComplexIndexes` and throws with the package names to remove. This is the one place the new
family knows the old one's name.

### 5.8 Packaging, publishing and repository mechanics

- **Old ids (D6).** No forwarder packages. A dependency-only package needs `NoWarn NU5128` or a
  `_._` placeholder, and it would only install assemblies whose namespaces the consumer still has
  to change, so it buys nothing. The precedent is `DotNet.Testcontainers`: publishing stopped and
  the last version was deprecated as Legacy with `Testcontainers 2.0.1` as the alternate. Do the
  same once 6.0.0 is out: deprecate the latest 5.x of each old id with the alternate set and a
  message naming the support window. Deprecation is web-UI only on nuget.org (no API or CLI), it
  does not unlist, the README stays visible, and consumers see it in Visual Studio and in
  `dotnet list package --deprecated`. Do not unlist: pinned restores must keep working.
- **Trusted publishing.** The policy on nuget.org has scopes; pushing a new id needs the "push new
  packages" scope with a glob that matches it. The `CodoMetis.ValueRanges` family publishes under
  the same owner, so a policy scoped to `CodoMetis.*` may already exist; the policy is per
  repository and workflow file, though, so this repository's policy needs the scope checked before
  the first 6.0.0 tag. The workflow file name (`release.yml`) and the environment (`nuget`) do not
  change, so `ReleaseWiringConventionTests` stays valid. A missing scope fails the publish job with
  an authentication error that never mentions the id.
- **Package validation.** The first release of each new id omits `PackageValidationBaselineVersion`,
  as `Directory.Build.props` already anticipates; `PackageValidationBaselineName` exists and could
  diff against the old id, but the namespace change makes every member a break, so start the
  baseline at 6.0.0 instead. `PackagingConventionTests`' baseline rule needs a "no baseline on a
  first release" branch.
- **Repository.** Rename to `CodoMetis.EFCore.Syntagma`; update `RepositoryUrl`, `PackageProjectUrl` and the
  absolute links in the packed READMEs; keep `SECURITY.md`'s table on both lines.
- **SBOM.** Unchanged: `src/*/` is globbed and the id comes from the directory name.

### 5.9 Convention tests and CI that change

| Where | What is hard-coded today | Change |
|---|---|---|
| `PackagingConventionTests` | count of three projects; baseline rule | derive the count from `RepositoryLayout.ShippingProjects`; allow the first-release case |
| `ClaudeMdConsistencyTests` | assembly list | rename |
| `BuilderApiParityTests` | the two annotation/builder pairs | rename, and add the dialect's own keys |
| `DocumentationApiTests` | provider scope by filename token and package-id suffix | unchanged in shape |
| `SecurityPolicyConsistencyTests` | core found as the non-satellite project; `Microsoft.EntityFrameworkCore.Abstractions` | unchanged; add the `net11.0` floor when it lands |
| `test/consumer-smoke-test.sh` | two satellites by name; the core csproj path; the sentinel literal | loop over `src/*/`; rename; read the sentinel from the dialect |
| `dotnet.yml`, `release.yml` | the core csproj path for the version | rename |
| `dependabot.yml` | `/src/*` | unchanged |
| `.github` branch protection | required check name | keep `Test (ubuntu-latest)`; configure it on `support/5.x` too |

Multi-targeting (D7) adds `actions/setup-dotnet` with both SDKs, a second test run per TFM, and a
second consumer per satellite in the smoke test once `net11.0` exists.

### 5.10 Consumer migration guide (outline for the 6.0.0 README section)

1. Replace the package references; the old ids are deprecated with the alternate shown.
2. Replace the namespaces.
3. Replace `UseNpgsqlComplexIndexes()` / `UseSqlServerComplexIndexes()` / `UseComplexIndexes()`
   with the provider-builder form in 5.6.
4. Run `dotnet ef migrations add`; expect one migration containing only the snapshot rewrite (5.5).
5. Removed: the three obsolete temporal constants; the pre-5.0.2 temporal operation overrides in
   the generator and the legacy `PropertyPaths` snapshot form are **kept**, because consumers
   replay old migrations on fresh databases forever.

## 6. Phase 3: the 6.x feature backlog, ordered by reuse

1. **Update guard.** Same trigger with `BEFORE UPDATE`, predicate over the old row.
   `HasUpdateConstraint(x => x.FinalizedAt != null, …)`.
2. **Append-only.** Unconditional update and delete guards plus a `BEFORE TRUNCATE` statement
   trigger. The `REVOKE` answers on dba.stackexchange assume the application role is not the table
   owner, which is rarely true for an EF application that runs its own migrations.
3. **Guards on SQL Server and SQLite.** SQL Server: `AFTER DELETE`, set-based
   (`IF EXISTS (SELECT 1 FROM deleted WHERE …) THROW 50001, …`), the guard name in the message;
   `CREATE OR ALTER TRIGGER`; `HasTrigger` in the model is mandatory there. SQLite: `BEFORE DELETE
   … WHEN … BEGIN SELECT RAISE(ABORT, 'msg'); END`, which surfaces as `SQLITE_CONSTRAINT`; worth a
   small package because consumers unit-test on SQLite and a guard missing from the test database
   is a silent divergence. MySQL and Oracle: keep the dialect seam open, no package without demand;
   note that MySQL triggers do not fire on foreign-key cascades.
4. **Violation mapper.** `TryGetRowConstraintViolation(out string name)` per provider
   (PostgreSQL: `SqlState` 23514 and `ConstraintName`; SQL Server: error number and message;
   SQLite: extended result code), so application code has one shape. `ExecuteDelete` throws the
   provider exception directly, not `DbUpdateException`; the mapper takes `Exception`.
5. **Column immutability.** `HasImmutableColumn(x => x.CreatedAt)` renders
   `BEFORE UPDATE … WHEN (OLD."CreatedAt" IS DISTINCT FROM NEW."CreatedAt")`; needs a two-root
   predicate (`old`, `new`) in the translator.
6. **Typed check constraints.** `HasCheckConstraint(x => x.ValidTo > x.ValidFrom)` resolving
   complex-type and JSON paths, written into EF's native check constraint so every provider renders
   it through `AddCheckConstraintOperation` with no custom DDL. EF's own `HasCheckConstraint` is raw
   SQL that knows nothing about complex properties; `EFCore.CheckConstraints` (conventions for enums
   and discriminators) is prior art with a different scope.
7. **Transition guards.** Allowed status transitions as an old-and-new predicate. Enums stay
   refused for the converter reason, so it takes stored values.
8. **Read models** for every new kind, on the rule that the differ builds from the reader.

Non-goals (D12): a generic `HasTrigger(body)` and deferred constraint triggers with subquery
bodies. Once a body is arbitrary SQL there is nothing to diff, which is the EF team's objection,
and it is right. Triggers whose semantics the package owns are a different matter: row constraints
here, and the templates in 7.5 and 7.8.

## 7. Horizon: what the umbrella admits, and why each item was set aside before

The thesis is four tests: declared in the model, diffable by an identity, rendered at design time,
enforced or provided by the database. Section 6 stopped at indexes and constraints because the
name under consideration did; the thesis does not stop there. Each item below records the reason
it was set aside during planning, what it needs from the foundation in section 5, and its caveat.
None is scheduled (D13); an item enters section 6 when a consumer asks. Together they are the
argument for a name that does not enumerate (5.1).

### 7.1 Row-level security policies (PostgreSQL)

`ALTER TABLE … ENABLE ROW LEVEL SECURITY`, optionally `FORCE`, and
`CREATE POLICY name ON t [AS PERMISSIVE | RESTRICTIVE] FOR {ALL | SELECT | INSERT | UPDATE | DELETE}
[TO roles] [USING (…)] [WITH CHECK (…)]`. Set aside as "a third category beyond indexes and
constraints". Fit: identity is table, name, command, roles, the permissive flag and both resolved
predicates; both predicates go through the typed translator, which needs one marker for session
settings (`Session.Setting("app.tenant_id")` rendering `current_setting('app.tenant_id')`), since
that is what tenant policies compare against; `DROP POLICY IF EXISTS` before create, `ALTER POLICY
… RENAME TO` for a name-only change. Caveat: table owners bypass policies unless `FORCE` is set,
and an EF application that runs its own migrations usually connects as the owner, so the API
makes `FORCE` explicit and the documentation covers the separate-role setup. Rows a policy hides
are simply absent to EF, and an update of one fails as a concurrency exception; that is how RLS
works, and the docs must say so.

### 7.2 Extended statistics (PostgreSQL)

`CREATE STATISTICS [IF NOT EXISTS] name (ndistinct, dependencies, mcv) ON col, col [, (expr)] FROM t`
(PostgreSQL 10; expressions since 14), `DROP STATISTICS IF EXISTS`. Set aside as a third category;
the smallest item here. Fit: identity is schema, name, kinds and the ordered columns or expressions,
which resolve through the same path resolution as index parts, so complex-type columns and JSON
extractions work unchanged. Statistics objects live in the schema namespace, so the registry from
4.0 applies. Needs nothing beyond the contributor SPI.

### 7.3 Domain types (PostgreSQL)

`CREATE DOMAIN email AS text [COLLATE …] [DEFAULT …] [NOT NULL] [CHECK (VALUE …)]`, with columns
typed as the domain. Set aside as a third category; it fits the value-object origin of the package
exactly: `HasDomain<Email>("email", "text", v => v.Length <= 320)`, the typed check translated with
the lambda root mapped to `VALUE`. Identity is schema, name, base type and constraints. Two
foundation needs: placement below the table level, because the domain must exist before the
`CREATE TABLE` that uses it and go after the `DROP TABLE` (5.3); and a type-mapping plugin, because
EF needs a store-type mapping for `email` and Npgsql maps an unknown store type name to nothing, so
the feature registers an `IRelationalTypeMappingSourcePlugin` that maps each declared domain to its
base type's mapping with the domain as the store type, through the same options extension. Caveat:
`ALTER DOMAIN` adds and drops constraints but cannot change the base type; a base-type change is
drop-and-recreate and fails while columns use it, so it throws with guidance.

### 7.4 Deferrable foreign keys and unique constraints (PostgreSQL)

Set aside as a third category, and because it touches EF's own operations. Foreign keys are the
easy half: `ALTER TABLE … ALTER CONSTRAINT fk DEFERRABLE INITIALLY DEFERRED` appended after the base
`AddForeignKeyOperation`, since PostgreSQL allows `ALTER CONSTRAINT` for foreign keys only. Unique
and primary keys fix deferrability at creation, so they need the rewrite hook (5.3) to turn EF's
`AddUniqueConstraintOperation`, `AddPrimaryKeyOperation` or the inline constraint in
`CreateTableOperation` into a `SqlOperation`. Declared as annotations on EF's own key and foreign
key metadata, which the snapshot generator already emits. Caveats: Npgsql 10 exposes no
deferrability option for either kind, and that must be re-checked against Npgsql 11 before building;
and a deferred violation surfaces at `COMMIT`, so the `PostgresException` comes from the transaction
commit rather than from `SaveChanges`, which the documentation must say.

### 7.5 Trigger templates with fixed semantics (every provider)

Set aside twice: under the row-guard thesis as "not an invariant", and listed as a non-goal as
`updated_at` maintenance. Re-admitted for two reasons. The definition is a template with a diffable
identity (table, column, template, name), not SQL. And the EF team's own position on
dotnet/efcore#10770 is that the usual motivation for trigger support is update-timestamp generation
and that a narrow feature would serve better than generic trigger support; this is that feature.
Templates: **touch** (`updated_at := now()` on update) and **version bump**
(`version := OLD.version + 1`), later the closure-table maintenance in 7.8. One shared function per
template, taking the column name as a trigger argument and assigning through
`jsonb_populate_record(NEW, jsonb_build_object(TG_ARGV[0], …))`, so no `hstore` and no per-column
function. SQL Server renders an `AFTER UPDATE` set-based update joined to `inserted`; SQLite an
`AFTER UPDATE … BEGIN UPDATE … END`. EF synergy: the feature configures the property as
`ValueGeneratedOnAddOrUpdate()` and, for the version bump, `IsConcurrencyToken()`, so EF reads the
value back and uses it for optimistic concurrency without application code. Foundation: the row
constraint contributor from 4.2 generalised to templates, and the `HasTrigger` registration from
4.2. The non-goal is unchanged: arbitrary bodies.

### 7.6 Views, materialized views and stored procedure bodies (with caveat)

Set aside because the EF team's objection on #10770 applies literally: the body is arbitrary SQL,
the diff is by text, and a text diff cannot tell a semantic change from a reformat. Admitted with
exactly that caveat, because the lifecycle gap is real: EF Core 7 and later map inserts, updates
and deletes to stored procedures (`InsertUsingStoredProcedure`, …) but never create them, and
`ToView()` maps to a view EF never creates; both are `migrationBuilder.Sql` today with no snapshot
awareness. Shape: `HasViewDefinition(sql)` on a `ToView()` type; `HasMaterializedView(sql)`, whose
indexes go through the index contributor; `HasStoredProcedureBody(sql)`, where the signature (name,
parameters, result columns) comes from EF's own `IStoredProcedure` metadata and only the body is
raw. Identity is name plus normalised text; ordering by an explicit `DependsOn` for views over
views; drop-and-create rather than `CREATE OR REPLACE VIEW`, which cannot change a column's type.
`REFRESH MATERIALIZED VIEW` is runtime and out of scope. A typed view body is imaginable, since EF
can render a LINQ query to SQL at design time (`ToQueryString()`), but the stored form is still the
SQL text, and EF's translation changing across versions would re-create the view on every upgrade.

### 7.7 Declarative partitioning (PostgreSQL): feasibility

Set aside as the one feature that must change EF's own `CREATE TABLE`. PostgreSQL cannot convert a
regular table into a partitioned one; `PARTITION BY` has to be in the `CREATE TABLE`. So the
contributor must **rewrite** the base `CreateTableOperation`, not append to the list, and that is
the capability the foundation lacks until the rewrite hook in 5.3 exists.

The design-time route, with no runtime wiring: the rewrite replaces the `CreateTableOperation` with
a `SqlOperation` whose text is the stock generator's own rendering of that operation (the differ
takes `IMigrationsSqlGenerator` as a dependency) with `PARTITION BY RANGE | LIST | HASH (cols)`
appended before the terminator, so column DDL is never re-implemented here. The alternative,
overriding `Generate(CreateTableOperation)` in the custom generator, degrades silently when the
wiring is missing: an unpartitioned table, no error, which is the failure this package exists to
prevent. Partitions are `CREATE TABLE t_2026_01 PARTITION OF t FOR VALUES FROM (…) TO (…)`
operations with identity parent, name and bounds, plus a `DEFAULT` partition, `DETACH` and `DROP`
on removal; they are declared as enumerable ranges (`HasRangePartitions(x => x.OccurredAt,
Monthly, from, to)`), so the set is deterministic and a new month is an ordinary diff. Automatic
future partitions (pg_partman) stay out. Validations, all at `migrations add`: the primary key and
every unique constraint must include the partition key; `IsCreatedConcurrently` conflicts with
partitioned tables and is rejected; sub-partitioning is deferred; a table that is unpartitioned in
the snapshot and partitioned in the target throws with guidance, because converting a populated
table is a manual data migration and stays out of scope permanently.

Verdict: feasible, not unimaginable, and not "solely by the foundation". The foundation needs the
rewrite hook and the design-time use of the stock generator; with those two, partitioning is a
contributor like the others, PostgreSQL-only, and the largest single feature in this document. It
is gated behind a spike: one partitioned entity with two range partitions and a unique constraint,
through `migrations add`, `Migrate()`, `EnsureCreated()`, a second migration that adds a partition,
and the snapshot round trip. If the generator-text approach proves brittle, the fallback is
rendering `CREATE TABLE` from the operation through the dialect, a larger but bounded job.

### 7.8 Triggers in general, and `WITH RECURSIVE`

Generic triggers stay a non-goal (D12): a body is arbitrary SQL, nothing diffs, and every consumer
would get a different trigger under one API. What the umbrella admits is triggers whose semantics
the package owns: row constraints (4.2), the templates in 7.5, and the closure table below.

`WITH RECURSIVE` is a query construct, not a schema object. EF Core has no recursive-CTE support in
LINQ, and that gap lives in the query pipeline, a different seam this package does not touch. It
reaches the migrations seam in two forms. As the raw body of a recursive view (7.6). And as
**hierarchy support**: a trigger template that maintains a closure table (`ancestor_id`,
`descendant_id`, `depth`) on insert, on parent change and on delete, declared as
`HasClosureTable(x => x.ParentId)`. The closure table is an ordinary entity, so ancestors and
descendants are plain LINQ joins, which is what an application without recursive CTEs actually
needs. Identity is table, parent column and closure table name. It is the second concrete reason
the templates in 7.5 are in.

### 7.9 What each item needs from the foundation

| Item | Contributor as-is | Placement below tables | Rewrite hook | Stock generator at design time | Type-mapping plugin | Translator addition |
|---|---|---|---|---|---|---|
| 7.1 policies | yes | | | | | session-setting marker |
| 7.2 statistics | yes | | | | | |
| 7.3 domains | | yes | | | yes | `VALUE` root |
| 7.4 deferrable | FK half | | unique half | | | |
| 7.5 templates | from 4.2 | | | | | |
| 7.6 views, procedures | yes | | | | | |
| 7.7 partitioning | | | yes | yes | | |
| 7.8 closure tables | from 7.5 | | | | | |

## 8. EF Core 11 in the family

- The `net11.0` target is added in the first 6.x after GA (D8), with `[11.0.0, 12.0.0)` floors on
  `Microsoft.EntityFrameworkCore.Abstractions`, `Npgsql.EntityFrameworkCore.PostgreSQL` and
  `Microsoft.EntityFrameworkCore.SqlServer` for that target.
- **Zero-DDL handover.** A `HasComplexIndex` whose name and columns match a native EF Core 11
  `HasIndex` over the same complex-type path scaffolds nothing instead of drop-and-create, and the
  native-collision check learns the difference between "same index, moved" and "two indexes, one
  name". Same for `WITHOUT OVERLAPS` and `PERIOD` against Npgsql 11.
- **Constructor.** `#if NET11_0_OR_GREATER` around the differ constructor for the sixth parameter,
  the day it ships.
- **Snapshot generator.** `CSharpSnapshotGenerator` was refactored in rc.1 (parameters object,
  centralised annotation handling, complex-collection branch). Not subclassed here, but the
  round-trip fixtures must be regenerated on the `net11.0` run.
- The integration job needs the .NET 11 SDK; `PostgresIntegrationTests` runs twice.

## 9. Risks and open questions

- **D4 is Ricardo's call.** The document uses `Syntagma` as a working name; every candidate in
  5.1 reads the same, and the rename is mechanical.
- **The shared function's name is permanent** once a consumer's migration carries it (4.2); it is
  owner-branded, so D4 does not gate 5.4.0.
- **Name-uniqueness for temporal kinds** (4.0) is a behaviour change on a model that already fails
  at apply time. Changelog it as a fix.
- **Snapshot v2** produces one empty migration per consumer (5.5). A consumer with a CI gate on
  `has-pending-model-changes` sees it fail once after upgrading; the migration guide has to say so
  up front.
- **The `net11.0` edge** (section 3): an application on .NET 11 with EF Core 10 must stay on 5.x.
- **Trusted-publishing scope** (5.8) fails silently until updated.
- **`AddTrigger` in EF's model** (4.2): confirm what the snapshot generator emits and that Npgsql's
  update pipeline ignores it, before relying on it.
- **Default constraint names.** `RC_{table}` for the unconditional form only; conditional ones
  require a name. Revisit if a default from the predicate turns out to be wanted.
- **Effort.** 4.1 and 4.2 are each about the size of the 5.2.0 typed-filter work. Section 5 is
  the largest change since 5.0.0; the split into 5.3 to 5.6 is the commit order.

## 10. Sequence

| Step | Line | Content | Gate |
|---|---|---|---|
| 0 | 5.3.1 or 5.4.0 | Constraint-name registry across all kinds and native names (4.0) | `verify-the-guard` on each collision pair |
| 1 | 5.4.0 | Characterisation test for four-kind ordering; generic descriptor differ; `[Obsolete]` constants | Suite green after each kind moves |
| 2 | 5.4.0 | Delete constraint on PostgreSQL, docs, smoke-test discriminator | Integration test, `verify-the-guard` on every validation |
| 3 | pre-6.0 | D4 settled; trusted-publishing scope checked for the new ids; repository renamed | nuget.org confirmations |
| 4 | 6.0.0 | Rename and split; plugin host; dialect; translator move; snapshot v2; old-package guard; convention tests; migration guide | Smoke test on both providers from a cold cache; snapshot fixture from 5.x |
| 5 | post-6.0 | Deprecate the old ids with the alternate; `support/5.x` branch protection | |
| 6 | 6.1 | `net11.0` target after EF 11 GA; zero-DDL handover; SQL Server guards; typed check constraints | Both TFMs green, two smoke consumers per provider |
| 7 | 6.2 | Update constraints, append-only, SQLite package, violation mapper, column immutability | |
| 8 | 6.3+ | Horizon items by demand (section 7); the partitioning spike before it is scheduled | Spike passes end to end |

## 11. Sources

Retrieved 2026-09-19.

- EF Core 11 versions and GA: https://www.nuget.org/packages/Microsoft.EntityFrameworkCore ;
  https://learn.microsoft.com/en-us/ef/core/what-is-new/ ;
  https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/whatsnew
- Complex-type indexes: https://github.com/dotnet/efcore/issues/31246 ;
  https://github.com/dotnet/efcore/pull/38192
- Npgsql 11: https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL ;
  https://github.com/npgsql/efcore.pg/issues/3708 ; https://github.com/npgsql/efcore.pg/issues/3710
- Differ surface at rc.1:
  https://github.com/dotnet/efcore/blob/v11.0.0-rc.1.26425.128/src/EFCore.Relational/Migrations/Internal/MigrationsModelDiffer.cs
- Trigger demand: https://github.com/dotnet/efcore/issues/10770 ;
  https://github.com/win7user10/Laraue.EfCoreTriggers ;
  https://learn.microsoft.com/en-us/ef/core/modeling/entity-types (table triggers)
- Plugin mechanics: https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore/Internal/ServiceProviderCache.cs ;
  https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore/Infrastructure/CoreOptionsExtension.cs ;
  https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.Design/Design/Internal/DesignTimeServicesBuilder.cs ;
  https://github.com/dotnet/efcore/blob/release/10.0/src/EFCore.Design/Design/DesignTimeServiceCollectionExtensions.cs ;
  https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG.NTS/Extensions/NpgsqlNetTopologySuiteServiceCollectionExtensions.cs ;
  https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG.NTS/build/netstandard2.0/Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite.targets
- PostgreSQL: https://www.postgresql.org/docs/18/sql-createtrigger.html ;
  https://www.postgresql.org/docs/18/sql-createrule.html ;
  https://www.postgresql.org/docs/18/plpgsql-errors-and-messages.html
- nuget.org: https://learn.microsoft.com/en-us/nuget/nuget-org/deprecate-packages ;
  https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing ;
  https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation ;
  https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu5128 ;
  https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview ;
  https://www.nuget.org/packages/DotNet.Testcontainers ;
  https://www.nuget.org/packages/System.Reactive.Compatibility
