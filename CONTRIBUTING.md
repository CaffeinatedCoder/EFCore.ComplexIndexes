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

## Pull requests

- One concern per PR, with the reasoning in the description rather than only the diff.
- Full suite green, including integration if you have Docker.
- User-visible changes get a changelog entry in the root [README.md](README.md) *and* in the
  affected package READMEs under `src/`. `ChangelogConsistencyTests` enforces that the version being
  shipped is documented.
- Match the surrounding style. Comments explain *why*, especially where behaviour is load-bearing
  and non-obvious.

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
