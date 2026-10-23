# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build EFCore.ComplexIndexes.slnx

# Test
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj

# Run a single test class
dotnet test --filter "ClassName=MigrationModelDifferTests"

# Run a single test method
dotnet test --filter "FullyQualifiedName~MigrationModelDifferTests.SingleIndex_IsCreated"

# Test on another EF Core patch: restore and build with the same properties, then check deps.json
dotnet restore -p:EfFloat=10.0.13 -p:NpgsqlEfFloat=10.0.3
dotnet build -c Release --no-restore -p:EfFloat=10.0.13 -p:NpgsqlEfFloat=10.0.3
test/assert-dependency-versions.sh test/EFCore.ComplexIndexes.Tests/bin/Release/net10.0/EFCore.ComplexIndexes.Tests.deps.json 10.0.13 10.0.3

# Pack NuGet packages
dotnet pack src/EFCore.ComplexIndexes/EFCore.ComplexIndexes.csproj
```

Tests run in parallel at the method level (`Scope = ExecutionScope.MethodLevel`). The
`PostgresIntegrationTests` class (`[TestCategory("Integration")]`, `[DoNotParallelize]`) spins up a
PostgreSQL 18 Testcontainer and applies generated DDL for real; `Postgres16IntegrationTests` does
the same on PostgreSQL 16, one fresh database per test, so an extension an earlier test installed
cannot hide a migration that would not install it. `SqlServerIntegrationTests` (also
`[TestCategory("SqlServer")]`, run by a CI job of its own) does the same against SQL Server 2022, a
database per test, and reads the indexes back from `sys.indexes`. Without Docker they need excluding
via `--filter "TestCategory!=Integration"`. The SQL Server image is amd64 only: on Apple Silicon it
needs Rosetta in the Docker VM (OrbStack: `orb config set rosetta true`), and crashes under QEMU.

Locally, no Docker means those tests go **inconclusive**. When the `CI` environment variable is set
they **fail** instead — an unreachable container must not quietly retire the end-to-end layer while
the build reports green.

## CI

`.github/workflows/dotnet.yml` runs on pushes and PRs to `main`, `release/**` and `net11`: the
unit suite, the integration suites (Docker; `Integration (PostgreSQL 18)` and
`Integration (SQL Server 2022)`, one job each), then a pack job whose value is partly that packing
*is* a check — a package declaring `PackageReadmeFile` without packing the file fails with
NU5019. A last job, `CI`, needs every other job, runs `if: always()`, and passes only when each of
them reports `success`. It is the one required build check (#110): GitHub reports a skipped job as
Success even on a required check, whether its own condition or a failed dependency skipped it, so
per-lane required checks cannot tell a skip from a pass, and every new lane cost an edit to every
protected branch. `WorkflowGateConventionTests` holds that every job is in the gate's `needs`, that
it runs `always()`, and that it still reports as `CI`. Everything runs on
ubuntu-latest.

`dotnet.yml` is also a reusable workflow (`workflow_call`, #111). `release.yml` calls it before
`verify`, so a release runs every lane on the tagged commit, gated by the same `CI` job, and
`nightly.yml` calls it on the default branch every night and dispatches it on each release line
that exists (`release/5.x` after the cut), waiting for the result. The nightly is for the inputs
that change without a diff: `postgres:18`, `postgres:16` and `mssql/server:2022-latest` float within
their version on purpose, as a consumer's server does, and each integration job writes the image
digests it used to its summary so a failure with no diff can be compared with the last green run;
the consumer smoke test restores from nuget.org, with `Microsoft.EntityFrameworkCore.Design` and
`dotnet-ef` held to `10.*` so EF Core 11's release does not break it. A failed nightly opens an
issue titled "Nightly CI failed", or comments on the open one: GitHub's own email reaches only the
user who last edited the cron line, and nobody for the dispatched release-line runs. GitHub turns
a public repository's schedules off after 60 days without activity, which is why nothing depends
on the nightly. The SDK comes from `global.json`: `10.0.400` with `rollForward: latestPatch`, the
band CI used before the pin, so patches arrive and a new feature band needs a PR (the consumer
projects get a copy, since they live outside the repository).

Every lane above tests the floor the packages declare: the test project pins EF Core, its
providers and Npgsql EF at 10.0.0, and the unit job reads the test host's deps.json
(`test/assert-dependency-versions.sh`) to show it. A provider pinned higher pulls EF Core up behind
it; until 5.5.0 a Sqlite pin at 10.0.11 ran every lane on EF Core 10.0.11 beside Npgsql EF 10.0.0,
a mix no consumer has (#133). The `Newest EF Core patch` lane runs the unit, integration and smoke
suites again on the newest 10.x patches on nuget.org, looked up when it runs, so the nightly tests
a patch the night it ships. `test/Directory.Build.targets` floats the test projects only
(`-p:EfFloat=… -p:NpgsqlEfFloat=…`; set both, since Npgsql EF 10.0.1 and later need EF Core 10.0.4),
and `src/` still compiles against its floor, which is what a consumer on a newer patch runs. The
lane exists because the package replaces EF's differ and SQL generator through internal API that
carries no promise across patches, and a fixes-only line has no other way to notice a patch that
breaks it. It reads deps.json as well, since a float that does not take passes every test on the
floor, and it runs its suites in one step that reports every failing suite, not the first.

Actions in all workflows are pinned to commit SHAs with the tag in a trailing
comment; `.github/dependabot.yml` keeps those pins current (weekly, grouped, minors and patches
only — majors on the publish path are deliberate, see the artifact-action comments in
`release.yml`) and covers the test project's NuGet references monthly — not `src/`, whose EF Core
and provider floors are release decisions. A `src/` entry with no version updates exists only so
security updates there honour its ignore list: Dependabot's fix for the build-only NU1903 would be
a *direct* `System.Security.Cryptography.Xml` or `.Pkcs` reference, shipping to consumers a package
they never had. The test entry carries the same ignores, because its job follows the
ProjectReference and writes the pin into the `src/` csproj. That is how #49 and #51 got through
while the `src/` job skipped the package as configured. An ignore list covers only the jobs it is
configured on, so `PackagingConventionTests` also fails any public reference that only a
`PrivateAssets=all` reference reaches, whichever job wrote it.

The unit job used to fan out across ubuntu/windows/macos for the repository-convention tests, which
do real path work. Dropped in favour of reacting if it ever bites: **no shipped code touches the
filesystem** (the `Path`-looking code under `src/` splits dotted *property* paths), so the other
runners only ever proved that the test harness composes paths portably. That is a contributor-
environment property, not a package one, and it fails loudly on the machine of whoever hits it.
Note what the matrix was *not* buying, in case it looks tempting to restore: whether the packaged
`.targets` works under Windows MSBuild is still untested either way, since the smoke test is
Linux-only and `PackagingConventionTests` reads the `.targets` rather than executing it.

The job keeps a single-entry `matrix.os` rather than a plain `runs-on`, so re-adding an OS stays a
one-line change and it still reports as `Test (ubuntu-latest)`, which branch protection required
until the `CI` gate replaced the per-lane checks. The gate's own name is now the one that must not
change: a renamed required check stays required, never reports, and hangs PRs instead of failing
them.

Both workflows generate a **CycloneDX SBOM per package** (`*.cdx.json`) alongside the nupkgs, via
the `cyclonedx` tool pinned in `.config/dotnet-tools.json`. `--exclude-filter` drops
`Microsoft.EntityFrameworkCore.Design` and its subtree: that reference is `PrivateAssets=all`, so
without the filter the core package's SBOM would list ~45 MSBuild/Roslyn components against a nuspec
declaring one, handing consumers vulnerability alerts for code they never receive.
`PackagingConventionTests` asserts the filter still covers every `PrivateAssets=all` reference — an
SBOM that misdescribes the package is worse than none. Publishing one is a courtesy: the CRA's SBOM
duty falls on commercial consumers for their own products, not on this package.

That filter test flattens the three projects into one set of ids, so it only ever asks whether a
name is *somewhere* private; a second guard asserts a reference marked `PrivateAssets=all` in one
project is marked so in **every** project. Dropping it from a single csproj is invisible to every
other check — the package still builds, the SBOM test still passes — while that one nuspec starts
declaring the dependency and its consumers restore the whole subtree behind it. That is what makes
"consumers never receive it" true, and it is the premise `NU1903` staying a warning rests on
(`Directory.Build.props`), along with the `System.Security.Cryptography.*` ignores in
`.github/dependabot.yml`. Confirmed by removing the metadata from one satellite: a consumer of the
resulting package inherits all eight advisories.

`.github/workflows/release.yml` publishes on a `v*` tag. `Directory.Build.props` stays the single
source of truth: the tag is verified against it rather than driving it, packages are discovered from
the pack output (so a future satellite needs no workflow edit), and pushes use `--skip-duplicate` so
a re-run after a partial failure is safe. The release gate runs the full suite, which means
`ChangelogConsistencyTests` blocks a version nothing documents. Before that, its `ci` job runs every
lane of `dotnet.yml` (above); `verify` needs it.

Publishing uses **Trusted Publishing (OIDC)** — there is deliberately no long-lived NuGet key in this
repository. `NuGet/login@v1` exchanges the run's signed OIDC token for an API key valid one hour,
issued only if the run matches the policy configured on nuget.org (owner + repository + workflow file
name + environment). The one repository secret, `NUGET_USER`, holds the nuget.org *profile name*; it
is not a credential and is a secret only to keep it out of build logs.

The workflow is **three jobs**, and the split is the security boundary. `verify` builds, tests and
packs with no `id-token` permission at all — nothing in it can mint a token. `publish` carries
`id-token: write`, is gated on the `nuget` environment, and only downloads and pushes what `verify`
already produced. So a reviewer is asked after the suite has passed rather than before, no token
exists until they approve, and the artifact published is the one that was tested. `release` runs
last with `contents: write` and no `id-token`: it creates the GitHub release if none exists (notes
taken from the root `CHANGELOG.md`'s `## [<version>] — <date>` section, so an empty extraction fails the
job instead of publishing a blank release) and attaches the SBOMs — their only durable home, since
workflow artifacts expire after 90 days and nuget.org has no slot for them. Only the SBOMs are
attached: nuget.org repository-signs packages on ingestion, so a `.nupkg` from there never matches
ours byte-for-byte, and attaching ours would invite a hash comparison that fails for a benign
reason. No job holds both `id-token: write` and `contents: write`.

Three things must stay in sync or the policy stops matching — by design, so fix the policy rather
than working around it: the workflow **file name** (`release.yml`), the **environment** name
(`nuget`, declared on the `publish` job *and* in the nuget.org policy), and the repository owner/name.

## Tracking

Work is tracked as labelled GitHub issues, not as lists in docs or memory: open the issue before
the work, give the PR the issue's labels and milestone, open it with `Closes #n`. The label legend
is the pinned Roadmap issue (#35) and `CONTRIBUTING.md`'s Issues section; `AGENTS.md` has the
rules in short form, including the two supported lines (`EFCore.ComplexIndexes` 5.x, LTS on EF
Core 10, on `release/5.x` from the `v5.5.0` tag; `CodoMetis.EFCore.SchemaKit` 11.x on EF Core 11,
on `main`), the branches, and the silent-fix rule: from 5.5.0 a fix that changes what an existing
model scaffolds ships as a minor, never a patch, with `### Changed answers` at the top of its
changelog entry. `.github/workflows/pr-hygiene.yml` enforces the label half through
`pr-hygiene.sh`; `ChangelogConsistencyTests` and `SecurityPolicyConsistencyTests` the file half.

## Quality controls

The signature failure here is a migration that scaffolds *and applies* cleanly while being
semantically wrong — no exception, no failed apply, just a database that does not enforce what the
model declared. Three of the eleven issues found in the 5.0.1 audit were that shape. The controls
below exist because ordinary review does not catch it.

**Convention tests** enforce what is otherwise invisible until a consumer installs the package:

| Test class | Guards |
|---|---|
| `ChangelogConsistencyTests` | The changelog lives in four files (root `CHANGELOG.md` + one per package), in the form ported from CodoMetis.ValueRanges (CONTRIBUTING.md, Changelog). Asserts the shipped version is documented in the root and in every package changelog, no changelog runs ahead of `Directory.Build.props`, package changelogs are a subset of the root's, sections are newest-first, every root version has its release-link definition, and no README has grown a duplicate copy. Every `##` heading must be `## [x.y.z] — yyyy-mm-dd`: `release.yml` and `pr-hygiene.sh` find the section by it, and a heading in another style is no section to any test, so its lines would go unchecked while the release job published a blank release. The form: at most one introductory line, then `### Changed answers`, `Breaking`, `Added`, `Changed`, `Fixed` in that order, one line per change, at most 60 words a change and 40 for the introduction, absolute links. `### Changed answers` appears only in a minor (`x.y.0`, `y > 0`), each row links its issue and its `docs/upgrading.md` entry, and after 5.4.1 a change worded as a former value ("where it rendered …") must sit there; `docs/upgrading.md` runs oldest version first. Headings may be previews (`## [11.0.0-preview.N]`), which `ReleaseVersion` orders by SemVer. |
| `DocumentationLinkTests` | Relative markdown links resolve to real files and `#anchors` to real headings, and the packed READMEs under `src/` carry **no** relative links at all. nuget.org renders those READMEs with nothing to resolve a relative path against, so `[docs](docs/postgresql-indexes.md)` renders as a live link that 404s for every consumer arriving from the package page — while looking correct in the repository. |
| `DocumentationApiTests` | Every method name the user-facing docs cite exists in the public surface (calls into EF Core, Npgsql, DI and the BCL are an explicit allowlist, so everything else has to be ours), and no provider page cites another provider's *exclusive* API — exclusive meaning after subtracting what core and the other satellite also declare, since `IsUnique`/`HasName`/`IncludeProperties` exist on all three builders. Package validation already fails the pack on a removed public member, so what this adds is the rename fixed in source and forgotten in prose, and the name simply typed wrong. Changelogs are deliberately out of scope: an entry saying 5.0.0 shipped `HasExclusionConstraint` stays true after a later rename, and asserting over them would turn every rename into pressure to rewrite history. |
| `PackagingConventionTests` | Every package ships its own README as `PackageReadmeFile`; `.targets` ship to both `build/` and `buildTransitive/`, reference a real `IDesignTimeServices` in their own assembly, and set `ForProvider` on satellites but not on core. No public reference pins a package that only a `PrivateAssets=all` reference brings in, read from each project's restore graph (the shape of a Dependabot security pin; the one deliberate case, core's EF Core `Abstractions` ceiling, is named with its reason). Package validation is enabled and its baseline is the shipped version or the release before it, never older and never a preview — the baseline is what `dotnet pack` diffs the public surface against (CP0002 on a removed member), and one left behind stops seeing API added since it. Each package's packed nuspec carries its changelog's section for the shipped version as `releaseNotes`, compared against a renderer of the test's own; a seeded changelog covers what the real ones lack (a preview heading, `$(…)`, `%3B`, a neighbouring version that shares a prefix), the pack fails naming the file on each fail-closed case, and a changelog edit reaches a package repacked into the same folder. Each satellite's packed nuspec depends on the core at exactly its own version, `[x.y.z]` (#169). These pack for real (`--no-build`, in the test run's configuration). |
| `ClaudeMdConsistencyTests` | This file. Prose cannot be asserted, so it checks the falsifiable parts: cited paths and file names exist, annotation keys under a prefix this repo owns are declared somewhere, `Type.Member` references resolve, and the stated size of the Npgsql whitelist matches it. Those are what a rename rots silently — and the count claim had already gone stale by two. |
| `BuilderApiParityTests` | Every key in a satellite's annotation whitelist is reachable from a builder method. `SqlServer:DataCompression` sat whitelisted with no API for a full release; this catches that class of drift by invoking every builder extension and diffing the keys it sets. |
| `SecurityPolicyConsistencyTests` | SECURITY.md's supported-versions table has one `✅` row per supported line, newest first: this branch's line from `PackageValidationBaselineVersion` (the release on NuGet, never the version being prepared), the other line's from `CompanionLineRelease`; never a preview, at most two, adjacent majors, and the `< x.y` row meets the oldest. Each supported line has a "**N.x** targets **EF Core M**" sentence, and for the line this branch prepares M is the core project's dependency floor. All prose a version bump forgets — the sibling repository shipped 6.2.0 with the table still saying 6.1.x. |
| `WorkflowGateConventionTests` | The `CI` gate in `dotnet.yml`, the one required build check: every other job is in its `needs`, it runs `if: always()`, it reads `toJSON(needs)` whole and fails any result but `success`, and it reports as `CI` and no other job does. A lane the gate does not need can fail while the PR merges, and a gate without `always()` is skipped when a lane fails, which branch protection reads as Success. |
| `CiInputConventionTests` | The CI inputs that change without a diff: `global.json` pins a feature band (`x.y.z00`, `latestPatch`), every job that checks out the repository sets up .NET from it and not from `dotnet-version`, and `nightly.yml` runs on a schedule, calls `dotnet.yml`, and its `report` job needs every other job, runs `always()`, fails on any result but `success` and can open the issue. A job back on `10.0.x` builds with whatever band the runner has and passes. |
| `ReleaseWiringConventionTests` | The release path's silent failures: `release.yml` exists under the file name the nuget.org policy names, `publish` depends through `needs` on a job that calls `dotnet.yml` (which keeps its `workflow_call` trigger) with no `always()`, `failure()` or `cancelled()` on that path, the publish job is gated on the `nuget` environment, only that job holds `id-token: write`, no job holds both `id-token: write` and `contents: write`, the tag is checked against `Directory.Build.props`, the GitHub release step passes `--prerelease --latest=false` for a preview and `--latest=false` for a stable tag older than the current Latest by `sort -V` (script lines only — the comment above it explains both rules, and would pass with the code gone), the SBOM tool is pinned in the manifest, and no second publishing path exists under `scripts/`. Rename the environment and nothing else in the build notices — the next tag fails to publish with an authentication error that never mentions the rename. |

**`test/consumer-smoke-test.sh`** is the only check that exercises *delivery* rather than code. Every
other layer is verified in isolation and in-process; this one packs the nupkgs, restores them into a
throwaway project created **outside the repository** (inside it, `Directory.Build.props` would apply
and the project would stop resembling a consumer's), and runs a real `dotnet ef migrations add`. It
then asserts on scaffolded content — never on the exit code, because the failure being hunted is a
`migrations add` that succeeds while silently omitting every index. Neutering both `.targets` files
reproduces exactly that: table and columns scaffold fine, indexes vanish, exit code 0. One consumer
project per satellite; it runs on every PR in both of its invocation modes (the `consumer` job packs
its own feed, the `pack` job passes the one it just built, as the release workflow does). Before
the consumers it checks that every package in the feed carries release notes ending in its
changelog link, so the artifacts that ship are held to what `PackagingConventionTests` holds of a
pack of its own.

**Index options do not prove which differ ran.** Entity-level provider annotations reach the
operation unfiltered, so the core differ emits `Npgsql:IndexMethod` and `SqlServer:FillFactor` just
as the satellites do — a neutered satellite `.targets` still yields a migration that looks right,
because the core registration rides along through `buildTransitive` and covers it. Each provider is
therefore pinned by something only its own differ does: PostgreSQL by an exclusion constraint, whose
design-time DDL the core differ has never heard of, and SQL Server by a *rejection* — a clustered
complex index must fail at `migrations add`, and scaffolding cleanly is the failure. Both were
confirmed by neutering each satellite's `.targets` in turn; the index assertions kept passing, which
is precisely the point.

Two more things are load-bearing and easy to "simplify" away. `NUGET_PACKAGES` is redirected to a
private folder because NuGet resolves id+version from the global packages folder before consulting
any source — a locally built package is otherwise shadowed by whatever build of that same version
was restored before, up to and including the one on nuget.org, and the test silently reports on the
wrong artifact. And the generated `nuget.config` uses **package source mapping**, not source order:
this repository's own ids must come from the local feed and nothing else, while every transitive
dependency must come from nuget.org. Restricting the whole restore to the local feed instead (the
`--source` flag) fails with NU1101, and it fails only when a feed is passed in, because packing
first happens to populate the private cache — which is how it reached a release.

**`MigrationHarness`** (under `test/…/Harness/`) is the shared rig: build a model from a
`DbContext`, construct any differ, render operations through either the stock provider generator or
this package's. Investigating a suspected differ bug should be three lines, not a re-derived
40-line setup. `NpgsqlSql(operations, complexIndexWiring: false)` is the one that matters most — it
shows what a consumer who forgot `UseNpgsqlComplexIndexes()` actually gets.

**`TranslatorParitySweepTests`** (under `test/…/TranslatorParity/`) is the typed translator's
differential test. Every construct `NpgsqlLinqIndexTranslator` accepts is read from its own tables
(`StringMethods`, `ValueMembers`, the operator sets, `LiteralTypes`), crossed with every operand
shape (non-nullable, nullable, `ToJson()` member, converter member), compiled by Roslyn so the
lambdas have the compiler's real shapes, and rendered both ways: through the declaration and the
differ, and through EF Core's `ToQueryString()` (`Where` for filters, `Select` for expressions).
Both sides are parsed with PostgreSQL's precedence before they are compared, so parentheses and
aliases never count and a regrouping always does. A disagreement is fixed or listed in
`test/EFCore.ComplexIndexes.Tests/TranslatorParity/waivers.txt` under a block with its reason; a
planner rewrite such as `NOT (a = b)` against `a <> b` is a `rule:` block there, never a silent
normalization. `PostgresIntegrationTests.Translator_parity_answers_alike_on_postgres` runs the same
rows on PostgreSQL 18 and fails a pair that renders alike but answers differently, which is how a
`ulong` concatenation EF Core writes as `5.0` was found. Add a construct to the translator's tables,
not only to its dispatch, or the sweep cannot see it.

**Skills** in `.claude/skills/`:

- `migration-safety-review` — the domain review lens: silent degradation across the design-time and
  runtime seams, definition-store identity keys, name collisions, provider scoping, annotation flow,
  snapshot churn, operation ordering. Use when touching a differ, a SQL generator, a whitelist, a
  definition store, a `.targets`, or before a release.
- `verify-the-guard` — revert the source fix and confirm the new test fails. This caught a test of
  mine during the audit that asserted nothing (the two declarations it set up deduplicated into one,
  so no exception was ever possible) and it passed against broken code. Extended for guards whose
  subject is *delivery*, where there is no source fix to revert: break the mechanism instead, and
  mind that the two `.targets` are redundant, that provider index options do not discriminate which
  differ ran, and that the conditions — cold checkout, pre-built feed, private package cache —
  are the axis the bug hides on.
- `plain-docs` — the voice for the consumer pages (the root README, the package READMEs, `docs/`):
  plain, written to "you", example first, edge cases last, every fact as exact as before. Its last
  section lists what a rewrite must keep: code blocks byte for byte, the headings published
  changelogs link to, absolute links in packed READMEs, and the API names `DocumentationApiTests`
  reads. Ported from CodoMetis.ValueRanges and CodoMetis.TypeKit.

## Architecture

This library fills a gap in EF Core 10.0 migrations: EF Core can model complex properties (value objects) but does not generate migration SQL for indexes on their nested columns. This library hooks into EF Core's design-time pipeline to produce correct `CREATE INDEX` / `DROP INDEX` SQL.

### Solution layout

| Project | Purpose |
|---------|---------|
| `src/EFCore.ComplexIndexes` | Core library — provider-agnostic fluent API and migration differ |
| `src/EFCore.ComplexIndexes.PostgreSQL` | Satellite package — Npgsql index methods (GIN, GiST, BRIN, Hash, SP-GiST), expression/JSON/LINQ indexes, temporal + exclusion constraints |
| `src/EFCore.ComplexIndexes.SqlServer` | Satellite package — clustered/covering/online/fill-factor options; rejects expression parts and NULLS ordering with clear errors |
| `test/EFCore.ComplexIndexes.Tests` | MSTest suite covering path extraction, serialization, and migration diffing |

Shipping projects live under `src/`, the test project under `test/`; the `.slnx` groups them into
matching solution folders. Shared NuGet metadata and the package version live in the root
`Directory.Build.props`, which still applies to every project beneath it. Each shipping project
carries its own `README.md`, packed as that package's NuGet landing page, and its own
`CHANGELOG.md`, which is not packed — keep those in sync with the root `CHANGELOG.md` when
releasing. What the first `migrations add` after an upgrade does goes in `docs/upgrading.md`, not
in the changelog line.

The package changelog's section for `$(PackageVersion)` is that package's release notes, which
nuget.org's Release Notes tab and Visual Studio's Updates tab show. `src/Directory.Build.targets`
sets `PackageReleaseNotes` from it before `GenerateNuspec`, for the three shipping projects only.
nuget.org renders the field as plain text (measured on real package pages and in NuGetGallery's
`PreFormattedText`: line breaks kept, bare URLs linked, Markdown shown literally), so links become
`text (url)`, backticks, bold markers and the `### ` of a heading are dropped, and one footer line
links the changelog at the version's tag. Links into this repository at `blob/main/` (or
`tree/main/`) are rewritten to the version's tag, in the notes and in a copy of the README packed in
place of the source (#151): a package cannot change once published, and after the `v5.5.0` tag
`main` is SchemaKit. The release job in `release.yml` does the same to the GitHub release's notes.
The source keeps `blob/main`, which `DocumentationLinkTests` resolves against the checkout, so on
the tagged commit that test is what proves the frozen links resolve. The pack fails, naming the file, on a missing changelog,
a missing or empty section, or notes over nuget.org's 35,000 characters: a package with nothing in
a version says "No change in this package.", so the section always exists. The target also adds
the changelog to `NuGetPackInput`, since `GenerateNuspec` is incremental and a repack would
otherwise keep the old notes.

The same file pins each satellite's dependency on the core to `[$(Version)]`
(`PinProjectReferenceDependencies`), since a ProjectReference packs as `>=` while the satellites
bind to the core's internals (#169). A direct core reference still overrides the pin with only
warning NU1608, and 5.4.x satellites declare `>=`, so the core differ also refuses a satellite
assembly of another version (`CustomMigrationsModelDiffer.ThrowIfSatelliteVersionDiffers`).

### Documentation layout

The root `README.md` is the landing page: what the package is, install, runtime wiring, the
provider-agnostic core API, and a table pointing at the rest. Provider-specific reference lives
under `docs/` (`postgresql-indexes.md`, `postgresql-constraints.md`, `sqlserver.md`) — the split
follows the seam, since PostgreSQL indexes go through the differ plus the custom generator while the
temporal and exclusion constraints are rendered as design-time `SqlOperation`s.

The packed READMEs under `src/` are a separate audience and a separate constraint: nuget.org renders
them with no base to resolve against, so **every link in them must be an absolute GitHub URL**. They
are condensed on purpose and will overlap `docs/` — that duplication is the price of a package page
that stands alone, and `DocumentationLinkTests` guards only the part that fails silently.

`ROADMAP.md` at the root is the plan for 5.5.0 and the 6.0 family: the delete guard, the generic
descriptor differ, the rename under the `CodoMetis.` prefix, the plugin host and EF Core 11. It sits at the
root on purpose: `DocumentationLinkTests` checks its links, but `DocumentationApiTests` scans only
`docs/`, so the roadmap may name methods that do not exist yet.

### How it works end-to-end

1. **Fluent API** (`ComplexIndexExtensions.cs`) — User calls `.HasComplexIndex(...)` or `.HasComplexCompositeIndex(x => new { x.Prop, x.Complex.Nested })` in `OnModelCreating`. These methods store all index metadata as EF Core annotations on the property or entity.

2. **Annotation storage** — `ComplexIndexAnnotations.cs` defines the annotation key constants. Composite index definitions are JSON-serialized via `CompositeIndexSerializer` and stored as a single annotation on the entity type.

3. **Design-time service injection** — Each project ships a `.targets` file (under `build/`) that injects a `DesignTimeServicesReferenceAttribute` into the consuming assembly at compile time. EF Core's design-time host discovers this attribute and instantiates the custom `IDesignTimeServices`, which replaces the default `IMigrationsModelDiffer`.

4. **Migration differ** (`CustomMigrationsModelDiffer.cs`) — Extends `MigrationsModelDiffer`. During `dotnet ef migrations add`, it recursively walks entity type annotations and complex type properties to find index annotations, resolves the actual database column names (respecting both convention-based naming like `Origin_Source` and explicit `HasColumnName` overrides), and emits `CreateIndexOperation` / `DropIndexOperation`. **Operation ordering is load-bearing**: custom drops go *before* the base operations, creates after — an index moving between native `HasIndex` and a complex-index declaration produces a base create plus our same-named drop, and only this order keeps the scaffold from colliding at apply time (the temporal/exclusion differs follow the same rule).

5. **PostgreSQL satellite** (`NpgsqlComplexIndexMigrationsModelDiffer.cs`) — Extends the core differ, validates Npgsql-specific annotations, and normalizes JSON element annotations before passing operations upstream.

### Annotation forwarding is a whitelist

Property-level annotations reach the `CreateIndexOperation` only through
`IsForwardedIndexAnnotation` (virtual on the core differ, default **nothing**; the Npgsql differ
whitelists exactly its six `Npgsql:*` index-option keys, plus every key under the per-parameter
`Npgsql:StorageParameter:` prefix). The key an option is *stored* under can differ from the key the
provider generator *reads*: `ToOperationAnnotationName` maps `Npgsql:IndexCollation` to
`Relational:Collation` at stamping time, because the property-level API writes options onto the
property, where the relational key would be read as the column's collation. Never revert to sweeping "everything
except known keys": column facets (`Relational:ColumnName`, `Relational:ColumnType`, …) leaked into
scaffolded migrations that way, and snapshot/code-model asymmetries caused phantom drop/create
churn (see `PhantomIndexChurnTests`).

### Diff polish: renames and the wiring sentinel

The differ recognizes two churn cases: a drop/create pair identical except for the name becomes a
`RenameIndexOperation` when `CanRenameIndexes` is true (Npgsql and SqlServer satellites; core
default false — SQLite's generator can't rename annotation-only indexes), and source indexes on
tables the base operations rename are compared under their new table identity (drops still target
the old name, since they run before the rename). EF writes the target's schema into `NewSchema`,
null for the default one, so null is a move there and never "unchanged" (#185). The exclusion/temporal differs apply the same two
rules: renamed-table normalization, and name-only changes → `ALTER TABLE … RENAME CONSTRAINT`
(placed *after* the base ops — the rename references the new table name). A constraint rename
keeps its dependents, so `DependsOnChangedTemporalConstraint` compares principal constraints
name-insensitively and the temporal FK doesn't churn. Column renames are the third case (#182):
`CustomMigrationsModelDiffer.ResolveSource` resolves the source model under the names the base
`RenameColumnOperation`s give its columns (`RenderedColumn`, keyed by the source table, since EF
names the operation's table as the target has it), so a renamed column no longer drops and creates
everything that reads it. Only SQL this package renders from a column takes the new name — column
parts, placeholders, JSON extractions, INCLUDE entries, the implicit filter — while names are built
from the source's own columns (`ResolvedIndexPart.NameToken`), which turns a default name that
follows the column into a rename. Verbatim SQL is compared as written. SQL Server is the exception for filters: `sp_rename` rejects a column a filtered index reads, so the SQL Server differ's internal `RequiresIndexRecreation` overload, which also gets the filter's columns, rebuilds such an index around the rename (the implicit `IS NOT NULL` filter included). PostgreSQL has a case of its own (#184): Npgsql renders an `ALTER COLUMN` that changes a generated column's expression, storage or `tsvector` configuration as `DROP COLUMN` plus `ADD COLUMN`, and re-creates EF's indexes only. The Npgsql differ's `RequiresIndexRecreation` treats such an operation as in the way, and the exclusion and temporal diffs apply the same check (`RecreatedColumns`), a temporal FK going with a rebuilt principal constraint as it does for #143. The SQL Server differ's `ALTER COLUMN` rule asks what EF Core's generator asks (`IssuesAlterColumn`, its `alterStatementNeeded` plus the computed-column check), so a default or comment change alone keeps the index (#186). Because renames run after the base operations, a pair stays a
rename only when nothing before it needs the old object gone: not a base operation it is in the way of
(`RequiresIndexRecreation`, #141), and not a name the target model gives to anything else in its namespace
(`CustomMigrationsModelDiffer.IsIndexNameInUse`, the Npgsql `TargetNames` inventory for constraints, #143). Otherwise it
falls back to drop-first, create-last, and a temporal constraint takes its temporal FKs with it (2BP01). Indexes that need the custom generator
(`RequiresPartsAnnotation`) get `CustomMigrationsModelDiffer.RuntimeWiringSentinel` appended to
`Columns`: the custom generator renders from the parts annotation and ignores `Columns`, while the
stock generator fails loudly at apply time — deliberate, because a column-only NULLS-ordered index
would otherwise apply silently minus its NULLS clause. INCLUDE lists are transformed via
`TransformIndexAnnotation` → `ResolveIncludeList`: entries resolve as property paths with verbatim
column-name fallback, and in the target model an entry must name a column of the table. Without
that check an entry whose property was removed read the same on both sides, and the migration's
`DROP COLUMN` took a PostgreSQL index with it in silence (#142).

### Index identity and dedup

`ComplexIndexStorage.AddOrReplace` is the single store for entity-level definitions: same ordered
parts (direction ignored) + same filter → replace (re-declaring updates); same parts +
*different* filter → coexisting partial indexes, both of which must be explicitly named (default
names would collide). Single-column indexes exist in two forms: property-level (one per property,
stored as property annotations) and entity-level `HasComplexIndex(x => x.Complex.Prop, …)`
(stored as a one-part composite definition — use this for multiple filtered indexes per column).

Names are validated at **two** levels, because neither alone is sufficient. `AddOrReplace` rejects
an explicit name already used on the same entity (fast feedback at the declaration), and the
differ's `ValidateUniqueIndexNames` rejects duplicate resolved names within the provider's scope (below) — the only place
that sees across the two stores (property-level annotations vs the entity-level list) and knows the
*default* names, which depend on resolved column names. It validates the **target model only**: a
snapshot that already contains a collision must stay diffable, or the model could never be fixed.
Without these, two same-named declarations scaffolded fine and failed at apply time with 42P07.

`NpgsqlExclusionConstraintExtensions.Store` follows the identical rule — ordered elements
(operators ignored, as direction is for indexes) **plus filter**, with the same
both-must-be-named guard, plus the same two-level name-uniqueness validation
(`ValidateUniqueExclusionNames`). **The filter has to stay in the identity key**: filtered overlap
protection is the entire reason the EXCLUDE API exists, and keying on elements alone silently
discarded all but the last declaration, so "no overlap among active rows" + "no overlap among
revoked rows" collapsed to one constraint. Name collisions matter more here than for indexes:
every ADD is preceded by `DROP CONSTRAINT IF EXISTS`, so a duplicate name does not fail at apply
time — the second constraint silently replaces the first.

The per-kind checks above each compare a kind with itself on one table, which is not how
PostgreSQL scopes names, so since 5.4.0 the Npgsql differ's `ValidateNamesAcrossKinds` runs last
over everything the target model introduces — complex indexes, exclusion, temporal and temporal
foreign key constraints — plus EF's own indexes, keys, foreign keys and check constraints, and
(since 5.5.0) the model's tables, views and sequences. Two
namespaces: constraint names per **table** (42710; against an exclusion constraint, a silent
replacement instead, confirmed on PostgreSQL 18: the temporal `UNIQUE` vanished and the migration
applied clean), and index names per **schema**, where every primary key, unique, exclusion and
temporal constraint also owns an index, and which tables, views and sequences share — the
relation namespace, `pg_class` (42P07). That includes the sequence PostgreSQL creates for an
identity or serial column, which is in no model: `ImplicitSequenceName` names it as PostgreSQL's
`ChooseRelationName` does, truncation past 63 bytes included, and `PostgresIntegrationTests` asks
a live server for the same names. A collision is reported only when one party is
this package's; two of EF's own objects are EF's business. It collects into a list, not a set: the
two same-named declarations it exists to find produce identical entries, and a set merged them.

Where an index name must be unique is a provider fact, declared through
`CustomMigrationsModelDiffer.IndexNameScope` and applied by both core checks
(`ValidateUniqueIndexNames`, `ValidateNoNativeIndexNameCollision`): `IndexNameScope.Database` by
default, `IndexNameScope.Schema` in the PostgreSQL satellite, `IndexNameScope.Table` in the SQL
Server one. The default is SQLite's rule, and it has to be *database*, not schema: SQLite's provider
keeps a configured schema in the model but leaves it out of the DDL, so `north.a` and `south.b`
share one namespace (probed 2026-09-22). Until 5.4.1 both checks were per table everywhere, and the
SQLite test "the same name on a different table is not a collision" approved a model that
`sqlite3` rejects with "index … already exists". The widest scope is also the right default for
providers without a satellite: too wide costs a rename the database did not need (MySQL scopes per
table), too narrow a failed apply. On PostgreSQL a clash between two plain indexes is therefore
reported by the core, before `ValidateNamesAcrossKinds` runs; the cross-kind check keeps complex
indexes in its list for the pairs only it sees — keys, constraint-backed indexes, and a table in the
unset default schema against one naming `public`, which the core compares as configured. Each
collision throws once, from whichever check sees it first; do not "deduplicate" by filtering index
pairs out of the cross-kind check, since it is the only net for the `public` case.

How names compare is the other half of the rule, declared beside the scope through
`CustomMigrationsModelDiffer.IndexNameComparer`: `OrdinalIgnoreCase` by default and in the SQL Server
satellite, `Ordinal` in the PostgreSQL one, where EF Core's quoted identifiers keep their case. SQLite
compares identifiers without regard to case even when quoted, and SQL Server does under its default
collation, so until 5.5.0 `ix_code` and `IX_CODE` passed both checks and failed at apply. Ignoring
case is the default for the reason the widest scope is: a case-sensitive database pays a rename.

### The read model is the differ's reader

`ComplexIndexModelExtensions.GetDeclaredComplexIndexes` (core) and
`NpgsqlExclusionModelExtensions.GetDeclaredExclusionConstraints` (PostgreSQL) turn the annotations
into `ComplexIndexDeclaration` / `ExclusionConstraintDeclaration` objects — the public surface an
application uses to check its own conventions ("every unique index and exclusion constraint on a
withdrawable aggregate is filtered"). Since 5.2.0 the differs build their descriptors from exactly
these readers and resolve columns on top; there is no second parser. Keep it that way: a read model
that disagrees with the differ silently checks something other than what the migration enforces,
which is the outcome the guard exists to prevent. Declarations are reported unresolved — parts as
property paths, `Name` null for a default-named one — because resolution needs the relational model
and, for JSON members and templates, the satellite; `FindComplexIndex` therefore matches explicit
names only.

The mutable side (`ComplexIndexMutableExtensions`, `NpgsqlExclusionMutableExtensions`) works on
`IMutableEntityType`: `AddComplexIndex` is `ComplexIndexStorage.AddOrReplace` retyped, so it
carries the same identity and name rules; `AddComplexIndexFilter` / `AddExclusionConstraintFilter`
AND a predicate onto selected declarations through `ComplexIndexStorage.Conjoin`, whose
idempotence rule is deliberately narrow — the predicate is "already there" only when it is the
whole filter or the exact conjunct this method appends. They amend what is declared *at the time
of the call*: from `OnModelCreating` after the configurations, never from a model-finalizing
convention, since a convention-source annotation write cannot overwrite the explicit blob.

### Two integration seams: design-time vs. runtime

There are two distinct hook points, and it matters which one a feature uses:

- **Design-time** (`IDesignTimeServices` via the `.targets`-injected attribute) replaces `IMigrationsModelDiffer`. This runs during `dotnet ef migrations add` and is auto-wired — consumers do nothing.
- **Runtime** (`IMigrationsSqlGenerator`) converts operations to SQL when migrations are *applied*. This is **not** auto-wired; consumers opt in with `optionsBuilder.UseNpgsqlComplexIndexes()` (a `ReplaceService` helper).

Since 5.1.0 the runtime seam also carries the **differ**: `UseComplexIndexes()` (core),
`UseNpgsqlComplexIndexes()` and `UseSqlServerComplexIndexes()` replace `IMigrationsModelDiffer` in the
context's own service provider, because `EnsureCreated()`, `GenerateCreateScript()` and the
pending-model-changes check in `Migrate()` run *that* differ and never see the design-time attribute —
without it, `EnsureCreated()` creates the tables and silently none of the indexes. Design-time
selection is unaffected: EF's `AddDbContextDesignTimeServices` seeds the design-time collection with
the context's differ as a factory registration, and the `.targets` registration is appended after it
(`DesignTimeServiceRegistrationTests`). Related and easy to miss: `MigrationsModelDiffer.HasDifferences`
runs EF's protected `Diff`, not `GetDifferences`, so the core overrides it to route through
`GetDifferences` — otherwise `dotnet ef migrations has-pending-model-changes`, `Migrate()`'s
pending-changes warning and `migrations remove` all reported "no changes" for a complex-index-only
change (`PendingModelChangesTests`).

On SQLite the runtime seam also carries a **generator** (#95). EF Core's `SqliteMigrationsSqlGenerator`
rebuilds a table after every other operation and re-creates only EF's indexes, so nothing the differ
emits can run after it. `UseComplexIndexes()` adds `ComplexIndexOptionsExtension`, whose
`ApplyServices` wraps the registered SQLite generator in `SqliteRebuildMigrationsSqlGenerator`
(wrapping the registration keeps another package's earlier one; a `ReplaceService` keyed on SQLite's
type is honoured by building the replacement as the inner). The differ asks the *context* for the
generator in effect (`ICurrentDbContext`, the second constructor) and only when it is the wrapper
turns a rebuild of a table with complex indexes into drops plus creates marked
`CustomIndex:AfterTableRebuild`; otherwise it refuses (#86). The marked creates sit *before* the base
operations, with `__requires_UseComplexIndexes__()` ANDed onto the filter: the wrapper moves them
behind the rebuild, any other generator fails on them before anything is dropped. The marker is in
the filter, not `Columns`, on purpose: with SQLite's double-quoted string literals on (a default
build), an unknown quoted column is taken for a string and the index applies. And before the base
operations, because further down EF's generator holds back a create over a column renamed into a
dropped one's name and loses it.

Anything that depends on the runtime seam silently degrades when a consumer forgets the wiring, so
**prefer rendering DDL at design time** (a `SqlOperation` baked into the migration) whenever the
statement can be built from resolved column names — that is why exclusion *and* temporal
constraints take that route. The runtime seam is reserved for cases where EF's own operation type
is the only way to express the change (expression indexes, `NULLS FIRST/LAST`), and those carry the
`RuntimeWiringSentinel` so a missing wiring fails loudly instead of applying something wrong.

Selecting the design-time differ is deliberately order-independent (`ComplexIndexDesignTimeRegistration`).
A satellite consumer gets *two* `DesignTimeServicesReferenceAttribute`s — the satellite's, plus the
core package's riding along through `buildTransitive` — and EF simply enumerates them, resolving
last-registration-wins. So the satellites' `.targets` set `ForProvider` (EF skips a satellite
entirely when diffing another provider's context), the satellite registration removes any core
registration, and the core registration backs off when a satellite differ is already present.
Registering with a bare `AddSingleton` on both sides picks a differ by luck of NuGet's restore order.

Most index metadata (GIN/operators/include/etc.) flows as *real Npgsql annotation keys* (`Npgsql:IndexMethod`, …) on the `CreateIndexOperation`, so Npgsql's own runtime SQL generator renders it — this package never touches SQL generation for those. Expression indexes are the exception (see below).

### Expression indexes (`HasExpressionIndex`)

Expression indexes are **provider-specific** and deliberately live in the satellite, not core: PostgreSQL/SQLite render `CREATE INDEX … ((expr))` natively, but SQL Server has no functional-index DDL (it models the same intent via persisted computed columns). Exposing the API in provider-agnostic core would be a false promise — a SQL Server consumer could call it and get a `CreateIndexOperation` with empty `Columns` that the stock generator can't render. So:

- The **entry point** `HasExpressionIndex` (on `EntityTypeBuilder<TEntity>`) lives in `EFCore.ComplexIndexes.PostgreSQL` (`NpgsqlExpressionIndexExtensions.cs`), as does its `ExpressionIndexBuilder`.
- Core owns only the inert **plumbing**: the `IIndexExpression` seam (`SqlIndexExpression` ships today; a future LINQ add-on plugs in here), `IndexPartDefinition`/`ResolvedIndexPart`/`IndexPartsSerializer`, `CompositeIndexDefinition.Parts`, the differ's part-handling, and the `ComplexIndexStorage` helper satellites call to dedup-and-store definitions. None of it activates unless a satellite populates it.

Each column-list entry is a "part"; an index is an ordered list of parts. Verbatim string parts are emitted as-is (no property→column resolution).

An `IndexPartDefinition` is exactly one of three kinds: a **column** (`PropertyPath`, resolved to a
column name), a **verbatim expression** (`Expression`, emitted as-is), or a **template**
(`Template`, produced by `NpgsqlLinqIndexTranslator` from a typed lambda — SQL with
`{Property.Path}` placeholders, literal braces escaped `{{`/`}}`). Three virtuals on the core
differ let satellites resolve what the core cannot:

- `IsForwardedIndexAnnotation` — the annotation whitelist (see below).
- `ResolveUnmappedPart` — a path with no table column; the Npgsql differ builds a JSON extraction
  when the path traverses a `ToJson()` complex property, honoring `HasJsonPropertyName`, rendered
  **exactly as Npgsql's query translation renders the member** (`NpgsqlQuerySqlGenerator.VisitJsonScalar`
  and `GenerateJsonPath`, identical in Npgsql 10 and 11): `->>` for one step, `#>> '{A,B}'` for
  more (`ARRAY[…]::text[]` when a segment is not ASCII-alphanumeric), and a `CAST` to the member's
  store type unless it is a string mapping, `decode(…, 'base64')` for `bytea`, `jsonb` for a
  primitive collection or a `json`/`jsonb` scalar (checked in that order, as Npgsql does). PostgreSQL uses an expression index only for a query whose expression
  matches it, so this is not cosmetic: until 5.4.0 every member was `"col" -> 'A' ->> 'B'` text,
  and indexes on nested or typed members applied, enforced, and served no query
  (`NpgsqlJsonMemberRenderingTests` asserts each rendering against EF's own `ToQueryString()`).
  Date and time members stay text — their casts are not IMMUTABLE and cannot appear in an index —
  and the part records a `TextFallback`; `ValidateQueryUsableLeadingParts` rejects a non-unique
  index that starts with one (target model only), and `ResolveFilter` records a filter that compares
  one with a column, which would fail at apply as text against a timestamp, and the differ rejects
  it for the target model only, so a snapshot holding one still diffs (exclusion filters flag
  their rendering-1 members the same way). A path that *ends* at the JSON-mapped complex
  property — or at a complex collection, which is always JSON — resolves to the container column
  as a plain **column** part (so a whole-document GIN needs no runtime wiring); a complex property
  nested inside the document resolves to a `jsonb` extraction. A table-split complex property
  stays unresolved: there is no single column to stand for it.

  **Rendering changes need `ComplexIndexAnnotations.RenderingVersion`.** Parts are resolved at diff
  time on *both* sides, so changing how a member renders produces no migration at all — both sides
  resolve alike and existing databases keep the old index forever. Every declaration writes the
  version onto the model; a snapshot without it (scaffolded before 5.4.0) is resolved by the old
  rules, which is what turns the change into one drop-and-create per affected index. Default index
  names come from `ResolvedIndexPart.NameToken` (container plus path), never from the rendered SQL,
  so a rendering change never renames an index; and `ResolvedIndexPart` equality ignores both
  internal members, or identical SQL would still rebuild. Exclusion constraint filters pass
  `renderingVersion: 1` and keep the old rendering: rebuilding a constraint buys no enforcement.
  The exception is a member the current rendering casts (a number, boolean, uuid or enum): as
  text it never applied (42883, 42804), so since 5.5.0 it takes the current rendering, on both
  sides of the diff alike.
- `ResolveTemplatePart` — substitutes template placeholders with quoted columns or parenthesized
  JSON extractions. Since 5.2.0 the core implements it, quoting through the `QuoteIdentifier`
  virtual (ANSI by default; the SQL Server satellite brackets), so satellites override neither.

Filters go through the same placeholder resolver (`ResolveFilter`, called while building
descriptors for indexes and, in the Npgsql differ, exclusion constraints), with a narrower rule
because filters are pre-existing SQL: only a brace pair holding a dotted identifier path *outside a
single-quoted literal* is a placeholder, there is no `{{` escape (`'{{1,2},{3,4}}'` is an array
literal), and an unresolvable placeholder throws. The resolved filter is what the descriptor
carries on both sides of the diff, so placeholder filters never churn and need no runtime wiring.
`ResolveProperty` (core, shared by every path walk) also unwraps one member of a converter-mapped
value object — `Email.Value` resolves to the `Email` column only when the property has a converter
and the member's type equals the converter's provider type; without that check `CreatedAt.Year`
would silently index the whole column. A model snapshot has neither the value object nor the
converter: it persists the property as its provider type (`string`) on a property-bag type, so the
member check has nothing to check against. On a property-bag type, for an indexer property without
a converter, the resolver therefore accepts the persisted scalar — the path was validated against
the configured model when the snapshot was scaffolded. This matters more than churn: the first
`migrations add` succeeds because the snapshot does not hold the path yet, and everything that
diffs the resulting snapshot fails — the next `migrations add`, `has-pending-model-changes`, and
`Migrate()`, whose pending-changes check throws by default since EF Core 9. `SnapshotRoundTripTests`
covers every place such a path can appear, at the top level and inside a complex type.

Typed filters (`NpgsqlTypedFilterExtensions`, PostgreSQL only) run
`NpgsqlLinqIndexTranslator.TranslatePredicate` at the declaration and store the resulting
placeholder template as the filter string — nothing downstream knows the filter was typed. The
predicate subset is boolean structure over the expression translator's operands, with two
deliberate refusals: enums, because the stored form depends on a value conversion the translator
cannot see (`Status == Status.Active` would compare `text` against `0` and fail at apply), and
literals without a portable SQL spelling (`DateTime`, `Guid`), which `IFormattable` used to render
as bare text. Both throw at the declaration.

`NULLS FIRST/LAST` (`DbOrder.NullsFirst/NullsLast`, `ExpressionIndexBuilder.NullsFirst()/NullsLast()`)
rides on the parts as `NullSort`. EF's native `CreateIndexOperation` has no slot for it, so any
index containing a nulls-ordered part is routed through the `IndexParts` annotation and the custom
Npgsql generator — i.e. it needs `UseNpgsqlComplexIndexes()` even when column-only. The SQL Server
differ rejects nulls-ordered and expression parts with targeted errors, and the core differ does the
same on a provider no satellite serves (`ValidateRenderableParts`, target model only). There the
sentinel column is no guard: SQLite with double-quoted string literals on (a default build) takes
it, and a quoted expression, for a string, and the index applied with a constant column (#134).

`CreateIndexOperation.Columns` is a `string[]` of quoted identifiers with no slot for an expression, so:
- The differ stamps the ordered, resolved parts onto the operation as the `CustomIndex:IndexParts` annotation (`ResolvedIndexPart` + `IndexPartsSerializer`), **only when a part is an expression** (column-only indexes are untouched).
- `NpgsqlComplexIndexSqlGenerator` (extends `NpgsqlMigrationsSqlGenerator`) overrides `Generate(CreateIndexOperation, …)`: if that annotation is present it renders the full `CREATE INDEX` itself (column parts quoted, expression parts wrapped in parens, reusing the forwarded Npgsql annotations for `USING`/`INCLUDE`/`NULLS NOT DISTINCT`/etc.); otherwise it delegates to `base`. This requires the runtime `UseNpgsqlComplexIndexes()` wiring.

`CompositeIndexDefinition` carries the ordered parts additively via `Parts` (with `EffectiveParts` falling back to the legacy `PropertyPaths` field) so migration snapshots written before expression support still deserialize.

### Exclusion constraints (`HasExclusionConstraint`)

PostgreSQL-only (`NpgsqlExclusionConstraintExtensions.cs`), the answer to "temporal uniqueness with
a filter": PostgreSQL's `ADD CONSTRAINT UNIQUE/PRIMARY KEY` grammar never accepts `WHERE`, only
EXCLUDE constraints do. Definitions (`ExclusionConstraintDefinition`: ordered parts, each a
property path *or* verbatim expression plus an operator; method defaulting to gist; filter; name;
deferrability) are JSON-stored under `CustomExclusion:Constraints`. Unlike expression indexes, the
differ renders the full `ALTER TABLE … ADD CONSTRAINT … EXCLUDE …` / `DROP CONSTRAINT` DDL as
`SqlOperation`s at **design time** — no runtime `UseNpgsqlComplexIndexes()` wiring involved (as of
v5.0.2 temporal constraints and temporal FKs are rendered the same way, for the same reason; the
generator's `AddUniqueConstraintOperation`/`AddForeignKeyOperation` overrides remain only so
migrations scaffolded before that change still render). Every
ADD is preceded by `DROP CONSTRAINT IF EXISTS` in the same `SqlOperation` (and standalone drops use
`IF EXISTS`), so adopting a same-named hand-written constraint applies without 42P07 and a
re-applied migration self-heals. The snapshot round trip is covered end-to-end by
`SnapshotRoundTripTests`, which compile a real generated snapshot with Roslyn and diff against it —
if a constraint nevertheless re-emits on every `migrations add` in a consuming app, the *compiled*
snapshot is stale (`--no-build`, stale migrations assembly), not a differ bug. The
`btree_gist` auto-injection is shared with temporal constraints (single `CREATE EXTENSION`, same
`UseBtreeGist()` / `SuppressTemporalExtensionAutoInjection()` switches) and triggers when a gist
constraint (method compared case-insensitively) has a column element whose store type has no GiST
operator class of its own, or an expression element under a B-tree operator — whatever the
operator: `<>` on an integer needs it as much as `=` does.

### Provider validation is scoped, never a list sweep

Satellites reject what their provider cannot express by overriding
`ValidateCreateIndexOperation(CreateIndexOperation)`, which the core calls for each operation *it*
builds from a complex-index declaration. Never validate by iterating the finished operation list:
it also holds the operations the base EF differ emitted for native `HasIndex` declarations, and
policing those turns any provider index option the satellite doesn't happen to know about into a
hard failure of the consumer's whole `migrations add` — for indexes that never touched this package.
The check has to exist because entity-level provider annotations reach the operation *unfiltered*
(only the property-level path goes through `IsForwardedIndexAnnotation`), so `.UseGin()` on a SQL
Server model is caught, while a native `HasIndex(...).HasMethod("gin")` is left alone. The
property-level path used to be the loophole: the whitelist dropped the other satellite's options
without a word, so a property-level `.UseGin()` diffed by SQL Server applied as a plain B-tree.
Since 5.1.0 each satellite's `IsForwardedIndexAnnotation` also returns true for the *other*
provider's prefix (`Npgsql:` / `SqlServer:`) — forwarded solely so the same
`ValidateCreateIndexOperation` rejects it, which keeps one message for both declaration styles.

### Declarations on TPC types and types with no table

Every descriptor scan asks `CustomMigrationsModelDiffer.DeclarationTables` where a declaration
lands: the type's own table and, under TPC, the table of every type derived from it, each resolved
against that table's entity type — where EF Core puts its own `HasIndex`. Until 5.5.0 the scans used
the declaring type's table only, so on a concrete TPC base a unique index or exclusion constraint
held among the base's rows and not among a derived type's, and applied clean (#93). The rule is
gated on rendering version 3, like any change of answer: a snapshot scaffolded before it resolves
to the declaring table only, which is what makes the first `migrations add` create the copies. That
is also why exclusion and temporal declarations mark the version now; a constraint-only model
carried none. The name checks need nothing new, since they iterate descriptors.

Each copy's name comes from `CustomMigrationsModelDiffer.CopyName`. A default name includes its
table already. An explicit name stays on the declaring type's table, and on every table where the
provider keeps index names per table (SQL Server), as EF Core repeats its own `HasIndex` name.
Elsewhere the copy takes the name followed by `_` and its table: EF Core repeats the name there too,
and the migration fails at apply (42P07, SQLite "index … already exists", measured on EF Core 10).
Until that rule, the name checks refused a repeated explicit name, and two partial indexes or
exclusion constraints over the same parts, which must both be named, could take no name that
passed (#167). A temporal foreign key owns no index and keeps its explicit name on every table. The
names this package makes up (a default name with more than one table, an explicit name with its
table) are shortened past the identifier limit as EF Core shortens its own (`ShortenIdentifier`,
#145, the temporal foreign key's since #168); a name the user wrote is refused instead, since a
shorter one fixes it.

Two things refuse rather than copy. EF Core 10 leaves a TPC type's complex properties out of its
derived tables (on save the value is dropped, a query over the derived type throws), so
`ThrowIfMissingFromDerivedTable` fails a declaration reading such a member. It has to check
mappings, not `GetColumnName`: on a derived table a table-split member resolves to its bare
property name, `Contact.Email` to `"Email"`, which a derived property of that name would answer. And
a temporal foreign key to a TPC type with derived types fails, since its rows live in several
tables; EF Core creates no foreign key for such a relationship.

A type whose `DeclarationTables` is empty — mapped to no table, with no derived type that is —
still fails through `ThrowIfDeclaredOnUnmappedType` (since 5.1.0; before, it produced nothing).
Types mapped to a view, SQL query or function are still skipped silently — an index on those is
nothing this package could create.

### Key extension points

- **Adding a new provider**: Subclass `CustomMigrationsModelDiffer` (override `IsForwardedIndexAnnotation` and `IndexNameScope` unless the provider keeps index names unique across the database, optionally `ValidateCreateIndexOperation`/`ResolveUnmappedPart`/`QuoteIdentifier`), implement `IDesignTimeServices` to replace the differ, and ship a `.targets` file that injects the attribute (with `ForProvider` set). The PostgreSQL project is the full-featured reference; the SQL Server project is the minimal one (whitelist + validation, no custom SQL generator).
- **New index options**: Add constants to `ComplexIndexAnnotations.cs` (or `NpgsqlAnnotations.cs`), expose them via `ComplexIndexBuilder`, and read them in the differ when constructing `CreateIndexOperation`.

### Expression path extraction

`ComplexIndexExtensions` parses anonymous-type lambda expressions (`x => new { x.Name, x.Address.City }`) by recursively walking `MemberExpression` chains to produce dotted property paths. These paths are then matched against the EF Core metadata model to resolve column names.

Every extraction entry point threads the lambda's `ParameterExpression` down to `ExtractSinglePart`,
which requires the member chain to bottom out at exactly that parameter. This is not optional
politeness: a captured variable or static member (`x => captured.Name`) produces a perfectly
well-formed path — `"captured.Name"` — that no property lookup can ever match, so without the check
the mistake surfaces much later as an opaque "could not resolve property path" from the differ.
`NpgsqlLinqIndexTranslator.TryGetPath` has always done the same check; core now matches it.

### Per-column sort direction (`DbOrder.Asc`/`DbOrder.Desc`)

`DbOrder.Asc`/`Desc` are identity marker functions; `ExtractSinglePart` peels them (and `Convert` boxing) off the expression in any order to record a `Descending` flag per part. Markers of *different* kinds compose (`NullsLast(Desc(x))`); markers of the *same* kind do not — `Asc(Desc(x))` throws rather than letting one win, since silently picking either sorts the index opposite to what was written. Parts are copied via `IndexPartDefinition.WithSortOptions`, which lives on the type so a new member can't be dropped by a caller rebuilding a part by hand (`Template` was lost that way). Unlike expression indexes, descending columns are **provider-agnostic and need no satellite work**: the differ maps direction onto the native `CreateIndexOperation.IsDescending` (`bool[]`), which every relational provider renders. The differ leaves `IsDescending` **null** when all parts are ascending, so existing ascending indexes don't churn. To avoid snapshot churn, `HasComplexCompositeIndex` keeps writing the legacy `PropertyPaths` form when every column is ascending and only switches to the ordered `Parts` form when a descending column is present. Note: wrapping a member in `DbOrder.Desc(...)` makes it a method call, so C# requires naming it in the anonymous type (`new { x.A, B = DbOrder.Desc(x.B) }`).
