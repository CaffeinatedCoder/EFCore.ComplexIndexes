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
# Both satellites are covered, in separate consumer projects. The SQL Server one is not redundant:
# ForProvider scoping means EF skips a satellite whose provider does not match, so a typo there
# leaves SQL Server consumers with no differ at all — and nothing else would notice.
#
# The consumer projects are created outside the repository on purpose. Inside it,
# Directory.Build.props would apply and they would stop resembling anything a consumer builds.
#
# Usage: consumer-smoke-test.sh [feed-directory]
#   With no argument the packages are packed fresh. Pass a directory of existing .nupkg files
#   (the release workflow passes its pack output) to test exactly the artifacts being shipped.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

feed="${1:-}"
failed=0

# A private package cache, and not an optimisation to skip.
#
# NuGet resolves id+version from the global packages folder before it ever looks at a source, so with
# the version number held at 5.0.3 a locally built package is shadowed by whatever 5.0.3 was restored
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

# Quiet while it works, but never silent when it does not: swallowing this output once hid an
# NU1101 behind a bare "exit code 1" in CI.
run() {
    if ! output="$("$@" 2>&1)"; then
        echo "FAILED: $*"
        echo "$output" | sed 's/^/    /'
        exit 1
    fi
}

assert_contains() {
    if grep -q "$1" "$2"; then
        echo "  ok: $3"
    else
        echo "  FAIL: $3 (expected '$1' in $(basename "$2"))"
        failed=1
    fi
}

# One tool install shared by both consumers; the per-project alternative downloads it twice.
run dotnet tool install dotnet-ef --version "10.*" --tool-path "$work/tools"
export PATH="$work/tools:$PATH"

# Creates a consumer project and installs the satellite. Program.cs is written by the caller
# beforehand, at $work/<name>.cs.
#
# Source mapping, not just source ordering: this package's own ids must come from the local feed and
# nowhere else, because these versions also exist on nuget.org and a published one satisfying the
# restore would mean testing something unrelated to the working tree. Everything else (EF Core,
# Npgsql) has to come from nuget.org, so restricting the whole restore to the local feed is not an
# option either — that fails with NU1101 on the transitive dependencies. <clear/> drops any
# machine-level sources.
scaffold_consumer() {
    local name="$1" package="$2" app="$work/$1"

    echo "==> $name: consumer project, $package"
    mkdir -p "$app"
    cd "$app"
    run dotnet new console --framework net10.0 --output .

    cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="EFCore.ComplexIndexes*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

    cp "$work/$name.cs" Program.cs

    # No --source flag: it would override the mapping above and restrict the whole restore,
    # transitive dependencies included, to the local feed.
    run dotnet add package "$package" --version "$version"
    run dotnet add package Microsoft.EntityFrameworkCore.Design

    # Built explicitly first: `dotnet ef` reports a compile error only as "Build failed. Use dotnet
    # build to see the errors", which in CI is indistinguishable from the design-time host failing.
    run dotnet build

    echo "==> $name: dotnet ef migrations add Initial"
    run dotnet-ef migrations add Initial
}

# A second `migrations add` against an unchanged model must scaffold nothing. If the snapshot and the
# code model disagree, the indexes are dropped and recreated on every migration — which applies
# cleanly and is therefore invisible without this check.
assert_no_churn() {
    local name="$1"
    cd "$work/$1"
    run dotnet-ef migrations add NoChanges
    local second
    second="$(find Migrations -name '*_NoChanges.cs' | head -1)"

    if grep -qE 'migrationBuilder\.(CreateIndex|DropIndex|Sql|AddColumn|CreateTable)' "$second"; then
        echo "  FAIL: $name re-running migrations add produced operations — the model churns"
        grep -E 'migrationBuilder\.' "$second" | sed 's/^/      /'
        failed=1
    else
        echo "  ok: no churn on an unchanged model"
    fi
}

migration_of() {
    find "$work/$1/Migrations" -name '*_Initial.cs' | head -1
}

# ── PostgreSQL ────────────────────────────────────────────────────────────────────────────────────

cat > "$work/postgres.cs" <<'CSHARP'
using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

Console.WriteLine("consumer");

public sealed class Address
{
    public string City    { get; set; } = "";
    public string Zip     { get; set; } = "";
    public string Country { get; set; } = "";
}

public sealed class Order
{
    public int                    Id     { get; set; }
    public Address                Ship   { get; set; } = new();
    public NpgsqlRange<DateTime>  Period { get; set; }
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

            // Provider-specific rendering: reaches the operation as a real Npgsql annotation.
            entity.HasComplexIndex(o => o.Ship.Zip, index => index.UseGin());

