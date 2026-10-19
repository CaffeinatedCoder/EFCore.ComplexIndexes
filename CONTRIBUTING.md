# Contributing

Issues and pull requests are welcome. For a security report, follow [SECURITY.md](SECURITY.md)
instead — please don't open a public issue for those.

## Getting set up

```bash
dotnet build EFCore.ComplexIndexes.slnx
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj
```

The integration suite spins up a real PostgreSQL 18 container via Testcontainers. Without Docker it
goes inconclusive locally, so exclude it if you need to:

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
- **Lines:** from November 2026 there are two supported lines, one per EF Core major (issue #3): `EFCore.ComplexIndexes` 5.x on EF Core 10 / .NET 10 (LTS, to 2028-11, branch `release/5.x`, fixes only after 5.5.0, #36) and its successor `CodoMetis.EFCore.SchemaKit` 11.x on EF Core 11 / .NET 11 (branch `main`, #37). A fix for both goes to `main` first and is cherry-picked with `-x`.

`.github/workflows/pr-hygiene.yml` fails a PR with no type label, a bug with no severity, or no
closing reference, and a `silent` PR into a minor without its row under `### Changed answers`.
It runs again when labels or the description change, so fixing it needs no new commit.

## Pull requests

- Open with `Closes #n` and follow the template: the problem as a consumer would have seen it,
  the change, and what proves it. Give the PR the issue's labels and milestone.
- One concern per PR, with the reasoning in the description rather than only the diff.
- Full suite green, including integration if you have Docker.
- User-visible changes get a changelog entry in the root [CHANGELOG.md](CHANGELOG.md) *and* in the
  affected packages' `CHANGELOG.md` under `src/`. `ChangelogConsistencyTests` enforces that the
  version being shipped is documented, and that the heading style stays the one `release.yml` reads
  to build the release notes.
- Provider-specific documentation belongs under [docs/](docs), not in the root README — it is the
  landing page. The packed READMEs under `src/` must link out with **absolute** GitHub URLs, since
  nuget.org cannot resolve a relative path; `DocumentationLinkTests` enforces both.
- Renaming a public API means updating the prose that names it, and a provider page documents only
  what that provider can actually do. `DocumentationApiTests` enforces both; an example that calls
  into EF Core, Npgsql or the BCL adds the method to its `ExternalApi` allowlist.
- Match the surrounding style. Comments explain *why*, especially where behaviour is load-bearing
  and non-obvious.

## Branches and releases

Two lines are supported at once, one per EF Core major (decided 2026-10-19, #36):

- **`EFCore.ComplexIndexes` 5.x** on EF Core 10 / .NET 10, the LTS line, maintained until EF Core
  10 leaves support in November 2028.
- **`CodoMetis.EFCore.SchemaKit` 11.x** on EF Core 11 / .NET 11, a new package with a new API
  (#37, #61). It starts at 11.0.0 and tracks the EF Core major.

Each line has a branch, a `Version` in its own `Directory.Build.props`, and the same CI
(`dotnet.yml` and `pr-hygiene.yml` run on `main` and `release/**`), and a release is a `v*` tag on
the branch it ships from. `release.yml` checks the tag against that `Version` and takes the release
notes from the `## <version>` section of the root `CHANGELOG.md`, so a mistyped tag or an
undocumented version stops before anything is published.

### Until the `v5.5.0` tag

**`main`** is 5.5.0, the last 5.x minor with a change of its own: it refuses EF Core 11 at
registration (#55).

### At the `v5.5.0` tag

Cut **`release/5.x`** from the tag and protect it like `main`, with the required
`Test (ubuntu-latest)` check. `main` then becomes SchemaKit (#38): new ids, `net11.0`, and
`11.0.0-preview.N` releases during the EF Core 11 release candidates, tagged on `main`.

### After the `v5.5.0` tag

- **`release/5.x`** takes fixes only: loud ones in a patch or a minor, silent ones in a minor
  ([below](#a-fix-that-changes-an-answer)). A fix to code both lines have goes into `main` first;
  once it has merged, cherry-pick it onto a branch from `release/5.x` (`git cherry-pick -x`, so the
  commit names the original) and open a PR into `release/5.x` that links the `main` PR. A change for
  5.x only, such as its version bump or a fix to code SchemaKit does not have (its API is new, and it
  leaves to EF Core 11 what EF Core 11 does natively, #39), goes straight into `release/5.x`.
- **`main`** is the SchemaKit line: previews until 11.0.0, then its minors. Features go here.

### A fix that changes an answer

A silent fix is one where `migrations add` scaffolded, the migration applied, and the database
ended up different from what the model declared; the fix changes what an existing model
scaffolds. With two lines, a major means a new EF Core release and nothing else (11.0.0 is the
first), so a silent fix has no major to wait for on the LTS line. From 5.5.0 on it ships as a
minor on every line that has the code, never a patch, and that minor says so:

- **Its changelog entry opens with `### Changed answers`**, directly after the entry's
  introduction and before any other change: one row per changed answer, each linking its issue
  (`https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/n`) and saying what the
  first `migrations add` after upgrading produces, as the 5.4.0 entry's **Upgrading** notes do.
  The entry's other changes follow under a heading of their own (`### Fixed`, `### New`,
  `### Changed`), so the rows end where the next heading begins.

`ChangelogConsistencyTests` holds the form:
`### Changed answers` appears only in a minor, sits before every other change of its entry, and
each row links an issue. That test reads the file, not the label. The label half is
`.github/scripts/pr-hygiene.sh`: a PR labelled `silent` into a minor fails until its closing
issue has a row there, and into a patch it fails outright.

### Releases on two lines

- **Issues:** `Closes #n` closes an issue only when the PR merges into the default branch, `main`.
  An issue fixed only on a release branch is closed by hand, with a comment linking the PR.
- **Changelog:** a change goes under the version it ships in on each branch, never under the next
  major too. `main`'s changelog keeps every release's section, of both lines, so it stays the full
  history; a cherry-pick brings its 5.x entry with it. A preview has its own
  `## 11.0.0-preview.N` section, which `release.yml` publishes as its notes, marked as a
  prerelease. The package changelogs under `src/` follow the root, as they do today.
- **GitHub's Latest release** stays on the newest stable version of the newest line.
  `release.yml` publishes a preview with `--prerelease --latest=false`, and a stable tag with
  `--latest=false` when the current Latest is newer by `sort -V`, so a 5.x patch tagged after
  SchemaKit 11.0.0 leaves the repository page pointing at 11.x. `ReleaseWiringConventionTests` checks both.
- **SECURITY.md's supported-versions table** names the latest release of each line on NuGet,
  never the version being prepared, and has no row for a preview.
  `SecurityPolicyConsistencyTests` compares this branch's row with
  `PackageValidationBaselineVersion`, so move the row when you move the baseline, which happens
  when you open the next version right after a tag; the tagged commit still names the release
  before it. The other line's row is compared with `CompanionLineRelease` in
  `Directory.Build.props`, which is empty until 11.0.0 ships. From then on, a minor on one line
  moves its row and `CompanionLineRelease` on the other branch in a PR of its own. The table gets
  its second row in the PR that opens the first version after `v11.0.0`.
- **The ApiCompat baseline** is the latest stable release before the version being prepared,
  never a preview. SchemaKit has new ids and no baseline until 11.0.0 ships (#46).

## Changelog

A version's section of the root `CHANGELOG.md` is the GitHub release's notes, word for word, so it
is written for someone deciding what to change before they upgrade. The heading is `## x.y.z`
(`## 11.0.0-preview.N` for a preview); `release.yml` matches it literally. A short introduction
says what the release is about, then one bullet per change, leading with **Fixed:**, **New:** or
**Changed:** and ending, where the first `migrations add` after upgrading does something, with an
**Upgrading:** note. In a minor that changes an answer, `### Changed answers` comes between the
introduction and the bullets, and the remaining bullets go under a heading of their own
([above](#a-fix-that-changes-an-answer)). Links are absolute, because the release page cannot
resolve a relative one.

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
