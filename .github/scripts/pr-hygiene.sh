#!/usr/bin/env bash
# Checks a pull_request event payload against the tracking rules in CONTRIBUTING.md#issues:
# exactly one type label, a severity on a bug, and a closing reference to the issue it resolves.
# A `silent` PR into a minor must also have its issue's row under "### Changed answers" in that
# version's CHANGELOG.md section (CONTRIBUTING.md#branches-and-releases); that is the one rule here
# that reads the checkout, and only those two text files. Needs no token and runs locally on a
# saved event:
#   .github/scripts/pr-hygiene.sh path/to/event.json [checkout root, default .]
set -euo pipefail

event=${1:?usage: pr-hygiene.sh <event.json> [checkout root]}
root=${2:-.}
author=$(jq -r '.pull_request.user.login' "$event")
labels=$(jq -r '.pull_request.labels[].name' "$event")
body=$(jq -r '.pull_request.body // ""' "$event")

# Dependabot opens its own PRs with its own labels; they close no issue.
if [[ $author == "dependabot[bot]" ]]; then
  echo "Dependabot PR, not checked."
  exit 0
fi

problems=()

types=$(grep -cxE 'bug|enhancement|documentation|tests|chore' <<<"$labels" || true)
if (( types != 1 )); then
  problems+=("Needs exactly one type label (bug, enhancement, documentation, tests, chore); it has $types.")
fi

if grep -qx bug <<<"$labels" && ! grep -q '^severity/' <<<"$labels"; then
  problems+=("A bug needs a severity/* label (critical, high, medium, low).")
fi

# GitHub's closing keywords, with #n or owner/repo#n. Only these link the PR to its issue.
closing='\b(close[sd]?|fix(e[sd])?|resolve[sd]?):? +([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?#[0-9]+'
if ! grep -qiE "$closing" <<<"$body"; then
  problems+=("The description needs a closing reference to its issue, such as 'Closes #12'.")
fi

# A silent fix: a migration that scaffolded and applied cleanly while the database ended up
# different from the model. Into a major (x.0.0, or its preview) it goes as it always has. From
# 10.0.0 on a minor may carry one too, so the LTS line can receive it; the price is a row under
# "### Changed answers" at the top of that version's entry, linking the issue, which
# ChangelogConsistencyTests cannot tie to the label because a test does not see labels. A patch
# may not carry one at all.
if grep -qx silent <<<"$labels"; then
  version=$(sed -n 's:.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)
  if [[ $version =~ ^[0-9]+\.([0-9]+)\.([0-9]+)$ ]]; then
    minor=${BASH_REMATCH[1]} patch=${BASH_REMATCH[2]}
    if (( patch != 0 )); then
      problems+=("A silent fix ships in a minor, not a patch, but Directory.Build.props prepares $version. Bump the minor and add a '### Changed answers' row.")
    elif (( minor != 0 )); then
      # The version's section ("## x.y.0", the heading release.yml matches literally), then its
      # "### Changed answers" subsection.
      changed=$(awk -v heading="## $version" '
        $0 == heading { found = 1; next }
        found && /^## / { exit }
        found && /^### / { inside = ($0 ~ /^### Changed answers[[:space:]]*$/); next }
        found && inside { print }
      ' "$root/CHANGELOG.md")
      for issue in $(grep -oiE "$closing" <<<"$body" | grep -oE '[0-9]+$' | sort -u); do
        if ! grep -qE "/issues/$issue\)" <<<"$changed"; then
          problems+=("This PR is labelled silent and closes #$issue, but CHANGELOG.md's $version section has no '### Changed answers' row linking that issue (CONTRIBUTING.md#branches-and-releases).")
        fi
      done
    fi
  fi
fi

if (( ${#problems[@]} )); then
  printf '::error::%s\n' "${problems[@]}"
  echo "See the label legend in the pinned Roadmap issue and CONTRIBUTING.md#issues."
  exit 1
fi
echo "Labels and issue link are in order."
