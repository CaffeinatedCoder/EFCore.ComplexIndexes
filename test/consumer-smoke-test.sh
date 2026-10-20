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
# Both satellites are covered, in separate consumer projects, and the core package on SQLite. The SQL Server one is not redundant:
# ForProvider scoping means EF skips a satellite whose provider does not match, so a typo there
# leaves SQL Server consumers with no differ at all — and nothing else would notice.
#
# The consumer projects are created outside the repository on purpose. Inside it,
# Directory.Build.props would apply and they would stop resembling anything a consumer builds.
#
# Usage: consumer-smoke-test.sh [feed-directory]
#   With no argument the packages are packed fresh. Pass a directory of existing .nupkg files
#   (the release workflow passes its pack output) to test exactly the artifacts being shipped.
#
#   SMOKE_EF_VERSION and SMOKE_NPGSQL_EF_VERSION pin the consumers to one EF Core patch (#133): the
#   provider and Design are added at those versions, and each consumer's deps.json is read to show
#   they took. Set both, since Npgsql EF 10.0.1 and later need EF Core 10.0.4 or later. Unset, the
#   provider comes from the satellite's dependency range (its floor) and Design at the newest 10.x.

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

# The repository's SDK, not whichever is newest on the machine. The consumer projects live outside
# the repository, where its global.json does not reach, so without this copy they would build with
# any SDK the runner image happens to carry — .NET 11's, once that ships — while every other job
# builds with the band global.json pins.
cp "$repo_root/global.json" "$work/global.json"

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

# The release notes nuget.org shows, on the artifacts that ship: the pack job and the release
# workflow pass their own feed. PackagingConventionTests holds what the notes say; this holds that
# every package in the feed carries them, footer last, as src/Directory.Build.targets writes them.
echo "==> Release notes in the packed nuspecs"
for package in EFCore.ComplexIndexes EFCore.ComplexIndexes.PostgreSQL EFCore.ComplexIndexes.SqlServer; do
    footer="Full changelog: https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/v$version/src/$package/CHANGELOG.md</releaseNotes>"
    if unzip -p "$feed/$package.$version.nupkg" "$package.nuspec" | grep -qF "$footer"; then
        echo "  ok: $package carries its changelog section as release notes"
    else
        echo "  FAIL: $package.$version.nupkg has no release notes ending in its changelog link"
        failed=1
    fi
done

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

# Creates a consumer project and installs the package, plus any further packages named after it.
# Program.cs is written by the caller beforehand, at $work/<name>.cs.
#
# Source mapping, not just source ordering: this package's own ids must come from the local feed and
# nowhere else, because these versions also exist on nuget.org and a published one satisfying the
# restore would mean testing something unrelated to the working tree. Everything else (EF Core,
# Npgsql) has to come from nuget.org, so restricting the whole restore to the local feed is not an
# option either — that fails with NU1101 on the transitive dependencies. <clear/> drops any
# machine-level sources.
scaffold_consumer() {
    local name="$1" package="$2" provider="$3" provider_version="$4" app="$work/$1"
    # Packages the consumer needs besides the satellite, such as SQLite's provider for the core.
    local extra=("${@:5}")

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
    # The 10.x line, as for dotnet-ef above. Unversioned, this restores nuget.org's latest, which
    # becomes EF Core 11 the day it ships: a package for net11.0, refused by this net10.0 project.
    if [[ -n "${SMOKE_EF_VERSION:-}" ]]; then
        run dotnet add package "$provider" --version "$provider_version"
        run dotnet add package Microsoft.EntityFrameworkCore.Design --version "$SMOKE_EF_VERSION"
        for extra_package in "${extra[@]+"${extra[@]}"}"; do
            run dotnet add package "$extra_package" --version "$SMOKE_EF_VERSION"
        done
    else
        run dotnet add package Microsoft.EntityFrameworkCore.Design --version "10.*"
        # The same holds for the further packages, all of them EF Core's.
        for extra_package in "${extra[@]+"${extra[@]}"}"; do
            run dotnet add package "$extra_package" --version "10.*"
        done
    fi

    # Built explicitly first: `dotnet ef` reports a compile error only as "Build failed. Use dotnet
    # build to see the errors", which in CI is indistinguishable from the design-time host failing.
    run dotnet build

    # A pin that did not take tests the floor again and passes, so the versions are read back from
    # what the consumer loads.
    if [[ -n "${SMOKE_EF_VERSION:-}" ]]; then
        local npgsql=""
        [[ "$provider" == Npgsql.* ]] && npgsql="$provider_version"
        "$repo_root/test/assert-dependency-versions.sh" "bin/Debug/net10.0/$name.deps.json" "$SMOKE_EF_VERSION" $npgsql
    fi

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

    // The runtime wiring also registers the differ in the context's own service provider, which EF's
    // design-time host copies before applying the .targets-injected registration. Present here so the
    // scaffold below proves that the design-time registration still wins with it in place.
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql("Host=localhost;Database=smoke").UseNpgsqlComplexIndexes();

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

scaffold_consumer postgres EFCore.ComplexIndexes.PostgreSQL \
    Npgsql.EntityFrameworkCore.PostgreSQL "${SMOKE_NPGSQL_EF_VERSION:-}"

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

    // Runtime wiring present for the same reason as in the PostgreSQL consumer.
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlServer("Server=localhost;Database=smoke;Trusted_Connection=True;TrustServerCertificate=True")
                  .UseSqlServerComplexIndexes();

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

scaffold_consumer sqlserver EFCore.ComplexIndexes.SqlServer \
    Microsoft.EntityFrameworkCore.SqlServer "${SMOKE_EF_VERSION:-}"

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

# ── SQLite (core package) ────────────────────────────────────────────────────────────────────────
#
# SQLite rebuilds a table to alter a column, after every other operation, and drops the complex
# indexes on it. UseComplexIndexes() wraps SQLite's SQL generator to create them again after the
# rebuild, and the design-time differ scaffolds that only when it finds the wrapper on the context
# dotnet ef builds. In-process tests construct both halves themselves; here dotnet ef does. The
# environment variables select the model (SMOKE_V2: Nickname optional, a rebuild) and the wiring
# (SMOKE_UNWIRED: no UseComplexIndexes()); dotnet ef runs each command in a new process.

cat > "$work/sqlite.cs" <<'CSHARP'
using EFCore.ComplexIndexes;
using Microsoft.EntityFrameworkCore;

using var db = new ClubContext();
var connection = db.Database.GetDbConnection();
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'members'";
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine("index: " + reader.GetString(0));

public sealed class EmailAddress
{
    public string Value { get; set; } = "";
}

public sealed class Member
{
    public int          Id       { get; set; }
    public string?      Nickname { get; set; }
    public EmailAddress Email    { get; set; } = new();
}

public sealed class ClubContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=club.db");
        if (Environment.GetEnvironmentVariable("SMOKE_UNWIRED") != "1")
            options.UseComplexIndexes();
    }

    protected override void OnModelCreating(ModelBuilder model)
        => model.Entity<Member>(entity =>
        {
            entity.ToTable("members");
            entity.ComplexProperty(m => m.Email);
            entity.HasComplexIndex(m => m.Email.Value, isUnique: true, indexName: "ux_members_email");
            if (Environment.GetEnvironmentVariable("SMOKE_V2") != "1")
                entity.Property(m => m.Nickname).IsRequired();
        });
}
CSHARP

