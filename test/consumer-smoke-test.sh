#!/usr/bin/env bash
#
# End-to-end check of the path a real consumer takes, which nothing else in the suite covers.
#
# The unit tests construct the differ in-process; the integration tests apply operations the differ
# already produced. Neither exercises the chain that actually delivers this package: NuGet restore ->
# the packaged .targets injecting DesignTimeServicesReferenceAttribute -> EF's design-time host
# discovering it -> the right differ winning -> `dotnet ef migrations add` scaffolding real SQL.
#
# That chain is where a lost .targets, a wrong ForProvider, or a broken registration degrades
# silently: `migrations add` still succeeds, it just quietly omits the indexes. So the assertions
# here are about scaffolded *content*, never about the exit code alone.
#
# The consumer project is created outside the repository on purpose. Inside it, Directory.Build.props
# would apply and the project would stop resembling anything a consumer builds.
#
# Usage: consumer-smoke-test.sh [feed-directory]
#   With no argument the packages are packed fresh. Pass a directory of existing .nupkg files
#   (the release workflow passes its pack output) to test exactly the artifacts being shipped.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

app="$work/app"
feed="${1:-}"

# A private package cache, and not an optimisation to skip.
#
# NuGet resolves id+version from the global packages folder before it ever looks at a source, so with
# the version number held at 5.0.2 a locally built package is shadowed by whatever 5.0.2 was restored
# before — including the one published on nuget.org. The test then reports on a package that has
# nothing to do with the working tree. It passed a deliberately sabotaged build that way.
export NUGET_PACKAGES="$work/packages"

version="$(dotnet msbuild "$repo_root/src/EFCore.ComplexIndexes/EFCore.ComplexIndexes.csproj" -getProperty:Version)"
version="$(echo "$version" | tr -d '[:space:]')"

if [[ -z "$feed" ]]; then
    feed="$work/feed"
    echo "==> Packing $version"
    dotnet pack "$repo_root/EFCore.ComplexIndexes.slnx" -c Release -o "$feed" --verbosity quiet
else
    feed="$(cd "$feed" && pwd)"
    echo "==> Using existing feed: $feed"
fi

ls "$feed"/EFCore.ComplexIndexes."$version".nupkg >/dev/null

echo "==> Creating a consumer project at $app"
mkdir -p "$app"
cd "$app"
dotnet new console --framework net10.0 --output . --verbosity quiet >/dev/null

# <clear/> matters: without it a same-named package on nuget.org could satisfy the restore and the
# test would pass without ever touching the freshly built artifacts.
cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

cat > Program.cs <<'CSHARP'
using EFCore.ComplexIndexes;
using Microsoft.EntityFrameworkCore;

Console.WriteLine("consumer");

public sealed class Address
{
    public string City { get; set; } = "";
    public string Zip  { get; set; } = "";
}

public sealed class Order
{
    public int     Id   { get; set; }
    public Address Ship { get; set; } = new();
}

public sealed class ShopContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql("Host=localhost;Database=smoke");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>(entity =>
        {
            entity.ComplexProperty(o => o.Ship, ship => ship.Property(a => a.City).HasComplexIndex());
            entity.HasComplexCompositeIndex(o => new { o.Ship.City, o.Ship.Zip });
        });
    }
}
CSHARP

echo "==> Restoring the packed satellite from the local feed"
dotnet add package EFCore.ComplexIndexes.PostgreSQL --version "$version" --source "$feed" >/dev/null
dotnet add package Microsoft.EntityFrameworkCore.Design >/dev/null

dotnet new tool-manifest >/dev/null
dotnet tool install dotnet-ef --version "10.*" >/dev/null

echo "==> dotnet ef migrations add Initial"
dotnet tool run dotnet-ef migrations add Initial >/dev/null

migration="$(find Migrations -name '*_Initial.cs' | head -1)"
[[ -n "$migration" ]] || { echo "FAIL: no migration was scaffolded"; exit 1; }

failed=0
assert_contains() {
    if grep -q "$1" "$2"; then
        echo "  ok: $3"
    else
        echo "  FAIL: $3 (expected '$1' in $(basename "$2"))"
        failed=1
    fi
}

echo "==> Asserting the scaffolded migration"
assert_contains 'IX_Orders_Ship_City"'          "$migration" "property-level index on the complex column"
assert_contains 'IX_Orders_Ship_City_Ship_Zip'  "$migration" "entity-level composite index"
assert_contains 'Ship_City'                     "$migration" "complex property resolved to its real column name"

# The phantom-churn guard. A second `migrations add` against an unchanged model must scaffold
# nothing: if the snapshot and the code model disagree, the indexes are dropped and recreated on
# every single migration, which applies cleanly and is therefore invisible without this check.
echo "==> dotnet ef migrations add NoChanges (must be empty)"
dotnet tool run dotnet-ef migrations add NoChanges >/dev/null
second="$(find Migrations -name '*_NoChanges.cs' | head -1)"

if grep -qE 'migrationBuilder\.(CreateIndex|DropIndex|Sql|AddColumn|CreateTable)' "$second"; then
    echo "  FAIL: re-running migrations add produced operations — the model churns"
    grep -E 'migrationBuilder\.' "$second" | sed 's/^/      /'
    failed=1
else
    echo "  ok: no churn on an unchanged model"
fi

[[ $failed -eq 0 ]] || { echo; echo "consumer smoke test FAILED"; exit 1; }
echo
echo "consumer smoke test passed ($version)"
