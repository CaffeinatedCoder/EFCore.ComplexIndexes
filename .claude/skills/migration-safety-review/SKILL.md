---
name: migration-safety-review
description: Review a change to EFCore.ComplexIndexes for migrations that scaffold and apply cleanly while being semantically wrong. Use when changing a migration differ, a migrations SQL generator, an annotation whitelist, a definition store, a .targets file, or packaging metadata — and before cutting a release.
---

# Migration-safety review

The dangerous failure in this repo is not a crash. It is a migration that scaffolds without
complaint, applies without error, and leaves the database with different semantics than the model
declared. Three of the eleven issues found in the 5.0.1 audit were exactly that, including the two
most severe. A consumer has no signal at all: `dotnet ef migrations add` succeeds, `dotnet ef
database update` succeeds, and the guarantee they asked for silently is not there.

Generic code review does not look for this. This skill is that lens.

## How to use this

Work through the sections that touch the change. For each question, answer it against the actual
code — not from the docs, which may describe the intent rather than the behaviour. When a question
has no obvious answer, write the test that answers it; that is usually faster than reasoning, and it
is what the audit did to confirm every finding.

Report findings with the failure scenario spelled out concretely: the model a user would write, the
DDL that results, and what the database ends up doing differently from what was declared.

## 1. Silent degradation across the two integration seams

This package hooks EF Core in two places, and the difference is load-bearing:

- **Design-time** (`IMigrationsModelDiffer`) is auto-wired. Whatever it emits lands in the migration.
- **Runtime** (`IMigrationsSqlGenerator`) is **not** auto-wired — consumers opt in with
  `UseNpgsqlComplexIndexes()`.

Anything that depends on the runtime seam degrades when a consumer forgets the wiring. Ask:

- Does this feature render its DDL at **design time** (baked into the migration as a `SqlOperation`
  or a fully-populated native operation), or does it depend on a custom SQL generator at apply time?
- If it depends on the generator: **render the operations through the stock provider generator and
  read the SQL.** Does it fail loudly, or does it produce valid-but-wrong DDL?
  `MigrationHarness.NpgsqlSql(operations, complexIndexWiring: false)` does this in one line.
- If it produces valid-but-wrong DDL, that is a bug regardless of what the docs say about requiring
  the wiring. Prefer moving the feature to design-time rendering. Failing that, it needs a loud
  failure — see `CustomMigrationsModelDiffer.RuntimeWiringSentinel`.

Worked example: `HasTemporalConstraint` emitted an `AddUniqueConstraintOperation` annotated with the
period column. Without the wiring, the stock generator rendered
`ALTER TABLE rooms ADD CONSTRAINT ak UNIQUE ("RoomId", "Period")` — valid DDL that applies cleanly
and permits exactly the overlapping rows the constraint exists to forbid. Fixed by rendering the
DDL at design time, which removed the dependency entirely.

## 2. Definition stores: what silently replaces what

Declarations are accumulated into annotations by `ComplexIndexStorage.AddOrReplace` and
`NpgsqlExclusionConstraintExtensions.Store`. Each has an **identity key**, and anything not in that
key is a facet that a re-declaration overwrites. Ask:

- What is the identity key for this store? Is every field that makes two declarations *genuinely
  different* part of it?
- If a user writes two declarations that differ only by a field outside the key, do they get two
  objects or one? Write the test.

Worked example: exclusion-constraint identity was the ordered elements alone. Declaring
"no overlap among active grants" and "no overlap among revoked grants" — same columns, different
filters — silently produced one constraint. The filter had to be in the key, because filtered
overlap protection is the entire reason the API exists.

## 3. Name collisions

Two declarations that resolve to the same index or constraint name produce two `CREATE`/`ADD`
statements under one name. Ask:

- Can two declarations resolve to the same name? Include **default** names, which depend on
  resolved column names and so are only known in the differ.
- Can a property-level and an entity-level declaration collide? They live in separate stores that
  cannot see each other.
- What happens at apply time — an error (PostgreSQL 42P07), or a silent overwrite? Exclusion
  constraints prefix every `ADD` with `DROP CONSTRAINT IF EXISTS`, so a duplicate name applies
  cleanly and the second constraint replaces the first. That is worse than the error.

Validation belongs at **two** levels: the declaration (fast feedback) and the differ (the only place
that sees across stores and knows default names). The differ check must validate the **target model
only** — a snapshot that already contains a collision has to stay diffable, or the model can never
be fixed.

## 4. Provider scoping and design-time registration

- A satellite's `.targets` must set `ForProvider` (`_Parameter2`). Without it, EF applies that
  satellite's differ to every provider, and a solution using two providers gets whichever NuGet's
  restore order registered last.
- Registration must be **order-independent**: the core package's design-time attribute rides along
  transitively next to a satellite's, and EF resolves last-registration-wins. See
  `ComplexIndexDesignTimeRegistration`.
- `.targets` must ship to **both** `build/` and `buildTransitive/`.

`PackagingConventionTests` now enforces the mechanical parts. The judgement part is: does this
change introduce a new way for two differs to compete?

## 5. Annotation flow

- Property-level annotations reach index operations only through `IsForwardedIndexAnnotation` — a
  **whitelist**. Never widen it to "everything except known keys": column facets leaked into
  scaffolded migrations that way, causing phantom drop/create churn.
- Entity-level provider annotations reach the operation **unfiltered**, which is why
  `ValidateCreateIndexOperation` exists.
- Validation must be scoped to operations this package built. Sweeping the finished operation list
  also catches the base differ's operations for native `HasIndex` declarations, turning any provider
  index option the satellite does not know about into a hard failure of the consumer's whole
  `migrations add`.
- Entity-level definitions round-trip through **JSON**. Enums flatten to numbers and integrals can
  degrade; a provider generator reading `as SomeEnum?` or `as int?` gets null and silently drops the
  option. Any new non-string annotation value needs a round-trip test.

## 6. Churn and the snapshot round trip

The differ compares against the *compiled* model snapshot. Ask:

- Does an unchanged model produce zero operations? Run the diff with the same model on both sides.
- Does it still produce zero after a snapshot round trip? `SnapshotRoundTripTests` compiles a real
  generated snapshot with Roslyn — extend it for new declaration types.
- Are values compared structurally? Arrays compare by reference under `object.Equals`, so
  structurally-identical annotation values from two model builds never match. Use
  `AnnotationValues.ValuesEqual`.

## 7. Operation ordering

Ordering is load-bearing and easy to break silently, because a wrongly-ordered migration usually
still scaffolds. Custom drops go **before** the base operations; creates and constraint adds go
**after**. Renames that reference a new table name go after the base `RenameTable`. If the change
adds a new operation kind, ask what it depends on and place it accordingly — then assert the index
positions in a test, as `OperationOrderingTests` does.

## Finishing

Before reporting, confirm each finding by running it. A hypothesis about differ behaviour is cheap
to check with `MigrationHarness`, and the audit's most severe findings all looked speculative until
the SQL was printed. For anything you then fix, apply the `verify-the-guard` skill.
