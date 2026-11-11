using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── A rename whose old name the target gives to something else (#143) ──

    // Renames ran after the base operations, so EF's CREATE INDEX or ADD CONSTRAINT under the old
    // name ran while the renamed object still held it: 42P07 or 42710 at apply. Each migration below
    // is applied as `database update` applies it, in one transaction.

    private class RenamedAway
    {
        public int                   Id     { get; set; }
        public string                Code   { get; set; } = "";
        public int                   RoomId { get; set; }
        public NpgsqlRange<DateOnly> Period { get; set; }
        public RenamedAwayEmail      Email  { get; set; } = new();
    }

    private class RenamedAwayEmail
    {
        public string Value { get; set; } = "";
    }

    private class RenamedAwayStay
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public NpgsqlRange<DateOnly> During { get; set; }
    }

    private abstract class RenamedAwayContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract string Table { get; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RenamedAway>(b =>
            {
                b.ToTable(Table);
                b.Property(x => x.Code).HasColumnName("code");
                b.Property(x => x.RoomId).HasColumnName("room_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                Declare(b, modelBuilder);
            });

        protected abstract void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder);
    }

    private class IndexRenamedAwayV1Context(DbContextOptions<IndexRenamedAwayV1Context> options) : RenamedAwayContext(options)
    {
        protected override string Table => "renamed_away_index";

        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
            => b.HasComplexIndex(x => x.Email.Value, indexName: "ra_ix_a");
    }

    private class IndexRenamedAwayV2Context(DbContextOptions<IndexRenamedAwayV2Context> options) : RenamedAwayContext(options)
    {
        protected override string Table => "renamed_away_index";

        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
        {
            b.HasComplexIndex(x => x.Email.Value, indexName: "ra_ix_b");
            b.HasIndex(x => x.Code).HasDatabaseName("ra_ix_a");
        }
    }

    [TestMethod(DisplayName = "A native index taking a renamed complex index's old name applies (#143)")]
    public void Native_index_taking_renamed_index_old_name_applies()
    {
        var v1 = BuildRelationalModel<IndexRenamedAwayV1Context>();
        Apply(GetDifferences(null, v1));

        ApplyInTransaction(GetDifferences(v1, BuildRelationalModel<IndexRenamedAwayV2Context>()));

        Assert.AreEqual("email", IndexedColumn("ra_ix_b"));
        Assert.AreEqual("code", IndexedColumn("ra_ix_a"));
    }

    private class ExclusionRenamedAwayV1Context(DbContextOptions<ExclusionRenamedAwayV1Context> options) : RenamedAwayContext(options)
    {
        protected override string Table => "renamed_away_exclusion";

        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
            => b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: "ra_ex_a");
    }

    private class ExclusionRenamedAwayV2Context(DbContextOptions<ExclusionRenamedAwayV2Context> options) : RenamedAwayContext(options)
    {
        protected override string Table => "renamed_away_exclusion";

        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
        {
            b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: "ra_ex_b");
            b.HasAlternateKey(x => x.Code).HasName("ra_ex_a");
        }
    }

    [TestMethod(DisplayName = "A unique constraint taking a renamed exclusion constraint's old name applies (#143)")]
    public void Unique_constraint_taking_renamed_exclusion_old_name_applies()
    {
        var v1 = BuildRelationalModel<ExclusionRenamedAwayV1Context>();
        Apply(GetDifferences(null, v1));

        ApplyInTransaction(GetDifferences(v1, BuildRelationalModel<ExclusionRenamedAwayV2Context>()));

        Assert.AreEqual("x", ConstraintType("renamed_away_exclusion", "ra_ex_b"));
        Assert.AreEqual("u", ConstraintType("renamed_away_exclusion", "ra_ex_a"));
    }

    private abstract class TemporalRenamedAwayContext(DbContextOptions options) : RenamedAwayContext(options)
    {
        protected override string Table => "renamed_away_temporal";

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<RenamedAwayStay>(b =>
            {
                b.ToTable("renamed_away_stays");
                b.Property(x => x.RoomId).HasColumnName("room_id");
                b.Property(x => x.During).HasColumnName("during");
                b.HasTemporalForeignKey<RenamedAwayStay, RenamedAway>(
                    x => x.RoomId, x => x.During, x => x.RoomId, x => x.Period, name: "ra_fk_stays");
            });
        }
    }

    private class TemporalRenamedAwayV1Context(DbContextOptions<TemporalRenamedAwayV1Context> options) : TemporalRenamedAwayContext(options)
    {
        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
            => b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "ra_uq_a");
    }

    private class TemporalRenamedAwayV2Context(DbContextOptions<TemporalRenamedAwayV2Context> options) : TemporalRenamedAwayContext(options)
    {
        protected override void Declare(EntityTypeBuilder<RenamedAway> b, ModelBuilder modelBuilder)
        {
            b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "ra_uq_b");
            b.HasAlternateKey(x => x.Code).HasName("ra_uq_a");
        }
    }

    [TestMethod(DisplayName = "A unique constraint taking a renamed temporal constraint's old name applies, with its temporal foreign key")]
    public void Unique_constraint_taking_renamed_temporal_old_name_applies()
    {
        var v1 = BuildRelationalModel<TemporalRenamedAwayV1Context>();
        Apply(GetDifferences(null, v1));

        ApplyInTransaction(GetDifferences(v1, BuildRelationalModel<TemporalRenamedAwayV2Context>()));

        Assert.AreEqual("u", ConstraintType("renamed_away_temporal", "ra_uq_b"));
        Assert.AreEqual("u", ConstraintType("renamed_away_temporal", "ra_uq_a"));
        Assert.AreEqual("f", ConstraintType("renamed_away_stays", "ra_fk_stays"));
    }

    // ── A column rename keeps every index and constraint on the column (#182) ──

    private class ColumnRenamed
    {
        public int                   Id        { get; set; }
        public string                Code      { get; set; } = "";
        public DateTime?             RevokedAt { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
        public RenamedAwayEmail      Email     { get; set; } = new();
    }

    private abstract class ColumnRenamedContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ColumnRenamed>(b =>
            {
                b.ToTable("column_renamed");
                b.Property(x => x.Code).HasColumnName("code" + suffix);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at" + suffix);
                b.Property(x => x.Period).HasColumnName("period" + suffix);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email" + suffix).HasComplexIndex(isUnique: true));

                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, filter: "{RevokedAt} IS NULL", indexName: "cr_ix_code_email");
                b.HasComplexIndex(x => x.Code, ix => ix.HasName("cr_ix_code_incl").IncludeProperties("Email.Value"));
                b.HasExpressionIndex(x => x.Code.ToLower(), indexName: "cr_ix_code_lower");
                b.HasExclusionConstraint(x => x.Code, x => x.Period, filter: "{RevokedAt} IS NULL", name: "cr_ex_code_period");
                b.HasTemporalConstraint(x => x.Email.Value, x => x.Period, name: "cr_uq_email_period");
            });
    }

    private class ColumnRenamedV1Context(DbContextOptions<ColumnRenamedV1Context> options) : ColumnRenamedContext(options, "");

    private class ColumnRenamedV2Context(DbContextOptions<ColumnRenamedV2Context> options) : ColumnRenamedContext(options, "_v2");

    [TestMethod(DisplayName = "A column rename keeps every complex index and constraint on the column (#182)")]
    public void Column_rename_keeps_indexes_and_constraints()
    {
        var v1 = BuildRelationalModel<ColumnRenamedV1Context>();
        Apply(GetDifferences(null, v1));

        string[] indexes     = ["cr_ix_code_email", "cr_ix_code_incl", "cr_ix_code_lower", "cr_ex_code_period", "cr_uq_email_period"];
        var      before      = indexes.ToDictionary(index => index, RelationOid);
        var      defaultName = RelationOid("IX_column_renamed_email");

        ApplyInTransaction(GetDifferences(v1, BuildRelationalModel<ColumnRenamedV2Context>()));

        // The same relation, not one created again under the same name.
        foreach (var index in indexes)
            Assert.AreEqual(before[index], RelationOid(index), $"'{index}' was rebuilt.");
        Assert.AreEqual(defaultName, RelationOid("IX_column_renamed_email_v2"), "The default-named index was rebuilt, not renamed.");

        StringAssert.Contains(IndexDefinition("cr_ix_code_email"), "WHERE (revoked_at_v2 IS NULL)");
        StringAssert.Contains(IndexDefinition("cr_ix_code_incl"), "INCLUDE (email_v2)");
        StringAssert.Contains(IndexDefinition("cr_ex_code_period"), "USING gist (code_v2, period_v2) WHERE (revoked_at_v2 IS NULL)");
    }

    private static uint? RelationOid(string name)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("SELECT oid FROM pg_class WHERE relname = @name", connection);
        cmd.Parameters.AddWithValue("name", name);
        return (uint?)cmd.ExecuteScalar();
    }

    private static string IndexDefinition(string index)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("SELECT pg_get_indexdef(oid) FROM pg_class WHERE relname = @index", connection);
        cmd.Parameters.AddWithValue("index", index);
        return (string?)cmd.ExecuteScalar() ?? throw new AssertFailedException($"Index '{index}' does not exist.");
    }

    private static string? IndexedColumn(string index)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT a.attname FROM pg_index i
              JOIN pg_class c ON c.oid = i.indexrelid
              JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
             WHERE c.relname = @index
            """, connection);
        cmd.Parameters.AddWithValue("index", index);
        return (string?)cmd.ExecuteScalar();
    }

    private static string? ConstraintType(string table, string constraint)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT contype::text FROM pg_constraint WHERE conrelid = @table::regclass AND conname = @constraint", connection);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("constraint", constraint);
        return (string?)cmd.ExecuteScalar();
    }
}
