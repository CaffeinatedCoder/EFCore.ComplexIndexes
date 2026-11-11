# Contributing

Issues and pull requests are welcome. For a security report, follow [SECURITY.md](SECURITY.md)
instead — please don't open a public issue for those.

## Getting set up

```bash
dotnet build EFCore.ComplexIndexes.slnx
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj
```

The integration suite spins up real PostgreSQL 18 and 16 and SQL Server 2022 containers via
Testcontainers. Without Docker it goes inconclusive locally, so exclude it if you need to:

```bash
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj --filter "TestCategory!=Integration"
```

On CI the same suite **fails** rather than skipping. An unreachable container must not quietly
retire the end-to-end layer while the build reports green.

Shipping projects live under `src/`, the test project under `test/`.
[CLAUDE.md](CLAUDE.md) is the architecture guide — worth reading before changing the differ.

## The quality bar

This package generates database migrations. Its characteristic failure is not a crash: it is a
migration that scaffolds without complaint, applies without error, and leaves the database with
different semantics than the model declared. Three of the eleven issues fixed in 5.0.2 were exactly
that, and a consumer gets no signal at all when it happens.

Everything below exists because of that failure mode.

**A bug fix needs a regression test, and the test must be proven to work.** Write the test, watch it
fail, apply the fix, watch it pass — then *revert the fix and confirm the test fails again*. A test
that passes both with and without the fix is not a regression test. This has caught vacuous tests in
this repository more than once. See [`.claude/skills/verify-the-guard`](.claude/skills/verify-the-guard/SKILL.md).

**Changes to the differ, a SQL generator, an annotation whitelist, a definition store, or a
`.targets` file get a migration-safety review.** The lens is written down in
[`.claude/skills/migration-safety-review`](.claude/skills/migration-safety-review/SKILL.md): what the
*stock* provider generator emits when the package's own is not wired, whether a declaration can
silently overwrite another, whether a name collision surfaces as an error or a silent replace.

**Prefer a loud failure to a silent one.** Where the package cannot render something correctly, it
should fail at `migrations add` or at apply time with an actionable message — never emit DDL that
applies cleanly and does the wrong thing.

**Prefer a test to a convention.** Mechanical rules belong in the suite, not in a document nobody
re-reads: `ChangelogConsistencyTests`, `PackagingConventionTests`, and `BuilderApiParityTests` all
exist because the alternative was remembering.

**Use the shared harness.** `MigrationHarness` builds a model, constructs any differ, and renders
operations through either the stock provider generator or this package's. Investigating a suspected
bug should be three lines, not a re-derived setup.

## Issues

Use the **Bug** or **Enhancement** form. The bug form asks whether the failure is silent or loud,
because that decides how the fix ships. A migration that scaffolded and applied cleanly while the
database ended up different from the model is fixed in a minor, never a patch, under "Changed
answers" ([below](#a-fix-that-changes-an-answer)). A loud failure can go in a patch.

Releases and longer themes are tracked as `epic` issues, with the work as sub-issues. The pinned
[Roadmap](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/35) lists them all,
along with what each label means. A maintainer sets the `package/*`, `area/*` and `severity/*`
labels when triaging, and a pull request gets the same labels as the issue it closes.

### Labels

The legend, as the Roadmap issue states it (the same scheme as CodoMetis.ValueRanges and
EntglDb/BLite):

