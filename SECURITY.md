# Security policy

## Reporting a vulnerability

**Please do not open a public issue for security reports.**

Report privately through GitHub:

> **[Report a vulnerability](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/security/advisories/new)**
> (Security → Advisories → Report a vulnerability)

If you cannot use GitHub advisories, email **meistercoder@mr-gross.de** with `SECURITY` in the
subject.

Helpful to include, as far as you have it:

- The package and version (`EFCore.ComplexIndexes`, `.PostgreSQL`, or `.SqlServer`)
- The EF Core and provider versions, and the target database
- A minimal `OnModelCreating` snippet that triggers it
- The generated migration or DDL, and what you expected instead
- Why you consider it security-relevant — the impact you have in mind

## What to expect

This is a single-maintainer, non-commercial open-source project. There is no SLA, and the
following is a good-faith intention rather than a guarantee:

| | |
|---|---|
| Acknowledgement | within 7 days |
| Initial assessment | within 30 days |
| Fix or documented decision not to fix | best effort, tracked in the advisory |

You will be told either way. If a report turns out not to be a security issue, it gets handled as
an ordinary bug and you will be told that too, with reasoning.

## Disclosure

Coordinated disclosure. The intent is to publish a fix and a GitHub Security Advisory together,
and to keep the report private until then. Ninety days from acknowledgement is the default ceiling
for going public regardless of fix status — if that timing does not suit you, say so in the report
and it can be discussed.

Reporters are credited in the advisory unless they ask not to be.

## Supported versions

Up to two lines are supported at once, one per EF Core major. From EF Core 11 on the line ships
under a new package id: `EFCore.ComplexIndexes` 5.x serves EF Core 10, and its successor
`CodoMetis.EFCore.SchemaKit` 11.x serves EF Core 11. Each line receives security fixes on its
latest released minor. Older minors of a line are fixed by upgrading to its latest minor, which
never changes the EF Core major you target. Older lines are not maintained — the remedy for those
is to upgrade.

| Version | Supported |
|---|---|
| 5.5.x | ✅ |
| < 5.5 | ❌ |

Previews (`11.0.0-preview.N`) are not supported versions and have no row: a fix reaches a preview
in the next preview.

### How the table changes

- **Until SchemaKit 11.0.0 ships**, one line is supported: the latest 5.x minor.
- **When SchemaKit 11.0.0 ships**, the table gets its second row: 11.0.x, and below it the latest
  5.x minor. From then on each line's row moves when that line ships a minor.
- **When EF Core 10 leaves support** (November 2028), 5.x stops receiving fixes and its row goes.
  The `EFCore.ComplexIndexes` ids are then deprecated on nuget.org with SchemaKit as the
  alternate, and not before: while EF Core 10 is supported they are the only package serving it.

### For how long

There is no fixed end-of-support date. Each line is tied to the EF Core major it targets:

- **5.x** targets **EF Core 10** (`net10.0`, .NET 10 LTS, and EF Core 10 on .NET 11 too). From
  5.5.0 on it refuses to register on any other EF Core major, and it takes fixes only. The
  intention is to maintain it for as long as Microsoft supports EF Core 10 (until November 2028).
- **11.x**, published as `CodoMetis.EFCore.SchemaKit`, targets **EF Core 11** (`net11.0`, .NET 11,
  Npgsql 11), as previews until it is released. The intention is to maintain it for as long as
  Microsoft supports EF Core 11.

A new major means a new EF Core release, and nothing else. A fix that changes what a migration
scaffolds for an existing model ships as a minor on every supported line, with a "Changed answers"
section at the top of that version's changelog entry, so a line never stops receiving the fixes
that matter most. If this intention changes, it is recorded here before anywhere else.

### If this project stops being maintained

This is a single-maintainer project, and that is the honest continuity risk. The signal would be
unambiguous: the repository archived, the packages marked deprecated on nuget.org, and a note here.
Published versions stay on nuget.org regardless (a package can be unlisted, not removed), the code
is MIT-licensed, and the release path needs nothing but this repository — forking is the intended
continuity mechanism, not a fallback.

Advisories are published as GitHub Security Advisories, which reach the GitHub Advisory Database
and from there `dotnet restore` (NuGetAudit): a consumer on an affected version sees a build
warning without subscribing to anything.

## Where this package sits

Useful context for judging impact, and for anyone doing supply-chain due diligence.

The package has **no runtime presence in your application's request path**. It runs in two places:

1. **Design time** — a replacement `IMigrationsModelDiffer` invoked by `dotnet ef migrations add`.
   It reads your model and emits migration operations.
2. **Migration apply time** — only for PostgreSQL expression indexes and `NULLS FIRST/LAST`
   ordering, and only when a consumer opts in with `UseNpgsqlComplexIndexes()`. Since 5.1.0 that
   call (and `UseComplexIndexes()` / `UseSqlServerComplexIndexes()`) also registers the differ at
   runtime, where `EnsureCreated()`, `GenerateCreateScript()` and `Migrate()`'s pending-model-changes
   check run it — still against your own model, still opt-in.

Its inputs come from your own `OnModelCreating` code, not from user input. Anyone who can change
that code can already run arbitrary code in your build.

Published packages carry Source Link metadata (repository and commit) and symbol packages, and are
published through GitHub Actions Trusted Publishing — no long-lived credential exists that could
publish on this project's behalf. Each release also carries a CycloneDX SBOM per package
(`*.cdx.json`), attached to the [GitHub release](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases)
for that version, describing the dependencies a consumer actually takes on.

## In scope

- **Identifier escaping failures.** Column, table, schema, index, constraint, and JSON property
  names are escaped before being written into DDL. A value that escapes its quoting and injects
  arbitrary DDL is a vulnerability — even though it originates in model code.
- **Silently incorrect DDL where a declared constraint is a security control.** If the differ emits
  a migration that applies cleanly while omitting or weakening a declared `UNIQUE`, exclusion, or
  temporal constraint, and that constraint was enforcing something like tenant isolation or
  non-overlapping grants, treat it as security-relevant and report it privately. Several bugs of
  exactly this shape were fixed in 5.0.2.
- **Supply-chain integrity issues** — anything suggesting a published package does not correspond
  to the source at its recorded commit, or a weakness in the release workflow.

## Out of scope

- **Raw SQL emitted verbatim.** `HasExpressionIndex(string)`, `HasFilter`/`filter:` predicates,
  exclusion operators, and `UseMethod` are documented as passed through unchanged, without
  property-to-column resolution or quoting. Constructing SQL from untrusted input and handing it to
  those APIs is a defect in the calling application, not in this package.
- **Vulnerabilities in EF Core, Npgsql, or the SQL Server provider.** Report those to their
  projects; if one affects this package's behaviour, a report here is still welcome so it can be
  documented.
- **A migration that fails to apply.** That is a bug — please open a normal issue.
