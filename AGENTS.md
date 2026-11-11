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
  5.5.0 its fix ships as a minor (never a patch), whose changelog
  entry opens with `### Changed answers`, a row per changed answer linking the issue and its
  `docs/upgrading.md` entry (`pr-hygiene.sh` fails a `silent` PR into a minor without that row).
  Changelog lines follow CONTRIBUTING.md#changelog
  Add `breaking` to anything that changes an existing answer or signature, and `stacked` to a PR
  whose base is another open PR
- **PRs** carry their issue's labels and milestone, open with `Closes #n`, and describe the
  problem, the change and how it was verified (`.github/PULL_REQUEST_TEMPLATE.md`).
  `pr-hygiene.yml` fails a PR with no type label, a bug with no severity, or no closing reference
- **Epics:** a release is an `epic` issue with a milestone of the same version (`5.6.1`). Its work
  is attached as native sub-issues and carries the same milestone. A theme that spans releases is
  an `epic` without a milestone; a finding it produces belongs to the release epic it ships in
- **Roadmap:** the pinned `roadmap` issue (#35) holds every epic as a sub-issue; `ROADMAP.md` keeps
  the long-form design notes
- **Branches:** one line, on `main`. `EFCore.ComplexIndexes` 5.x is the EF Core 10 package (LTS,
  to 2028-11) and takes fixes only since 5.5.0. Every PR targets `main`, a release is a `v*` tag
  on `main`, and `Closes #n` closes the issue at the merge. Its successor for EF Core 11,
  `CodoMetis.EFCore.SchemaKit`, is a new package in its own repository,
  `CaffeinatedCoder/CodoMetis.EFCore.SchemaKit` (decided 2026-11-11, #211): no SchemaKit work,
  issue or epic belongs here. `release/5.x` held 5.x from the `v5.5.0` tag until it was merged
  back into `main`; where an older issue or PR names it, read `main`. SECURITY.md has one
  supported row, held to `PackageValidationBaselineVersion`.
  See CONTRIBUTING.md#branches-and-releases