            // Null ordering has no slot on EF's CreateIndexOperation, so it routes through the parts
            // annotation and carries the sentinel that makes a missing runtime wiring fail loudly.
            entity.HasComplexIndex(o => DbOrder.NullsLast(o.Ship.Country));

            // The discriminator for "the Npgsql differ is the one that ran". Index options are not:
            // entity-level provider annotations reach the operation unfiltered, so the core differ
            // emits those too. Exclusion constraints are rendered as design-time DDL by the Npgsql
            // differ alone — the core differ has never heard of them.
            entity.HasExclusionConstraint(o => o.Ship.City, o => o.Period, name: "ex_orders_city_period");
        });
    }
}
CSHARP

scaffold_consumer postgres EFCore.ComplexIndexes.PostgreSQL

echo "==> postgres: asserting the scaffolded migration"
pg="$(migration_of postgres)"
[[ -n "$pg" ]] || { echo "FAIL: no migration was scaffolded"; exit 1; }

assert_contains 'IX_Orders_Ship_City"'         "$pg" "property-level index on the complex column"
assert_contains 'IX_Orders_Ship_City_Ship_Zip' "$pg" "entity-level composite index"
assert_contains 'Ship_City'                    "$pg" "complex property resolved to its real column name"
assert_contains 'Npgsql:IndexMethod'           "$pg" "provider option forwarded as an Npgsql annotation"
assert_contains 'gin'                          "$pg" "the GIN index method survives packaging"
assert_contains '__requires_UseNpgsqlComplexIndexes__' "$pg" "null-ordered index carries the runtime-wiring sentinel"
assert_contains 'EXCLUDE'                      "$pg" "exclusion constraint DDL — only the Npgsql differ emits this"
assert_contains 'ex_orders_city_period'        "$pg" "the exclusion constraint keeps its declared name"

assert_no_churn postgres

# ── SQL Server ────────────────────────────────────────────────────────────────────────────────────

cat > "$work/sqlserver.cs" <<'CSHARP'
using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.SqlServer;
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
        => options.UseSqlServer("Server=localhost;Database=smoke;Trusted_Connection=True;TrustServerCertificate=True");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>(entity =>
        {
            entity.ComplexProperty(o => o.Ship, ship => ship.Property(a => a.City).HasComplexIndex());

            // Provider-specific rendering. Deliberately not clustered: the SQL Server differ rejects
            // a clustered complex index when the primary key already holds the clustered slot.
            entity.HasComplexIndex(o => o.Ship.Zip, index => index.HasFillFactor(80).IsCreatedOnline());
        });
    }
}
CSHARP

scaffold_consumer sqlserver EFCore.ComplexIndexes.SqlServer

echo "==> sqlserver: asserting the scaffolded migration"
ss="$(migration_of sqlserver)"
[[ -n "$ss" ]] || { echo "FAIL: no migration was scaffolded"; exit 1; }

assert_contains 'IX_Orders_Ship_City"' "$ss" "property-level index on the complex column"
assert_contains 'Ship_Zip'             "$ss" "complex property resolved to its real column name"
assert_contains 'SqlServer:FillFactor' "$ss" "provider option forwarded as a SQL Server annotation"
assert_contains 'SqlServer:Online'     "$ss" "second provider option on the same index"

assert_no_churn sqlserver

# The discriminator for "the SQL Server differ is the one that ran". Index options are not: they
# reach the operation unfiltered and the core differ emits them just the same, so a neutered
# satellite .targets still produces a migration that looks correct. What only this satellite does is
# *refuse* — a clustered complex index is rejected because the primary key already holds the
# clustered slot. If the satellite is not registered, the declaration scaffolds silently instead.
echo "==> sqlserver: a rejection only this satellite's differ performs"
cd "$work/sqlserver"
sed 's/index.HasFillFactor(80).IsCreatedOnline()/index.IsClustered()/' Program.cs > Program.cs.tmp
mv Program.cs.tmp Program.cs

if reject_output="$(dotnet-ef migrations add Clustered 2>&1)"; then
    echo "  FAIL: a clustered complex index scaffolded cleanly — the SQL Server differ did not run"
    failed=1
elif grep -qi "clustered" <<<"$reject_output"; then
    echo "  ok: clustered complex index rejected at migrations add"
else
    echo "  FAIL: migrations add failed, but not with the clustered-index rejection:"
    echo "$reject_output" | tail -5 | sed 's/^/      /'
    failed=1
fi

[[ $failed -eq 0 ]] || { echo; echo "consumer smoke test FAILED"; exit 1; }
echo
echo "consumer smoke test passed ($version)"
