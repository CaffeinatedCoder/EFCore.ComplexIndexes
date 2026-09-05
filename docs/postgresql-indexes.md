# PostgreSQL — indexes

Provided by the **EFCore.ComplexIndexes.PostgreSQL** package, via
[Npgsql](https://www.npgsql.org/efcore/). The core package is included automatically.

For temporal `UNIQUE` and `EXCLUDE` constraints, see
[PostgreSQL — temporal and exclusion constraints](postgresql-constraints.md).

## Per-column null ordering

`DbOrder.NullsFirst(...)` / `DbOrder.NullsLast(...)` control where nulls sort; the markers compose with `Desc`:

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, Reviewed = DbOrder.NullsLast(DbOrder.Desc(x.ReviewedAt)) });
// CREATE INDEX ... ON ... (name, reviewed_at DESC NULLS LAST);
```

Null ordering has no slot on EF's native index operation, so these indexes render through the package's PostgreSQL SQL generator — they require the one-time [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it) wiring, and the SQL Server differ rejects the markers (SQL Server has no `NULLS FIRST/LAST` syntax).

## Index methods on a complex property

Use the builder-callback overload to reach the PostgreSQL-specific options (GIN, GiST, BRIN, SP-GiST, Hash, operator classes, `INCLUDE`, concurrent creation, nulls-distinct):

```csharp
builder.ComplexProperty(x => x.Payload, c =>
    c.Property(x => x.Json)
     .HasComplexIndex(idx => idx
         .UseGin()
         .HasOperators("jsonb_path_ops"))
);
```

Storage parameters render as `WITH (…)`; call `HasStorageParameter` once per parameter. Strings are
quoted, booleans render bare. Per-column collations are positional — an empty entry leaves that
column on its default:

```csharp
builder.HasComplexCompositeIndex(x => new { x.Name, x.Email.Value }, idx => idx
    .UseCollation("C", "")
    .HasStorageParameter("fillfactor", 70)
    .HasStorageParameter("deduplicate_items", false));
// CREATE INDEX ... ON people ("Name" COLLATE "C", email) WITH (fillfactor=70, deduplicate_items=false);
```

The index collation is independent of the column's own `UseCollation` on the property, which is never
copied onto the index.

## Expression (functional) indexes

> Requires [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it).
> Available as an extension on `EntityTypeBuilder<TEntity>`, so it works on any entity — complex or not.

**Each string is emitted verbatim** — there is no property-to-column resolution and no automatic quoting. Write the final SQL exactly as it should appear inside the index, referencing real column names.

**Single expression:**

```csharp
// CREATE INDEX "IX_person_lowerlastname" ON person ((lower(last_name)));
builder.HasExpressionIndex("lower(last_name)");
```

**With unique / filter / explicit name:**

```csharp
builder.HasExpressionIndex(
    "lower(email)",
    isUnique:  true,
    filter:    "deleted_at IS NULL",
    indexName: "ix_person_email_ci");
```

**Multiple ordered parts + provider options (builder callback):**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("country")            // a plain column, written as raw SQL
    .Expression("lower(email)")       // a SQL expression
    .IsUnique()
    .HasFilter("deleted_at IS NULL")
    .HasName("ix_person_country_email_ci"));
// CREATE UNIQUE INDEX "ix_person_country_email_ci"
//   ON person ((country), (lower(email)))
//   WHERE deleted_at IS NULL;
```

**Descending parts:** call `.Descending()` after any part to sort it descending:

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("created_at").Descending()
    .Expression("lower(email)"));
// CREATE INDEX ... ON person ((created_at) DESC, (lower(email)));
```

**Full-text / JSONB with a GIN index:**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("to_tsvector('english', body)")
    .UseGin());
// CREATE INDEX ... ON articles USING gin ((to_tsvector('english', body)));
```

**Covering expression index (`INCLUDE`):**

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("lower(email)")
    .IsUnique()
    .IncludeProperties("display_name"));
```

### Quoting tip

Strings are passed through untouched, so identifiers that need PostgreSQL quoting (e.g. PascalCase columns) must include the quotes yourself. C# raw string literals keep this readable:

```csharp
// CREATE INDEX ... ON "People" ((lower("Email")));
builder.HasExpressionIndex(""" lower("Email") """.Trim());
```

## Typed (LINQ) expression indexes

> Requires [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it), like all expression indexes.

Instead of raw SQL, pass a lambda — property paths stay symbolic and are resolved against the
finalized model at `migrations add` time, so `HasColumnName`, complex-property columns, and even
`ToJson()` members are honored automatically:

```csharp
builder.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true);
// CREATE UNIQUE INDEX ... ON people ((lower("email")));

