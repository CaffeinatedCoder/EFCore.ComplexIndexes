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
