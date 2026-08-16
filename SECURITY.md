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

Security fixes land on the latest released minor version. Older majors are not maintained — the
remedy for those is to upgrade.

| Version | Supported |
|---|---|
| 5.0.x | ✅ |
| < 5.0 | ❌ |

## Where this package sits

Useful context for judging impact, and for anyone doing supply-chain due diligence.

The package has **no runtime presence in your application's request path**. It runs in two places:

1. **Design time** — a replacement `IMigrationsModelDiffer` invoked by `dotnet ef migrations add`.
   It reads your model and emits migration operations.
2. **Migration apply time** — only for PostgreSQL expression indexes and `NULLS FIRST/LAST`
   ordering, and only when a consumer opts in with `UseNpgsqlComplexIndexes()`.

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