builder.HasExpressionIndex(x => (x.Nickname ?? x.FirstName) + " " + x.LastName);
// CREATE INDEX ... ON people (((coalesce("nickname", "first_name") || ' ') || "last_name"));
```

The supported subset is deliberately small and fails loudly: `ToLower`/`ToUpper`, `Trim`/`TrimStart`/`TrimEnd`, `Substring` (1-based conversion handled), `Replace`, `string.Length`, string concatenation (`+`), null coalescing (`??`), and constants (captured variables are evaluated and inlined invariant-culture). Anything else throws `NotSupportedException` **at declaration time** with a pointer to the raw-SQL overload.

Paths see through a value converter as well: when `Email` is a value object mapped as one column,
`x => x.Email.Value.ToLower()` resolves `Email.Value` to that column — provided `Value` has the
converter's provider type, which is what makes the column hold exactly that member.

Filters take the same placeholders: `filter: "{DeletedAt} IS NULL"` or
`"{Payload.Kind} = 'invoice'"` resolve to the column — or the JSON extraction — at `migrations add`,
and the resolved text is what the migration carries. Only a brace pair holding a dotted identifier
path outside a single-quoted literal is a placeholder, so array and JSON literals such as
`'{urgent}'` and `'{"a": 1}'` are left alone.

## Typed filters

> No runtime wiring required — the predicate is translated at declaration into a filter template
> and resolved into the migration like any placeholder filter.

Every entry point that takes a `filter:` string also takes a predicate, and the builders take
`HasFilter(x => …)`:

```csharp
builder.HasComplexIndex(x => x.Email, x => x.RevokedAt == null, isUnique: true);
// CREATE UNIQUE INDEX ... WHERE "revoked_at" IS NULL

builder.HasComplexCompositeIndex(x => new { x.GranteeId, x.Kind },
    x => x.RevokedAt != null && x.Kind == "federated");
// WHERE ("revoked_at" IS NOT NULL AND "kind" = 'federated')

builder.HasExpressionIndex(x => x.Email.ToLower(), x => !x.IsDeleted);

builder.ComplexProperty(x => x.Address, c => c.Property(a => a.City)
    .HasComplexIndex(ix => ix.HasFilter<Person>(p => p.DeletedAt == null)));
```

The builders are not generic, so `HasFilter<TEntity>` takes the entity type explicitly. The subset
is small and fails at the declaration: `==`/`!=` (against `null`: `IS NULL`/`IS NOT NULL`), `<`,
`<=`, `>`, `>=`, `&&`, `||`, `!`, boolean properties, captured booleans, and on the operands the
string operations and constants a typed expression index accepts. Two things are refused on
purpose. An enum comparison: how an enum is stored depends on the property's value conversion,
which a filter cannot see, so `Status == Status.Active` would compare a text column against `0`
and fail when the migration is applied — write the stored value in the string overload. And a
value with no portable SQL spelling, a `DateTime` or `Guid`, which used to come out as bare text.

## JSON member indexes

> Requires [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it) — JSON member indexes are expression indexes under the hood.

When a complex property is mapped to JSON with `ToJson()`, its members have no table columns — yet
the **same index declarations keep working**: the differ resolves them to `->>`
extraction expressions instead. Moving a value object between scalar columns and a JSON document
does not force you to rewrite its indexes:

```csharp
builder.ComplexProperty(x => x.Name, c => c.ToJson("name"));

// Entity level …
builder.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
// … or property level, inside the complex property:
//   c.Property(x => x.ShortName).HasComplexIndex(isUnique: true);

// ALTER: CREATE UNIQUE INDEX "ux_employer_short_name" ON employers (("name" ->> 'ShortName'));
```

Nested complex types become `->` segments (`("profile" -> 'Address' ->> 'City')`), and
`HasJsonPropertyName` is honored. Members are extracted as **text**; for typed comparisons or
ordering semantics use `HasExpressionIndex` with an explicit cast.

### Indexing the whole document

> No runtime wiring: the container is a real column, so the index renders through the stock generator.

Point the selector at the JSON-mapped complex property itself — or at a complex collection, which is
always JSON — and the index lands on the `jsonb` container column. The PostgreSQL idiom is a GIN
index, usually with `jsonb_path_ops`:

```csharp
builder.ComplexProperty(x => x.Payload, c => c.ToJson("payload"));
builder.ComplexCollection(x => x.Tags,  c => c.ToJson("tags"));

builder.HasComplexIndex(x => x.Payload, ix => ix.UseGin().HasOperators("jsonb_path_ops"));
// CREATE INDEX "IX_orders_payload" ON orders USING gin (payload jsonb_path_ops);

builder.HasComplexIndex(x => x.Tags, ix => ix.UseGin());
// CREATE INDEX "IX_orders_tags" ON orders USING gin (tags);
```

A complex property *nested inside* the document has no column of its own and resolves to a `->`
extraction instead (`("payload" -> 'Address')`, yielding `jsonb`), so it is an expression index and
needs the runtime wiring like the member indexes above.
