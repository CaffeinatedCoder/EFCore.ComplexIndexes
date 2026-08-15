---
name: verify-the-guard
description: Prove a regression test actually fails without its fix, by reverting the source change and re-running. Use whenever adding a test alongside a bug fix or a new validation in this repo, before reporting the work as done.
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
