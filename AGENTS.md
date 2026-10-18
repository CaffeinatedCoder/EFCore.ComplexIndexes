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
  the migration scaffolded and applied cleanly and the database differs from the model. Its fix
  went in the next major up to 10.0.0; after 10.0.0 it ships as a minor on every supported line
  (never a patch), whose changelog entry opens with `### Changed answers`, a row per changed
  answer linking the issue (`pr-hygiene.sh` fails a `silent` PR into a minor without that row).
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
- **Branches:** up to two lines are supported at once, 10.x (LTS, EF Core 10 / .NET 10) and 11.x
  (EF Core 11 / .NET 11). Until the `v10.0.0` tag: `main` is 10.0.0 (the rename, new ids);
  `release/5.x` holds 5.x patches and then takes no changes; `net11` holds the .NET 11 retarget,
  ships `11.0.0-preview.N`, and merges `main` forward after every change, so nothing lands on
  `net11` that `main` should have. At the tag, `release/10.x` is cut from it and `net11` becomes
  `main`. After it, a change for both merges into `main` first and is cherry-picked (`-x`) into a
  PR against `release/10.x`; additions ship as 10.x minors until `v11.0.0`, after which 10.x takes
  fixes only. An issue fixed only on a release branch is closed by hand, because `Closes #n` acts
  only on `main`. SECURITY.md has one row per line, held to `PackageValidationBaselineVersion`
  for this branch's line and to `CompanionLineRelease` in `Directory.Build.props` for the other.
  See CONTRIBUTING.md#branches-and-releases
