# CodoMetis.EFCore.SchemaKit: types, surface and engines

Design for #61, written 2026-10-19 against `EFCore.ComplexIndexes` 5.4.1, the .NET 11 SDK
11.0.100-rc.2.26504.105, EF Core 11.0.0-rc.2 (dotnet/efcore `release/11.0` at `c8a9187`) and Npgsql
11.0.0-rc.2 (`b038505`). **Proposed**, for the maintainer's review. It replaces `ROADMAP.md`
section 5 as the plan for SchemaKit; that section is input here, not a plan.

The rule is the one the CodoMetis.ValueRanges range types follow: **a declaration PostgreSQL or SQL
Server would reject does not compile.** It reaches past the public surface: the same closed
vocabulary governs the engines underneath, so an extension package cannot render an unresolved path
or an unquoted name either. Every "does not compile" claim below names a probe in
[`test/SchemaKit.ApiProbes`](../test/SchemaKit.ApiProbes), which compiles the forbidden code with the
SDK's own Roslyn (as a separate assembly, warnings as errors) against a prototype of this design and
asserts the diagnostic. Each probe also compiles an allowed twin that differs only in the forbidden
part, so no probe can pass on an unrelated typo. 30 probes, 11 behaviour tests.

- [1. Summary](#1-summary)
- [2. Code culture: what SchemaKit takes from EF Core and Npgsql](#2-code-culture-what-schemakit-takes-from-ef-core-and-npgsql)
- [3. Language features this design relies on](#3-language-features-this-design-relies-on)
- [4. The type model, from declaration to SQL](#4-the-type-model-from-declaration-to-sql)
- [5. Principles](#5-principles)
- [6. Core concepts](#6-core-concepts)
- [7. The chosen surface: declarations are values](#7-the-chosen-surface-declarations-are-values)
- [8. The alternatives and why they lose](#8-the-alternatives-and-why-they-lose)
- [9. The engines](#9-the-engines)
- [10. Extending SchemaKit from a CodoMetis package](#10-extending-schemakit-from-a-codometis-package)
- [11. CodoMetis.TypeKit](#11-codometistypekit)
- [12. What is left out (#39)](#12-what-is-left-out-39)
- [13. Every 5.x call and its new form](#13-every-5x-call-and-its-new-form)
- [14. The first slice: 11.0.0-preview.1 (#38)](#14-the-first-slice-1100-preview1-38)
- [15. Room for the horizon](#15-room-for-the-horizon)
- [16. Open questions for the maintainer](#16-open-questions-for-the-maintainer)

## 1. Summary

**Declarations are immutable values. A typed factory builds them from lambdas, one call applies a
list of them to EF's model, and engines with typed stages carry them to SQL.** Each contributor
(indexes, exclusion constraints, later guards, policies, ltree paths) owns a C# 15 `closed` record
union whose cases carry exactly the options the database accepts, so an option the database would
refuse is a parameter or a method that does not exist. Underneath, a declaration is resolved into a
value-equal descriptor of identifiers and SQL fragments, and rendered through a builder that has no
hole for a string. The kernel stores, diffs and orders declarations through one open interface and
never switches over a contributor's types.

```csharp
public sealed class RoleGrantConfiguration : IEntityTypeConfiguration<RoleGrant>
{
    public void Configure(EntityTypeBuilder<RoleGrant> b) => b.DeclarePostgres(pg =>
    [
        pg.Exclusion(
            pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
            where: x => x.RevokedAt == null,
            name: "ex_role_grant_active_period"),

        pg.Unique(x => x.Code.ToLower(), where: x => x.RevokedAt == null, nullsDistinct: false),

        pg.Gin(x => x.Payload, GinOpClass.JsonbPathOps),
    ]);
}
```

- **Surface** ([7](#7-the-chosen-surface-declarations-are-values)): of three surfaces compared
  ([8](#8-the-alternatives-and-why-they-lose)), this one makes all nine principles hold, is immutable
  all the way through, and puts each method's options in signature help.
- **Engines** ([9](#9-the-engines)): type mapping defers to the provider's mappings
  (`INpgsqlRangeTypeMapping` decides range-ness, as in Npgsql's own validator); expressions are
  translated at declaration into a closed tree, with EF's query pipeline as the test oracle; the
  differ splices design-time `SqlOperation`s around EF's sorted operations; annotations are
  `CodoMetis:{Owner}:{hash}` with the 5.x format read forever.
- **Extension** ([10](#10-extending-schemakit-from-a-codometis-package)): an ltree contributor is
  compiled end to end against the prototype, and the mistakes its author could make are compile
  errors.
- **TypeKit** ([11](#11-codometistypekit)): not adopted for 11.0.0; its `[Record]` analyzer is the
  one piece worth a later dependency.
- **First preview** ([14](#14-the-first-slice-1100-preview1-38)): the kernel plus exclusion
  constraints end to end.

## 2. Code culture: what SchemaKit takes from EF Core and Npgsql

SchemaKit sits inside EF Core's migrations pipeline and beside Npgsql's provider, so it should read
like them: services with explicit dependencies, plugins resolved as lists, annotations as the
model's extension slot, one generator method per operation, type mappings as the single source of
store-type truth. Read at dotnet/efcore `release/11.0` (`c8a9187`, 2026-10-16) and npgsql/efcore.pg
`v11.0.0-rc.2` (`b038505`, 2026-10-18). Where SchemaKit departs, the departure is the point of the
row.

| EF Core / Npgsql | What it does | SchemaKit |
|---|---|---|
| `*Dependencies` sealed records (`MigrationsSqlGeneratorDependencies`, `RelationalTypeMappingSourceDependencies`) | A service's constructor takes one record, so adding a dependency never breaks a derived class; consumers swap members with `with` | **Follows.** Every SchemaKit service that can be derived from takes a record: `SchemaContributorDependencies` (the provider's `ISqlGenerationHelper`, `IRelationalTypeMappingSource`, the dialect, the diagnostics logger), `SchemaKitDifferDependencies` |
| `MigrationsModelDiffer`'s positional five-parameter constructor, "internal API" docs on `GetDifferences` | The one service without a record; a subclass breaks when EF adds a parameter (EF 12 adds a sixth) | **Departs, by containing it.** SchemaKit's differ is the only class that derives from it, is internal, and takes EF's five plus one `SchemaKitDifferDependencies`. Contributors never see EF's differ |
| Plugin interfaces resolved as `IEnumerable<T>` (`IRelationalTypeMappingSourcePlugin` singleton, `IMethodCallTranslatorPlugin` scoped, `IConventionSetPlugin` scoped) | Extension without replacing a single service; first non-null wins for type mappings | **Follows.** `ISchemaContributor` is a multi-registration singleton; contributors are stateless |
| `UseNodaTime()` on `NpgsqlDbContextOptionsBuilder`, `NpgsqlNodaTimeOptionsExtension`, `AddEntityFrameworkNpgsqlNodaTime()`, `Validate` checking the plugin reached the internal provider, `PopulateDebugInfo["Npgsql:UseNodaTime"]` | The provider-plugin idiom, copied by CodoMetis.ValueRanges | **Follows to the name:** `UseSchemaKit()`, `SchemaKitOptionsExtension`, `AddEntityFrameworkNpgsqlSchemaKit()`, `PopulateDebugInfo["CodoMetis:SchemaKit"]` |
| `ReplaceService` | Last registration wins, silently | **Departs.** SchemaKit replaces the differ and generator once and checks nobody replaced them after it ([9.9](#99-the-ownership-check-57)) |
| `IAnnotatable`, `ConfigurationSource`, prefixes (`Relational:`, `SqlServer:`, `Npgsql:`) | Annotations are the model's extension slot; explicit configuration outranks conventions | **Follows.** Declarations are annotations under `CodoMetis:` ([9.7](#97-annotations-and-the-snapshot-58)), written with `ConfigurationSource.Explicit` |
| `NpgsqlAnnotationCodeGenerator` turns `Npgsql:*` annotations into fluent calls in snapshots | Snapshots read like configuration | **Departs.** SchemaKit annotations stay `HasAnnotation("CodoMetis:…", json)` in snapshots: a snapshot must not depend on public method names, and the declaration API is the part most likely to change between majors |
| `MigrationsSqlGenerator`: one virtual `Generate(XOperation, …)` per operation, dispatch by exact type, `UnknownOperation` on a miss | A new operation type is impossible without replacing the generator and the C# operation generator | **Follows the consequence.** Contributors emit only `SqlOperation`, the one operation type that round-trips through scaffolding, `Sort` and apply unchanged ([9.6](#96-sql-generation)) |
| `ISqlGenerationHelper.DelimitIdentifier`; Npgsql's quotes only when needed | Identifiers are delimited in one place | **Follows.** The dialect delegates to the provider's helper; nothing in a contributor spells a quote |
| `RelationalTypeMapping` (`StoreType`, `GenerateSqlLiteral`, `ElementTypeMapping`), `NpgsqlRangeTypeMapping.SubtypeMapping`, **`INpgsqlRangeTypeMapping`** (new in Npgsql 11, PR #3975) | The provider knows the store type; Npgsql's validator asks the mapping, not the CLR type, whether a column is a range | **Follows.** SchemaKit asks the same questions of the same mappings ([9.2](#92-type-mapping)); a ValueRanges column qualifies for `&&` because its mapping implements `INpgsqlRangeTypeMapping`, not because SchemaKit knows ValueRanges |
| `RelationalSqlTranslatingExpressionVisitor` + `IMethodCallTranslator` plugins + `QuerySqlGenerator` | LINQ to SQL, inside a query compilation context | **Departs, with EF as the oracle** ([9.3](#93-expression-translation)): declaration lambdas are translated without a context, into a closed expression tree, and checked against EF's query rendering in tests |
| `IModelValidator`, `CoreStrings`/`RelationalStrings`, `IDiagnosticsLogger<DbLoggerCategory.Migrations>` | Validation throws the first error; warnings are logged events with ids | **Follows for warnings, departs for errors:** SchemaKit collects every finding of a diff into one exception, each naming its declaration ([9.8](#98-validation-and-diagnostics)) |
| Closed unions | None in EF Core or Npgsql: hierarchies there are open (`SqlExpression`, `MigrationOperation`) and dispatch by `switch` with a default or by dictionary | **Departs**, on purpose: this is what the design adds ([section 4](#4-the-type-model-from-declaration-to-sql)) |

## 3. Language features this design relies on

Checked on 2026-10-19, on the installed SDK 11.0.100-rc.2.26504.105 (Roslyn 5.12.0-1.26504.105),
against Microsoft Learn's [What's new in C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)
(2026-09-11), the [`closed` modifier](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/closed)
and [union types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union)
pages (2026-08-14), the csharplang [closed hierarchies](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-15.0/closed-hierarchies.md)
and [unions](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-15.0/unions.md) speclets,
and CaffeinatedCoder/CodoMetis.ValueRanges#148's probe.

| Feature | Version | What this design uses it for | Verified |
|---|---|---|---|
| `closed` classes | C# 15 | Every contributor's declaration union and every fixed option set | `closed` is implicitly abstract (`abstract closed` is CS9384), class-only (CS0106 on a struct or interface). A switch over the direct cases needs no discard; a missing case is CS8509, a warning, an error under `TreatWarningsAsErrors`. Deriving in another assembly is CS9382. Exhaustiveness holds **across assemblies** with the SDK's compiler: a consumer's complete switch over `PgIndex` compiles clean. nuget.org's newest Roslyn (5.9.0) gets that last point wrong, which is why the probes pin the SDK's 5.12.0-1 from the dotnet-tools feed. `IsClosedTypeAttribute` is in `System.Private.CoreLib` on net11.0, so no polyfill. |
| Extension members | C# 14 | `extension(PgKey<string>)` for `Collate`, `extension<TEntity>(EntityTypeBuilder<TEntity>)` for the apply calls, and the seam for other packages to add factory methods | Shipped with .NET 10 |
| `field` keyword | C# 14 | `init => field = Check(value)`: the run-time guard for what bypasses the types (`with`, the annotation reader) | Shipped |
| Collection expressions, `[CollectionBuilder]` | C# 12 | Key lists: `[pg.Key(x => x.A), pg.Key(x => x.B).Desc()]`, each element converted to the method's key type | Shipped |
| Interpolated string handlers | C# 10 | `SqlHandler`: DDL holes that accept dialect-checked values and no `string` ([9.6](#96-sql-generation)) | Shipped |
| Static abstract interface members | C# 11 | `ISchemaDeclaration<TSelf>`: the read model deserializes a contributor's annotations without a service provider | Shipped |
| `union` | C# 15 | **Not used** in the first slice ([below](#why-closed-records-and-not-union)) | Compiles on net11.0 rc.2 with the runtime's `UnionAttribute` and `IUnion` |

Both C# 15 features compile at the default `LangVersion` (15.0) on rc.2, with no `preview` switch,
and `LangVersion` 14 rejects them (CS9327 for `closed`). Learn still labels C# 15 preview and no page
says the features are final, so SchemaKit pins the SDK in `global.json` until .NET 11 GA. The
previews are prerelease packages, so a language change before GA costs a preview, not a release.

### Why closed records and not `union`

ValueRanges reached the same answer in its #148 and #149, and the reasons carry over:

- A union combines types that already exist; SchemaKit's cases are its own types, and they share
  members (`Name`, `Where`, `Concurrently`) that a union cannot declare.
- A union is a struct over `object? Value`: value-type cases box, the default value is an empty
  union, and there is no `with`. Records give value equality, `with` and inheritance for free.
- System.Text.Json's union support recognizes the BCL attribute only, and the v2 annotation format
  is JSON ([section 9.7](#97-annotations-and-the-snapshot-58)).

`union` stays available for a place where it does fit: combining existing, unrelated types, such as
a storage-parameter value of `int`, `bool` or `string` if a contributor needs one.

## 4. The type model, from declaration to SQL

The public surface is the visible end of one type model that also governs the internals. A
declaration passes four stages, and each stage is a type the next one cannot be handed in place of
another:

```
  OnModelCreating                         dotnet ef migrations add / EnsureCreated / pending check
  ────────────────                        ─────────────────────────────────────────────────────────
  PgSchema<TEntity>                        SchemaResolver                SchemaSql
  typed factory          ──►  PgIndex  ──► (paths → columns,  ──►  PgIndexDescriptor ──► (holes: Identifier,  ──► SqlOperation
  (lambdas, options)          (closed       JSON, store types)      (Identifier,          SqlFragment, SqlName,
                               union,                               SqlFragment,          SqlOperator, numbers)
                               value)       annotation              value equality)
                                  │         CodoMetis:Pg.Index:…        │
                                  ▼                                     ▼
                              read model                         the differ: equal → nothing,
                              GetDeclarations<PgIndex>()         equal but name → Rename,
                                                                 else Drop + Create, by placement
```

| Stage | Types | Holds | Closed where | Who builds it |
|---|---|---|---|---|
| **Declaration** | `PgIndex` (`BTree`, `Hash`, `Gist`, `SpGist`, `Gin`, `Brin`), `PgExclusion` (`Gist`, `SpGist`), `SqlServerIndex` (`Clustered`, `Nonclustered`); keys `BTreeKey`, `GinKey`, …; `KeyTarget` (`Column`, `Template`); `Predicate`; options `Deferral`, `GistBuffering`, `NullOrder`, `Compression` | Property paths, placeholder templates, typed options. What the model says, unresolved | Every union and every option set | The typed factory, from lambdas; the annotation reader |
| **Descriptor** | `PgIndexDescriptor`, `PgExclusionDescriptor`, `SqlServerIndexDescriptor` (`ISchemaDescriptor`) | `Identifier`s, `SqlFragment`s, `Operational<T>` flags. What the database will hold | Value equality is the diff identity | The contributor's `Resolve`, through `SchemaResolver` |
| **Statement** | `SchemaSql`, `SqlHandler` | DDL text whose holes are only dialect-checked values | No `AppendFormatted(string)` | The contributor's `Create`/`Drop`/`Rename` |
| **Operation** | `SqlOperation` | The text EF scaffolds, sorts and applies | One operation type | The kernel |

Three properties hold across the stages, and each is a probe:

- **A contributor renders only what was resolved.** `Create` takes the descriptor, not the
  declaration (`E4.render-a-declaration`, CS0115), and the descriptor holds identifiers and SQL, not
  paths.
- **Nothing reaches the DDL unquoted.** `SchemaSql`'s interpolation holes accept `Identifier`,
  `TableRef`, `SqlName`, `SqlOperator`, `SqlFragment`, `int` and `bool`; a `string` hole is CS1503
  (`E1.string-in-ddl`). `Identifier` and `SqlFragment` have no public constructor and no conversion
  from `string` (`E2.fragment-from-string` CS1729, `E3.identifier-from-string` CS0029); only the
  dialect makes an `Identifier`, after checking its length.
- **Every switch is over a union its owner closed.** The declaration union, the descriptor's
  method fragment, the option singletons, `KeyTarget`, `NameClaim`, `SchemaPlacement`,
  `SchemaDiagnostic`: each switch in the prototype has no discard, so a new case is CS8509 at every
  switch over it (`P9.missing-arm`, `X1.extension-misses-its-own-case`).

This is where the design goes past ValueRanges. There, a range union is closed by a private
constructor and a throwing `_` arm, which C# could not prove. Here `closed` proves it, and the same
discipline carries from the value a user declares down to the operation EF executes. The open
points are exactly two: `ISchemaObject`/`ISchemaContributor` at the seam, and `Sql.Raw<T>` in a
lambda. Both are named, and neither is reachable by accident.

## 5. Principles

Each principle comes with the 5.x declaration it retires, the forbidden SchemaKit declaration, and
the diagnostic the probe asserts. In 5.x every one of these compiled; most failed only when the
migration was applied. These are the principles of the public surface (P probes); the engine seam
has its own in [section 4](#4-the-type-model-from-declaration-to-sql) (E probes), and the extension
walkthroughs in [section 10](#10-extending-schemakit-from-a-codometis-package) (X and V probes).

### P1. The access method decides which options exist

`PgIndex` is a closed hierarchy with one case per access method, and each case carries exactly what
PostgreSQL accepts for that method: ordering, `NULLS FIRST/LAST`, `UNIQUE` and
`NULLS NOT DISTINCT` on B-tree only; `INCLUDE` on B-tree, GiST and SP-GiST; one key on Hash and
SP-GiST; operator classes typed by method. The factory takes options as parameters, so an option
the method does not have is not there to pass.

```csharp
// 5.x: compiled, scaffolded, and failed at apply with "access method "gin" does not support unique indexes"
c.Property(x => x.Tags).HasComplexIndex(ix => ix.UseGin().IsUnique());

pg.Gin(x => x.Tags, unique: true);                              // CS1739  P1.gin-unique
pg.Unique([pg.Key(x => x.Tags).Ops(GinOpClass.ArrayOps)]);      // CS0029  P1.gin-unique-key
pg.Gin(x => x.Tags, include: [x => x.Rank]);                    // CS1739  P1.gin-include
pg.Hash(x => x.Name, include: [x => x.Rank]);                   // CS1739  P1.hash-include
pg.Gin([pg.Key(x => x.Tags).Desc()]);                           // CS0029  P1.gin-desc
pg.Gist([pg.Key(x => x.Period).NullsLast()]);                   // CS0029  P1.gist-nulls
pg.Hash([pg.Key(x => x.Name), pg.Key(x => x.Rank)]);            // CS9174  P1.hash-two-keys
pg.BTree(x => x.Name, nullsDistinct: false);                    // CS1739  P1.nulls-not-distinct-without-unique
pg.BTree([pg.Key(x => x.Payload).Ops(GinOpClass.JsonbPathOps)]);// CS0029  P1.opclass-of-other-method
```

How the keys do it: `pg.Key(x => …)` returns a `PgKey<TValue>` that belongs to no method yet and
converts implicitly to every method's key type. `Desc()`, `NullsFirst()`, `NullsLast()` and
`Ops(BTreeOpClass)` return a `BTreeKey`, which converts to nothing else; `Ops(GinOpClass)` returns a
`GinKey`, and so on. The step that adds a method-specific option is the step that decides where the
key may go.

### P2. A declaration's stage is its type

An exclusion constraint of equality elements only is a unique constraint, and one with no elements
is a syntax error. The element list is built in stages: `pg.Equal(…)` starts an
`ExclusionDraft<TEntity>`, which converts to nothing, and the first non-equality element turns it
into `ExclusionElements<TEntity>`, the only type `pg.Exclusion` accepts. Order is kept, so the
5.x scheduling shape renders the same DDL.

```csharp
// 5.x: R34/R35, an ArgumentException in OnModelCreating
pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.RoleId));   // CS1503  P2.exclusion-without-overlap
```

Stages are immutable values, which is what makes them work. A mutating builder that stored each call
as it went would leave the half-built constraint in the model when the chain stopped early
([section 8](#8-the-alternatives-and-why-they-lose)).

### P3. Operands and options are typed by the CLR type, where the CLR type decides the store type

`Overlaps` has overloads for `NpgsqlRange<T>`, `NpgsqlRange<T>?`, `T[]` and `List<T>` (ranges,
multiranges and arrays) and nothing else. `Collate` is an extension on `PgKey<string>` and
`PgKey<char>` only.

```csharp
// 5.x: R43 at migrations add for temporal; for exclusion, "operator does not exist" at apply
pg.Equal(x => x.GranteeId).Overlaps(x => x.Kind);         // CS0411  P3.overlaps-on-text
pg.Key(x => x.Rank).Collate("C");                         // CS1929  P3.collate-on-integer
```

Where only the model knows the store type, the compiler cannot help: a `ToJson()` complex property
is a `Payload` in C# and `jsonb` in the database, so `GinOpClass.JsonbPathOps` on it is checked at
`migrations add`, not at compile time. Other packages add overloads for their own types through
C# 14 extension members on `PgSchema<TEntity>`, `ExclusionDraft<TEntity>` and
`ExclusionElements<TEntity>`: the ValueRanges EF satellite can make `Overlaps(x => x.Validity)`
compile for a `DateRange` without SchemaKit knowing the type.

### P4. Each provider has its own vocabulary, and its own list

SQL Server's index is `Clustered | Nonclustered`, because SQL Server allows `INCLUDE` and a filter
only on a nonclustered index; its key has `Desc()` and no null ordering. Each provider's apply call
takes a list of its own marker interface (`IPostgresObject`, `ISqlServerObject`), so a declaration for
one provider does not go into the other's list.

```csharp
// 5.x: R51-R53 at migrations add, R29/R49 for the cross-provider case
ss.Nonclustered([ss.Key(x => x.Name).NullsLast()]);                 // CS1061  P4.sqlserver-nulls-order
ss.Clustered(x => x.Name, include: [x => x.Rank]);                  // CS1739  P4.sqlserver-clustered-include
ss.Clustered(x => x.Name, where: x => x.DeletedAt == null);         // CS1739  P4.sqlserver-clustered-filter
people.DeclareSqlServer(ss => [PgSchema.For<Person>().Gin(x => x.Tags)]); // CS0029  P4.postgres-declaration-on-sqlserver
```

### P5. SQL is a call you write, never a string you pass

Every filter, key and element is a typed lambda. Verbatim SQL is `Sql.Raw<T>("…")` inside one, with
the same `{Property.Path}` placeholders 5.x filters take: one greppable escape hatch, typed by `T`, so
`Collate` and `Overlaps` still apply to it. A string where a lambda is expected does not convert.

```csharp
// 5.x: filter: "deleted_at IS NULL" was the default spelling, and a typo surfaced at apply
pg.BTree(x => x.Name, where: "deleted_at IS NULL");                          // CS1503  P5.string-filter
pg.BTree(x => x.Name, where: x => Sql.Raw<bool>("{DeletedAt} IS NULL"));     // compiles
```

### P6. Options with a fixed set of values are closed

`Deferral`, `GistBuffering`, `NullOrder` and SQL Server's `Compression` are closed records with
private cases and public singletons. A C# enum is an `int`: `(DbNullSort)7` compiles, and every
switch over an enum needs a discard. A closed option has no spelling outside its set, and "initially
deferred but not deferrable" has none at all.

```csharp
pg.Exclusion(pg.Overlaps(x => x.Period), deferral: (Deferral)2);   // CS0030  P6.deferral-out-of-range
```

### P7. Lambdas are bound to the entity being configured

The factory is `PgSchema<TEntity>`, handed in by `DeclarePostgres` on `EntityTypeBuilder<TEntity>`,
so every lambda's parameter is the configured entity. 5.x's `HasFilter<TEntity>` took a free type
argument and resolved same-named members against the wrong entity without an error.

```csharp
pg.BTree(x => x.Name, where: (Order o) => !o.Cancelled);   // CS1661  P7.filter-of-another-entity
```

`Predicate.Of<TEntity>` stays free on purpose, for conventions over an interface
([section 7](#conventions-are-functions)); its paths are checked against each entity it is applied to.

### P8. Declarations are values

Every declaration is a record with `init` properties. It can be compared, put in a collection,
shared across configurations and changed with `with`; it cannot be changed in place. No case has a
property whose valid values depend on another property, so `with` cannot build what the factory
would refuse (`NULLS NOT DISTINCT` lives inside `Uniqueness`, which only `PgIndex.BTree` has).

```csharp
var index = PgSchema.For<Person>().BTree(x => x.Name);
index.Name = "ix_people_name";                              // CS8852  P8.no-mutation
var named = index with { Name = "ix_people_name" };         // compiles
```

### P9. Closed inside: a contributor's union is complete, and only it can extend it

A case declared outside the contributor's assembly is refused, and a switch that misses a case is an
error in every contributor (and every consumer switch) at once.

```csharp
public sealed record LtreeIndex(ValueList<BTreeKey> Keys) : PgIndex;   // CS9382  P9.no-foreign-case

index switch { PgIndex.BTree => …, …, PgIndex.Gin => … }               // CS8509  P9.missing-arm (Brin)
```

ValueRanges could only guarantee this at run time (a private constructor and a throwing `_` arm);
`closed` makes it a compile-time property. Other packages extend SchemaKit at the seam instead
([section 9.10](#910-the-contributor-contract-at-a-glance-56)): `LtreeIndex` implements `IPostgresObject` and is its own
contributor's union.

### What the compiler cannot see

The type system covers structure: which options exist, which stage a value is in, which operand
types an operator takes, which provider a declaration belongs to. These stay checks at declaration
(`OnModelCreating`) or at `migrations add`, with the 5.x messages:

| Check | When | Why the compiler cannot | Future |
|---|---|---|---|
| Lambda bodies outside the translator's subset (`x.Name.Normalize()`, an enum comparison, a `DateTime` literal, an expression key on SQL Server) | Declaration | C# cannot restrict what an expression tree contains | A SchemaKit analyzer: [Q4](#16-open-questions-for-the-maintainer) |
| An empty key list (`pg.BTree([])`) | Declaration | No collection type a collection expression can target is non-empty | |
| Operator classes and operators against a store type only the model knows (`jsonb_path_ops` on a `ToJson()` property) | `migrations add` | The CLR type is not the store type | |
| Identifier length (63 bytes, 128 characters), name collisions within a kind and across kinds | `migrations add` | Strings, and the default names depend on resolved columns | |
| A second clustered index on a SQL Server table, or one next to a clustered primary key | `migrations add` | A property of the whole table, not of one declaration | |
| Server-version gates (`INCLUDE` on GiST needs 12, on SP-GiST 14; `NULLS NOT DISTINCT` 15) | `migrations add` with a declared minimum version | The server version is configuration | [Q7](#16-open-questions-for-the-maintainer) |
| `with` or the annotation reader building an equality-only exclusion constraint | Construction (`init` guard) | `with` bypasses the stage types | |

## 6. Core concepts

| Concept | Type (kernel unless noted) | What it is |
|---|---|---|
| Declaration | `ISchemaObject` (open), `ISchemaDeclaration<TSelf>` | One schema object a model declares. Open interface for the kernel; each contributor's union implements it, with static codec members |
| Declaration union | `PgIndex`, `PgExclusion` (PostgreSQL), `SqlServerIndex` (SQL Server) | A `closed` record hierarchy, one case per shape the database distinguishes |
| Provider marker | `IPostgresObject`, `ISqlServerObject` | What a provider's apply call accepts; open, so other packages' declarations join the same list |
| Key target | `KeyTarget` = `Column(path)` \| `Template(sql)` | What a key or element is built from, unresolved |
| Key | `PgKey<TValue>`, `BTreeKey`, `GinKey`, `GistKey`, `SpGistKey`, `HashKey`, `BrinKey` (PostgreSQL); `SqlServerKey` | A key target plus the options its method allows |
| Operator class | `BTreeOpClass`, `GinOpClass`, … | Typed by method; well-known ones as statics, any other by name |
| Exclusion element | `ExclusionElement`, `ExclusionOperator`, stages `ExclusionDraft<T>` → `ExclusionElements<T>` | A key and the operator two rows must not both satisfy |
| Predicate | `Predicate` (stored as placeholder SQL); `Predicate.Of<T>(…)` | A partial-index or constraint condition |
| Expression tree | `SchemaExpression`, `SchemaFunction`, `[SchemaFunction]` | The closed tree lambdas translate into ([9.3](#93-expression-translation)) |
| Raw SQL | `Sql.Raw<T>("…")` | The one escape hatch, inside a lambda |
| Fixed option | `Deferral`, `GistBuffering`, `NullOrder`, `Compression` | Closed singletons, never enums |
| Factory | `PgSchema<TEntity>`, `SqlServerSchema<TEntity>` | Stateless struct of pure functions; `PgSchema.For<T>()` outside a configuration |
| Apply | `DeclarePostgres`, `DeclareSqlServer` (on `EntityTypeBuilder<T>`); `Declare` (on `IMutableEntityType`) | Stores each declaration as one annotation |
| Read model | `GetDeclarations<T>()`, `FindDeclaration<T>(name)` | Returns the values that were declared; the differ reads the same |
| Amend | `Redeclare<T>(Func<T, T>)`, `AndWhere(Predicate)` | Replace declarations with a function of themselves |
| Store identity | `ISchemaObject.Identity` | Same identity on an entity replaces; hashed into the annotation key |
| Descriptor | `ISchemaDescriptor`; `PgIndexDescriptor`, … | A resolved declaration; its value equality is the diff identity, `WithName` detects renames |
| Operational flag | `Operational<T>` | A descriptor field never compared (`CONCURRENTLY`) |
| Resolver | `SchemaResolver` | Paths to SQL for one table, both diff sides alike ([9.4](#94-resolution)) |
| Store facts | `StoreFacts` | What the provider's type mapping says about a key ([9.2](#92-type-mapping)) |
| SQL vocabulary | `Identifier`, `TableRef`, `SqlName`, `SqlOperator`, `SqlFragment` | Values only the dialect or the resolver can make |
| Statement builder | `SchemaSql`, `SqlHandler` | Interpolated DDL with no hole for a string ([9.6](#96-sql-generation)) |
| Dialect | `ISchemaDialect` | Delimiting (through the provider's `ISqlGenerationHelper`), identifier limits, operator grammar, literals |
| Name claim | `NameClaim` = `Relation` \| `TableIndex` \| `Constraint` \| `Trigger` | The namespaces a name occupies ([9.8](#98-validation-and-diagnostics)) |
| Diagnostic | `SchemaDiagnostic`, `SchemaValidationException` | A finding naming its declaration; all findings thrown together |
| Contributor | `ISchemaContributor` (open), `SchemaContributor<TDeclaration, TDescriptor>` | Resolves, renders and claims names for one union ([9.10](#910-the-contributor-contract-at-a-glance-56)) |
| Placement | `SchemaPlacement` = `BeforeTables` \| `AfterTables(order)` | Where a contributor's operations go relative to EF's |

## 7. The chosen surface: declarations are values

The same declarations throughout this section and [section 8](#8-the-alternatives-and-why-they-lose),
so the three surfaces can be compared line by line:

1. a unique, partial index on a complex-type member, nulls not distinct, covering a second column
2. a GIN index with `jsonb_path_ops` on a JSON document
3. an expression index on `lower(email)`, descending on a second key with nulls last
4. the scheduling exclusion constraint: no overlapping periods per grantee and role among rows not revoked
5. on SQL Server, a clustered index and a covering nonclustered one

```csharp
public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b) => b.DeclarePostgres(pg =>
    [
        pg.Unique(x => x.Email.Value,                                       // 1
            where: x => x.DeletedAt == null,
            include: [x => x.Name],
            nullsDistinct: false,
            name: "ux_people_email"),

        pg.Gin(x => x.Payload, GinOpClass.JsonbPathOps),                    // 2

        pg.BTree([pg.Key(x => x.Email.Value.ToLower()),                     // 3
                  pg.Key(x => x.ReviewedAt).Desc().NullsLast()],
            name: "ix_people_email_reviewed"),
    ]);
}

public sealed class RoleGrantConfiguration : IEntityTypeConfiguration<RoleGrant>
{
    public void Configure(EntityTypeBuilder<RoleGrant> b) => b.DeclarePostgres(pg =>
    [
        pg.Exclusion(                                                        // 4
            pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
            where: x => x.RevokedAt == null,
            deferral: Deferral.InitiallyDeferred,
            name: "ex_role_grant_active_period"),
    ]);
}

// 5, in a SQL Server model
b.DeclareSqlServer(ss =>
[
    ss.Clustered(x => x.CreatedAt),
    ss.Nonclustered(x => x.Email.Value, unique: true, where: x => x.DeletedAt == null,
        include: [x => x.Name], fillFactor: 80, online: true),
]);
```

The DDL is 5.x's, clause for clause (`PrototypeBehaviourTests` renders an index of this shape and
the exclusion constraint through the prototype's contributors and asserts the text):

```sql
CREATE UNIQUE INDEX "ux_people_email" ON "people" ("email_value") INCLUDE ("name") NULLS NOT DISTINCT WHERE "deleted_at" IS NULL;
CREATE INDEX "IX_people_payload" ON "people" USING gin ("payload" jsonb_path_ops);
CREATE INDEX "ix_people_email_reviewed" ON "people" ((lower("email_value")), "reviewed_at" DESC NULLS LAST);
ALTER TABLE "role_grants" DROP CONSTRAINT IF EXISTS "ex_role_grant_active_period";
ALTER TABLE "role_grants" ADD CONSTRAINT "ex_role_grant_active_period"
  EXCLUDE USING gist ("grantee_id" WITH =, "role_id" WITH =, "period" WITH &&)
  WHERE ("revoked_at" IS NULL) DEFERRABLE INITIALLY DEFERRED;
```

### Values outside a configuration

A declaration is a value, so it can live in a field, be shared, and be tested without a model:

```csharp
static class Grants
{
    static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    public static readonly PgExclusion ActivePeriod = Pg.Exclusion(
        Pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedAt == null);
}

[TestMethod]
public void Active_period_ignores_revoked_rows() =>
    Assert.AreEqual("{RevokedAt} IS NULL", Grants.ActivePeriod.Where!.Template);
```

### Conventions are functions

5.x's mutable API (`AddComplexIndex`, `AddComplexIndexFilter`, `AddExclusionConstraintFilter`) becomes
two calls on `IMutableEntityType`: `Declare` takes values, `Redeclare` maps them. The convention "every
unique index and exclusion constraint on a withdrawable aggregate ignores revoked rows" is:

```csharp
var notRevoked = Predicate.Of<IWithdrawable>(x => x.RevokedAt == null);

foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.ClrType.IsAssignableTo(typeof(IWithdrawable))))
{
    entity.Redeclare<PgIndex>(ix => ix is PgIndex.BTree { Unique: not null } ? ix.AndWhere(notRevoked) : ix);
    entity.Redeclare<PgExclusion>(ex => ex.AndWhere(notRevoked));
}
```

`AndWhere` keeps 5.x's idempotence rule (a predicate already present as the whole filter or as the
last conjunct is not added twice). Like 5.x, it amends what is declared at the time of the call, so it
belongs after the configurations.

### Reading declarations back

`GetDeclarations<PgIndex>()` and `GetDeclarations<PgExclusion>()` on an entity type, and
`FindDeclaration<T>(name)` on the model, return the values that were declared: the same records,
equal to them, deserialized from the annotations. The differ builds its descriptors from the same
reader and resolves columns on top, which keeps 5.x's rule that the read model is the differ's reader
true by construction. Declarations stay unresolved, as in 5.x: parts are property paths, and a
default-named declaration has a null `Name`.

### Why this one

- **Most uncompilable.** Every probe in [section 5](#5-principles) holds. P2 holds only because
  stages are immutable, and P8 only because declarations are values.
- **Immutable all the way.** Factories are pure, values are records, the apply call is the one
  mutation, and it is EF's own annotation store.
- **Discoverable.** `pg.` lists the methods by the names PostgreSQL uses (`BTree`, `Unique`, `Hash`,
  `Gist`, `SpGist`, `Gin`, `Brin`, `Exclusion`, `Key`, `Equal`, `Overlaps`); signature help on
  `pg.Gin(` shows exactly the options GIN has; a wrong option is CS1739 naming the parameter.
- **Fits `ModelBuilder`.** One extension call on `EntityTypeBuilder<T>`, inside or outside
  `IEntityTypeConfiguration<T>`, returning the builder for chaining. Nothing new to learn about where
  configuration goes.
- **Open at the seam, closed inside.** Other packages add factory methods by extension members and
  declarations by implementing `IPostgresObject`; nobody adds a case to SchemaKit's unions.
- **Snapshot.** The annotation is the value's JSON, so the format follows the type and the read model
  cannot drift from it ([section 9.7](#97-annotations-and-the-snapshot-58)).

The cost: two kinds of syntax for options (named parameters for indexes, a staged chain for exclusion
elements), and a list in a lambda (`pg => [ … ]`) that EF users have not seen before.

## 8. The alternatives and why they lose

### A. Staged, mutating fluent builders (EF style)

Extension methods on `EntityTypeBuilder<T>` returning builders whose type encodes the method and the
stage, writing annotations as each call is made, the way EF's `HasIndex(…).IsUnique()` does.

```csharp
b.HasPostgresIndex(x => x.Email.Value)                                  // 1
    .UsingBTree().IsUnique(nullsDistinct: false)
    .HasFilter(x => x.DeletedAt == null).Include(x => x.Name).HasName("ux_people_email");
b.HasPostgresIndex(x => x.Payload).UsingGin().HasOperators(GinOpClass.JsonbPathOps);      // 2
b.HasPostgresIndex(k => k.Key(x => x.Email.Value.ToLower())                                // 3
                         .Key(x => x.ReviewedAt).Desc().NullsLast())
    .UsingBTree().HasName("ix_people_email_reviewed");
b.HasExclusionConstraint()                                              // 4
    .Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period)
    .HasFilter(x => x.RevokedAt == null).IsDeferrable(Deferral.InitiallyDeferred)
    .HasName("ex_role_grant_active_period");
b.HasSqlServerIndex(x => x.CreatedAt).IsClustered();                     // 5
b.HasSqlServerIndex(x => x.Email.Value).IsNonclustered().IsUnique()
    .HasFilter(x => x.DeletedAt == null).Include(x => x.Name).HasFillFactor(80).IsCreatedOnline();
```

- **Uncompilable:** most of P1 and P4 (`UsingGin()` returns a builder without `IsUnique`), but the
  method has to be chosen before any method-specific option, and anything called before
  `UsingGin()` — `HasPostgresIndex(…).Include(…)` — is already stored. P2 fails outright: the
  builder has written the equality elements by the time the chain stops short of `Overlaps`, so the
  half-built constraint is in the model and the error moves to `migrations add`. P8 fails: builders
  are the opposite of values.
- **Immutability:** none; each call writes an annotation.
- **Discoverability:** good for a single option, poor for the whole: `b.` lists SchemaKit's entry
  points among EF's hundred members, and what a GIN index accepts is spread over a chain of return
  types.
- **Fit with `ModelBuilder`:** the best of the three; it is EF's own idiom.
- **Seam:** other packages add extension methods on SchemaKit's builder types, which then have to be
  public and stable as well, a second public surface beside the declarations.
- **Snapshot:** the same format is possible, but a re-declaration has to find and update what earlier
  calls stored, which is the class of bug 5.x had ("property-level re-declaration keeps stale
  annotations").

Augmenting EF's own `IndexBuilder` (`HasIndex(…).HasMethod("gin")`, which Npgsql already offers as
strings) was considered as a variant of A and set aside: EF's builder cannot be made method-typed from
outside, so none of P1 is reachable, and expression keys cannot be EF indexes at all.

### B. Declarations as values (chosen)

[Section 7](#7-the-chosen-surface-declarations-are-values).

### C. Data-first records with object initializers

The same closed records as B, built with `new` and `required` members instead of a factory.

```csharp
b.DeclarePostgres(
[
    new PgIndex.BTree([Key.Of<Person>(x => x.Email.Value)])                                  // 1
    {
        Unique = new Uniqueness(NullsDistinct: false),
        Where = Predicate.Of<Person>(x => x.DeletedAt == null),
        Include = [Path.Of<Person>(x => x.Name)],
        Name = "ux_people_email",
    },
    new PgIndex.Gin([Key.Of<Person>(x => x.Payload).Ops(GinOpClass.JsonbPathOps)]),          // 2
    new PgExclusion.Gist(                                                                      // 4
        Exclusion.Of<RoleGrant>().Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period))
    {
        Where = Predicate.Of<RoleGrant>(x => x.RevokedAt == null),
        Deferral = Deferral.InitiallyDeferred,
    },
]);
```

- **Uncompilable:** P1 and P4 hold (`new PgIndex.Gin { Unique = … }` is CS0117), P2 holds through
  the element type, P6 and P8 hold. **P7 is lost:** every lambda names its entity
  (`Key.Of<Person>`), so nothing ties it to the entity being configured, which is 5.x's
  `HasFilter<TEntity>` hole again.
- **Immutability:** as B.
- **Discoverability:** object-initializer completion lists the properties, but nothing guides the
  constructor-versus-initializer split, and every lambda needs its type argument spelled out.
- **Fit with `ModelBuilder`:** as B for the apply call; the declarations read like data rather than
  configuration.
- **Seam, snapshot:** as B; it is B without the factory.

C is what B's values look like underneath, and remains available: the records' constructors are
public, so a code generator or a test can build them directly. As the authoring surface it loses P7
and the inferred entity type for nothing in return.

### Comparison

| | A. Staged builders | **B. Values** | C. Records |
|---|---|---|---|
| Uncompilable (of P1–P9) | P1 partly, P3–P7, P9; not P2 or P8 | **all nine** | all but P7 |
| Immutable | no | **yes** | yes |
| Discoverable | per call | **signature help shows each method's options** | property lists, explicit types |
| Fit with `ModelBuilder` | best | **one call per entity** | one call per entity |
| Extensible at the seam | builder types become public API | **extension members on the factory; new unions implement `IPostgresObject`** | new unions only |
| Snapshot | stale-state risk on re-declaration | **value's JSON, one annotation each** | as B |

## 9. The engines

The kernel is a set of engines, each with one responsibility, typed inputs and outputs, and one
seam. A contributor plugs into the seam; it never reimplements an engine. The prototype has a
working miniature of the diff and SQL engines (`Prototype/Kernel/Engine.cs`, `SqlText.cs`); the
others are specified here.

| Engine | Input | Output | Seam for a contributor | Leans on |
|---|---|---|---|---|
| [9.1 Services](#91-services-and-registration) | options, design-time services | one differ, one generator, the contributor list | `ISchemaContributor` registration | EF's service builders, Npgsql's plugin idiom |
| [9.2 Type mapping](#92-type-mapping) | a resolved column or JSON member | `StoreFacts` | none: it asks the provider | `IRelationalTypeMappingSource`, `INpgsqlRangeTypeMapping` |
| [9.3 Expression translation](#93-expression-translation) | a lambda, at declaration | `SchemaExpression` (closed tree), stored as a template | `[SchemaFunction]` marker methods | nothing at declaration; EF's query pipeline as test oracle |
| [9.4 Resolution](#94-resolution) | declaration + relational model | `SqlFragment`s, `Identifier`s | `SchemaResolver`, handed to `Resolve` | EF's `IRelationalModel`, `StoreObjectIdentifier` |
| [9.5 Model diffing](#95-model-diffing) | source and target models | ordered `SqlOperation`s | descriptor equality, `Rename`, `NameClaims`, placement | EF's differ for everything else |
| [9.6 SQL generation](#96-sql-generation) | a descriptor | statements | `Create`, `Drop`, `Rename` over `SchemaSql` | `ISqlGenerationHelper` |
| [9.7 Annotations and the snapshot](#97-annotations-and-the-snapshot-58) | declarations | `CodoMetis:` annotations, and back | `ISchemaDeclaration<T>` codec members | EF's snapshot generator |
| [9.8 Validation and diagnostics](#98-validation-and-diagnostics) | descriptors, claims | one exception listing every finding | `NameClaim`s, `SchemaDiagnostic` | EF's diagnostics logger for warnings |
| [9.9 The ownership check](#99-the-ownership-check-57) | the built service providers | a startup failure, or nothing | none | `CoreOptionsExtension.ReplacedServices` |

### 9.1 Services and registration

```csharp
options.UseNpgsql(cs, o => o.UseSchemaKit());          // the provider-builder idiom, as UseNodaTime()
options.UseSqlServer(cs, o => o.UseSchemaKit());
```

- `UseSchemaKit()` adds a `SchemaKitOptionsExtension` whose `ApplyServices` calls
  `AddEntityFrameworkNpgsqlSchemaKit()`: `EntityFrameworkNpgsqlServicesBuilder.TryAdd` for the
  dialect and the built-in contributors (singleton, multiple registrations), then
  `ReplaceService` for `IMigrationsModelDiffer` and, on PostgreSQL, `IMigrationsSqlGenerator`. Its
  `Validate` checks that the contributors reached the internal service provider, as NodaTime's and
  ValueRanges' do, and runs the ownership check ([9.9](#99-the-ownership-check-57)). Its
  `ExtensionInfo` writes `PopulateDebugInfo["CodoMetis:SchemaKit"]`, so two contexts with and without
  SchemaKit do not share a service provider.
- Another package registers its contributor from its own options extension:
  `o.UseHierarchies()` calls `services.AddSchemaKitContributor<LtreeContributor>()`, a plain
  `TryAddEnumerable`. Ordering is by placement, never registration order.
- **Design time** keeps the `.targets`-injected `DesignTimeServicesReferenceAttribute` with
  `ForProvider`, as Npgsql's NodaTime and NTS plugins do. EF seeds the design-time collection from
  the context's runtime services, so the attribute's job shrinks to registering the design-time-only
  pieces; with one differ type in the family, 5.x's back-off-and-remove registration
  (`ComplexIndexDesignTimeRegistration`) becomes a `TryAdd`.
- **Dependencies records.** `SchemaContributorDependencies` (the provider's `ISqlGenerationHelper`,
  `IRelationalTypeMappingSource`, the dialect, `IDiagnosticsLogger<DbLoggerCategory.Migrations>`)
  is the one constructor parameter of `SchemaContributor<,>`; adding a dependency in an 11.x minor
  breaks no contributor. The kernel differ takes EF's five positional parameters plus
  `SchemaKitDifferDependencies` and is internal, so EF 12's sixth parameter is SchemaKit's problem
  alone.

### 9.2 Type mapping

**Responsibility:** tell a contributor what a key is in the database, without SchemaKit owning a
single store-type rule. The provider already knows; SchemaKit asks.

```csharp
public sealed record StoreFacts(
    RelationalTypeMapping Mapping,    // the provider's mapping for the column or JSON member
    string StoreType,                 // Mapping.StoreType
    bool IsCollatable,                // text-like store types
    bool IsRange,                     // Mapping is INpgsqlRangeTypeMapping (PostgreSQL)
    bool IsMultirange,                // Mapping is NpgsqlMultirangeTypeMapping
    bool IsJsonDocument,              // NpgsqlJsonTypeMapping / NpgsqlStructuralJsonTypeMapping, jsonb
    RelationalTypeMapping? Element);  // ElementTypeMapping, SubtypeMapping
```

- **Source.** For a column, the relational model's `IColumn.PropertyMappings[0].TypeMapping`; for a
  JSON member, `IProperty.GetRelationalTypeMapping()` on the member, which Npgsql builds against the
  container's column type. SchemaKit never calls `FindMapping(Type)` on a CLR type: the store type is
  a model fact (`HasColumnType`, `ToJson()`, a value converter), not a CLR one.
- **Range-ness is the provider's answer.** `INpgsqlRangeTypeMapping` is new in Npgsql 11 (PR #3975,
  for NodaTime ranges in temporal keys); Npgsql's own model validator uses it to decide whether a
  `WITHOUT OVERLAPS` key part is a range. SchemaKit uses the same test for exclusion `&&` operands,
  so CodoMetis.ValueRanges' `ValueRangeTypeMapping`, which implements the interface without deriving
  from `NpgsqlRangeTypeMapping`, qualifies with no code in SchemaKit. Caveat: the interface sits in
  Npgsql's `Storage.Internal` namespace, so SchemaKit pins the test behind one internal helper and a
  test per Npgsql minor.
- **Where it is used:** validating operator classes and operators against the store type at
  `migrations add` ([what the compiler cannot see](#what-the-compiler-cannot-see)), choosing the cast
  for a JSON member (5.x's `CAST(… AS integer)`, `decode(…, 'base64')`, text for date and time),
  rendering a literal with `Mapping.GenerateSqlLiteral` when a predicate compares against a constant
  (the provider's spelling, not SchemaKit's), and deciding whether `btree_gist` is needed (a scalar
  `=` element under GiST).
- **No plugin seam.** A package that adds a store type adds an `IRelationalTypeMappingSourcePlugin`
  to the provider, and SchemaKit sees its mappings through the same source. That is the whole
  extension story for types, and it is why SchemaKit has no type registry of its own (unlike
  ValueRanges' process-global `RangeTypeRegistry`).

### 9.3 Expression translation

**Responsibility:** turn the lambdas in declarations (key expressions, `where:` predicates) into SQL
that matches what EF's queries produce, at declaration, without a `DbContext`.

**Two options weighed.**

| | A. EF Core's query pipeline | **B. A dedicated translator into a closed expression tree** |
|---|---|---|
| How | Build a query over the entity, translate with `RelationalSqlTranslatingExpressionVisitor` and the provider's method/member translators, render with `QuerySqlGenerator` | `LambdaTranslator` (5.x's `NpgsqlLinqIndexTranslator`, moved to the kernel) produces a `SchemaExpression` tree; the dialect renders it at diff time |
| When it can run | Only with a `QueryCompilationContext`, which needs a `DbContext` and its scoped services; `VisitParameter` refuses a bare lambda parameter until the queryable layer has bound it. Not at `OnModelCreating` | At declaration, with nothing but the lambda |
| Captured variables | Become parameters (`@p0`); an index predicate needs literals, so they would have to be re-inlined | Inlined as literals through the column's type mapping, or refused when there is no portable literal |
| Fidelity to queries | Identical by construction, for the EF version that rendered it | Identical by test: each rendering is checked against EF's `ToQueryString()` for the same member (5.x's `NpgsqlJsonMemberRenderingTests`) |
| Stability | EF changes its rendering in a major; an index expression that follows silently would rebuild every affected index on upgrade, or worse, not match the stored one | Rendering changes are deliberate and versioned (`r`, 5.x's `RenderingVersion`): an old declaration keeps its old rendering until it is re-declared |
| Function semantics | Translators may emit `STABLE` functions (`now()`, `textanycat`); PostgreSQL refuses them in an index | The tree's function set is closed and each entry is known `IMMUTABLE` |
| Extensibility | Every query translator plugin, for free | Marker methods (below) |

**Chosen: B, with A as the oracle.** The deciding row is "when it can run": declarations are values
built in `OnModelCreating`, and the lambda must be translated where it is written so the value is
complete, comparable and serializable. Stability decides it again for the snapshot.

```csharp
public closed record SchemaExpression
{
    public sealed record Path(string PropertyPath) : SchemaExpression;                 // resolved per side
    public sealed record Literal(object? Value) : SchemaExpression;                     // rendered by the column's mapping
    public sealed record Function(SchemaFunction Name, ValueList<SchemaExpression> Arguments) : SchemaExpression;
    public sealed record Binary(BinaryOperator Operator, SchemaExpression Left, SchemaExpression Right) : SchemaExpression;
    public sealed record Not(SchemaExpression Operand) : SchemaExpression;
    public sealed record IsNull(SchemaExpression Operand, bool Negated) : SchemaExpression;
    public sealed record Coalesce(ValueList<SchemaExpression> Operands) : SchemaExpression;
    public sealed record Raw(string Template) : SchemaExpression;                       // Sql.Raw<T>
}
```

- `SchemaFunction` is a closed set for the kernel's own (`lower`, `upper`, `btrim`, `ltrim`,
  `rtrim`, `substr`, `replace`, `length`, concatenation) plus one open case,
  `SchemaFunction.Declared(string StableName)`, which is how packages extend it:
  ```csharp
  // CodoMetis.Hierarchies
  public static class LtreeFunctions
  {
      [SchemaFunction("nlevel", Immutable = true)]
      public static int NLevel(this TreePath path) => throw new InvalidOperationException("Translated by SchemaKit, never called.");
  }
  // usage: pg.BTree(x => x.Path.NLevel())
  ```
  This is EF's `[DbFunction]` idea applied at declaration. The stable name, not the CLR method, is
  what the snapshot stores, so renaming the C# method never touches a migration. A function without
  `Immutable = true` is refused in an index expression at declaration.
- The tree is stored as the placeholder template 5.x already uses (`lower({Email.Value})`), so v1
  and v2 read alike; the tree is the in-memory form the dialect renders.
- The refusals stay where 5.x put them, at declaration: enums (the stored form depends on a value
  conversion), literals with no portable spelling (`DateTime`, `Guid`), methods outside the set. An
  analyzer could move them to compile time ([Q4](#16-open-questions-for-the-maintainer)).
- SQL Server gets typed filters for free: the same tree, rendered with brackets and `= 1` for a
  boolean (ROADMAP 5.4).

### 9.4 Resolution

**Responsibility:** one place that turns a declaration's paths into a table's SQL, identically for
both sides of a diff.

- `SchemaResolver` (handed to `Contributor.Resolve`) exposes `Table`, `Dialect`, `Target(KeyTarget)
  → SqlFragment`, `Predicate(Predicate) → SqlFragment`, `StoreFacts(KeyTarget)`, and
  `Name(declared, prefix, parts) → Identifier` (5.x's default-name rule, length-checked).
- It walks EF's relational model with `StoreObjectIdentifier`, carrying over 5.x's rules unchanged:
  complex-type columns and `HasColumnName`, the converter-member unwrap (`Email.Value` with a
  converter whose provider type matches), the property-bag rule for snapshots, JSON members rendered
  as Npgsql's queries render them, the JSON container as a plain column, `Sql.Raw` placeholders
  outside single-quoted literals only.
- **Both sides through the same resolver** is what makes rendering a non-event: a 5.x snapshot's
  declarations and the new model's are resolved by the same code, so a different quoting policy
  (Npgsql's helper quotes only when needed, 5.x always did) changes the DDL text of *new* statements
  and never produces a diff.
- Renamed tables: the source side resolves against the table's new identity, as 5.x does, so a
  table rename does not rebuild its indexes.

### 9.5 Model diffing

**Responsibility:** compute SchemaKit's operations and place them among EF's.

1. Run EF's `GetDifferences` (base), which is already sorted.
2. Read both models' declarations per contributor (v2 annotations, and 5.x's through the v1 reader).
3. Resolve each to a descriptor ([9.4](#94-resolution)).
4. Validate the **target** only ([9.8](#98-validation-and-diagnostics)): a snapshot that already
   holds a collision stays diffable.
5. Match: equal descriptors produce nothing; a pair where `from.WithName(to.Name).Equals(to)` is a
   rename if the contributor's `Rename` accepts it, otherwise drop and create; the rest are drops and
   creates. `Operational<T>` fields compare equal whatever their value, so `CONCURRENTLY` never
   rebuilds an index.
6. Order: `[drops by descending placement] + EF's operations + [renames] + [creates by ascending
   placement]`, with `SchemaPlacement.BeforeTables` objects created before EF's operations and dropped
   after them. This is 5.x's load-bearing order, generalised.

Details that come from reading EF's differ:

- **`SqlOperation` would land in `Sort`'s leftovers.** EF classifies by exact runtime type, and
  `SqlOperation` belongs to no bucket, so SchemaKit never passes its operations through `Sort`; it
  splices them around the sorted base list.
- **`HasDifferences` does not call `Sort`**, so SchemaKit overrides it to route through
  `GetDifferences` (5.x's `PendingModelChangesTests`), and never filters in a `Sort` override.
- **Dependencies between SchemaKit objects** (a trigger on a shared function; 5.x's temporal FK that
  had to rebuild with its principal constraint) are declared, not discovered: an optional
  `ISchemaDependent.DependsOn(TDescriptor) → IEnumerable<SchemaRef>` on a contributor. When a
  dependency is dropped or rebuilt, its dependents are rebuilt around it; creates are ordered
  topologically within a placement. EF's `Multigraph` is internal, so the kernel carries its own.
- **EF index renames are unconditional** (a name-only change is always `RenameIndexOperation`).
  SchemaKit's rename is the contributor's decision, which is how SQLite, which cannot rename an
  annotation-only index, stays correct.

### 9.6 SQL generation

**Responsibility:** turn a descriptor into DDL, with no way to emit an unquoted name.

- A contributor writes `sql.Statement($"CREATE INDEX {d.Name} ON {d.Table} ({sql.List(d.Keys)})")`.
  The literal parts are its own code; the holes go through `SqlHandler`, which has an
  `AppendFormatted` for `Identifier` (delimited by the provider's `ISqlGenerationHelper`), `TableRef`,
  `SqlName` (delimited when needed: operator classes), `SqlOperator` (checked against PostgreSQL's
  operator grammar), `SqlFragment`, `int` and `bool`, and none for `string` (`E1`, `X2`). EF's
  `FromSql($"…")` is the precedent; its holes become parameters, these become quoted identifiers.
- The kernel turns statements into `SqlOperation`s and sets `SuppressTransaction` where a statement
  asks for it (`CREATE INDEX CONCURRENTLY`). Under `MigrationsSqlGenerationOptions.Idempotent`,
  where EF wraps statements in a transaction-bound block, a suppressed-transaction statement is
  refused with the declaration named, the way Npgsql refuses `CONCURRENTLY` there.
- **Why only `SqlOperation`:** `CSharpMigrationOperationGenerator` throws `UnknownOperation` for a
  `MigrationOperation` subclass at `migrations add`, and `MigrationsSqlGenerator` dispatches by exact
  type and throws at apply. A new operation type would need SchemaKit to replace both generators and
  the snapshot would carry types consumers cannot compile without SchemaKit. `SqlOperation` is the
  one type that survives scaffolding, sorting and applying untouched.
- **Rendered at design time, always.** Indexes too: 5.x rendered them as `CreateIndexOperation`s and
  needed the runtime generator for expression and null-ordered ones (the `RuntimeWiringSentinel`).
  EF 11's own JSON indexes show the alternative's failure mode: SQL Server's `CREATE JSON INDEX` is
  rebuilt at apply time from whatever model the generator is handed, and falls back to a plain index
  without one. A design-time statement cannot degrade that way.
- **The custom generator stays** in the PostgreSQL package for one job: replaying migrations 5.x
  scaffolded (`CustomIndex:IndexParts`, the pre-5.0.2 temporal annotations), which consumers apply
  to fresh databases forever. It derives from `NpgsqlMigrationsSqlGenerator` and calls `base` for
  everything else, so Npgsql's `WITHOUT OVERLAPS`/`PERIOD` rendering is untouched.

### 9.7 Annotations and the snapshot (#58)

**Naming.** One annotation per declaration, on the declaring entity type:

```
CodoMetis:{Owner}:{hash}
```

- `CodoMetis:` is the owner prefix, never the product name (ROADMAP 5.1), since keys live in
  consumers' snapshots forever. It sits beside EF's `Relational:` and the providers' `SqlServer:` and
  `Npgsql:`.
- `Owner` is the contributor's namespace: `Pg.Index`, `Pg.Exclusion`, `SqlServer.Index`, later
  `Pg.RowConstraint`, `Ltree.Path`. A package picks its own; the kernel refuses two contributors with
  one owner at registration.
- `hash` is the first 16 hex digits of SHA-256 over the declaration's **store identity**. Declaring
  the same identity again overwrites the same annotation, which is the replace rule without a
  lookup, and a rename rewrites the value in place.
- Model-level declarations (domains, ROADMAP 7.3) use the same scheme on the model.

**Values.** The declaration's JSON: `$kind` for the case, camel-cased, a format version `v`, and
for declarations whose rendering depends on it the rendering version `r` (5.x's model-wide
`CustomIndex:RenderingVersion`, now per declaration).

```text
CodoMetis:Pg.Exclusion:dbfab59b70e9737b
{"$kind":"gist","elements":[{"target":{"$t":"column","path":"GranteeId"},"operator":{"sql":"="}},{"target":{"$t":"column","path":"RoleId"},"operator":{"sql":"="}},{"target":{"$t":"column","path":"Period"},"operator":{"sql":"&&"}}],"name":"ex_role_grant_active_period","where":{"template":"{RevokedAt} IS NULL"},"deferral":"InitiallyDeferred"}

CodoMetis:Pg.Index:3793116cc5c1e71b
{"$kind":"btree","keys":[{"target":{"$t":"column","path":"Email.Value"}}],"unique":{"nullsDistinct":false},"include":["Name"],"name":"ux_people_email","where":{"template":"{DeletedAt} IS NULL"}}
```

These two are the prototype's output, asserted verbatim with their keys and round trip by
`PrototypeBehaviourTests.Annotations_have_the_documented_v2_shape`; `v` and `r` are not prototyped.
Before 11.0.0 the JSON gets a hand-written writer with a fixed property order, so the snapshot text
does not depend on System.Text.Json's reflection order.

**Codec.** `ISchemaDeclaration<TSelf>` has `static abstract string OwnerId` and `static abstract
TSelf FromAnnotationValue(string)`, so `GetDeclarations<PgIndex>()` deserializes without a service
provider, which `OnModelCreating` and conventions do not have.

**Round trip rule.** A value left out of the JSON must read back as the same value. The prototype
learned it the hard way: writing with "omit defaults" dropped `"nullsDistinct":false` (false is
`bool`'s default), and the reader took the constructor's default, `true`, so a `NULLS NOT DISTINCT`
index read back as a plain unique one and would have been rebuilt by the next diff, silently. The
property is now always written, the verbatim test fails without that, and the real codec gets a
convention test: every omitted field's JSON default equals its reader default.

**In the snapshot**, EF's `CSharpSnapshotGenerator` emits annotations no code generator claims as
`.HasAnnotation(name, value)`, sorted by name, so the order is stable. SchemaKit deliberately does
not register an `IAnnotationCodeGenerator` that would turn them into `DeclarePostgres(…)` calls: a
snapshot that called the public API would break when the API changes, and the snapshot outlives
every API version. In a compiled model (`dbcontext optimize`) the annotations are kept as
`AddAnnotation`, because `EnsureCreated()` and the pending-changes check need them at run time.

**One annotation per declaration**, not 5.x's array per entity and kind: an array rewrites whole on
any change, two branches that each add an index conflict on one line, and a reader cannot see which
declaration changed.

**The 5.x format is read forever:**

| 5.x annotation | Read as |
|---|---|
| `CustomIndex:CompositeIndexes` (entity; `paths` or `parts`, `unique`, `filter`, `name`, `props`) | One `PgIndex` or `SqlServerIndex` per element. `props["Npgsql:IndexMethod"]` picks the case; `Npgsql:IndexOperators`, `IndexCollation`, `IndexInclude`, `NullsDistinct`, `CreatedConcurrently`, `StorageParameter:*` and the `SqlServer:*` keys become the case's options |
| `CustomIndex:IsIndexed`, `IsUnique`, `Filter`, `Name` and the `Npgsql:*` / `SqlServer:*` keys on a complex-type **property** | A one-key declaration on the owning entity type, path from the entity root |
| `CustomIndex:RenderingVersion` (model) | `r` on every index read from 5.x; absent means 1, as in 5.x |
| `CustomExclusion:Constraints` | One `PgExclusion` per element; `method` picks the case |
| `CustomTemporal:Constraints`, `CustomTemporal:ForeignKeys` | Not declarations any more ([section 12](#12-what-is-left-out-39)); read only so the handover to Npgsql's native keys is computed instead of drop-and-create (#59) |
| `CustomTemporal:SuppressAutoExtension` | The same switch for `btree_gist` injection |
| `CustomIndex:IndexParts`, `CustomTemporal:WithoutOverlaps`, `ForeignKeyDependentPeriod`, `ForeignKeyPrincipalPeriod` (on operations in old migrations) | Rendered by the legacy SQL generator; never written again |

A 5.x declaration v2 cannot represent (a GIN index declared unique) never applied to a database; the
reader throws with the declaration and the reason, rather than guessing.

**What the swap costs a consumer.** The differ compares resolved descriptors, not annotations, so a
model declared with SchemaKit against a 5.x snapshot produces **no operations** when the declarations
resolve alike (#59 is the proof per feature). `has-pending-model-changes` stays green; the snapshot is
rewritten to v2 by the consumer's next `migrations add`, whatever it was for. A `migrations add` run
only to see gives a migration with empty `Up` and `Down` and a rewritten snapshot. The migration
guide says that first, and #58's round-trip test checks it against a real 5.x snapshot compiled with
Roslyn.

### 9.8 Validation and diagnostics

**What is left once the types forbid what they can** is listed under
[what the compiler cannot see](#what-the-compiler-cannot-see). The engine's job is to report it well.

- **When.** At declaration for what the value alone decides (lambda bodies, empty key lists, an
  identifier over the limit when the name is explicit); at `migrations add`, `EnsureCreated()` and
  the pending-changes check for what needs the model (paths, store types, default names, name
  collisions, provider-wide rules such as one clustered index per table).
- **Shape.** A closed `SchemaDiagnostic` union with one case per finding and a stable id
  (`SK1001` name collision, …), each carrying a `DeclarationRef`: the entity type, the owner, and the
  explicit name or, for a default-named declaration, its resolved name and its first key. The
  prototype has `NameCollision`; its message is
  `SK1001: Pg.Index 'ex_role_grant_active_period' on 'role_grants' and Pg.Exclusion 'ex_role_grant_active_period' on 'role_grants' both claim the relation name …`.
- **All at once.** EF's model validator throws at the first error. SchemaKit's validation collects
  every finding of the target model into one `SchemaValidationException` with a `Diagnostics` list:
  a consumer moving forty indexes to SchemaKit fixes them in one pass, not forty.
- **Name claims** are the registry 5.4.0 introduced (`ValidateNamesAcrossKinds`), generalised:
  each descriptor returns the namespaces its name occupies as closed `NameClaim`s
  (`Relation(schema, name)` for PostgreSQL index names, `TableIndex(table, name)` for SQL Server's,
  `Constraint(table, name)`, `Trigger(table, name)`). An exclusion constraint claims a constraint
  name and a relation name, since its backing index lives in the schema's namespace; a delete guard
  claims a trigger name and a constraint name, since the application sees it as a constraint. EF's
  own keys, indexes and constraints are entered too, and a collision is reported only when one party
  is SchemaKit's. A namespace is a kernel case, not a contributor's invention.
- **Warnings** go through `IDiagnosticsLogger<DbLoggerCategory.Migrations>` with event ids, as EF's
  do: `NULLS NOT DISTINCT` against a configured PostgreSQL version below 15 (Npgsql renders it
  ungated), `INCLUDE` below 11 (Npgsql silently drops it).
- **Messages never guess.** A refusal names the declaration and the rule, and the 5.x messages carry
  over where the rule does.

### 9.9 The ownership check (#57)

SchemaKit owns `IMigrationsModelDiffer` and `IMigrationsSqlGenerator`. When another package replaces
either after SchemaKit's registration, every SchemaKit declaration disappears from migrations without
an error. The check makes that a startup failure.

- **Runtime.** `SchemaKitOptionsExtension.Validate` reads `CoreOptionsExtension.ReplacedServices`
  (public, `IReadOnlyDictionary<(Type, Type?), Type>`). EF applies `ReplaceService` entries after
  every extension's `ApplyServices`, keyed by service type, so a later `ReplaceService` overwrites
  SchemaKit's entry and is visible there. If the entry for either service is not SchemaKit's type, it
  throws, naming the replacing type and pointing to the contributor contract.
- **Design time.** The `.targets`-registered design-time services run before EF's own `TryAdd`s and
  before the user's `IDesignTimeServices`. SchemaKit registers a check on the scaffolder's resolved
  dependencies (`MigrationsScaffolderDependencies.MigrationsModelDiffer`) that throws the same way.
  This is the part to spike first in #57: it relies on EF's design-time registration order.
- **Message.** `"IMigrationsModelDiffer is 'Contoso.AuditDiffer', not SchemaKit's. SchemaKit
  declarations would be left out of migrations without an error. Register 'Contoso.AuditDiffer' as a
  SchemaKit contributor instead of replacing the differ: see docs/contributors.md."`
- **Not covered**, as #57 says: a package that replaced the services *before* SchemaKit loses to
  SchemaKit, and SchemaKit cannot see it. The answer there is the other package's guard; CodoMetis
  packages never replace either service (CaffeinatedCoder/CodoMetis.ValueRanges#334).
- The 5.x-to-SchemaKit guard from ROADMAP 5.7 is the same check with one more rule: a differ whose
  type name starts with `EFCore.ComplexIndexes` gets a message naming the packages to remove.

### 9.10 The contributor contract at a glance (#56)

```csharp
public interface ISchemaContributor                       // the kernel's view: open
{
    string Owner { get; }
    SchemaPlacement Placement { get; }
    ISchemaDescriptor Resolve(ISchemaObject declaration, SchemaResolver resolver);
    void Create(ISchemaDescriptor descriptor, SchemaSql sql);
    void Drop(ISchemaDescriptor descriptor, SchemaSql sql);
    bool Rename(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql);
    IEnumerable<NameClaim> Claims(ISchemaDescriptor descriptor);
}

public abstract class SchemaContributor<TDeclaration, TDescriptor> : ISchemaContributor   // a contributor's view: typed
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    where TDescriptor : class, ISchemaDescriptor
{
    protected SchemaContributor(SchemaContributorDependencies dependencies);   // not prototyped
    protected abstract TDescriptor Resolve(TDeclaration declaration, SchemaResolver resolver);
    protected abstract void Create(TDescriptor descriptor, SchemaSql sql);
    protected abstract void Drop(TDescriptor descriptor, SchemaSql sql);
    protected virtual bool Rename(TDescriptor from, TDescriptor to, SchemaSql sql) => false;
    protected abstract IEnumerable<NameClaim> Claims(TDescriptor descriptor);
}

public interface ISchemaDescriptor { TableRef Table { get; } Identifier Name { get; } ISchemaDescriptor WithName(Identifier name); }
```

- **Closed inside, open at the seam.** The kernel holds `IEnumerable<ISchemaContributor>` and routes
  each declaration by `Owner`. It never switches over a contributor's types; a contributor never sees
  another's. Inside a contributor every switch is over its own union, with no discard. The built-in
  features are contributors themselves (`PgIndexContributor`, `PgExclusionContributor`,
  `SqlServerIndexContributor` in the prototype), so the contract is proven by its first users.
- **The descriptor must have value equality**, since equality is the diff identity. C# cannot
  constrain a type argument to a record; the kernel checks it at registration (a compiler-generated
  `EqualityContract` on `TDescriptor`) and SchemaKit's convention tests check every built-in one.
  [Section 11](#11-codometistypekit) weighs TypeKit's `[Record]` analyzer for it.
- **Compatibility.** The contract is public under the same promise as the declarations, from 11.0.0.
  New capabilities arrive as optional interfaces a contributor may implement (`ISchemaDependent`,
  the rewrite hook ROADMAP 7.7 needs, below-table placement beyond `BeforeTables`), never as members
  on `ISchemaContributor`, so a contributor compiled against 11.0.0 keeps working on every 11.x.

## 10. Extending SchemaKit from a CodoMetis package

Extension is the reason SchemaKit exists (#37, #56), so it is designed as a first-class path with
two depths: a package that only adds **vocabulary** (factory methods, operands, functions) writes
extension members and reuses SchemaKit's declarations; a package that adds a **schema object** writes
a contributor. Both are walked through below, and the second is compiled end to end by the X probes.

### 10.1 A new schema object: CodoMetis.Hierarchies' ltree contributor

The ltree support planned in CodoMetis.Hierarchies needs a trigger that maintains a materialized
path and a check on it; its type mapping and query translation live in Hierarchies itself and never
touch SchemaKit. The migration-aware part ships in a satellite that references the SchemaKit kernel
and the PostgreSQL package; SchemaKit references nothing of it.

```csharp
// CodoMetis.Hierarchies.EFCore.PostgreSQL.SchemaKit
public closed record LtreeObject : IPostgresObject, ISchemaDeclaration<LtreeObject>          // 1. its own closed union
{
    public static string OwnerId => "Ltree.Path";                                              //    its own annotation namespace
    public string Owner => OwnerId;
    public string? Name { get; init; }
    public string Identity => this switch { MaintainedPath m => $"maintained|{m.Path}|{m.Parent}", PathCheck c => $"check|{c.Path}" };
    public string ToAnnotationValue() => …;
    public static LtreeObject FromAnnotationValue(string json) => …;

    public sealed record MaintainedPath(string Path, string Parent) : LtreeObject;
    public sealed record PathCheck(string Path) : LtreeObject;
}

public sealed record LtreeDescriptor(TableRef Table, Identifier Name, SqlFragment Ddl, bool IsTrigger) : ISchemaDescriptor   // 2. its descriptor
{
    public ISchemaDescriptor WithName(Identifier name) => this with { Name = name };
}

public sealed class LtreeContributor : SchemaContributor<LtreeObject, LtreeDescriptor>       // 3. its contributor
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 30);

    protected override LtreeDescriptor Resolve(LtreeObject declaration, SchemaResolver r) => declaration switch
    {
        LtreeObject.MaintainedPath m => new(r.Table, r.Name(m.Name, "TR", [new KeyTarget.Column(m.Path)]),
            r.Sql.Fragment($"BEFORE INSERT OR UPDATE ON {r.Table} FOR EACH ROW EXECUTE FUNCTION codometis_ltree_path()"), true),
        LtreeObject.PathCheck c => new(r.Table, r.Name(c.Name, "CK", [new KeyTarget.Column(c.Path)]),
            r.Sql.Fragment($"CHECK (nlevel({r.Target(new KeyTarget.Column(c.Path))}) > 0)"), false),
    };

    protected override void Create(LtreeDescriptor d, SchemaSql sql)
    {
        if (d.IsTrigger) sql.Statement($"CREATE TRIGGER {d.Name} {d.Ddl}");
        else sql.Statement($"ALTER TABLE {d.Table} ADD CONSTRAINT {d.Name} {d.Ddl}");
    }

    protected override void Drop(LtreeDescriptor d, SchemaSql sql) { … }

    protected override IEnumerable<NameClaim> Claims(LtreeDescriptor d) =>
        d.IsTrigger ? [new NameClaim.Trigger(d.Table, d.Name)] : [new NameClaim.Constraint(d.Table, d.Name)];
}

public static class LtreeSchemaExtensions                                                        // 4. its factory method
{
    extension<TEntity>(PgSchema<TEntity> pg) where TEntity : class
    {
        public LtreeObject.MaintainedPath MaintainedPath(Expression<Func<TEntity, TreePath>> path, Expression<Func<TEntity, int?>> parent) => …;
    }
}

// 5. a consumer: one list, two contributors, one differ
b.DeclarePostgres(pg => [pg.MaintainedPath(x => x.Path, x => x.ParentId), pg.Gist([pg.Key(x => x.Path)])]);
```

| The package **implements** | The package **reuses** |
|---|---|
| `LtreeObject` (closed union, codec, identity) | `IPostgresObject`, `ISchemaDeclaration<T>`: storage, read model, `Redeclare` |
| `LtreeDescriptor` | `Identifier`, `TableRef`, `SqlFragment`, value equality as diff identity, rename detection |
| `LtreeContributor` (`Resolve`, `Create`, `Drop`, `Claims`) | `SchemaResolver`: path resolution, JSON, default names, identifier limits, store facts |
| `MaintainedPath` on `PgSchema<T>` | `PgSchema<T>.Gist` and every other SchemaKit declaration in the same list |
| | The differ: matching, ordering, name registry across every contributor, the ownership check, snapshot v2 |

**What it cannot get wrong, because the types forbid it** (each compiled as a separate assembly
against the prototype):

| Mistake | Diagnostic | Probe |
|---|---|---|
| A case added to the union and not handled in `Resolve` | CS8509, naming `LtreeObject.PathCheck` | `X1.extension-misses-its-own-case` |
| A name interpolated as a string into DDL (`{d.Name.Name}`) | CS1503, `string` → `Identifier` | `X2.extension-renders-a-raw-name` |
| Its declaration placed in a SQL Server list | CS0029, → `ISqlServerObject` | `X3.extension-declaration-on-sqlserver` |
| Rendering from the declaration instead of the resolved descriptor | CS0115 | `E4.render-a-declaration` |
| A `SqlFragment` or an `Identifier` made from a string | CS1729, CS0029 | `E2`, `E3` |
| A case added to SchemaKit's `PgIndex` instead of its own union | CS9382 | `P9.no-foreign-case` |

What stays its responsibility: the identity it hashes, the JSON it writes (with the round-trip rule
of [9.7](#97-annotations-and-the-snapshot-58)), and the SQL text in its literals.

### 10.2 New vocabulary only: CodoMetis.ValueRanges

ValueRanges needs no contributor. Its columns already appear in exclusion constraints, and what it
adds is the right to write them typed:

```csharp
// CodoMetis.ValueRanges.EFCore.PostgreSQL.SchemaKit, or the EF package itself behind a reference
public static class ValueRangesSchemaExtensions
{
    extension<TEntity>(ExclusionDraft<TEntity> draft) where TEntity : class
    {
        public ExclusionElements<TEntity> Overlaps(Expression<Func<TEntity, DateRange>> range) => draft.WithUncheckedOperator(range, "&&");
        public ExclusionElements<TEntity> Overlaps(Expression<Func<TEntity, DateTimeOffsetRange>> range) => draft.WithUncheckedOperator(range, "&&");
        // one overload per range and set type, as its aggregates already do (constraints don't drive inference)
    }
}

b.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Validity))]);   // Validity : DateRange
```

- **Implements:** extension members on `PgSchema<T>`, `ExclusionDraft<T>` and
  `ExclusionElements<T>`; optionally `[SchemaFunction]` markers for range functions in index
  expressions (`lower(validity)` as `x.Validity.LowerBound()`), each with its stable name.
- **Reuses:** every SchemaKit type. `WithUncheckedOperator` is the named escape hatch the typed
  overloads are built on; ValueRanges' overloads make it typed again for its own types.
- **Checked twice.** At compile time, the overload set makes `Overlaps(x => x.Name)` fail as P3 shows.
  At `migrations add`, the type-mapping engine confirms the column's mapping implements
  `INpgsqlRangeTypeMapping` ([9.2](#92-type-mapping)), which ValueRanges' mappings do, so a range
  stored through some other converter is still caught.
- **Cannot get wrong:** it cannot add a case to `PgExclusion` (CS9382), cannot bypass the stage types
  (`ExclusionDraft` still converts to nothing), and cannot emit SQL at all.

ValueRanges' own rule (CaffeinatedCoder/CodoMetis.ValueRanges#334) already forbids it to replace the
differ or generator; with SchemaKit it never needs to.

## 11. CodoMetis.TypeKit

The maintainer allowed SchemaKit to depend on CodoMetis.TypeKit packages where that serves its
purpose. TypeKit 2.x (read at `~/RiderProjects/CodoMetis.TypeKit`, README and `design/roadmap.md`,
2026-10-19) offers value objects with one `Create`, smart enums (`IClosedSet`) whose `Match` breaks
the build on a new member, `Option`/`Result` that cannot be misused, analyzers CMTK0001–0014
(including generic-constraint attributes `[Sealed]`, `[Record]`, `[Distinct]`, …), Metalama-based
generators, and an EF Core package that maps value objects to columns.

**Where it could fit, one by one:**

| Candidate | Fit | Verdict |
|---|---|---|
| `Identifier` as a validated value object | The rule is the same ("an invalid name is never constructed"), but the limit belongs to the **dialect** (63 bytes, 128 characters), and a TypeKit value object validates in one dialect-free `Create`. SchemaKit's `Identifier` is made only by `ISchemaDialect.Identifier`, which is the shape the rule needs | Own type |
| `Result`/`Option` in the differ and diagnostics | EF's contract for `GetDifferences` is an exception; inside, SchemaKit accumulates `SchemaDiagnostic`s into one exception ([9.8](#98-validation-and-diagnostics)). `Result` would be an internal style choice with no consumer-visible gain | Not needed |
| Smart enums for small vocabularies (`Deferral`, `GistBuffering`, operators) | Both give exhaustiveness. `closed` records give it through `switch` patterns, carry data per case (`Uniqueness`, `AfterTables(order)`), support `with`, and need no generator; `IClosedSet` gives JSON by name for free, which SchemaKit wrote in 15 lines. TypeKit's own roadmap moves to C# 15 unions at 3.0 | `closed` |
| `[Record]` analyzer on `SchemaContributor<, TDescriptor>` | **The best fit.** A descriptor must have value equality because equality is the diff identity, and C# cannot say "a record" in a `where` clause. TypeKit's analyzer would make a class-typed descriptor a build error in the contributor's own project | Wanted, later |
| `[Sealed]`/`[Distinct]` elsewhere | `TDeclaration` is already constrained through `ISchemaDeclaration<TSelf>`; nothing else needs them | Not needed |
| `CodoMetis.TypeKit.EntityFrameworkCore` | Pins EF Core 10.0.12; cannot sit beside EF Core 11 without NU1605/binding conflicts. SchemaKit does not map value objects anyway | Excluded |

**The costs that decide it:**

- **Platform.** The core package targets `net10.0` and runs on a `net11.0` consumer; the EF package
  does not. TypeKit plans a `net11.0` target "in a 2.x minor when a feature needs it".
- **Coupling.** Any reference to the core package, even for internal use, is a nuspec dependency, and
  the core package deliberately brings the analyzers to everyone who references it. Every SchemaKit
  consumer would compile under CMTK0001–0014, including CMTK0010's measured cost on large files. If
  TypeKit types appeared in SchemaKit's public API, SchemaKit's majors and support window would also
  be tied to TypeKit's (TypeKit 3.0 plans to drop `net10.0`).
- **Native AOT, trimming, Metalama.** Not a concern: TypeKit's run-time packages are AOT-checked,
  and Metalama is only in its Generators package, which SchemaKit would not reference.
- **The alternative** costs little: `Identifier`, the singleton JSON converter, and a registration-time
  check plus a convention test for record descriptors are already in the prototype or specified.

**Decision: not adopted for 11.0.0; adopt the analyzers partly, later.** SchemaKit's public API stays
free of TypeKit types. The one thing worth taking is the `[Record]` constraint on `TDescriptor`,
which only an analyzer can enforce at compile time. That becomes worth the dependency when TypeKit has
a `net11.0` line and the maintainer wants TypeKit's analyzers to be part of what every CodoMetis
family consumer gets. Both are the maintainer's call
([Q10, Q11](#16-open-questions-for-the-maintainer)), and neither changes TypeKit here.

## 12. What is left out (#39)

| 5.x feature | SchemaKit | Why |
|---|---|---|
| `HasTemporalConstraint` | **Left out** (decided) | Npgsql 11's `HasKey(…).WithoutOverlaps()` / `HasAlternateKey(…).WithoutOverlaps()` covers it, and since CaffeinatedCoder/CodoMetis.ValueRanges#335 every ValueRanges range and multirange column too |
| `HasTemporalForeignKey` | **Left out** (decided) | Npgsql 11's `WithPeriod()`. Its one gap, Npgsql's own `NpgsqlRange<T>[]` multirange, is Npgsql's to close, and the guide names it |
| `UseBtreeGist()` | Left out | `modelBuilder.HasPostgresExtension("btree_gist")` is the native spelling; SchemaKit keeps the automatic injection for exclusion constraints with `=` elements |
| Column-only indexes (plain, composite, unique, filtered with plain SQL) | **Open (#39)**; the design keeps a SchemaKit form either way | EF Core 11's `HasIndex` covers complex-type, JSON and complex-collection paths, and Npgsql's and EF SQL Server's `IndexBuilder` extensions carry the options as strings and flags. SchemaKit's form adds P1–P7 and typed filters, and the migration guide offers both. #39 measures the native DDL and default names against 5.x before the guide recommends one |
| Expression and JSON-member indexes, `NULLS FIRST/LAST` on expressions, typed and placeholder filters, exclusion constraints | **Kept** | No native form for expressions, typed filters or exclusion. JSON members do have one on EF Core 11, but not a usable one on Npgsql 11 rc.2: scaffolding a member-path index fails ("Cannot scaffold C# literals of type `RelationalJsonIndex`", asserted by Npgsql's own `JsonIndexTest`), and mixed sort directions fail inside EF's differ. Where the native form works, its DDL is rebuilt at apply time from the model rather than stored in the migration ([9.6](#96-sql-generation)) |
| SQL Server index options | Kept in SchemaKit's typed form | EF's SQL Server `IndexBuilder` has native counterparts; SchemaKit's adds P4. Same #39 measurement |

## 13. Every 5.x call and its new form

"Native" names the EF Core 11 or Npgsql 11 API; "dropped" means there is no replacement on purpose.
Signatures as of 5.4.1. Input for #59 and the migration guide.

### Core (`EFCore.ComplexIndexes`)

| 5.x | SchemaKit |
|---|---|
| `ComplexTypePropertyBuilder.HasComplexIndex(isUnique, filter, indexName)` and `<TProperty>` overload | Entity-level, path from the root: `pg.BTree(x => x.Address.City, where: …, name: …)` / `pg.Unique(…)`; `ss.Nonclustered(…)`. Or native `HasIndex(x => x.Address.City)` (#39) |
| `ComplexTypePropertyBuilder.HasComplexIndex(Action<ComplexIndexBuilder>)` and `<TProperty>` overload | As above; the builder's options become parameters of the method's factory |
| `EntityTypeBuilder<T>.HasComplexIndex(x => x.A, isUnique, filter, indexName)` | `pg.BTree(x => x.A, …)` / `pg.Unique(x => x.A, …)`; or native `HasIndex(x => x.A).IsUnique().HasFilter(sql).HasDatabaseName(…)` when the filter has no placeholders |
| `EntityTypeBuilder<T>.HasComplexIndex(x => x.A, Action<ComplexIndexBuilder>)` | As above |
| `HasComplexCompositeIndex(x => new { … }, isUnique, filter, indexName)` and the callback overload | `pg.BTree([pg.Key(x => x.A), pg.Key(x => x.B)], …)`; or native `HasIndex(x => new { … })` |
| `ComplexIndexBuilder.IsUnique()`, `HasFilter(string)`, `HasName(string)` | `pg.Unique(…)` / `unique:` on SQL Server; `where: x => …` (or `Sql.Raw<bool>`); `name:` |
| `DbOrder.Asc`, `DbOrder.Desc` | `pg.Key(…).Asc()` / `.Desc()`; `ss.Key(…).Desc()`; native `IsDescending(…)` |
| `DbOrder.NullsFirst`, `DbOrder.NullsLast`, `DbNullSort` | `pg.Key(…).NullsFirst()` / `.NullsLast()`; no SQL Server form (P4); native `HasNullSortOrder(…)` for column keys |
| `IIndexExpression`, `SqlIndexExpression` | `Sql.Raw<T>("…")` inside a key lambda |
| `IndexNameScope` | Dropped from the public surface; a dialect property |
| `CompositeIndexDefinition`, `IndexPartDefinition`, `ResolvedIndexPart`, `CompositeIndexSerializer`, `IndexPartsSerializer` | Dropped; the declaration records replace them, and the v1 reader is internal |
| `ComplexIndexDeclaration` | The declaration records themselves |
| `GetDeclaredComplexIndexes()`, `GetComplexIndexes()` (entity and model) | `GetDeclarations<PgIndex>()` / `GetDeclarations<SqlServerIndex>()` on the declaring type; an overload over the hierarchy, base types first, as `GetComplexIndexes()` does |
| `FindComplexIndex(name)` | `FindDeclaration<PgIndex>(name)` |
| `IMutableEntityType.AddComplexIndex(CompositeIndexDefinition)` | `entityType.Declare([index])` |
| `AddComplexIndexFilter(string, where)` | `entityType.Redeclare<PgIndex>(ix => …ix.AndWhere(Predicate.Raw("…")))` |
| `UseComplexIndexes()`, `UseComplexIndexes<TContext>()`, `AddComplexIndexes()` | `UseSchemaKit()` (kernel only, for a provider without a SchemaKit package) |
| `CustomDesignTimeServices` | Internal; still injected by the `.targets` |
| `CustomMigrationsModelDiffer` and its virtuals (`ValidateCreateIndexOperation`, `ValidateCreatedIndexes`, `CanRenameIndexes`, `IndexNameScope`, `IsForwardedIndexAnnotation`, `ToOperationAnnotationName`, `TransformIndexAnnotation`, `ResolveUnmappedPart`, `ResolveTemplatePart`, `ResolveFilter`, `QuoteIdentifier`, `MeasureIdentifier`, `ThrowIfIdentifierTooLong`, `ThrowIfDeclaredOnUnmappedType`, `ResolveColumnName`, `ResolveProperty`) | Dropped as public API; replaced by `ISchemaContributor` and `ISchemaDialect` ([section 9.10](#910-the-contributor-contract-at-a-glance-56)) |
| `CustomMigrationsModelDiffer.RuntimeWiringSentinel` | Dropped; nothing new needs runtime wiring. The legacy generator still recognises it in old migrations |
| `ComplexIndexAnnotations.*` constants | Dropped as public API; read by the v1 reader ([section 9.7](#97-annotations-and-the-snapshot-58)) |

### PostgreSQL (`EFCore.ComplexIndexes.PostgreSQL`)

| 5.x | SchemaKit |
|---|---|
| `UseGin()`, `UseGist()`, `UseBrin()`, `UseHash()`, `UseSpGist()`; B-tree by default | `pg.Gin(…)`, `pg.Gist(…)`, `pg.Brin(…)`, `pg.Hash(…)`, `pg.SpGist(…)`; `pg.BTree(…)` / `pg.Unique(…)`. Native: `HasMethod("gin")` |
| `HasOperators(params string[])` | Per key, typed by method: `pg.Key(x => x.Doc).Ops(GinOpClass.JsonbPathOps)`, `new GinOpClass("gin_trgm_ops")`; `pg.Gin(x => x.Doc, GinOpClass.JsonbPathOps)` for one key. Native: `HasOperators(…)` |
| `IncludeProperties(params string[])` | `include: [x => x.A]` on `BTree`, `Unique`, `Gist`, `SpGist` only. Native: `IncludeProperties(…)` |
| `IsCreatedConcurrently()` | `concurrently: true` on every method |
| `AreNullsDistinct(false)` | `pg.Unique(…, nullsDistinct: false)` |
| `UseCollation(params string[])` | Per key, string keys only: `pg.Key(x => x.Name).Collate("C")`. Native: `UseCollation(…)` |
| `HasStorageParameter(name, value)` | Method-specific parameters: `fillFactor` (B-tree, Hash, GiST, SP-GiST), `deduplicateItems` (B-tree), `buffering` (GiST), `fastUpdate`, `pendingListLimit` (GIN), `pagesPerRange`, `autosummarize` (BRIN) |
| `HasExpressionIndex(string, isUnique, filter, indexName)` | `pg.BTree(x => Sql.Raw<string>("lower(email)"), …)` / `pg.Unique(…)` |
| `HasExpressionIndex(Expression<Func<T, TResult>>, …)` | `pg.BTree(x => x.Email.ToLower(), …)`: any key lambda may be an expression |
| `HasExpressionIndex(Action<ExpressionIndexBuilder>)`; `ExpressionIndexBuilder.Expression`, `Descending`, `NullsFirst`, `NullsLast`, `IsUnique`, `HasFilter`, `HasName` | A key list: `pg.BTree([pg.Key(…), pg.Key(…).Desc().NullsLast()], where: …, name: …)` |
| `ComplexIndexBuilder.HasFilter<TEntity>(predicate)`, `ExpressionIndexBuilder.HasFilter<TEntity>(predicate)` | `where: x => …`, bound to the configured entity (P7) |
| Typed-filter overloads of `HasComplexIndex`, `HasComplexCompositeIndex`, `HasExpressionIndex`, `HasExclusionConstraint` | `where:` on every factory |
| `AddComplexIndexFilter<TEntity>(predicate, where)` | `Redeclare<PgIndex>(ix => ix.AndWhere(Predicate.Of<TEntity>(…)))` |
| `HasExclusionConstraint(equalityColumns, overlapsColumn, filter, name)` | `pg.Exclusion(pg.Equal(x => x.A).Equal(x => x.B).Overlaps(x => x.Period), where: …, name: …)` |
| `HasExclusionConstraint(Action<ExclusionConstraintBuilder<T>>)` | The same, built in the order written |
| `ExclusionConstraintBuilder.WithEquality`, `WithOverlaps` | `.Equal(x => …)`, `.Overlaps(x => …)` (typed operand, P3) |
| `ExclusionConstraintBuilder.WithColumn(column, op)`, `WithExpression(sql, op)` | `.WithUncheckedOperator(x => …, "-|-")`, `.WithUncheckedOperator(x => Sql.Raw<T>("…"), "=")` |
| `ExclusionConstraintBuilder.UseMethod("gist")` | `pg.Exclusion(…)` is GiST; `PgExclusion.SpGist` for one element; `gin`, `brin`, `hash`, `btree` dropped (PostgreSQL refuses or degenerates) |
| `ExclusionConstraintBuilder.HasFilter(string)`, `HasFilter(predicate)`, `HasName`, `IsDeferrable(initiallyDeferred)` | `where:`, `name:`, `deferral: Deferral.InitiallyImmediate` / `Deferral.InitiallyDeferred` |
| `ExclusionConstraintDeclaration`, `ExclusionPartDefinition`, `ExclusionConstraintDefinition` | `PgExclusion`, `ExclusionElement` |
| `GetDeclaredExclusionConstraints()`, `GetExclusionConstraints()`, `FindExclusionConstraint(name)` | `GetDeclarations<PgExclusion>()`, `FindDeclaration<PgExclusion>(name)` |
| `AddExclusionConstraintFilter(string)`, `AddExclusionConstraintFilter<TEntity>(predicate)` | `Redeclare<PgExclusion>(ex => ex.AndWhere(…))` |
| `HasTemporalConstraint(keys, period, name)` | **Native**: `HasAlternateKey(x => new { x.RoomId, x.Period }).WithoutOverlaps()` (Npgsql 11) |
| `HasTemporalForeignKey(…)` | **Native**: `HasOne(…).WithMany(…).HasForeignKey(…).WithPeriod()` (Npgsql 11) |
| `UseBtreeGist()` | **Native**: `HasPostgresExtension("btree_gist")` |
| `SuppressTemporalExtensionAutoInjection()` | `modelBuilder.SuppressBtreeGistInjection()` (same annotation) |
| `UseNpgsqlComplexIndexes()`, `<TContext>`, `AddNpgsqlComplexIndexes()` | `UseNpgsql(cs, o => o.UseSchemaKit())` |
| `NpgsqlComplexIndexSqlGenerator`, `NpgsqlComplexIndexMigrationsModelDiffer`, `NpgsqlComplexIndexDesignTimeServices` | Internal |
| `NpgsqlExclusionAnnotations.Constraints` | Dropped as public API; read by the v1 reader |

### SQL Server (`EFCore.ComplexIndexes.SqlServer`)

| 5.x | SchemaKit |
|---|---|
| `IsClustered()` | `ss.Clustered(…)`; non-clustered is `ss.Nonclustered(…)`. Native: `IsClustered()` |
| `IncludeProperties(params string[])` | `include: [x => x.A]` on `Nonclustered` only (P4). Native: `IncludeProperties(…)` |
| `IsCreatedOnline()` | `online: true`. Native: `IsCreatedOnline()` |
| `HasFillFactor(int)` | `fillFactor:`. Native: `HasFillFactor(…)` |
| `SortInTempDb()` | `sortInTempDb: true`. Native: `SortInTempDb()` |
| `UseDataCompression(DataCompressionType)` | `compression: Compression.Page` |
| `UseSqlServerComplexIndexes()`, `<TContext>`, `AddSqlServerComplexIndexes()` | `UseSqlServer(cs, o => o.UseSchemaKit())` |
| `SqlServerComplexIndexMigrationsModelDiffer`, `SqlServerComplexIndexDesignTimeServices` | Internal |

## 14. The first slice: 11.0.0-preview.1 (#38)

**The kernel plus exclusion constraints, end to end.** Exclusion constraints are the smallest
feature that exercises the whole design: a closed union (GiST, SP-GiST), the stage types (P2), a
typed operator (P3), a closed option (`Deferral`), the apply call, the read model, `Redeclare`, a
contributor with a rename, the name registry across kinds, `btree_gist` injection, and the v1 reader
for one 5.x annotation. They have no native counterpart, so nothing in the slice waits on #39.

| In preview.1 | Not yet |
|---|---|
| Kernel values: `ISchemaObject`, `ISchemaDeclaration<T>`, `KeyTarget`, `Predicate`, `Sql.Raw`, `ValueList`, `SchemaExpression` and the lambda translator moved from the PostgreSQL package (ROADMAP 5.4) | The dialect's SQL Server spellings |
| Kernel engines: `SchemaResolver` over EF's relational model, `StoreFacts` from the provider's mappings, `SchemaSql`/`SqlHandler`, `Identifier`/`SqlFragment`/`SqlName`/`SqlOperator`, `SchemaDiagnostic` with collect-all validation | `[SchemaFunction]` markers for other packages: preview.2 |
| `Declare`, `GetDeclarations`, `FindDeclaration`, `Redeclare` | |
| `ISchemaContributor`, `SchemaContributor<TDeclaration, TDescriptor>` with `SchemaContributorDependencies`, `SchemaPlacement`, `NameClaim`, the generic descriptor differ | The optional rewrite and below-table placement interfaces ([section 15](#15-room-for-the-horizon)) |
| Snapshot v2 for `Pg.Exclusion`, the v1 reader for `CustomExclusion:Constraints`, the #59 test for exclusion constraints | v2 and v1 for indexes |
| `PgSchema<T>`: `Equal`, `Overlaps`, `Exclusion`; `DeclarePostgres`; `UseSchemaKit()` on Npgsql's builder | `PgIndex` and its six methods: preview.2 |
| The ownership check, runtime half | Design-time half: after its spike |
| The compile probes, moved from `test/SchemaKit.ApiProbes` into the kernel's test project against the real assemblies | SQL Server: preview.3 |

Done when `migrations add`, `EnsureCreated()`, `GenerateCreateScript()` and the pending-changes check
all see a declared exclusion constraint on PostgreSQL 16 and 18, a 5.x model with exclusion
constraints swaps to SchemaKit with no operations, and the exclusion-constraint and engine probes
(P2, `P3.overlaps-on-text`, P6, P8, P9, E1–E4, V1) hold against the shipped assemblies. The X probes
move with the first contributor written outside the repository.

## 15. Room for the horizon

ROADMAP section 7 lists what the umbrella admits. Each is a contributor with its own closed union, and
needs from the contract at most one optional interface:

| Item | Declaration shape | Needs |
|---|---|---|
| Row constraints / delete guard (ROADMAP 4.2) | `PgRowGuard`, owner `Pg.RowConstraint`, cases `Delete(when?)`, `Update(when?)`, `Truncate`, `ImmutableColumn(path)`; append-only is a factory returning three values. `pg.DeleteGuard(name:, message:)` unconditional, `pg.DeleteGuard(when: x => x.FinalizedAt != null, name:, message:)` with `name` required (a parameter without a default: CS7036 when missing, since the application dispatches on it). Claims a trigger name and a constraint name. Designed for 5.x first (`design/delete-guard.md`, its own PR), stored there as a v1 blob the v1 reader adds a row for | Contract as-is |
| 7.1 Row-level security policies | `pg.Policy(name, PolicyCommand.Select, using: x => …, withCheck: x => …)`, `PolicyCommand` closed | A session-setting marker in the translator (`Session.Setting("app.tenant")`) |
| 7.2 Extended statistics | `pg.Statistics(name, StatisticsKind.Dependencies \| …, [pg.Key(…)])` | Contract as-is |
| 7.3 Domain types | `model.DeclarePostgres(pg => [pg.Domain<Email>("email", check: v => v.Value.Length <= 320)])` on the model | Below-table placement; a type-mapping plugin through the same options extension |
| 7.4 Deferrable keys | An annotation on EF's own key, set through a SchemaKit extension on `KeyBuilder` | The rewrite hook (unique half) |
| 7.5 Trigger templates | `pg.Touch(x => x.UpdatedAt)`, `pg.VersionBump(x => x.Version)` | Contract as-is |
| 7.6 Views and procedure bodies | `pg.ViewDefinition(Sql.Raw<…>(…))`, text-diffed, with the caveat the roadmap states | Contract as-is |
| 7.7 Partitioning | `pg.RangePartitions(x => x.OccurredAt, Monthly, from, to)` | The rewrite hook and the stock generator at design time |
| 7.8 Closure tables | `pg.ClosureTable(x => x.ParentId)` | From 7.5 |

## 16. Open questions for the maintainer

**Surface**

1. **Names.** `DeclarePostgres` / `DeclareSqlServer` were chosen over `HasPostgresSchema` (in
   PostgreSQL "schema" is a namespace) and over one `Declare` with overloads (with both providers
   referenced, overload resolution between the two lambdas depends on the lambda body). Also to
   confirm: `PgSchema<T>` / `SqlServerSchema<T>` for the factories, `pg.Unique` as B-tree's unique
   form, `Sql.Raw<T>`, and the owners `Pg.Index`, `Pg.Exclusion`, `SqlServer.Index` (and
   `Pg.RowConstraint` for the delete guard) that land in snapshots for good.
2. **Column-only indexes (#39).** Keep SchemaKit's typed index for column-only keys next to the native
   `HasIndex`, as this design does, or leave column-only indexes to the platform and keep `PgIndex`
   for expression and JSON-member keys only? Keeping it is what makes P1 reach plain indexes.
   Npgsql 11 rc.2 cannot scaffold a native member-path JSON index at all
   ([section 12](#12-what-is-left-out-39)).
3. **Exclusion element syntax.** The staged chain (`pg.Equal(…).Overlaps(…)`) is the one place the
   surface is not named parameters. The alternative, `pg.Exclusion(overlaps: x => x.Period, equal:
   [x => x.A, x => x.B])`, is flatter but fixes the element order and loses arbitrary operators. Keep
   the chain?

**Engines**

4. **An analyzer.** Lambda bodies are the one place the type system cannot reach. A SchemaKit
   analyzer could make the translator's refusals (an enum comparison, an expression key on SQL
   Server, a non-`Immutable` function) compile errors. In scope for 11.0.0, or later?
5. **Rendering indexes at design time** ([9.6](#96-sql-generation)) ends runtime wiring for new code
   but means scaffolded migrations show `migrationBuilder.Sql(…)` for indexes instead of
   `CreateIndex(…)`. Agreed?
6. **Expression translation** ([9.3](#93-expression-translation)): a dedicated closed tree with
   `[SchemaFunction]` markers and EF's query pipeline as the test oracle, rather than EF's pipeline
   itself. Agreed? And should a package register functions it does not own (EF's or Npgsql's own
   methods) through a mapping table, or only through markers it declares?
7. **Diagnostics all at once** ([9.8](#98-validation-and-diagnostics)) rather than EF's first-error
   validation. Agreed?
8. **Server-version gates.** `INCLUDE` on GiST (12), SP-GiST (14) and `NULLS NOT DISTINCT` (15).
   Npgsql's `SetPostgresVersion` is available at `migrations add`; this design logs a warning. Make it
   an error when a version is set?
9. **The design-time ownership check** depends on EF's design-time registration order; if the spike
   in #57 finds no reliable hook, is a runtime-only check acceptable for 11.0.0?

**Family**

10. **TypeKit on .NET 11.** SchemaKit would take TypeKit's analyzers (the `[Record]` constraint on
    descriptors) once TypeKit has a `net11.0` line. Does TypeKit get one before SchemaKit 11.0.0, and
    should TypeKit's analyzers be part of what every CodoMetis family consumer compiles under?
11. **TypeKit's EF Core package** pins EF Core 10. If the family is to share it on the 11 line, it
    needs an EF Core 11 build; SchemaKit itself does not need it.
12. **Where ValueRanges' SchemaKit vocabulary lives** ([10.2](#102-new-vocabulary-only-codometisvalueranges)):
    in a small satellite (`CodoMetis.ValueRanges.EFCore.PostgreSQL.SchemaKit`), so ValueRanges' EF
    package keeps no SchemaKit dependency, or inside the EF package?
13. **Collation and operator-class typing.** `Collate` is limited to string and char keys (P3). Extend
    the same treatment to operator classes for CLR types that decide their store type
    (`NpgsqlRange<T>` → `GistOpClass.RangeOps`), or keep operator classes typed by method only?
14. **First slice.** Exclusion constraints first ([section 14](#14-the-first-slice-1100-preview1-38)),
    or `PgIndex` first because more consumers use indexes?
