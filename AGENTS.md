# AGENTS.md

`CLAUDE.md` is the canonical source of repository instructions.

Before working in this repository, every coding agent that does not load `CLAUDE.md` automatically must read it in full and follow all of its guidance.

## Issues
Work is tracked as GitHub issues, not as lists in docs or memory. Open the issue before starting
the work, and close it from the PR (`Closes #n`).

- **Labels, on issues and PRs alike** (the ValueRanges and EntglDb/BLite scheme): exactly one type
  (`bug`, `enhancement`, `documentation`, `tests`, `chore`); every `package/*` the change affects
  (`core`, `postgresql`, `sqlserver`); the `area/*` concern (`indexes`, `expression-indexes`,
  `constraints`, `migrations`, `release`); and `severity/*` on a bug. Add `silent` to a bug where
  the migration scaffolded and applied cleanly and the database differs from the model. From
  5.5.0 its fix ships as a minor on every line that has the code (never a patch), whose changelog
  entry opens with `### Changed answers`, a row per changed answer linking the issue and its
  `docs/upgrading.md` entry (`pr-hygiene.sh` fails a `silent` PR into a minor without that row).
  Changelog lines follow CONTRIBUTING.md#changelog
  Add `breaking` to anything that changes an existing answer or signature, and `stacked` to a PR
  whose base is another open PR
- **PRs** carry their issue's labels and milestone, open with `Closes #n`, and describe the
  problem, the change and how it was verified (`.github/PULL_REQUEST_TEMPLATE.md`).
  `pr-hygiene.yml` fails a PR with no type label, a bug with no severity, or no closing reference
- **Epics:** a release is an `epic` issue with a milestone of the same version (`11.0.0`). Its work
  is attached as native sub-issues and carries the same milestone. A theme that spans releases is
  an `epic` without a milestone; a finding it produces belongs to the release epic it ships in
- **Roadmap:** the pinned `roadmap` issue (#35) holds every epic as a sub-issue; `ROADMAP.md` keeps
  the long-form design notes
- **Branches:** two lines, one per EF Core major. `EFCore.ComplexIndexes` 5.x is the EF Core 10
  line (LTS, to 2028-11). `CodoMetis.EFCore.SchemaKit` 11.x, a new package with a new API, is the
  EF Core 11 line (#37). Until the `v5.5.0` tag `main` is 5.5.0. At the tag, `release/5.x` is cut
  from it and protected like `main`, and `main` becomes SchemaKit. After it `release/5.x` takes
  fixes only: a fix to code both lines have merges into `main` first and is cherry-picked (`-x`)
  into a PR against `release/5.x`; a fix to code only 5.x has goes straight into `release/5.x`.
  An issue fixed only on a release branch is closed by hand, because `Closes #n` acts only on
  `main`. SECURITY.md has one row per line, held to `PackageValidationBaselineVersion` for this
  branch's line and to `CompanionLineRelease` in `Directory.Build.props` for the other.
  See CONTRIBUTING.md#branches-and-releases
