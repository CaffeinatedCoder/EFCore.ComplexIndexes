---
name: plain-docs
description: The voice for every page a consumer reads in EFCore.ComplexIndexes: the root README, the package READMEs and the guides in docs/. Plain, friendly, example-first and exact. Use when writing or rewriting any of them, including a new section, a new docs page, an upgrading entry, or a paragraph added beside a feature.
---

# Plain docs

The reader is a .NET developer deciding whether to try EFCore.ComplexIndexes, or using it right now.
They should understand each paragraph on the first read, and finish a section knowing what to type.
A page that is correct but hard to read loses them before the details matter.

This voice is friendly, not chatty. Every claim stays as exact as before. Plain words make the docs
readable; they never make them vaguer.

## Which pages

The root `README.md`, the package READMEs under `src/*/README.md`, and the consumer guides:
`docs/postgresql-indexes.md`, `docs/postgresql-constraints.md`, `docs/sqlserver.md` and
`docs/upgrading.md`. `CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, `ROADMAP.md`, `SECURITY.md` and
the changelogs keep their own style. The changelogs follow CONTRIBUTING.md#changelog, which
`ChangelogConsistencyTests` enforces.

## The voice

- **Write to "you".** "You declare the index in `OnModelCreating`" rather than "indexes are
  declared in `OnModelCreating`".
- **Say what the reader gets first, then how it works.** "`dotnet ef migrations add` writes the
  `CREATE INDEX` for you" comes before "the package replaces EF Core's `IMigrationsModelDiffer`".
- **One idea per sentence.** Split a sentence at a semicolon, a colon followed by a list, a dash
  that starts a second thought, or a second "and". About 20 words is a good ceiling.
- **Active voice, a person or a thing doing something.** "`migrations add` fails and names the
  index" rather than "a targeted error naming the index".
- **Natural word order.** Not "What the differ resolves, the migration carries", not "Every
  declaration the core differ refuses".
- **Don't personify the code.** A differ doesn't "refuse" or "see", a filter doesn't "say", a
  sentinel doesn't "guard". Write what happens: "`migrations add` fails", "the index is created
  without its filter".
- **State things positively when you can.** "Name both indexes" instead of "an index without a name
  is not allowed here".
- **No hype and no filler.** Avoid "simply", "just", "easy", "seamless", "powerful", "killer
  feature", "zero ceremony", "out of the box", "under the hood", "first-class", exclamation marks and
  emoji. Friendly comes from clarity, not from adjectives.

## Plain words

Use the left column in prose. Keep the API's own names in code (`HasComplexIndex`,
`UseNpgsqlComplexIndexes`, `ComplexIndexDeclaration`, `DbOrder.NullsLast`), and introduce a term
once where the API or EF Core forces it on the reader.

| Write | Instead of |
|---|---|
| the PostgreSQL package, the SQL Server package | satellite, satellite package |
| fail, throw, reject (a person or a database rejects) | refuse, refusal |
| the package's migration code, or "the differ" once you have said it builds the migration operations | the differ, unexplained |
| `UseNpgsqlComplexIndexes()` (name the call) | runtime wiring, the runtime seam |
| when you run `dotnet ef migrations add`; in the migration | at design time, design-time pipeline |
| when the migration runs, when you apply the migration | at apply time, at runtime |
| EF Core's own SQL generator | the stock generator |
| `migrations add` writes, the migration contains | scaffold, baked into the migration |
| a placeholder column that makes the migration fail | the sentinel |
| your code, your application, your model | the consumer, the caller |
| you declare, your declaration | the declaration, used bare as a subject |
| PostgreSQL uses the index for a query | the index serves the query |
| works with any relational provider | provider-agnostic |
| the migration runs without an error | applies cleanly |
| keeps, uses | honors, rides on, flows as |
| on purpose | deliberately, by design |
| what makes two declarations the same index | identity, the identity key |

Name an error after the plain description, in parentheses: "the migration fails when it runs
(42P07)", "`migrations add` fails (`InvalidOperationException`)". The code helps someone searching a
log; the words help everyone else. Keep SQLSTATEs and SQL Server message numbers wherever the old
text had them.

## Page shape

1. **Open with one or two sentences** on what the page lets the reader do. Then show the smallest
   example that works.
2. **Headings are tasks or questions** in guides: "Index a member of a JSON document", "Keep
   overlapping bookings out". Keep every heading something links to (see below), and the version
   headings in `docs/upgrading.md`.
3. **Example first, explanation after.** Each section leads with code where there is code, then says
   what it does in a few short sentences.
4. **Edge cases go last**, under a short "Good to know" list at the end of the section, not in the
   middle of the explanation. Each one is a bullet with a bold lead phrase.
5. **Lists for three or more parallel items.** A bullet starts with a bold phrase that a skimmer can
   read alone, followed by one or two sentences.
6. **Link instead of repeating.** A guide explains and a package README is the short version for
   NuGet; the same detail lives in one place and the other links to it.
7. **Upgrading entries** say what changed, what the first `migrations add` after the upgrade does,
   and what you do about it, in that order. Keep each one's SQLSTATE, row-cleanup advice and
   diagnostic query.

## What must not change

Rewriting the voice must not break what the tests guard, or what nothing guards. Run the unit suite
after every page:

```bash
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj --filter "TestCategory!=Integration"
```

- **Code blocks are not compiled, so nothing catches a mistake in one.** Leave the code as it is,
  byte for byte. A comment that states an output (generated SQL, an error message) must stay exact.
  You may reword a comment's prose part; if you touch the value, confirm it against the test that
  covers that behaviour, or run it. An error message quoted in a fence is the source's text: check
  it against the code, not against the old page.
- **API names are checked.** `DocumentationApiTests` reads every `Name(` in inline code and in
  `csharp` fences, and fails when it is not public API of this repository or on its allowlist of EF
  Core, Npgsql and BCL calls. A typo in a new sentence fails it, and so does an EF Core method the
  list lacks. Add a real external call to `ExternalApi` in the test.
- **Provider pages are scoped.** The same test fails when `docs/sqlserver.md` or the SQL Server
  README names a PostgreSQL-only method such as `UseGin()`, or the other way round. The root README
  and `docs/upgrading.md` name both.
- **Links and anchors are checked.** `DocumentationLinkTests` resolves relative links, their
  `#anchors`, and absolute `https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/…`
  links. Renaming a heading changes its anchor. Find every link to it with
  `git grep -n 'page.md#anchor'` and update them in the same change.
- **Keep headings that published text links to.** The changelogs and the release notes already on
  nuget.org link `blob/main/…#anchor` into `README.md`, `docs/*.md` and `docs/upgrading.md`. The
  test would make you edit the changelog to rename one, and the published notes cannot be edited.
  Check with `git grep -n -E '\.md#' -- '*.md' src` and keep every heading listed.
- **`docs/upgrading.md` has rules of its own.** The version sections run oldest first, and every
  `### Changed answers` row in a changelog links its entry here (`ChangelogConsistencyTests`). Keep
  each entry's heading.
- **Package READMEs are shown on NuGet.** Links in `src/*/README.md` are absolute GitHub URLs,
  because relative ones break there (`DocumentationLinkTests`). Keep them absolute. No README holds
  a changelog section (`ChangelogConsistencyTests`).
- **Strings a user searches for stay exact.** `__requires_UseNpgsqlComplexIndexes__`,
  `__requires_UseComplexIndexes__()`, annotation keys such as `CustomIndex:RenderingVersion`, and
  SQLSTATEs appear in error messages and logs.
- **Facts stay exact.** If the old sentence had a condition, an exception, a provider, a SQLSTATE
  or a version, the new one has it too, perhaps moved into "Good to know". When unsure what a
  sentence means, read the code or the test it describes before rewriting it. A sentence about
  what PostgreSQL or SQL Server does is backed by the integration tests; keep its meaning even when
  its wording changes.
- **The Documentation table** in the root README links every consumer page. Keep its rows in step
  with the pages' new opening sentences.