- **Type:** exactly one of `bug`, `enhancement`, `documentation`, `tests`, `chore`.
- **`package/*`:** the shipped packages affected: `package/core`, `package/postgresql`, `package/sqlserver`.
- **`area/*`:** the concern across packages: `indexes`, `expression-indexes`, `constraints`, `migrations`, `release`.
- **`severity/*`:** bugs only. `critical`, `high`, `medium` or `low`.
- **`silent`:** a bug that answers wrongly without throwing (a migration that creates a different index or constraint than the model declared is the typical case here).
- **`breaking`:** anything that changes an existing answer or signature.
- **`stacked`** (PRs): the base is another open PR. Merge them in order.
- **Releases:** each release is an `epic` with a milestone. Its work is attached as sub-issues and carries the same milestone. A PR carries its issue's labels and says `Closes #n`.
- **Lines:** this repository ships one line, on `main`: `EFCore.ComplexIndexes` 5.x on EF Core 10 / .NET 10 (LTS, to 2028-11, fixes only after 5.5.0, #36). Its successor for EF Core 11, `CodoMetis.EFCore.SchemaKit`, has its own repository, with its own issues and labels (#211).

`.github/workflows/pr-hygiene.yml` fails a PR with no type label, a bug with no severity, or no
closing reference, and a `silent` PR into a minor without its row under `### Changed answers`.
It runs again when labels or the description change, so fixing it needs no new commit.

## Pull requests

- Open with `Closes #n` and follow the template: the problem as a consumer would have seen it,
  the change, and what proves it. Give the PR the issue's labels and milestone.
- One concern per PR, with the reasoning in the description rather than only the diff.
- Full suite green, including integration if you have Docker.
- User-visible changes get a changelog line in the root [CHANGELOG.md](CHANGELOG.md) *and* in the
  affected packages' `CHANGELOG.md` under `src/`, in the form [below](#changelog).
  `ChangelogConsistencyTests` holds the form, and the heading style `release.yml` reads to build
  the release notes.
- Provider-specific documentation belongs under [docs/](docs), not in the root README — it is the
  landing page. The packed READMEs under `src/` must link out with **absolute** GitHub URLs, since
  nuget.org cannot resolve a relative path; `DocumentationLinkTests` enforces both.
- Renaming a public API means updating the prose that names it, and a provider page documents only
  what that provider can actually do. `DocumentationApiTests` enforces both; an example that calls
  into EF Core, Npgsql or the BCL adds the method to its `ExternalApi` allowlist.
- Match the surrounding style. Comments explain *why*, especially where behaviour is load-bearing
  and non-obvious.

## Branches and releases

This repository ships one line: **`EFCore.ComplexIndexes` 5.x** on EF Core 10 / .NET 10, the LTS
line, maintained until EF Core 10 leaves support in November 2028. From 5.5.0 it refuses EF Core 11
at registration (#55) and takes fixes only (#36): loud ones in a patch or a minor, silent ones in a
minor ([below](#a-fix-that-changes-an-answer)).

Its successor for EF Core 11, **`CodoMetis.EFCore.SchemaKit`**, is a new package with a new API. It
has its own repository,
[CaffeinatedCoder/CodoMetis.EFCore.SchemaKit](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit),
with its own branches, issues, releases and security policy (decided 2026-11-11, #211). Nothing of
it is built, tracked or released here, and this repository keeps its name.

**`main`** is the 5.x line and the only long-lived branch. Work goes into a branch from `main` and
a PR into `main`, where `dotnet.yml` and `pr-hygiene.yml` run. A release is a `v*` tag on `main`.
`release.yml` checks the tag against `Version` in `Directory.Build.props` and takes the release
notes from the `## [<version>] — <date>` section of the root `CHANGELOG.md`, so a mistyped tag or an
undocumented version stops before anything is published.

### Where `release/5.x` went

From the `v5.5.0` tag until 2026-11-11 the plan was two lines in this repository: 5.x on
`release/5.x`, cut from that tag, and SchemaKit on `main`, with a fix for both merged into `main`
first and cherry-picked. `v5.6.0` was tagged on `release/5.x` under that plan. When SchemaKit got
its own repository, `release/5.x` was merged back into `main` (#211). Where an older issue, PR or
commit names `release/5.x`, read `main`.

### A fix that changes an answer

A silent fix is one where `migrations add` scaffolded, the migration applied, and the database
ended up different from what the model declared; the fix changes what an existing model
scaffolds. 5.x has no next major: the package for the next EF Core major is SchemaKit, in its own
repository. So a silent fix has no major to wait for. From 5.5.0 on it ships as a minor, never a
patch, and that minor says so:

- **Its changelog entry opens with `### Changed answers`**, directly after the entry's
  introduction and before any other section: one row per changed answer, each linking its issue
  (`https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/n`) and its entry under
  "Changed answers in x.y.0" in [docs/upgrading.md](docs/upgrading.md), which says what the first
  `migrations add` after upgrading re-creates and what to clean up first. The entry's other
  changes follow under their own sections ([below](#changelog)).

`ChangelogConsistencyTests` holds the form:
`### Changed answers` appears only in a minor, sits before every other section of its entry, and
each row links its issue and an upgrading entry that exists. After 5.4.1, a change worded as a
former value ("where it rendered …") anywhere else in the entry fails it too. That test reads the file, not the label. The label half is
`.github/scripts/pr-hygiene.sh`: a PR labelled `silent` into a minor fails until its closing
issue has a row there, and into a patch it fails outright.

### Releases

- **Issues:** `Closes #n` closes its issue when the PR merges into `main`, the default branch,
  which is where every PR goes.
- **Changelog:** a change goes under the version it ships in. The root changelog keeps every
  release's section, so it stays the full history, and the package changelogs under `src/` follow
  the root.
- **Previews:** 5.x ships none. `release.yml` and `ChangelogConsistencyTests` still read a
  `## [x.y.z-preview.N] — <date>` heading, which `release.yml` would publish as a prerelease.
- **GitHub's Latest release** stays on the newest stable version. `release.yml` publishes a
  preview with `--prerelease --latest=false`, and a stable tag with `--latest=false` when the
  current Latest is newer by `sort -V`, so a tag pushed for an older version never takes the
  repository page. `ReleaseWiringConventionTests` checks both.
- **SECURITY.md's supported-versions table** has one supported row. It names the latest release on
  NuGet, never the version being prepared, and never a preview.
  `SecurityPolicyConsistencyTests` compares the row with `PackageValidationBaselineVersion`, so
  move the row when you move the baseline, which happens when you open the next version right
  after a tag; the tagged commit still names the release before it.
- **The ApiCompat baseline** is the latest stable release before the version being prepared,
  never a preview.

## Changelog

A version's section of the root `CHANGELOG.md` is the GitHub release's notes, word for word, so it
is written for someone deciding what to change before they upgrade. The package changelogs use the
same form, with the lines that affect that package. The form is CodoMetis.ValueRanges'. A package
changelog's section is also that package's release notes on nuget.org and in Visual Studio, set at
pack and shown as plain text: links read as "text (url)" and code marks are dropped, so a line has
to read well without them. A missing or empty section fails the pack.

- **The heading** is `## [x.y.z] — yyyy-mm-dd` (`## [x.y.z-preview.N] — yyyy-mm-dd` for a
  preview), and the root changelog ends with a `[x.y.z]: …/releases/tag/vx.y.z` definition per
  version. Write entries under the version being prepared, never under `Unreleased`. The date is
  the tag's: set it in all four changelogs in the commit that gets tagged, because no test or
  workflow checks it.
- **One optional line first**: what the release is about and, where something changes for an
  existing model, a link to its section of [docs/upgrading.md](docs/upgrading.md).
- **Then the sections, in this order:** `### Changed answers` (a minor only, see
  [above](#a-fix-that-changes-an-answer)), `### Breaking`, `### Added`, `### Changed`,
  `### Fixed`. Leave out the ones you don't need. A version with nothing for a package says so in
  one line: "No change in this package."
- **Breaking** holds everything that can break an existing model or database, in a major or not: a
  declaration that now fails at `migrations add` or at registration, an API removed or renamed.
  Each line says what to do about it.
- **One change per line, on one line.** Say what happens now and, for a fix, what happened before:
  "… applies, where it failed with Msg 5074". A "before" that was a migration which applied ("where
  it rendered …") is a changed answer and goes under `### Changed answers`. Start with the area,
  and name the provider in the line when the change is provider-specific ("Indexes: on SQL Server,
  …"). In `Added`, lead with the new member in bold.
- **Keep each section in area order:** `Registration`, `Indexes`, `Expression indexes`, `Filters`,
  `Constraints`, `Migrations`, `Packaging`, `Docs`. The areas follow the `area/*` labels, with
  typed and string filters, registration and packaging split out because a reader looks for them
  apart. Use no headings below the sections.
- **End with a link** to the guide that explains it, where one does. Links are absolute
  (`https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/docs/…`), because the
  release page can't resolve a relative one; `DocumentationLinkTests` checks that they resolve.
  The pack and the release job rewrite `blob/main` to the version's tag, in the release notes and
  in the packed READMEs, so a published version keeps linking its own documentation.
- **Leave out** the reasoning, the measurements and the history of how it was found, test names,
  internal refactoring with no effect you can observe, and fixes to something new in the same
  release. What the first `migrations add` after upgrading does goes in
  [docs/upgrading.md](docs/upgrading.md), the reasoning in the guide, the history in the commit and
  the issue.
- **Write in the plain voice of the guides:** short sentences, active voice, no emphasis markers or
  emoji.

`ChangelogConsistencyTests` holds the form: the heading, known sections in order, one line per
change, at most 60 words a change and 40 for the introduction, absolute links, and the
`Changed answers` rules.

## AI-assisted development

A substantial portion of this codebase — including much of the migration differ and its test suite —
was written with AI assistance, using [Claude Code](https://claude.com/claude-code). This is stated
plainly because the project has real adopters who deserve to know how it is built, not because
anything about it is unusual.

What that does and does not mean:

- **Direction, architecture, and acceptance are the maintainer's.** Design decisions, trade-offs, and
  what merges are human calls. AI is a tool used under review, not an autonomous committer.
- **Nothing is accepted on the basis that it looks right.** The verification discipline above exists
  precisely because plausible-looking code is cheap to produce. Fixes are proven against the bug they
  claim to fix; convention tests are proven by seeding the defect they claim to catch.
- **The audit trail is public.** Commit messages record what was wrong, what a consumer would have
  experienced, and how the fix was verified. That is the evidence, and it is more informative than
  any statement about authorship.
- **Code is judged on behaviour, not provenance.** A defect is a defect whoever typed it. AI
  assistance neither excuses a bug nor makes one more likely to be excused.

If you contribute with AI assistance, that is fine — the same bar applies. Please make sure you
understand and can defend what you are submitting, and that it is yours to contribute.
