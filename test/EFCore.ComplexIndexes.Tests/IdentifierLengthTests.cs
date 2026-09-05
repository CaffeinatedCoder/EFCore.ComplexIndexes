using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The names this package derives are never truncated, unlike EF's own default names. PostgreSQL
/// cuts an identifier past 63 bytes down with a NOTICE and applies the migration cleanly, so the
/// index exists under a name that neither the declaration nor a later constraint-violation error
/// matches — a slice dispatching on the constraint name falls through in silence. The differ now
/// rejects such a name at <c>migrations add</c>, measured the way the provider measures it.
/// </summary>
[TestClass]
public class IdentifierLengthTests
{
    private static readonly string SixtyThree = new('x', 63);
    private static readonly string SixtyFour  = new('x', 64);

    // 32 characters, 64 UTF-8 bytes: past PostgreSQL's limit, well inside SQL Server's.
    private static readonly string Umlauts = new('ä', 32);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id    { get; set; }
        public string       Name  { get; set; } = "";
        public EmailAddress Email { get; set; } = new();
    }

    private class Booking
    {
        public Guid                  Id     { get; set; }
        public Guid                  RoomId { get; set; }
        public NpgsqlRange<DateOnly> Period { get; set; }
    }

    private class NamedIndexContext(DbContextOptions options, string indexName) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: indexName);
            });
    }

    private class ExactLimitContext(DbContextOptions<ExactLimitContext> options)   : NamedIndexContext(options, SixtyThree);
    private class PastLimitContext(DbContextOptions<PastLimitContext> options)     : NamedIndexContext(options, SixtyFour);
    private class UmlautContext(DbContextOptions<UmlautContext> options)           : NamedIndexContext(options, Umlauts);
    private class SqlServerLimitContext(DbContextOptions<SqlServerLimitContext> options) : NamedIndexContext(options, new string('x', 128));
    private class SqlServerPastContext(DbContextOptions<SqlServerPastContext> options)   : NamedIndexContext(options, new string('x', 129));
    private class SqliteContext(DbContextOptions<SqliteContext> options)           : NamedIndexContext(options, new string('x', 200));

    private class UnnamedContext(DbContextOptions<UnnamedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                // IX_ + 60 + _Email_Value: the default name is what runs past the limit.
                b.ToTable(new string('t', 60));
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value);
            });
    }

    private class PropertyLevelContext(DbContextOptions<PropertyLevelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c => c.Property(e => e.Value).HasComplexIndex(indexName: SixtyFour));
            });
    }

    private class ExclusionContext(DbContextOptions<ExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings");
                b.HasKey(x => x.Id);
                b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: SixtyFour);
            });
    }

    private class TemporalContext(DbContextOptions<TemporalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings");
                b.HasKey(x => x.Id);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: SixtyFour);
            });
    }

    [TestMethod(DisplayName = "An explicit index name past PostgreSQL's 63 bytes is rejected at migrations add")]
    public void Npgsql_rejects_a_name_past_the_limit()
    {
        var target = MigrationHarness.NpgsqlModel<PastLimitContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, SixtyFour);
        StringAssert.Contains(exception.Message, "64 bytes");
        StringAssert.Contains(exception.Message, "at most 63");
        StringAssert.Contains(exception.Message, "indexName");
    }

    [TestMethod(DisplayName = "A name of exactly 63 bytes passes")]
    public void Npgsql_accepts_a_name_at_the_limit()
    {
        var target = MigrationHarness.NpgsqlModel<ExactLimitContext>();

        var creates = MigrationHarness.NpgsqlDiff(null, target).OfType<CreateIndexOperation>().ToList();

        Assert.HasCount(1, creates);
        Assert.AreEqual(SixtyThree, creates[0].Name);
    }

    [TestMethod(DisplayName = "PostgreSQL measures bytes: 32 umlauts are 64 bytes")]
    public void Npgsql_measures_utf8_bytes()
    {
        var target = MigrationHarness.NpgsqlModel<UmlautContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "64 bytes");
    }

    [TestMethod(DisplayName = "SQL Server measures characters: 32 umlauts pass, 129 characters do not")]
    public void SqlServer_measures_characters()
    {
        var umlauts = MigrationHarness.SqlServerModel<UmlautContext>();
        Assert.HasCount(1, MigrationHarness.SqlServerDiff(null, umlauts).OfType<CreateIndexOperation>().ToList());

        var atLimit = MigrationHarness.SqlServerModel<SqlServerLimitContext>();
        Assert.HasCount(1, MigrationHarness.SqlServerDiff(null, atLimit).OfType<CreateIndexOperation>().ToList());

        var pastLimit = MigrationHarness.SqlServerModel<SqlServerPastContext>();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.SqlServerDiff(null, pastLimit));

        StringAssert.Contains(exception.Message, "129 characters");
        StringAssert.Contains(exception.Message, "at most 128");
    }

    [TestMethod(DisplayName = "A provider without a limit accepts any length")]
    public void Core_without_a_limit_accepts_any_length()
    {
        var target = MigrationHarness.SqliteModel<SqliteContext>();

        Assert.HasCount(1, MigrationHarness.CoreDiff(null, target).OfType<CreateIndexOperation>().ToList());
    }

    [TestMethod(DisplayName = "A default name that runs past the limit is rejected too")]
    public void Default_name_past_the_limit_is_rejected()
    {
        var target = MigrationHarness.NpgsqlModel<UnnamedContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "IX_" + new string('t', 60) + "_Email_Value");
    }

    [TestMethod(DisplayName = "Property-level declarations are checked as well")]
    public void Property_level_name_is_checked()
    {
        var target = MigrationHarness.NpgsqlModel<PropertyLevelContext>();

        Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));
    }

    [TestMethod(DisplayName = "An exclusion constraint name past the limit is rejected")]
    public void Exclusion_constraint_name_is_checked()
    {
        var target = MigrationHarness.NpgsqlModel<ExclusionContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "exclusion constraint");
        StringAssert.Contains(exception.Message, "bookings");
    }

    [TestMethod(DisplayName = "A temporal constraint name past the limit is rejected")]
    public void Temporal_constraint_name_is_checked()
    {
        var target = MigrationHarness.NpgsqlModel<TemporalContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "temporal constraint");
    }

    // Guards against a future over-correction: the check must not make a snapshot undiffable.
    [TestMethod(DisplayName = "A source model carrying such a name stays diffable")]
    public void Source_model_is_not_validated()
    {
        var source = MigrationHarness.NpgsqlModel<PastLimitContext>();
        var target = MigrationHarness.NpgsqlModel<ExactLimitContext>();

        var operations = MigrationHarness.NpgsqlDiff(source, target);

        // Name-only change → a rename from the over-long name to the fixed one.
        Assert.IsTrue(operations.OfType<RenameIndexOperation>().Any(o => o.Name == SixtyFour && o.NewName == SixtyThree));
    }
}
