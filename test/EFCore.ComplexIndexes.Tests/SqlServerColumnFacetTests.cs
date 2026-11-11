using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// EF Core emits an <c>AlterColumnOperation</c> for any column facet that differs, and its SQL Server
/// generator issues <c>ALTER TABLE … ALTER COLUMN</c> only for some of them: a default is a
/// <c>DROP CONSTRAINT</c> plus <c>ADD DEFAULT</c>, a comment an <c>sp_addextendedproperty</c>. The SQL
/// Server differ took every <c>AlterColumnOperation</c> on a column an index reads as in the way
/// (#84, #141), so a default or comment change rebuilt every complex index on the column, offline on
/// every edition but Enterprise (#186). It now asks what EF Core's generator asks.
/// </summary>
[TestClass]
public class SqlServerColumnFacetTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Booking
    {
        public int          Id     { get; set; }
        public string       Code   { get; set; } = "";
        public int          Rooms  { get; set; }
        public string?      Note   { get; set; }
        public string       Upper  { get; set; } = "";
        public EmailAddress Email  { get; set; } = new();
    }

    private abstract class BookingContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual void Vary(EntityTypeBuilder<Booking> b) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings");
                b.Property(x => x.Code).HasMaxLength(20);
                b.Property(x => x.Note).HasMaxLength(100);
                b.Property(x => x.Upper).HasMaxLength(20).HasComputedColumnSql("UPPER([Code])");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(200));

                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, indexName: "ix_bookings_code_email");
                b.HasComplexIndex(x => x.Rooms, indexName: "ix_bookings_rooms");
                b.HasComplexIndex(x => x.Note, indexName: "ix_bookings_note");
                b.HasComplexIndex(x => x.Upper, indexName: "ix_bookings_upper");
                Vary(b);
            });
    }

    private class Before(DbContextOptions<Before> options) : BookingContext(options);

    private class DefaultValue(DbContextOptions<DefaultValue> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Code).HasDefaultValue("none");
    }

    private class DefaultSql(DbContextOptions<DefaultSql> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Rooms).HasDefaultValueSql("1");
    }

    private class Comment(DbContextOptions<Comment> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Code).HasComment("the booking code");
    }

    private class Shorter(DbContextOptions<Shorter> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Code).HasMaxLength(10);
    }

    private class TypeChanged(DbContextOptions<TypeChanged> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Rooms).HasColumnType("bigint");
    }

    private class Required(DbContextOptions<Required> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Note).IsRequired();
    }

    private class ComputedChanged(DbContextOptions<ComputedChanged> options) : BookingContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Booking> b) => b.Property(x => x.Upper).HasComputedColumnSql("LOWER([Code])");
    }

    private static IReadOnlyList<MigrationOperation> Diff<TTarget>() where TTarget : DbContext
        => MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<Before>(), MigrationHarness.SqlServerModel<TTarget>());

    private static string[] Rebuilt(IReadOnlyList<MigrationOperation> operations)
    {
        var dropped = operations.OfType<DropIndexOperation>().Select(o => o.Name).ToArray();
        CollectionAssert.AreEquivalent(dropped, operations.OfType<CreateIndexOperation>().Select(o => o.Name).ToArray());
        return dropped;
    }

    [TestMethod]
    public void A_default_value_change_keeps_the_index()
    {
        var operations = Diff<DefaultValue>();
        Assert.IsTrue(operations.OfType<AlterColumnOperation>().Any());
        CollectionAssert.AreEqual(Array.Empty<string>(), Rebuilt(operations));
    }

    [TestMethod]
    public void A_default_sql_change_keeps_the_index()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Rebuilt(Diff<DefaultSql>()));

    [TestMethod]
    public void A_comment_change_keeps_the_index()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Rebuilt(Diff<Comment>()));

    // What ALTER COLUMN, or a DROP COLUMN, still needs out of the way (#84, #141).

    [TestMethod]
    public void A_length_change_still_rebuilds_the_index()
        => CollectionAssert.AreEqual(new[] { "ix_bookings_code_email" }, Rebuilt(Diff<Shorter>()));

    [TestMethod]
    public void A_type_change_still_rebuilds_the_index()
        => CollectionAssert.AreEqual(new[] { "ix_bookings_rooms" }, Rebuilt(Diff<TypeChanged>()));

    [TestMethod]
    public void A_nullability_change_still_rebuilds_the_index()
        => CollectionAssert.AreEqual(new[] { "ix_bookings_note" }, Rebuilt(Diff<Required>()));

    [TestMethod]
    public void A_computed_column_change_still_rebuilds_the_index()
        => CollectionAssert.AreEqual(new[] { "ix_bookings_upper" }, Rebuilt(Diff<ComputedChanged>()));
}
