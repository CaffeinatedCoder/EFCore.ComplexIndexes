# CodoMetis.EFCore.SchemaKit: the public API

Design for #61, written 2026-10-19 against `EFCore.ComplexIndexes` 5.4.1, the .NET 11 SDK
11.0.100-rc.2.26504.105, EF Core 11.0.0-rc.2 and Npgsql 11.0.0-rc.2. **Proposed**, for the
maintainer's review. It replaces `ROADMAP.md` section 5 as the plan for SchemaKit's surface; that
section is input here, not a plan.

The rule is the one the CodoMetis.ValueRanges range types follow: **a declaration PostgreSQL or SQL
Server would reject does not compile.** Every "does not compile" claim below names a probe in
[`test/SchemaKit.ApiProbes`](../test/SchemaKit.ApiProbes), which compiles the forbidden declaration
with the SDK's own Roslyn against a prototype of this surface and asserts the diagnostic. Each probe
also compiles an allowed twin that differs only in the forbidden part, so a probe cannot pass on an
unrelated typo.

- [1. Summary](#1-summary)
- [2. Language features this design relies on](#2-language-features-this-design-relies-on)
- [3. Principles](#3-principles)
- [4. Core concepts](#4-core-concepts)
- [5. The chosen surface: declarations are values](#5-the-chosen-surface-declarations-are-values)
- [6. The alternatives and why they lose](#6-the-alternatives-and-why-they-lose)
- [7. The contributor contract (#56)](#7-the-contributor-contract-56)
- [8. The ownership check (#57)](#8-the-ownership-check-57)
- [9. Annotations and snapshot format v2 (#58)](#9-annotations-and-snapshot-format-v2-58)
- [10. What is left out (#39)](#10-what-is-left-out-39)
- [11. Every 5.x call and its new form](#11-every-5x-call-and-its-new-form)
- [12. The first slice: 11.0.0-preview.1 (#38)](#12-the-first-slice-1100-preview1-38)
- [13. Room for the horizon](#13-room-for-the-horizon)
- [14. Open questions for the maintainer](#14-open-questions-for-the-maintainer)

## 1. Summary

**Declarations are immutable values. A typed factory builds them from lambdas, and one call
applies a list of them to EF's model.** Each contributor (indexes, exclusion constraints, later
guards and policies) owns a C# 15 `closed` record hierarchy whose cases carry exactly the options
the database accepts for them, so an option the database would refuse is a parameter or a method
that does not exist. The kernel stores, diffs and reads declarations through one open interface and
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

Of the three surfaces compared in [section 6](#6-the-alternatives-and-why-they-lose), this one makes
the most uncompilable, is the only one that is immutable all the way through, puts every option in
signature help, and gives the read model, conventions and tests the same values the configuration
wrote. The first preview ships the kernel plus exclusion constraints end to end
([section 12](#12-the-first-slice-1100-preview1-38)).

## 2. Language features this design relies on

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
  is JSON ([section 9](#9-annotations-and-snapshot-format-v2-58)).

`union` stays available for a place where it does fit: combining existing, unrelated types, such as
a storage-parameter value of `int`, `bool` or `string` if a contributor needs one.

## 3. Principles

Each principle comes with the 5.x declaration it retires, the forbidden SchemaKit declaration, and
the diagnostic the probe asserts. In 5.x every one of these compiled; most failed only when the
migration was applied.

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
([section 6](#6-the-alternatives-and-why-they-lose)).

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
([section 5](#conventions-are-functions)); its paths are checked against each entity it is applied to.

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
([section 7](#7-the-contributor-contract-56)): `LtreeIndex` implements `IPostgresObject` and is its own
contributor's union.

### What the compiler cannot see

The type system covers structure: which options exist, which stage a value is in, which operand
types an operator takes, which provider a declaration belongs to. These stay checks at declaration
(`OnModelCreating`) or at `migrations add`, with the 5.x messages:

| Check | When | Why the compiler cannot | Future |
|---|---|---|---|
| Lambda bodies outside the translator's subset (`x.Name.Normalize()`, an enum comparison, a `DateTime` literal, an expression key on SQL Server) | Declaration | C# cannot restrict what an expression tree contains | A SchemaKit analyzer: [Q4](#14-open-questions-for-the-maintainer) |
| An empty key list (`pg.BTree([])`) | Declaration | No collection type a collection expression can target is non-empty | |
| Operator classes and operators against a store type only the model knows (`jsonb_path_ops` on a `ToJson()` property) | `migrations add` | The CLR type is not the store type | |
| Identifier length (63 bytes, 128 characters), name collisions within a kind and across kinds | `migrations add` | Strings, and the default names depend on resolved columns | |
| A second clustered index on a SQL Server table, or one next to a clustered primary key | `migrations add` | A property of the whole table, not of one declaration | |
| Server-version gates (`INCLUDE` on GiST needs 12, on SP-GiST 14; `NULLS NOT DISTINCT` 15) | `migrations add` with a declared minimum version | The server version is configuration | [Q7](#14-open-questions-for-the-maintainer) |
| `with` or the annotation reader building an equality-only exclusion constraint | Construction (`init` guard) | `with` bypasses the stage types | |

## 4. Core concepts

| Concept | Type (kernel unless noted) | What it is |
|---|---|---|
| Declaration | `ISchemaObject` (open), `ISchemaDeclaration<TSelf>` | One schema object a model declares. Open interface for the kernel; each contributor's union implements it. |
| Declaration union | `PgIndex`, `PgExclusion` (PostgreSQL), `SqlServerIndex` (SQL Server) | A `closed` record hierarchy, one case per shape the database distinguishes |
| Key target | `KeyTarget` = `Column(path)` \| `Template(sql)` | What a key or element is built from. Paths are resolved to columns, or on PostgreSQL to JSON extractions, at diff time, as in 5.x |
| Key | `PgKey<TValue>`, `BTreeKey`, `GinKey`, `GistKey`, `SpGistKey`, `HashKey`, `BrinKey` (PostgreSQL); `SqlServerKey` | A key target plus the options its method allows |
| Operator class | `BTreeOpClass`, `GinOpClass`, … | Typed by method; well-known ones as statics, any other by name |
| Exclusion element | `ExclusionElement`, `ExclusionOperator`, stages `ExclusionDraft<T>` → `ExclusionElements<T>` | A key and the operator two rows must not both satisfy |
| Predicate | `Predicate` (stored form: placeholder SQL); `Predicate.Of<T>(…)` | A partial-index or constraint condition |
| Raw SQL | `Sql.Raw<T>("…")` | The one escape hatch, inside a lambda |
| Fixed option | `Deferral`, `GistBuffering`, `NullOrder`, `Compression` | Closed singletons, never enums |
| Factory | `PgSchema<TEntity>`, `SqlServerSchema<TEntity>` | Stateless struct of pure functions; `PgSchema.For<T>()` outside a configuration |
| Apply | `DeclarePostgres`, `DeclareSqlServer` (on `EntityTypeBuilder<T>`); `Declare` (on `IMutableEntityType`) | Stores each declaration as one annotation |
| Read model | `GetDeclarations<T>()`, `FindDeclaration<T>(name)` | Returns the values that were declared; the differ reads the same |
| Amend | `Redeclare<T>(Func<T, T>)`, `AndWhere(Predicate)` | Replace declarations with a function of themselves |
| Identity | `ISchemaObject.Identity` | Store identity: same identity on an entity replaces. Diff identity is the resolved declaration's equality |
| Contributor | `ISchemaContributor` (open), `SchemaContributor<TDeclaration>` | Diffs and renders one union; [section 7](#7-the-contributor-contract-56) |
| Dialect | `ISchemaDialect` | The provider's spellings: quoting, identifier limit, JSON extraction, boolean literals (ROADMAP 5.4) |
| Placement | `SchemaPlacement` | Where a contributor's operations go relative to EF's |

## 5. The chosen surface: declarations are values

The same declarations throughout this section and [section 6](#6-the-alternatives-and-why-they-lose),
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

- **Most uncompilable.** Every probe in [section 3](#3-principles) holds. P2 holds only because
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
  cannot drift from it ([section 9](#9-annotations-and-snapshot-format-v2-58)).

The cost: two kinds of syntax for options (named parameters for indexes, a staged chain for exclusion
elements), and a list in a lambda (`pg => [ … ]`) that EF users have not seen before.

## 6. The alternatives and why they lose

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

[Section 5](#5-the-chosen-surface-declarations-are-values).

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

## 7. The contributor contract (#56)

### Shape

```csharp
// The kernel's view: open, so other packages plug in.
public interface ISchemaContributor
{
    string Owner { get; }                       // the annotation key's middle segment: "Pg.Index"
    SchemaPlacement Placement { get; }          // where its operations go relative to EF's
    IReadOnlyList<MigrationOperation> Create(ISchemaObject declaration, SchemaTable table, ISchemaDialect dialect);
    IReadOnlyList<MigrationOperation> Drop(ISchemaObject declaration, SchemaTable table, ISchemaDialect dialect);
    IReadOnlyList<MigrationOperation>? Rename(ISchemaObject from, ISchemaObject to, SchemaTable table, ISchemaDialect dialect);
}

// A contributor's view: typed over its own closed union.
public abstract class SchemaContributor<TDeclaration> : ISchemaContributor
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
{
    protected abstract IReadOnlyList<MigrationOperation> Create(TDeclaration declaration, SchemaTable table, ISchemaDialect dialect);
    protected abstract IReadOnlyList<MigrationOperation> Drop(TDeclaration declaration, SchemaTable table, ISchemaDialect dialect);
    protected virtual IReadOnlyList<MigrationOperation>? Rename(TDeclaration from, TDeclaration to, SchemaTable table, ISchemaDialect dialect) => null;
}
```

The prototype has this shape (`Prototype/Kernel/Seam.cs`). Two more members belong in the 11.0.0
contract and are not prototyped: `NameClaims(TDeclaration, SchemaTable)`, which tells the kernel's
name registry which namespaces a declaration's name occupies (an exclusion constraint claims a
constraint name per table *and* an index name per schema; this is 5.4.0's `ValidateNamesAcrossKinds`
generalised), and `Resolve`, the hook that turns paths into columns through the dialect, which 5.x
does in `ResolveUnmappedPart` and `ResolveTemplatePart`.

**Closed inside, open at the seam.** The kernel holds an `IEnumerable<ISchemaContributor>` and
routes each declaration by `Owner`. It never switches over a contributor's types; a contributor never
sees another's. Inside a contributor every switch is over its own union with no discard, so adding a
case is a compile error in each of them (P9). The built-in features are contributors themselves:
`PgIndexContributor`, `PgExclusionContributor` and `SqlServerIndexContributor` in the prototype.

### What the kernel does with them

The generic descriptor differ planned in ROADMAP 4.1, once, for every contributor:

1. read both models' declarations per contributor (the v2 annotations, and the 5.x annotations
   through the v1 reader, [section 9](#9-annotations-and-snapshot-format-v2-58));
2. resolve them into descriptors through the dialect, normalise source descriptors onto tables the
   base operations rename;
3. match by resolved equality; a pair equal but for the name becomes the contributor's `Rename`, or
   drop-and-create when it returns null;
4. validate the target model only: identifier length per dialect, then every `NameClaims` entry
   against every other and against EF's own indexes, keys and constraints, reporting a collision
   only when one party is SchemaKit's (5.4.0's rule);
5. emit `[drops by descending placement, EF's operations, renames, creates by ascending placement]`,
   the ordering 5.x established, plus the `btree_gist` injection as post-processing.

`HasDifferences` keeps routing through `GetDifferences` (5.x's `PendingModelChangesTests`).

### Rendering: design time only

Every contributor renders `SqlOperation`s at diff time, including indexes (5.x rendered complex
indexes as `CreateIndexOperation`s and needed the runtime SQL generator for expression and
null-ordered ones). Consequences:

- **No runtime wiring sentinel and no "requires `UseNpgsqlComplexIndexes()`" feature.** A design-time
  statement cannot degrade when a consumer forgets the runtime call, which is the rule 5.x already
  applied to exclusion and temporal constraints.
- `CREATE INDEX CONCURRENTLY` sets `SqlOperation.SuppressTransaction`.
- The custom `IMigrationsSqlGenerator` stays in the PostgreSQL package for one reason: migrations
  scaffolded by 5.x carry `CustomIndex:IndexParts` and the pre-5.0.2 temporal annotations, and
  consumers replay old migrations on fresh databases forever. It renders exactly those and nothing new.
- The runtime differ is still needed: `EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`'s
  pending-changes check run the context's differ, as 5.x documents.

### Registration

`options.UseNpgsql(cs, o => o.UseSchemaKit())` and `UseSqlServer(…)` likewise (ROADMAP 5.6) add an
options extension that registers the kernel differ, the dialect and the built-in contributors, and a
package's own contributors through `o.UseSchemaKit(k => k.Add<LtreeContributor>())` or its own
`UseHierarchies()` calling the same registration. Design time keeps the `.targets`-injected
`DesignTimeServicesReferenceAttribute`; with one differ type in the family, 5.x's back-off-and-remove
registration becomes a `TryAdd`, and contributors accumulate as plain `AddSingleton<ISchemaContributor, …>`.

### A contributor from another package

```csharp
// CodoMetis.Hierarchies.EFCore.PostgreSQL.SchemaKit — references the SchemaKit kernel; SchemaKit references nothing of it.
public closed record LtreeObject : IPostgresObject, ISchemaDeclaration<LtreeObject>
{
    public sealed record PathTrigger(string PathProperty, string ParentProperty) : LtreeObject;
    public sealed record PathCheck(string PathProperty) : LtreeObject;
    // Owner, Name, Identity, ToAnnotationValue, FromAnnotationValue …
}

public sealed class LtreeContributor : SchemaContributor<LtreeObject> { /* switches over its own two cases */ }

public static class LtreeSchemaExtensions
{
    extension<TEntity>(PgSchema<TEntity> pg) where TEntity : class
    {
        public LtreeObject.PathTrigger MaintainedPath(Expression<Func<TEntity, LTree>> path, Expression<Func<TEntity, object?>> parent) => …;
    }
}

b.DeclarePostgres(pg => [pg.MaintainedPath(x => x.Path, x => x.ParentId), pg.Gist([pg.Key(x => x.Path)])]);
```

The ltree declaration and SchemaKit's GiST index go in one list, rendered by two contributors through
one differ.

### Compatibility promise

The contract is public under the same promise as the declarations, from 11.0.0. New capabilities are
added as new optional interfaces a contributor may implement (the rewrite hook, below-table
placement), never as members on `ISchemaContributor`, so a contributor compiled against 11.0.0 keeps
working on every 11.x.

## 8. The ownership check (#57)

SchemaKit owns `IMigrationsModelDiffer` and `IMigrationsSqlGenerator`. When another package replaces
either after SchemaKit's registration, every SchemaKit declaration disappears from migrations without
an error. The check makes that a startup failure.

- **Runtime.** SchemaKit's options extension implements `Validate(IDbContextOptions)`. It reads
  `CoreOptionsExtension.ReplacedServices` (public): EF applies `ReplaceService` registrations after
  every extension's `ApplyServices`, keyed by service type, so a later `ReplaceService` overwrites
  SchemaKit's entry and is visible there. If the entry for either service is not SchemaKit's type,
  it throws, naming the replacing type and pointing to the contributor contract.
- **Design time.** The `.targets`-registered design-time services run before EF's own `TryAdd`s and
  before the user's `IDesignTimeServices`. SchemaKit registers a check on the scaffolder's resolved
  dependencies (`MigrationsScaffolderDependencies.MigrationsModelDiffer`) that throws the same way.
  This is the part to spike first in #57: it relies on EF's design-time registration order, which
  ROADMAP 5.6 documents from the 10.0 sources.
- **Message.** `"IMigrationsModelDiffer is 'Contoso.AuditDiffer', not SchemaKit's. SchemaKit
  declarations would be left out of migrations without an error. Register 'Contoso.AuditDiffer' as a
  SchemaKit contributor instead of replacing the differ: see docs/contributors.md."`
- **Not covered**, as #57 says: a package that replaced the services *before* SchemaKit loses to
  SchemaKit, and SchemaKit cannot see it. The answer there is the other package's guard; CodoMetis
  packages never replace either service (CaffeinatedCoder/CodoMetis.ValueRanges#334).
- The 5.x-to-SchemaKit guard from ROADMAP 5.7 is the same check with one more rule: a differ whose
  type name starts with `EFCore.ComplexIndexes` gets a message naming the packages to remove.

## 9. Annotations and snapshot format v2 (#58)

### Keys and values

One annotation per declaration, on the declaring entity type:

```
CodoMetis:{Owner}:{hash}
```

- `Owner` is the contributor's id: `Pg.Index`, `Pg.Exclusion`, `SqlServer.Index`, later
  `Pg.RowConstraint`, `Ltree.Path`. Owner-branded with `CodoMetis:`, never the product name
  (ROADMAP 5.1), since keys live in consumers' snapshots forever.
- `hash` is the first 16 hex digits of SHA-256 over the declaration's identity. Re-declaring the same
  identity overwrites the same annotation, which is the replace rule without a lookup, and a rename
  rewrites the value in place instead of moving the key.
- The value is the declaration's JSON, discriminated by `$kind`, camel-cased, nulls omitted, with a
  format version `v` and, for declarations whose rendering depends on it, the JSON-member rendering
  version `r` (5.x's model-wide `CustomIndex:RenderingVersion`, now per declaration).

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

One rule the prototype learned the hard way: **a value left out of the JSON must read back as the
same value.** Writing with "omit defaults" dropped `"nullsDistinct":false` (false is `bool`'s
default), and the reader then took the constructor's default, `true`: a `NULLS NOT DISTINCT` index
would have read back as a plain unique one and been rebuilt by the next diff, silently. The property
is now always written, the verbatim test fails without that, and the real writer needs the same rule
as a convention test: every omitted field's JSON default equals its reader default.

### Why one annotation per declaration

5.x stores one JSON array per entity type and kind (`CustomIndex:CompositeIndexes`,
`CustomExclusion:Constraints`). Any change rewrites the whole array in the snapshot, two branches that
each add an index conflict on one line, and a consumer reading the snapshot cannot see which
declaration changed. One annotation each fixes all three.

### The 5.x format is read forever

| 5.x annotation | Read as |
|---|---|
| `CustomIndex:CompositeIndexes` (entity; `paths` or `parts`, `unique`, `filter`, `name`, `props`) | One `PgIndex` or `SqlServerIndex` per element. `props["Npgsql:IndexMethod"]` picks the case; `Npgsql:IndexOperators`, `IndexCollation`, `IndexInclude`, `NullsDistinct`, `CreatedConcurrently`, `StorageParameter:*` and the `SqlServer:*` keys become the case's options |
| `CustomIndex:IsIndexed`, `IsUnique`, `Filter`, `Name` and the `Npgsql:*` / `SqlServer:*` keys on a complex-type **property** | A one-key declaration on the owning entity type, path from the entity root |
| `CustomIndex:RenderingVersion` (model) | `r` on every index read from 5.x; absent means 1, as in 5.x |
| `CustomExclusion:Constraints` | One `PgExclusion` per element; `method` picks the case |
| `CustomTemporal:Constraints`, `CustomTemporal:ForeignKeys` | Not declarations any more ([section 10](#10-what-is-left-out-39)); read only so the handover to Npgsql's native keys is computed instead of drop-and-create (#59) |
| `CustomTemporal:SuppressAutoExtension` | The same switch for `btree_gist` injection |
| `CustomIndex:IndexParts`, `CustomTemporal:WithoutOverlaps`, `ForeignKeyDependentPeriod`, `ForeignKeyPrincipalPeriod` (on operations in old migrations) | Rendered by the legacy SQL generator; never written again |

A 5.x declaration v2 cannot represent (a GIN index declared unique) never applied to a database; the
reader throws with the declaration and the reason, rather than guessing.

### What the swap costs a consumer

The differ compares resolved declarations, not annotations, so a model declared with SchemaKit
against a 5.x snapshot produces **no operations** when the declarations resolve alike (#59 is the
proof per feature). `has-pending-model-changes` stays green; the snapshot is rewritten to v2 by the
consumer's next `migrations add`, whatever it was for. If the consumer runs `migrations add` only to
see, they get a migration with empty `Up` and `Down` and a rewritten snapshot. The migration guide
says that first, and #58's round-trip test checks it against a real 5.x snapshot compiled with Roslyn.

## 10. What is left out (#39)

| 5.x feature | SchemaKit | Why |
|---|---|---|
| `HasTemporalConstraint` | **Left out** (decided) | Npgsql 11's `HasKey(…).WithoutOverlaps()` / `HasAlternateKey(…).WithoutOverlaps()` covers it, and since CaffeinatedCoder/CodoMetis.ValueRanges#335 every ValueRanges range and multirange column too |
| `HasTemporalForeignKey` | **Left out** (decided) | Npgsql 11's `WithPeriod()`. Its one gap, Npgsql's own `NpgsqlRange<T>[]` multirange, is Npgsql's to close, and the guide names it |
| `UseBtreeGist()` | Left out | `modelBuilder.HasPostgresExtension("btree_gist")` is the native spelling; SchemaKit keeps the automatic injection for exclusion constraints with `=` elements |
| Column-only indexes (plain, composite, unique, filtered with plain SQL) | **Open (#39)**; the design keeps a SchemaKit form either way | EF Core 11's `HasIndex` covers complex-type, JSON and complex-collection paths, and Npgsql's and EF SQL Server's `IndexBuilder` extensions carry the options as strings and flags. SchemaKit's form adds P1–P7 and typed filters, and the migration guide offers both. #39 measures the native DDL and default names against 5.x before the guide recommends one |
| Expression and JSON-member indexes, `NULLS FIRST/LAST` on expressions, typed and placeholder filters, exclusion constraints | **Kept** | No native form |
| SQL Server index options | Kept in SchemaKit's typed form | EF's SQL Server `IndexBuilder` has native counterparts; SchemaKit's adds P4. Same #39 measurement |

## 11. Every 5.x call and its new form

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
| `CustomMigrationsModelDiffer` and its virtuals (`ValidateCreateIndexOperation`, `ValidateCreatedIndexes`, `CanRenameIndexes`, `IndexNameScope`, `IsForwardedIndexAnnotation`, `ToOperationAnnotationName`, `TransformIndexAnnotation`, `ResolveUnmappedPart`, `ResolveTemplatePart`, `ResolveFilter`, `QuoteIdentifier`, `MeasureIdentifier`, `ThrowIfIdentifierTooLong`, `ThrowIfDeclaredOnUnmappedType`, `ResolveColumnName`, `ResolveProperty`) | Dropped as public API; replaced by `ISchemaContributor` and `ISchemaDialect` ([section 7](#7-the-contributor-contract-56)) |
| `CustomMigrationsModelDiffer.RuntimeWiringSentinel` | Dropped; nothing new needs runtime wiring. The legacy generator still recognises it in old migrations |
| `ComplexIndexAnnotations.*` constants | Dropped as public API; read by the v1 reader ([section 9](#9-annotations-and-snapshot-format-v2-58)) |

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

## 12. The first slice: 11.0.0-preview.1 (#38)

**The kernel plus exclusion constraints, end to end.** Exclusion constraints are the smallest
feature that exercises the whole design: a closed union (GiST, SP-GiST), the stage types (P2), a
typed operator (P3), a closed option (`Deferral`), the apply call, the read model, `Redeclare`, a
contributor with a rename, the name registry across kinds, `btree_gist` injection, and the v1 reader
for one 5.x annotation. They have no native counterpart, so nothing in the slice waits on #39.

| In preview.1 | Not yet |
|---|---|
| Kernel: `ISchemaObject`, `ISchemaDeclaration<T>`, `KeyTarget`, `Predicate`, `Sql.Raw`, `ValueList`, the lambda translator moved from the PostgreSQL package (ROADMAP 5.4) | The dialect's SQL Server spellings |
| `Declare`, `GetDeclarations`, `FindDeclaration`, `Redeclare` | |
| `ISchemaContributor`, `SchemaContributor<T>`, `SchemaPlacement`, the generic descriptor differ, the name registry | The optional rewrite and below-table placement interfaces ([section 13](#13-room-for-the-horizon)) |
| Snapshot v2 for `Pg.Exclusion`, the v1 reader for `CustomExclusion:Constraints`, the #59 test for exclusion constraints | v2 and v1 for indexes |
| `PgSchema<T>`: `Equal`, `Overlaps`, `Exclusion`; `DeclarePostgres`; `UseSchemaKit()` on Npgsql's builder | `PgIndex` and its six methods: preview.2 |
| The ownership check, runtime half | Design-time half: after its spike |
| The compile probes, moved from `test/SchemaKit.ApiProbes` into the kernel's test project against the real assemblies | SQL Server: preview.3 |

Done when `migrations add`, `EnsureCreated()`, `GenerateCreateScript()` and the pending-changes check
all see a declared exclusion constraint on PostgreSQL 16 and 18, a 5.x model with exclusion
constraints swaps to SchemaKit with no operations, and the exclusion-constraint probes (P2, `P3.overlaps-on-text`, P6, P8, P9) hold
against the shipped assembly.

## 13. Room for the horizon

ROADMAP section 7 lists what the umbrella admits. Each is a contributor with its own closed union, and
needs from the contract at most one optional interface:

| Item | Declaration shape | Needs |
|---|---|---|
| Row constraints / delete guard (ROADMAP 4.2) | `pg.DeleteGuard(name:, message:)` unconditional, `pg.DeleteGuard(when: x => x.FinalizedAt != null, name:, message:)` with `name` required (a parameter without a default: CS7036 when missing, since the application dispatches on it) | Contract as-is |
| 7.1 Row-level security policies | `pg.Policy(name, PolicyCommand.Select, using: x => …, withCheck: x => …)`, `PolicyCommand` closed | A session-setting marker in the translator (`Session.Setting("app.tenant")`) |
| 7.2 Extended statistics | `pg.Statistics(name, StatisticsKind.Dependencies \| …, [pg.Key(…)])` | Contract as-is |
| 7.3 Domain types | `model.DeclarePostgres(pg => [pg.Domain<Email>("email", check: v => v.Value.Length <= 320)])` on the model | Below-table placement; a type-mapping plugin through the same options extension |
| 7.4 Deferrable keys | An annotation on EF's own key, set through a SchemaKit extension on `KeyBuilder` | The rewrite hook (unique half) |
| 7.5 Trigger templates | `pg.Touch(x => x.UpdatedAt)`, `pg.VersionBump(x => x.Version)` | Contract as-is |
| 7.6 Views and procedure bodies | `pg.ViewDefinition(Sql.Raw<…>(…))`, text-diffed, with the caveat the roadmap states | Contract as-is |
| 7.7 Partitioning | `pg.RangePartitions(x => x.OccurredAt, Monthly, from, to)` | The rewrite hook and the stock generator at design time |
| 7.8 Closure tables | `pg.ClosureTable(x => x.ParentId)` | From 7.5 |

## 14. Open questions for the maintainer

1. **Names.** `DeclarePostgres` / `DeclareSqlServer` were chosen over `HasPostgresSchema` (in
   PostgreSQL "schema" is a namespace) and over one `Declare` with overloads (with both providers
   referenced, overload resolution between the two lambdas depends on the lambda body). Also to
   confirm: `PgSchema<T>` / `SqlServerSchema<T>` for the factories, `pg.Unique` as B-tree's unique
   form, `Sql.Raw<T>`, and the owners `Pg.Index`, `Pg.Exclusion`, `SqlServer.Index` that land in
   snapshots for good.
2. **Column-only indexes (#39).** Keep SchemaKit's typed index for column-only keys next to the
   native `HasIndex`, as this design does, or leave column-only indexes to the platform and keep
   `PgIndex` for expression keys only? Keeping it is what makes P1 reach plain indexes; dropping it
   halves the index surface.
3. **Rendering indexes at design time.** [Section 7](#rendering-design-time-only) renders every index
   as a `SqlOperation`, ending runtime wiring for new code. 5.x rendered indexes as
   `CreateIndexOperation`s, which keeps EF's own index operations in scaffolded migrations. Agreed?
4. **An analyzer.** The lambda bodies are the one place the type system cannot reach
   ([What the compiler cannot see](#what-the-compiler-cannot-see)). A SchemaKit analyzer could make
   the translator's refusals (an enum comparison, `Normalize()`, an expression key on SQL Server)
   compile errors. In scope for 11.0.0, or later?
5. **Exclusion element syntax.** The staged chain (`pg.Equal(…).Overlaps(…)`) is the one place the
   surface is not named parameters. The alternative, `pg.Exclusion(overlaps: x => x.Period, equal:
   [x => x.A, x => x.B])`, is flatter but fixes the element order (equality first) and loses
   arbitrary operators. Keep the chain?
6. **First slice.** Exclusion constraints first ([section 12](#12-the-first-slice-1100-preview1-38)),
   or `PgIndex` first because more consumers use indexes?
7. **Server-version gates.** `INCLUDE` on GiST (12), SP-GiST (14) and `NULLS NOT DISTINCT` (15) are
   syntax errors on older servers. Npgsql's `SetPostgresVersion` is available at `migrations add`;
   should SchemaKit check against it?
8. **The design-time ownership check** depends on EF's design-time registration order; if the spike
   in #57 finds no reliable hook, is a runtime-only check acceptable for 11.0.0?
9. **Collation and operator-class typing.** `Collate` is limited to string and char keys (P3). Extend
   the same treatment to operator classes for the CLR types that decide their store type
   (`NpgsqlRange<T>` → `GistOpClass.RangeOps`), or keep operator classes typed by method only?
