# PostgreSQL — indexes

This page covers what the **EFCore.ComplexIndexes.PostgreSQL** package adds to your indexes. You
can choose where nulls sort, set index methods and options, and index SQL expressions, lambdas and
JSON members. Filters can be lambdas too. The package works through
[Npgsql](https://www.npgsql.org/efcore/), and brings the core package with it.

For temporal `UNIQUE` and `EXCLUDE` constraints, see
[PostgreSQL — temporal and exclusion constraints](postgresql-constraints.md).

## Per-column null ordering

Wrap a member in `DbOrder.NullsFirst(...)` or `DbOrder.NullsLast(...)` to choose where its nulls
sort. You can combine it with `Desc`:

```csharp
builder.HasComplexCompositeIndex(
    x => new { x.Name, Reviewed = DbOrder.NullsLast(DbOrder.Desc(x.ReviewedAt)) });
// CREATE INDEX ... ON ... (name, reviewed_at DESC NULLS LAST);
```

EF Core's index operation has no place for null ordering, so the package's own SQL generator writes
these indexes. They need
[`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it) once in your
context's configuration. On SQL Server, `migrations add` fails on these markers, because SQL Server
has no `NULLS FIRST/LAST` syntax.

## Index methods on a complex property

Pass a builder callback to set PostgreSQL's options: GIN, GiST, BRIN, SP-GiST, Hash, operator classes,
`INCLUDE`, concurrent creation and nulls-distinct.

```csharp
builder.ComplexProperty(x => x.Payload, c =>
    c.Property(x => x.Json)
     .HasComplexIndex(idx => idx
         .UseGin()
         .HasOperators("jsonb_path_ops"))
);
```

For storage parameters, call `HasStorageParameter` once per parameter, and they become `WITH (…)`.
For collations, `UseCollation` takes one per column, by position. An empty entry leaves that column
on its default:

```csharp
builder.HasComplexCompositeIndex(x => new { x.Name, x.Email.Value }, idx => idx
    .UseCollation("C", "")
    .HasStorageParameter("fillfactor", 70)
    .HasStorageParameter("deduplicate_items", false));
// CREATE INDEX ... ON people ("Name" COLLATE "C", email) WITH (fillfactor=70, deduplicate_items=false);
```

Good to know:

- **Strings are quoted, booleans are written bare.** Pass each parameter as its own type.
- **Pass a fractional value as a string.** Npgsql's generator writes an index on plain columns, and
  formats numbers with the current culture. Under de-DE, `0.25` comes out as `0,25`, and the
  migration fails with a syntax error when it runs. `HasStorageParameter("x", "0.25")` writes
  `x='0.25'`, which PostgreSQL reads as the number.
- **The index collation is separate from the column's.** A `UseCollation` on the property never
  carries over to the index.

## Index a SQL expression

> Needs [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it).
> `HasExpressionIndex` extends `EntityTypeBuilder<TEntity>`, so you can use it on any entity, with
> complex properties or without.

**The package writes each string exactly as you give it.** It doesn't map properties to columns and
doesn't add quotes. Write the SQL as it should appear inside the index, with the real column names.

**One expression:**

```csharp
// CREATE INDEX "IX_person_lowerlastname" ON person ((lower(last_name)));
builder.HasExpressionIndex("lower(last_name)");
```

**Unique, filtered, with a name:**

```csharp
builder.HasExpressionIndex(
    "lower(email)",
    isUnique:  true,
    filter:    "deleted_at IS NULL",
    indexName: "ix_person_email_ci");
```

**Several parts in order, with provider options (builder callback):**

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

**Descending parts:** call `.Descending()` after a part to sort it in descending order.

```csharp
builder.HasExpressionIndex(idx => idx
    .Expression("created_at").Descending()
    .Expression("lower(email)"));
// CREATE INDEX ... ON person ((created_at) DESC, (lower(email)));
```

**Full-text search or JSONB, with a GIN index:**

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

### Quote column names yourself

The package passes strings through as they are. If a column name needs quotes in PostgreSQL, such as
a PascalCase one, write the quotes yourself. A C# raw string literal keeps that readable:

```csharp
// CREATE INDEX ... ON "People" ((lower("Email")));
builder.HasExpressionIndex(""" lower("Email") """.Trim());
```

## Typed (LINQ) expression indexes

> Needs [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it), like
> every expression index.

Instead of raw SQL, pass a lambda. `migrations add` maps its property paths to columns in your
finalized model, so the index follows `HasColumnName`, complex-property columns and even `ToJson()`
members:

```csharp
builder.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true);
// CREATE UNIQUE INDEX ... ON people ((lower("email")));

builder.HasExpressionIndex(x => (x.Nickname ?? x.FirstName) + " " + x.LastName);
// CREATE INDEX ... ON people (((coalesce("nickname", "first_name") || ' ') || "last_name"));
```

The lambda can use a small set of operations on purpose:

- **String methods:** `ToLower`/`ToUpper`, `Trim`/`TrimStart`/`TrimEnd` without arguments,
  `Substring` (the package converts its 0-based start to SQL's 1-based one) and `Replace`.
- **Members:** `string.Length`, and `.Value` and `.HasValue` on a nullable.
- **Operators:** string concatenation (`+`) and null coalescing (`??`). An operand of `+` that can be
  null is wrapped in `coalesce(…, '')`, so null counts as an empty string, as in C# and EF Core's
  queries.
- **Constants.** A captured variable is evaluated and written into the SQL, formatted with the
  invariant culture.

Anything else throws `NotSupportedException` **where you declare the index**, and the message points
you to the raw-SQL overload. That includes any other member of a scalar type, such as
`CreatedAt.Year`, which is not a mapped property.

Paths also reach through a value converter. When `Email` is a value object stored in one column,
`x => x.Email.Value.ToLower()` maps `Email.Value` to that column. This works when `Value` has the
converter's provider type, since then the column holds exactly that member.

Filters take the same placeholders. `filter: "{DeletedAt} IS NULL"` or `"{Payload.Kind} = 'invoice'"`
becomes the column, or the JSON read, when you run `migrations add`, and the migration contains the
result. Only a pair of braces around a dotted property path, outside a single-quoted string, is a
placeholder. Array and JSON literals such as `'{urgent}'` and `'{"a": 1}'` stay as you wrote them.

## Typed filters

> Needs no `UseNpgsqlComplexIndexes()`. The package turns the predicate into a filter with
> placeholders where you declare it. `migrations add` then writes it into the migration like any other
> placeholder filter.

Every method that takes a `filter:` string also takes a predicate, and the builders have
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

The index builders are not generic, so `HasFilter<TEntity>` takes the entity type explicitly.

A predicate can use a small set of operations, and anything else throws where you declare it:

- **Comparisons:** `==` and `!=` (against `null` they become `IS NULL` and `IS NOT NULL`), `<`, `<=`,
  `>` and `>=`.
- **Logic:** `&&`, `||` and `!`, boolean properties and captured booleans.
- **Null checks:** `.HasValue`, which becomes `IS NOT NULL`.
- **Operands:** the string operations and constants that a
  [typed expression index](#typed-linq-expression-indexes) accepts.

A comparison gives the same answer as C# where an operand is null. The package writes it the way EF
Core writes the same lambda in a query, so the index covers exactly the rows that query selects:

- **On a `string?`**, `x.Kind != "federated"` becomes `("Kind" <> 'federated' OR "Kind" IS NULL)`.
- **A negation** is pushed down to the comparison. A negated `<`, `<=`, `>` or `>=` over an operand
  that can be null becomes EF Core's `CASE WHEN … THEN FALSE ELSE TRUE END`.
- **A computed operand** is null-checked through its columns: `lower("Name") <> 'a' OR "Name" IS NULL`.
- **A captured variable holding null** compares with `IS NULL`.

Good to know:

- **Whether an operand can be null** comes from its C# declaration: a `Nullable<T>`, or a reference
  type not annotated as non-nullable. A `string` declared non-nullable but mapped to a nullable
  column is compared as non-nullable.
- **A `char` compares with a char literal.** `x.Grade == 'A'` becomes `"Grade" = 'A'`, although C#
  compares the char's code.
- **Enum comparisons throw, on purpose.** How an enum is stored depends on the property's value
  conversion, which a filter can't see. `Status == Status.Active` would compare a text column with
  `0`, and the migration would fail when it runs. Write the stored value in the string overload
  instead.
- **Values with no portable SQL spelling throw,** such as a `DateTime` or a `Guid`. They used to come
  out as bare text.

## JSON member indexes

> Needs [`UseNpgsqlComplexIndexes()`](../README.md#runtime-wiring--the-two-features-that-need-it):
> a JSON member index is an expression index.

The members of a complex property mapped to JSON with `ToJson()` have no table columns. You still
declare their indexes **the same way**, and the index is on an expression that reads the member. If
you move a value object between table columns and a JSON document, you don't rewrite its index
declarations:

```csharp
builder.ComplexProperty(x => x.Name, c => c.ToJson("name"));

// Entity level …
builder.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
// … or property level, inside the complex property:
//   c.Property(x => x.ShortName).HasComplexIndex(isUnique: true);

// ALTER: CREATE UNIQUE INDEX "ux_employer_short_name" ON employers (("name" ->> 'ShortName'));
```

The package reads each member **exactly the way Npgsql's queries read it**, because PostgreSQL uses
an expression index only for a query with the same expression. That decides the operator and the
cast:

| Member | Index expression | Used by |
|---|---|---|
| `string`, enum stored as string | `"name" ->> 'ShortName'` | `x.Name.ShortName == …` |
| nested member | `"profile" #>> '{Address,City}'` | `x.Profile.Address.City == …` |
| `int`, `long`, `short`, `decimal`, `double`, `bool`, `Guid`, enum | `CAST("profile" ->> 'Rank' AS integer)` (the member's store type) | `x.Profile.Rank == …`, `> …`, `ORDER BY` |
| `byte[]` | `decode("profile" ->> 'Blob', 'base64')` | `x.Profile.Blob == …` |
| primitive collection, `JsonDocument` or other `jsonb` member | `"profile" -> 'Tags'` (`jsonb`) | a GIN over it |
| `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` | `"profile" ->> 'At'` (text) | uniqueness only |

`HasJsonPropertyName` is followed throughout.

Date and time members are the exception. Npgsql's queries cast them to `timestamp with time zone`,
`date` or `time`. PostgreSQL can't index those casts, because the conversion from text is not
`IMMUTABLE`, so no index can match the query. The package keeps these members as text:

- **A unique index** over one still keeps the values unique.
- **A non-unique index that starts with one** makes `migrations add` fail, since no query would ever
  use it. Put another part first, or map the member to a regular column.
- **In the filter of an index or exclusion constraint**, you can test the member with `IS NULL` or
  compare it with a string literal. Comparing it with a column makes `migrations add` fail,
  because text against a timestamp fails when the migration runs.

> **Upgrading from 5.3 or earlier:** before 5.4.0 every member was read as text with `->` segments.
> That matched the query only for a top-level string, so such indexes kept values unique but no
> query used them. The first `migrations add` after the upgrade drops and re-creates each affected
> index under its existing name. Indexes on top-level string members stay as they are. Until that
> migration exists, `has-pending-model-changes` reports changes and `Migrate()` raises EF Core's
> pending-model-changes error. The re-create is a plain `CREATE INDEX`, which blocks writes while it
> builds. On a large table, declare the index with `IsCreatedConcurrently()` first.

### Indexing the whole document

> Needs no `UseNpgsqlComplexIndexes()`: the document is a real column, so Npgsql's own SQL generator
> writes the index.

Point the selector at the JSON-mapped complex property itself, or at a complex collection, which is
always JSON. The index then goes on the `jsonb` column that holds the document. In PostgreSQL that is
usually a GIN index, often with `jsonb_path_ops`:

```csharp
builder.ComplexProperty(x => x.Payload, c => c.ToJson("payload"));
builder.ComplexCollection(x => x.Tags,  c => c.ToJson("tags"));

builder.HasComplexIndex(x => x.Payload, ix => ix.UseGin().HasOperators("jsonb_path_ops"));
// CREATE INDEX "IX_orders_payload" ON orders USING gin (payload jsonb_path_ops);

builder.HasComplexIndex(x => x.Tags, ix => ix.UseGin());
// CREATE INDEX "IX_orders_tags" ON orders USING gin (tags);
```

A complex property *nested inside* the document has no column of its own. It becomes a `->` read
(`("payload" -> 'Address')`, which yields `jsonb`). That makes it an expression index, which needs
`UseNpgsqlComplexIndexes()` like the member indexes above.
