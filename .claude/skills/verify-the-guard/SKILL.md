---
name: verify-the-guard
description: Prove a guard actually fails without the thing it guards — revert the source fix, or break the mechanism for packaging and design-time wiring where there is no source fix to revert. Use whenever adding a test alongside a bug fix or a new validation in this repo, and whenever a check's subject is delivery rather than code, before reporting the work as done.
---

# Verify the guard

A regression test that passes both with and without the fix is not a regression test. It is a test
that happens to pass, and it will keep happening to pass while the bug comes back.

The discipline is one extra step: **after the test goes green, revert the source change and confirm
the test goes red.** Then restore and re-run.

This is cheap and it earns its keep. During the 5.0.1 audit it caught a test of mine that asserted
nothing: I wrote two exclusion-constraint declarations I believed would collide on their default
names, but they had identical elements *and* filters, so the store deduplicated them into one and
no exception was ever possible. The test passed against broken code. Only the counterfactual run
surfaced it.

## The loop

1. Write the test. Run it — it should **fail**, for the reason you expect. Read the failure message;
   if it fails for a different reason than the bug, the test is testing the wrong thing.
2. Apply the fix. Run it — it should pass.
3. **Revert only the source change**, keeping the test. Run again — it must fail.
4. Restore the fix. Run the full suite.

Step 1 is often skipped when the fix is already written. Step 3 recovers the same guarantee after
the fact, so do step 3 always, even when you did step 1.

## Reverting cleanly

Copy the files aside, strip the change, run, restore:

```bash
SCRATCH=$(mktemp -d)
cp src/EFCore.ComplexIndexes/SomeFile.cs "$SCRATCH/"
# remove just the new guard, then:
dotnet test test/EFCore.ComplexIndexes.Tests/EFCore.ComplexIndexes.Tests.csproj \
  --filter "TestCategory!=Integration"
cp "$SCRATCH/SomeFile.cs" src/EFCore.ComplexIndexes/
```

Revert the **source**, never the test. Reverting via `git checkout --` on a file that also contains
unrelated work will lose it — copy aside instead.

Confirm the restore: re-run the suite and check the count is back where it started. Never leave the
working tree with a partially reverted fix.

## What counts as failing for the right reason

- **Right:** `Assert.ThrowsExactly` reports no exception was thrown; `Assert.HasCount` reports 1
  instead of 2; the asserted SQL fragment is absent.
- **Wrong:** a `NullReferenceException`, a compile error, or a failure in a *different* test. A
  compile error usually means the test depends on API introduced by the fix — restructure it to
  exercise behaviour that exists either way, or accept that it is a feature test rather than a
  regression test and say so.

## When the guard's subject is delivery, not code

Packaging and design-time wiring have no one-line source fix to revert. The counterfactual is to
**break the mechanism** — neuter a `.targets`, drop a whitelist entry, hand the script a different
kind of input — and confirm the guard notices. Three traps, all of which produced a green run
against a deliberately broken build on 2026-08-15:

**The mechanism is redundant.** A satellite consumer gets two `DesignTimeServicesReferenceAttribute`s
— the satellite's and the core package's, riding along through `buildTransitive`. Neuter either one
alone and migrations still scaffold correctly, because the survivor covers it. Break them
**separately**, and expect the single-break case to pass; that is the design, not a failure.

**The assertion may not discriminate.** Entity-level provider annotations reach the operation
unfiltered, so `Npgsql:IndexMethod` and `SqlServer:FillFactor` appear whether the satellite differ
ran or the core one did. Asserting on them proves the package installed, not that the right differ
won. Pin a satellite with something only it does: an exclusion constraint for Npgsql (design-time
DDL the core differ has never heard of), and for SQL Server a **rejection** — a clustered complex
index must fail at `migrations add`, so scaffolding cleanly is the failure.

**The conditions carry the bug.** Re-running the same command proves nothing; vary the axis the
check silently depends on. A cold checkout instead of a warm `bin/`. A pre-built feed instead of one
the script packs itself — packing populates `NUGET_PACKAGES` as a side effect, and a restore that
only works because of it passes one way and fails the other. A private package cache instead of the
global one, which otherwise serves a same-versioned package from an earlier build, or from
nuget.org, in place of the one just built.

## Ways a guard passes vacuously

Worth checking before believing a green run:

- It asserted on something both the correct and broken paths produce (see above).
- It read a cached or previously published artifact rather than the one just built.
- It greps for a phrase that no longer occurs, because the phrase was reworded. Assert the match
  succeeded, so a reword fails loudly instead of quietly testing nothing.
- Its setup collapsed: two declarations meant to collide deduplicated into one, so the exception
  under test was never possible.

## Tests that legitimately pass either way

Some tests are not regression guards and should not be forced through this loop:

- Tests pinning behaviour that was *already* correct, added for documentation.
- Tests guarding against a **future** over-correction — for instance, that a colliding source model
  stays diffable, which protects against a validation being made too strict later.

Both are worth having. Just do not count them as evidence that a fix works, and say which they are
when reporting.

## Reporting

State the counterfactual result explicitly, with the failure output. "Added a test" says nothing
about whether the bug is caught; "reverting the fix makes these three tests fail, with this output"
is the claim that matters.

When several fixes land together, revert them all at once and confirm the failure count matches the
number of guards — a fix whose test still passes stands out immediately.