scaffold_consumer sqlite EFCore.ComplexIndexes \
    Microsoft.EntityFrameworkCore.Sqlite "${SMOKE_EF_VERSION:-}" Microsoft.EntityFrameworkCore.Sqlite
run dotnet-ef database update

echo "==> sqlite: a migration that rebuilds the table"
export SMOKE_V2=1
run dotnet-ef migrations add Rebuild
rebuild="$(find "$work/sqlite/Migrations" -name '*_Rebuild.cs' | head -1)"
assert_contains 'CustomIndex:AfterTableRebuild'  "$rebuild" "the create after the rebuild is marked"
assert_contains '__requires_UseComplexIndexes__' "$rebuild" "and carries the marker that fails without the wrapper"

if unwired_output="$(SMOKE_UNWIRED=1 dotnet-ef database update 2>&1)"; then
    echo "  FAIL: the rebuild applied without UseComplexIndexes()"
    failed=1
elif grep -q "no such function: __requires_UseComplexIndexes__" <<<"$unwired_output"; then
    echo "  ok: applied without UseComplexIndexes(), it fails on the marker"
else
    echo "  FAIL: database update failed, but not on the marker:"
    echo "$unwired_output" | tail -5 | sed 's/^/      /'
    failed=1
fi
run dotnet run --no-build
assert_contains 'index: ux_members_email' <(echo "$output") "the failed update left the index in place"

run dotnet-ef database update
run dotnet run --no-build
assert_contains 'index: ux_members_email' <(echo "$output") "the rebuild re-created the complex index"

assert_no_churn sqlite

echo "==> sqlite: without UseComplexIndexes(), the rebuild is refused"
unset SMOKE_V2
if refuse_output="$(SMOKE_UNWIRED=1 dotnet-ef migrations add Back 2>&1)"; then
    echo "  FAIL: a rebuild scaffolded without UseComplexIndexes()"
    failed=1
elif grep -q "SQLite rebuilds table 'members'" <<<"$refuse_output"; then
    echo "  ok: refused at migrations add"
else
    echo "  FAIL: migrations add failed, but not with the rebuild refusal:"
    echo "$refuse_output" | tail -5 | sed 's/^/      /'
    failed=1
fi

[[ $failed -eq 0 ]] || { echo; echo "consumer smoke test FAILED"; exit 1; }
echo
echo "consumer smoke test passed ($version)"
