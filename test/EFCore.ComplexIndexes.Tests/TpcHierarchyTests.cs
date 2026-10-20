using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A declaration on a TPC type is created on the table of every type derived from it, as EF Core
/// creates its own <c>HasIndex</c> there (#93). It used to land on the declaring type's table only:
/// on a concrete base, a unique index or an exclusion constraint held among the base's rows and not
/// among a derived type's, and the migration applied without a word.
/// </summary>
[TestClass]
public class TpcHierarchyTests
{
    private static readonly string[] Tables = ["bookings", "special_bookings", "vip_bookings"];

    private class Contact
    {
        public string Email { get; set; } = "";
    }

    private class Booking
    {
        public int                   Id      { get; set; }
        public int                   RoomId  { get; set; }
        public string                Code    { get; set; } = "";
        public NpgsqlRange<DateTime> During  { get; set; }
        public Contact               Contact { get; set; } = new();
    }

    private class SpecialBooking : Booking
    {
        // Named like the base's complex member on purpose: resolved on this table, Contact.Email
        // used to come out as "Email", which is this column.
        public string? Email { get; set; }
    }

    private class VipBooking : SpecialBooking
    {
        public int Level { get; set; }
    }

    private class Room
    {
        public int                   Id        { get; set; }
        public int                   RoomId    { get; set; }
        public NpgsqlRange<DateTime> Available { get; set; }
    }

    private class Payment
    {
        public int                   Id      { get; set; }
        public int                   RoomId  { get; set; }
        public NpgsqlRange<DateTime> During  { get; set; }
    }

    // The concrete base 'bookings', with 'special_bookings' below it and 'vip_bookings' below that.
    private abstract class TpcContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual bool MapsContact => false;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Booking>(b =>
            {
                b.UseTpcMappingStrategy();
                b.ToTable("bookings");
                if (!Database.IsNpgsql())
                    b.Ignore(x => x.During);
                if (MapsContact)
                    b.ComplexProperty(x => x.Contact);
                else
                    b.Ignore(x => x.Contact);
                Configure(b);
            });
            modelBuilder.Entity<SpecialBooking>().ToTable("special_bookings");
            modelBuilder.Entity<VipBooking>().ToTable("vip_bookings");
            ConfigureModel(modelBuilder);
        }

        protected virtual void Configure(EntityTypeBuilder<Booking> b) { }

        protected virtual void ConfigureModel(ModelBuilder modelBuilder) { }
    }

    private class UniqueIndexContext(DbContextOptions<UniqueIndexContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, isUnique: true);
    }

    private class CompositeContext(DbContextOptions<CompositeContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
            => b.HasComplexCompositeIndex(x => new { x.RoomId, x.Code }, isUnique: true, filter: "{RoomId} > 0");
    }

    private class ExpressionContext(DbContextOptions<ExpressionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasExpressionIndex(x => x.Code.ToLower(), isUnique: true);
    }

    private class ExclusionContext(DbContextOptions<ExclusionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasExclusionConstraint(x => x.RoomId, x => x.During);
    }

    private class TemporalContext(DbContextOptions<TemporalContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasTemporalConstraint(x => x.RoomId, x => x.During);
    }

    // The dependent side on the TPC base: every table of the hierarchy references the principal.
    private class DependentForeignKeyContext(DbContextOptions options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
            => b.HasTemporalForeignKey<Booking, Room>(x => x.RoomId, x => x.During, r => r.RoomId, r => r.Available);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Room>(r =>
            {
                r.ToTable("rooms");
                r.HasTemporalConstraint(x => x.RoomId, x => x.Available);
            });
    }

    // The principal side on a TPC type whose rows live in two tables.
    private class PrincipalWithDerivedTablesContext(DbContextOptions<PrincipalWithDerivedTablesContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasTemporalConstraint(x => x.RoomId, x => x.During);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Payment>(p =>
            {
                p.ToTable("payments");
                p.HasTemporalForeignKey<Payment, SpecialBooking>(x => x.RoomId, x => x.During, s => s.RoomId, s => s.During);
            });
    }

    // The principal side on the leaf: one table, which carries the base's temporal constraint.
    private class LeafPrincipalContext(DbContextOptions<LeafPrincipalContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasTemporalConstraint(x => x.RoomId, x => x.During);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Payment>(p =>
            {
                p.ToTable("payments");
                p.HasTemporalForeignKey<Payment, VipBooking>(x => x.RoomId, x => x.During, v => v.RoomId, v => v.During);
            });
    }

    private class NamedIndexContext(DbContextOptions<NamedIndexContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ix_booking_code");
    }

    private class NamedExclusionContext(DbContextOptions<NamedExclusionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasExclusionConstraint(x => x.RoomId, x => x.During, name: "ex_room_overlap");
    }

    private class NamedTemporalContext(DbContextOptions<NamedTemporalContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasTemporalConstraint(x => x.RoomId, x => x.During, name: "ak_room_during");
    }

    // #167: two partial indexes and two exclusion constraints over the same parts. They must both be
    // named, since their default names would be one name, so each table's copy needs a name of its own.
    private class PartialSiblingsContext(DbContextOptions options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
        {
            b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{RoomId} > 0", indexName: "ix_code_active");
            b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{RoomId} <= 0", indexName: "ix_code_archived");
            if (Database.IsNpgsql())
            {
                b.HasExclusionConstraint(x => x.RoomId, x => x.During, filter: "{Code} <> 'revoked'", name: "ex_active");
                b.HasExclusionConstraint(x => x.RoomId, x => x.During, filter: "{Code} = 'revoked'", name: "ex_revoked");
            }
        }
    }

    // The same model as a 5.4.1 snapshot holds it: rendering version 2, so the base's table only.
    private class Pre55PartialSiblingsContext(DbContextOptions<Pre55PartialSiblingsContext> options) : PartialSiblingsContext(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.HasAnnotation(ComplexIndexAnnotations.RenderingVersion, 2);
    }

    // An explicit name that fits on the base's table but not followed by a long derived table's name.
    private class LongTableExplicitNameContext(DbContextOptions<LongTableExplicitNameContext> options) : LongTableContext(options, LongConstraintTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
        {
            b.HasComplexIndex(x => x.Code, indexName: "ix_booking_code");
            b.HasExclusionConstraint(x => x.RoomId, x => x.During, name: "ex_room_overlap");
            b.HasTemporalConstraint(x => x.RoomId, x => x.During, name: "ak_room_during");
        }
    }

    // A temporal foreign key from the TPC base with an explicit name: foreign keys own no index, and
    // PostgreSQL keeps their names per table, so the name repeats as EF Core repeats its own.
    private class NamedForeignKeyContext(DbContextOptions<NamedForeignKeyContext> options) : DependentForeignKeyContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
            => b.HasTemporalForeignKey<Booking, Room>(x => x.RoomId, x => x.During, r => r.RoomId, r => r.Available, name: "fk_booking_room");
    }

    // #168: the derived table's name puts the temporal foreign key's default name at 74 bytes.
    private const string LongForeignKeyTable = "corporate_bookings_for_customer_accounts_region_xyz";

    private class LongForeignKeyTableContext(DbContextOptions<LongForeignKeyTableContext> options) : DependentForeignKeyContext(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            base.ConfigureModel(modelBuilder);
            modelBuilder.Entity<SpecialBooking>().ToTable(LongForeignKeyTable);
        }
    }

    // The same model as a 5.4.1 snapshot holds it: a model declaring only constraints carried no
    // rendering version, so the foreign key was on the base's table only.
    private class Pre55LongForeignKeyTableContext(DbContextOptions<Pre55LongForeignKeyTableContext> options) : DependentForeignKeyContext(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            base.ConfigureModel(modelBuilder);
            modelBuilder.Entity<SpecialBooking>().ToTable(LongForeignKeyTable);
            modelBuilder.Model.RemoveAnnotation(ComplexIndexAnnotations.RenderingVersion);
        }
    }

    // Each table in a schema of its own: one explicit name repeats, but never within a schema.
    private class NamedAcrossSchemasContext(DbContextOptions<NamedAcrossSchemasContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ix_booking_code");

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpecialBooking>().ToTable("special_bookings", "special");
            modelBuilder.Entity<VipBooking>().ToTable("vip_bookings", "vip");
        }
    }

    // A leaf declaration named like the base declaration's copy on another table of the schema.
    private class CopyCollisionContext(DbContextOptions<CopyCollisionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<VipBooking>().HasComplexIndex(x => x.Level, indexName: "IX_special_bookings_Code");
    }

    // The same, across kinds: an exclusion constraint named like an index copy.
    private class CrossKindCopyCollisionContext(DbContextOptions<CrossKindCopyCollisionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<VipBooking>().HasExclusionConstraint(x => x.Level, x => x.During, name: "IX_special_bookings_Code");
    }

    // The derived table's name makes the copy's default name 64 bytes long, one over PostgreSQL's limit,
    // so it is cut by exactly one character.
    private class LongDerivedTableContext(DbContextOptions<LongDerivedTableContext> options) : TpcContext(options)
    {
        public const string LongTable = "vip_bookings_with_a_rather_long_table_name_for_its_rows_";

        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code);

        protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<VipBooking>().ToTable(LongTable);
    }

    // #145: the derived table's name puts each copy's default name past PostgreSQL's 63 bytes, and an
    // explicit name would repeat on every table, so the copy takes a name shortened as EF's are.
    private abstract class LongTableContext(DbContextOptions options, string table) : TpcContext(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpecialBooking>().ToTable(table);
            ConfigureLongTable(modelBuilder);
        }

        protected virtual void ConfigureLongTable(ModelBuilder modelBuilder) { }
    }

    // 57 characters: 'IX_…_Code' is 65.
    private const string LongIndexTable = "special_bookings_for_corporate_customer_accounts_xyz_1234";

    // 51 characters: 'EX_…_RoomId_During' and 'AK_…_RoomId_During' are 68.
    private const string LongConstraintTable = "special_bookings_for_corporate_customer_accounts_xy";

    private class LongIndexTableContext(DbContextOptions<LongIndexTableContext> options) : LongTableContext(options, LongIndexTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, isUnique: true);
    }

    private class LongExclusionTableContext(DbContextOptions<LongExclusionTableContext> options) : LongTableContext(options, LongConstraintTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasExclusionConstraint(x => x.RoomId, x => x.During);
    }

    private class LongTemporalTableContext(DbContextOptions<LongTemporalTableContext> options) : LongTableContext(options, LongConstraintTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasTemporalConstraint(x => x.RoomId, x => x.During);
    }

    // Two declarations whose copies agree in their first 62 characters on the long table.
    private class LongTableSharedPrefixContext(DbContextOptions<LongTableSharedPrefixContext> options) : LongTableContext(options, LongIndexTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
        {
            b.HasComplexIndex(x => x.Code);
            b.HasComplexCompositeIndex(x => new { x.Code, x.RoomId });
        }
    }

    // Two derived tables whose names agree in the first 60 characters: cut to 63 bytes, the copies'
    // names would be one name, and PostgreSQL keeps index names unique per schema.
    private const string SiblingPrefix = "bookings_for_corporate_customer_accounts_in_the_region_xyz12";

    private class LongSiblingTablesContext(DbContextOptions<LongSiblingTablesContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
        {
            b.HasComplexIndex(x => x.Code);
            b.HasExclusionConstraint(x => x.RoomId, x => x.During);
        }

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpecialBooking>().ToTable(SiblingPrefix + "_special");
            modelBuilder.Entity<VipBooking>().ToTable(SiblingPrefix + "_vip");
        }
    }

    // EF Core shortens its own name for a native index over (Code, RoomId) on the long table to the
    // same 'IX_…_C~' as the copy of the complex index over Code.
    private class LongTableNativeIndexContext(DbContextOptions<LongTableNativeIndexContext> options) : LongTableContext(options, LongIndexTable)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code);

        protected override void ConfigureLongTable(ModelBuilder modelBuilder)
            => modelBuilder.Entity<SpecialBooking>().HasIndex(x => new { x.Code, x.RoomId });
    }

    // An explicit name over the limit on a TPC type: it stays on the base's table, so a shorter one
    // fixes it, as it does off a TPC hierarchy.
    private class LongExplicitExclusionNameContext(DbContextOptions<LongExplicitExclusionNameContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
            => b.HasExclusionConstraint(x => x.RoomId, x => x.During, name: "ex_" + new string('r', 61));
    }

    // Not on a TPC type, so an explicit name fixes it: the 5.2.0 refusal stands.
    private class LongPlainTableContext(DbContextOptions<LongPlainTableContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Plain>(b =>
            {
                b.ToTable(LongIndexTable);
                b.HasComplexIndex(x => x.Code);
            });
    }

    private class ComplexMemberContext(DbContextOptions<ComplexMemberContext> options) : TpcContext(options)
    {
        protected override bool MapsContact => true;

        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Contact.Email, isUnique: true);
    }

    private class PropertyLevelComplexMemberContext(DbContextOptions<PropertyLevelComplexMemberContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
            => b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Email).HasComplexIndex(isUnique: true));
    }

    private class JsonMemberContext(DbContextOptions<JsonMemberContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b)
        {
            b.ComplexProperty(x => x.Contact, c => c.ToJson());
            b.HasComplexIndex(x => x.Contact.Email, isUnique: true);
        }
    }

    private class FilterOverComplexMemberContext(DbContextOptions<FilterOverComplexMemberContext> options) : TpcContext(options)
    {
        protected override bool MapsContact => true;

        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, filter: "{Contact.Email} <> ''");
    }

    private class Plain
    {
        public int    Id   { get; set; }
        public string Code { get; set; } = "";
    }

    private class Special : Plain
    {
        public string Note { get; set; } = "";
    }

    private class TphContext(DbContextOptions<TphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Plain>(b =>
            {
                b.ToTable("plains");
                b.HasComplexIndex(x => x.Code, isUnique: true);
            });
            modelBuilder.Entity<Special>();
        }
    }

    // ── Helpers ──

    private static IRelationalModel Model<TContext>(string provider) where TContext : DbContext
        => provider switch
           {
               "Npgsql"    => MigrationHarness.NpgsqlModel<TContext>(),
               "SqlServer" => MigrationHarness.SqlServerModel<TContext>(),
               _           => MigrationHarness.SqliteModel<TContext>()
           };

    private static IReadOnlyList<MigrationOperation> Diff(string provider, IRelationalModel? source, IRelationalModel target)
        => provider switch
           {
               "Npgsql"    => MigrationHarness.NpgsqlDiff(source, target),
               "SqlServer" => MigrationHarness.SqlServerDiff(source, target),
               _           => MigrationHarness.CoreDiff(source, target)
           };

    private static List<CreateIndexOperation> Creates(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<CreateIndexOperation>().OrderBy(o => o.Table, StringComparer.Ordinal)];

    private static List<string> Sql(IEnumerable<MigrationOperation> operations, string marker)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains(marker))];

    // ── Every kind lands on every table ──

    [TestMethod(DisplayName = "A complex index on a concrete TPC base is created on every table of the hierarchy")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    [DataRow("SqlServer")]
    public void Complex_index_lands_on_every_table(string provider)
    {
        var creates = Creates(Diff(provider, null, Model<UniqueIndexContext>(provider)));

        CollectionAssert.AreEqual(Tables, creates.Select(c => c.Table).ToArray());
        CollectionAssert.AreEqual(Tables.Select(t => $"IX_{t}_Code").ToArray(), creates.Select(c => c.Name).ToArray());
        Assert.IsTrue(creates.All(c => c.IsUnique && c.Columns.SequenceEqual(["Code"])));
    }

    [TestMethod(DisplayName = "A composite index and its placeholder filter are resolved on each table")]
    public void Composite_index_lands_on_every_table()
    {
        var creates = Creates(Diff("Npgsql", null, Model<CompositeContext>("Npgsql")));

        CollectionAssert.AreEqual(Tables, creates.Select(c => c.Table).ToArray());
        Assert.IsTrue(creates.All(c => c.Columns.SequenceEqual(["RoomId", "Code"]) && c.Filter == "\"RoomId\" > 0"));
    }

    [TestMethod(DisplayName = "An expression index on a concrete TPC base is created on every table of the hierarchy")]
    public void Expression_index_lands_on_every_table()
    {
        var operations = Diff("Npgsql", null, Model<ExpressionContext>("Npgsql"));
        var sql        = MigrationHarness.NpgsqlSql(Creates(operations));

        foreach (var table in Tables)
            StringAssert.Contains(sql, $"CREATE UNIQUE INDEX \"IX_{table}_lowerCode\" ON {table} ((lower(\"Code\")))");
    }

    [TestMethod(DisplayName = "An exclusion constraint on a concrete TPC base is added to every table of the hierarchy")]
    public void Exclusion_constraint_lands_on_every_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<ExclusionContext>("Npgsql")), "EXCLUDE USING gist");

        Assert.HasCount(3, adds);
        foreach (var table in Tables)
            Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"EX_{table}_RoomId_During\" EXCLUDE")), table);
    }

    [TestMethod(DisplayName = "A temporal constraint on a concrete TPC base is added to every table of the hierarchy")]
    public void Temporal_constraint_lands_on_every_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<TemporalContext>("Npgsql")), "WITHOUT OVERLAPS");

        Assert.HasCount(3, adds);
        foreach (var table in Tables)
            Assert.IsTrue(adds.Any(s => s.Contains($"\"{table}\"") && s.Contains($"\"AK_{table}_RoomId_During\"")), table);
    }

    [TestMethod(DisplayName = "A temporal foreign key from a concrete TPC base is added to every table of the hierarchy")]
    public void Temporal_foreign_key_lands_on_every_dependent_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<DependentForeignKeyContext>("Npgsql")), "FOREIGN KEY");

        Assert.HasCount(3, adds);
        foreach (var table in Tables)
            Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{table}\"") && s.Contains("REFERENCES \"rooms\"")), table);
    }

    [TestMethod(DisplayName = "A temporal foreign key to a TPC type whose rows live in several tables fails")]
    public void Temporal_foreign_key_to_principal_with_derived_tables_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Diff("Npgsql", null, Model<PrincipalWithDerivedTablesContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "'special_bookings', 'vip_bookings'");
        StringAssert.Contains(exception.Message, "A foreign key references one table");
    }

    [TestMethod(DisplayName = "A temporal foreign key to a TPC leaf references its table and the base's constraint there")]
    public void Temporal_foreign_key_to_leaf_references_its_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<LeafPrincipalContext>("Npgsql")), "FOREIGN KEY");

        Assert.HasCount(1, adds);
        StringAssert.Contains(adds[0], "REFERENCES \"vip_bookings\"");
    }

    [TestMethod(DisplayName = "Under TPH the base's declaration is created once, on the shared table")]
    public void Tph_creates_one_index()
    {
        var creates = Creates(Diff("Sqlite", null, Model<TphContext>("Sqlite")));

        Assert.HasCount(1, creates);
        Assert.AreEqual("plains", creates[0].Table);
    }

    // ── Names ──

    [TestMethod(DisplayName = "An explicit index name stays on the TPC base's table and is followed by the table elsewhere, where names are per schema")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    public void Explicit_index_name_on_tpc_base_takes_the_table_on_derived_tables(string provider)
    {
        var creates = Creates(Diff(provider, null, Model<NamedIndexContext>(provider)));

        CollectionAssert.AreEqual(Tables, creates.Select(c => c.Table).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ix_booking_code", "ix_booking_code_special_bookings", "ix_booking_code_vip_bookings" },
            creates.Select(c => c.Name).ToArray());
    }

    [TestMethod(DisplayName = "On SQL Server, where index names are per table, an explicit name repeats on every table as EF's does")]
    public void Explicit_index_name_on_tpc_base_is_per_table_on_sql_server()
    {
        var creates = Creates(Diff("SqlServer", null, Model<NamedIndexContext>("SqlServer")));

        CollectionAssert.AreEqual(Tables, creates.Select(c => c.Table).ToArray());
        Assert.IsTrue(creates.All(c => c.Name == "ix_booking_code"));
    }

    [TestMethod(DisplayName = "An explicit index name on a TPC base takes each derived table's name in other schemas too")]
    public void Explicit_index_name_across_schemas_takes_the_table()
    {
        var creates = Creates(Diff("Npgsql", null, Model<NamedAcrossSchemasContext>("Npgsql")));

        CollectionAssert.AreEquivalent(
            new[] { "ix_booking_code", "ix_booking_code_special_bookings", "ix_booking_code_vip_bookings" },
            creates.Select(c => c.Name).ToArray());
        CollectionAssert.AreEquivalent(new string?[] { null, "special", "vip" }, creates.Select(c => c.Schema).ToArray());
    }

    [TestMethod(DisplayName = "An explicit exclusion constraint name stays on the TPC base's table and is followed by the table elsewhere")]
    public void Explicit_exclusion_name_on_tpc_base_takes_the_table_on_derived_tables()
    {
        var adds = Sql(Diff("Npgsql", null, Model<NamedExclusionContext>("Npgsql")), "EXCLUDE USING gist");

        Assert.HasCount(3, adds);
        Assert.IsTrue(adds.Any(s => s.Contains("ALTER TABLE \"bookings\" ADD CONSTRAINT \"ex_room_overlap\" EXCLUDE")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains("ALTER TABLE \"special_bookings\" ADD CONSTRAINT \"ex_room_overlap_special_bookings\" EXCLUDE")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains("ALTER TABLE \"vip_bookings\" ADD CONSTRAINT \"ex_room_overlap_vip_bookings\" EXCLUDE")), string.Join("\n", adds));
    }

    [TestMethod(DisplayName = "An explicit temporal constraint name stays on the TPC base's table and is followed by the table elsewhere")]
    public void Explicit_temporal_name_on_tpc_base_takes_the_table_on_derived_tables()
    {
        var adds = Sql(Diff("Npgsql", null, Model<NamedTemporalContext>("Npgsql")), "WITHOUT OVERLAPS");

        Assert.HasCount(3, adds);
        foreach (var (table, name) in new[] { ("bookings", "ak_room_during"), ("special_bookings", "ak_room_during_special_bookings"), ("vip_bookings", "ak_room_during_vip_bookings") })
            Assert.IsTrue(adds.Any(s => s.Contains($"\"{table}\"") && s.Contains($"\"{name}\"")), $"{table}: {string.Join("\n", adds)}");
    }

    [TestMethod(DisplayName = "An explicit temporal foreign key name repeats on every dependent table, as EF Core's does")]
    public void Explicit_foreign_key_name_on_tpc_base_repeats_on_every_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<NamedForeignKeyContext>("Npgsql")), "FOREIGN KEY");

        Assert.HasCount(3, adds);
        foreach (var table in Tables)
            Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"fk_booking_room\" FOREIGN KEY")), $"{table}: {string.Join("\n", adds)}");
    }

    // ── #167: partial siblings over the same parts ──

    [TestMethod(DisplayName = "Two named partial indexes over the same parts on a TPC base scaffold, a name per table (#167)")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    public void Partial_index_siblings_on_tpc_base_take_a_name_per_table(string provider)
    {
        var creates = Creates(Diff(provider, null, Model<PartialSiblingsContext>(provider)));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "bookings/ix_code_active", "bookings/ix_code_archived",
                "special_bookings/ix_code_active_special_bookings", "special_bookings/ix_code_archived_special_bookings",
                "vip_bookings/ix_code_active_vip_bookings", "vip_bookings/ix_code_archived_vip_bookings"
            },
            creates.Select(c => $"{c.Table}/{c.Name}").ToArray());
        Assert.HasCount(3, creates.Where(c => c.Filter == "\"RoomId\" > 0"));
        Assert.HasCount(3, creates.Where(c => c.Filter == "\"RoomId\" <= 0"));
    }

    [TestMethod(DisplayName = "The named partial siblings apply on SQLite, and each derived table enforces both under its own names (#167)")]
    public void Partial_index_siblings_apply_on_sqlite()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var target = MigrationHarness.SqliteModel<PartialSiblingsContext>(connection);
        using (var context = new MigrationHarness.EmptyContext(MigrationHarness.SqliteOptions(connection)))
        {
            var commands = context.GetService<IMigrationsSqlGenerator>().Generate(MigrationHarness.CoreDiff(connection, null, target), target.Model);
            context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
        }

        Execute("INSERT INTO vip_bookings (Id, RoomId, Code, Level) VALUES (1, 5, 'a', 1), (2, -1, 'b', 1)");

        var active = Assert.ThrowsExactly<SqliteException>(() => Execute("INSERT INTO vip_bookings (Id, RoomId, Code, Level) VALUES (3, 6, 'a', 1)"));
        StringAssert.Contains(active.Message, "UNIQUE constraint failed");
        var archived = Assert.ThrowsExactly<SqliteException>(() => Execute("INSERT INTO vip_bookings (Id, RoomId, Code, Level) VALUES (4, -2, 'b', 1)"));
        StringAssert.Contains(archived.Message, "UNIQUE constraint failed");

        // Each filter covers its own rows: an active and an archived row may share a code.
        Execute("INSERT INTO vip_bookings (Id, RoomId, Code, Level) VALUES (5, -3, 'a', 1)");

        using var names = connection.CreateCommand();
        names.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'vip_bookings' AND sql IS NOT NULL ORDER BY name)";
        Assert.AreEqual("ix_code_active_vip_bookings,ix_code_archived_vip_bookings", names.ExecuteScalar());

        void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [TestMethod(DisplayName = "On SQL Server the two named partial indexes repeat their names on every table, as EF's do (#167)")]
    public void Partial_index_siblings_on_tpc_base_repeat_their_names_on_sql_server()
    {
        var creates = Creates(Diff("SqlServer", null, Model<PartialSiblingsContext>("SqlServer")));

        Assert.HasCount(6, creates);
        foreach (var table in Tables)
            CollectionAssert.AreEquivalent(new[] { "ix_code_active", "ix_code_archived" }, creates.Where(c => c.Table == table).Select(c => c.Name).ToArray());
    }

    [TestMethod(DisplayName = "Two named exclusion constraints over the same parts on a TPC base scaffold, a name per table (#167)")]
    public void Exclusion_siblings_on_tpc_base_take_a_name_per_table()
    {
        var adds = Sql(Diff("Npgsql", null, Model<PartialSiblingsContext>("Npgsql")), "EXCLUDE USING gist");

        Assert.HasCount(6, adds);
        foreach (var (table, suffix) in new[] { ("bookings", ""), ("special_bookings", "_special_bookings"), ("vip_bookings", "_vip_bookings") })
        {
            Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"ex_active{suffix}\" EXCLUDE") && s.Contains("<> 'revoked'")), $"{table}: {string.Join("\n", adds)}");
            Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"ex_revoked{suffix}\" EXCLUDE") && s.Contains("= 'revoked'")), $"{table}: {string.Join("\n", adds)}");
        }
    }

    [TestMethod(DisplayName = "Upgrading the named partial siblings from a 5.4.1 snapshot keeps the base's names and creates the copies (#167)")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    [DataRow("SqlServer")]
    public void Partial_siblings_from_a_pre_55_snapshot_create_only_the_copies(string provider)
    {
        var operations = Diff(provider, Model<Pre55PartialSiblingsContext>(provider), Model<PartialSiblingsContext>(provider));

        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or RenameIndexOperation or DropTableOperation));
        var creates = Creates(operations);
        Assert.HasCount(4, creates);
        Assert.IsTrue(creates.All(c => c.Table != "bookings"));

        if (provider == "Npgsql")
        {
            var adds = Sql(operations, "EXCLUDE USING gist");
            Assert.HasCount(4, adds);
            Assert.IsTrue(adds.All(s => !s.Contains("ALTER TABLE \"bookings\"")), string.Join("\n", adds));
        }

        Assert.IsEmpty(Diff(provider, Model<PartialSiblingsContext>(provider), Model<PartialSiblingsContext>(provider)));
    }

    [TestMethod(DisplayName = "A declaration named like the copy of a base declaration on another table collides with it")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    public void Name_of_a_copy_collides(string provider)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff(provider, null, Model<CopyCollisionContext>(provider)));

        StringAssert.Contains(exception.Message, "Two complex indexes both resolve to the name 'IX_special_bookings_Code'");
    }

    [TestMethod(DisplayName = "A constraint named like the copy of a base index collides with it across kinds")]
    public void Name_of_a_copy_collides_across_kinds()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<CrossKindCopyCollisionContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "'IX_special_bookings_Code'");
        StringAssert.Contains(exception.Message, "42P07");
    }

    [TestMethod(DisplayName = "A copy's default name one byte over the limit is cut to 63 bytes and marked '~', as EF Core's are")]
    public void Copy_name_one_over_the_limit_is_shortened()
    {
        var creates = Creates(Diff("Npgsql", null, Model<LongDerivedTableContext>("Npgsql")));

        var name = creates.Single(c => c.Table == LongDerivedTableContext.LongTable).Name;
        Assert.AreEqual($"IX_{LongDerivedTableContext.LongTable}_Co~", name);
        Assert.AreEqual(63, name.Length);
    }

    [TestMethod(DisplayName = "A complex index on a TPC base with a long derived table scaffolds, its copy named as EF Core names its own (#145)")]
    public void Long_derived_table_index_copy_is_shortened()
    {
        var creates = Creates(Diff("Npgsql", null, Model<LongIndexTableContext>("Npgsql")));

        CollectionAssert.AreEqual(
            new[] { "IX_bookings_Code", $"IX_{LongIndexTable}_C~", "IX_vip_bookings_Code" },
            creates.Select(c => c.Name).ToArray());
        Assert.AreEqual(LongIndexTable, creates[1].Table);
        Assert.IsTrue(creates.All(c => c.IsUnique && c.Columns.SequenceEqual(["Code"])));
    }

    [TestMethod(DisplayName = "An exclusion constraint on a TPC base with a long derived table scaffolds, its copy's name shortened (#145)")]
    public void Long_derived_table_exclusion_copy_is_shortened()
    {
        var adds = Sql(Diff("Npgsql", null, Model<LongExclusionTableContext>("Npgsql")), "EXCLUDE USING gist");

        Assert.HasCount(3, adds);
        Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{LongConstraintTable}\" ADD CONSTRAINT \"EX_{LongConstraintTable}_RoomId_~\" EXCLUDE")),
                      string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains("ADD CONSTRAINT \"EX_bookings_RoomId_During\"")));
    }

    [TestMethod(DisplayName = "A temporal constraint on a TPC base with a long derived table scaffolds, its copy's name shortened (#145)")]
    public void Long_derived_table_temporal_copy_is_shortened()
    {
        var adds = Sql(Diff("Npgsql", null, Model<LongTemporalTableContext>("Npgsql")), "WITHOUT OVERLAPS");

        Assert.HasCount(3, adds);
        Assert.IsTrue(adds.Any(s => s.Contains($"\"AK_{LongConstraintTable}_RoomId_~\"")), string.Join("\n", adds));
    }

    [TestMethod(DisplayName = "Two copies that agree in their first 62 bytes are told apart by '~' and '~1', as EF Core's are")]
    public void Long_copies_sharing_a_prefix_are_uniquified()
    {
        var names = Creates(Diff("Npgsql", null, Model<LongTableSharedPrefixContext>("Npgsql")))
                   .Where(c => c.Table == LongIndexTable)
                   .ToDictionary(c => string.Join(",", c.Columns), c => c.Name);

        Assert.AreEqual($"IX_{LongIndexTable}_C~", names["Code"]);
        Assert.AreEqual($"IX_{LongIndexTable}_~1", names["Code,RoomId"]);
    }

    [TestMethod(DisplayName = "Copies on two long sibling tables that would shorten alike take '~' and '~1' within the schema")]
    public void Long_sibling_table_copies_are_uniquified_across_tables()
    {
        Assert.AreEqual(60, SiblingPrefix.Length, "The premise: the tables differ only past the cut.");
        var operations = Diff("Npgsql", null, Model<LongSiblingTablesContext>("Npgsql"));

        var indexes = Creates(operations).Where(c => c.Table.StartsWith(SiblingPrefix, StringComparison.Ordinal))
                                         .ToDictionary(c => c.Table, c => c.Name);
        Assert.AreEqual($"IX_{SiblingPrefix[..59]}~", indexes[SiblingPrefix + "_special"]);
        Assert.AreEqual($"IX_{SiblingPrefix[..58]}~1", indexes[SiblingPrefix + "_vip"]);

        var constraints = Sql(operations, "EXCLUDE USING gist");
        Assert.IsTrue(constraints.Any(c => c.Contains($"\"EX_{SiblingPrefix[..59]}~\"")), string.Join("\n", constraints));
        Assert.IsTrue(constraints.Any(c => c.Contains($"\"EX_{SiblingPrefix[..58]}~1\"")), string.Join("\n", constraints));

        Assert.IsEmpty(Diff("Npgsql", Model<LongSiblingTablesContext>("Npgsql"), Model<LongSiblingTablesContext>("Npgsql")));
    }

    [TestMethod(DisplayName = "A shortened copy name steps aside for the name EF Core shortened its own index to")]
    public void Long_copy_avoids_native_shortened_name()
    {
        var target = Model<LongTableNativeIndexContext>("Npgsql");
        var native = target.Tables.Single(t => t.Name == LongIndexTable).Indexes.Single().Name;
        Assert.AreEqual($"IX_{LongIndexTable}_C~", native, "The premise: EF Core shortens its own name this way.");

        var copy = Creates(Diff("Npgsql", null, target)).Single(c => c.Table == LongIndexTable && c.Columns.SequenceEqual(["Code"]));
        Assert.AreEqual($"IX_{LongIndexTable}_~1", copy.Name);
    }

    [TestMethod(DisplayName = "Shortened copy names compare equal on both sides of a diff, so they never churn")]
    public void Long_copies_do_not_churn()
    {
        Assert.IsEmpty(Diff("Npgsql", Model<LongIndexTableContext>("Npgsql"), Model<LongIndexTableContext>("Npgsql")));
        Assert.IsEmpty(Diff("Npgsql", Model<LongExclusionTableContext>("Npgsql"), Model<LongExclusionTableContext>("Npgsql")));
        Assert.IsEmpty(Diff("Npgsql", Model<LongTemporalTableContext>("Npgsql"), Model<LongTemporalTableContext>("Npgsql")));
    }

    [TestMethod(DisplayName = "An explicit name over the limit on a TPC type is refused with advice to shorten it, which now helps")]
    public void Long_explicit_name_on_tpc_type_advises_a_shorter_one()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<LongExplicitExclusionNameContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "64 bytes long");
        StringAssert.Contains(exception.Message, "Give the declaration an explicit, shorter name (name)");
    }

    [TestMethod(DisplayName = "An explicit name followed by a long derived table's name is shortened there as EF Core shortens its own")]
    public void Explicit_name_on_long_derived_table_is_shortened()
    {
        var operations = Diff("Npgsql", null, Model<LongTableExplicitNameContext>("Npgsql"));

        var index = Creates(operations).Single(c => c.Table == LongConstraintTable).Name;
        Assert.AreEqual(("ix_booking_code_" + LongConstraintTable)[..62] + "~", index);

        var sql = Sql(operations, "ADD CONSTRAINT");
        Assert.IsTrue(sql.Any(s => s.Contains($"\"{("ex_room_overlap_" + LongConstraintTable)[..62]}~\" EXCLUDE")), string.Join("\n", sql));
        Assert.IsTrue(sql.Any(s => s.Contains($"\"{("ak_room_during_" + LongConstraintTable)[..62]}~\"")), string.Join("\n", sql));
        Assert.IsTrue(sql.Any(s => s.Contains("\"ex_room_overlap\" EXCLUDE")), "The base's table keeps the name.");

        Assert.IsEmpty(Diff("Npgsql", Model<LongTableExplicitNameContext>("Npgsql"), Model<LongTableExplicitNameContext>("Npgsql")));
    }

    // ── #168: a temporal foreign key's copy on a long derived table ──

    [TestMethod(DisplayName = "A temporal foreign key's copy on a long derived table is shortened as EF Core shortens its own (#168)")]
    public void Long_derived_table_foreign_key_copy_is_shortened()
    {
        const string full = $"FK_{LongForeignKeyTable}_rooms_RoomId_During";
        Assert.AreEqual(74, full.Length, "The premise: the copy's default name runs 11 bytes past the limit.");

        var adds = Sql(Diff("Npgsql", null, Model<LongForeignKeyTableContext>("Npgsql")), "FOREIGN KEY");

        Assert.HasCount(3, adds);
        Assert.IsTrue(adds.Any(s => s.Contains($"ALTER TABLE \"{LongForeignKeyTable}\" ADD CONSTRAINT \"{full[..62]}~\" FOREIGN KEY")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains("ADD CONSTRAINT \"FK_bookings_rooms_RoomId_During\"")), string.Join("\n", adds));

        Assert.IsEmpty(Diff("Npgsql", Model<LongForeignKeyTableContext>("Npgsql"), Model<LongForeignKeyTableContext>("Npgsql")));
    }

    [TestMethod(DisplayName = "Upgrading the long-table temporal foreign key from a 5.4.1 snapshot adds the shortened copies (#168)")]
    public void Long_derived_table_foreign_key_from_a_pre_55_snapshot_adds_the_copies()
    {
        var operations = Diff("Npgsql", Model<Pre55LongForeignKeyTableContext>("Npgsql"), Model<LongForeignKeyTableContext>("Npgsql"));

        Assert.IsFalse(operations.Any(o => o is DropForeignKeyOperation or DropTableOperation or DropUniqueConstraintOperation));
        var adds = Sql(operations, "FOREIGN KEY");
        Assert.HasCount(2, adds);
        Assert.IsTrue(adds.All(s => !s.Contains("ALTER TABLE \"bookings\"")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains($"\"{$"FK_{LongForeignKeyTable}_rooms_RoomId_During"[..62]}~\"")), string.Join("\n", adds));
    }

    [TestMethod(DisplayName = "A default name over the limit off a TPC hierarchy is still refused, since an explicit name fixes it")]
    public void Long_default_name_off_tpc_still_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<LongPlainTableContext>("Npgsql")));

        StringAssert.Contains(exception.Message, $"'IX_{LongIndexTable}_Code'");
        StringAssert.Contains(exception.Message, "Give the declaration an explicit, shorter name (indexName)");
    }

    // ── Members EF Core 10 leaves out of a derived table ──

    [TestMethod(DisplayName = "A complex index over a complex member of a TPC base fails: derived tables have no column for it")]
    [DataRow("Sqlite")]
    [DataRow("SqlServer")]
    public void Complex_member_on_tpc_base_throws(string provider)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff(provider, null, Model<ComplexMemberContext>(provider)));

        StringAssert.Contains(exception.Message, "declares a complex index over 'Contact.Email'");
        StringAssert.Contains(exception.Message, "EF Core maps no column for 'Contact.Email' in 'special_bookings'");
    }

    [TestMethod(DisplayName = "A property-level complex index on a TPC base's complex member fails the same way")]
    public void Property_level_complex_member_on_tpc_base_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Sqlite", null, Model<PropertyLevelComplexMemberContext>("Sqlite")));

        StringAssert.Contains(exception.Message, "EF Core maps no column for 'Contact.Email' in 'special_bookings'");
    }

    [TestMethod(DisplayName = "A JSON member of a TPC base fails: derived tables have no JSON column for it")]
    public void Json_member_on_tpc_base_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<JsonMemberContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "EF Core maps no column for 'Contact.Email' in 'special_bookings'");
    }

    [TestMethod(DisplayName = "A filter placeholder over a complex member of a TPC base fails the same way")]
    public void Filter_over_complex_member_on_tpc_base_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<FilterOverComplexMemberContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "over 'Contact.Email'");
    }

    // ── Existing databases and snapshots ──

    [TestMethod(DisplayName = "Diffing the model against itself is empty: the copies compare equal on both sides")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    [DataRow("SqlServer")]
    public void Copies_do_not_churn(string provider)
    {
        var target = Model<UniqueIndexContext>(provider);

        Assert.IsEmpty(Diff(provider, Model<UniqueIndexContext>(provider), target));
    }

    // A snapshot scaffolded before 5.5.0 carries rendering version 2 at most, and none when the model
    // declares only constraints. Resolved by the old rule, it holds the base table's declarations
    // only, so the first migration after upgrading creates exactly the derived tables' copies.
    private class Pre55IndexContext(DbContextOptions<Pre55IndexContext> options) : UniqueIndexContextBase(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.HasAnnotation(ComplexIndexAnnotations.RenderingVersion, 2);
    }

    private class Pre55ExclusionContext(DbContextOptions<Pre55ExclusionContext> options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasExclusionConstraint(x => x.RoomId, x => x.During);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Model.RemoveAnnotation(ComplexIndexAnnotations.RenderingVersion);
    }

    private abstract class UniqueIndexContextBase(DbContextOptions options) : TpcContext(options)
    {
        protected override void Configure(EntityTypeBuilder<Booking> b) => b.HasComplexIndex(x => x.Code, isUnique: true);
    }

    [TestMethod(DisplayName = "Upgrading from a pre-5.5.0 snapshot creates the derived tables' indexes and nothing else")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    [DataRow("SqlServer")]
    public void Pre_55_snapshot_creates_the_derived_copies(string provider)
    {
        var operations = Diff(provider, Model<Pre55IndexContext>(provider), Model<UniqueIndexContext>(provider));

        Assert.IsFalse(operations.OfType<DropIndexOperation>().Any());
        CollectionAssert.AreEqual(new[] { "special_bookings", "vip_bookings" }, Creates(operations).Select(c => c.Table).ToArray());
    }

    [TestMethod(DisplayName = "Upgrading from a pre-5.5.0 snapshot adds the derived tables' exclusion constraints and nothing else")]
    public void Pre_55_snapshot_adds_the_derived_exclusion_constraints()
    {
        var operations = Diff("Npgsql", Model<Pre55ExclusionContext>("Npgsql"), Model<ExclusionContext>("Npgsql"));

        var adds = Sql(operations, "EXCLUDE USING gist");
        Assert.HasCount(2, adds);
        Assert.IsTrue(adds.All(s => !s.Contains("ALTER TABLE \"bookings\"")));
        Assert.IsFalse(operations.OfType<DropTableOperation>().Any() || operations.OfType<DropUniqueConstraintOperation>().Any());
    }

    [TestMethod(DisplayName = "Exclusion and temporal declarations mark the model's rendering version")]
    public void Constraint_declarations_mark_the_rendering_version()
    {
        Assert.AreEqual(ComplexIndexStorage.CurrentRenderingVersion, Model<ExclusionContext>("Npgsql").Model[ComplexIndexAnnotations.RenderingVersion]);
        Assert.AreEqual(ComplexIndexStorage.CurrentRenderingVersion, Model<TemporalContext>("Npgsql").Model[ComplexIndexAnnotations.RenderingVersion]);
        Assert.AreEqual(ComplexIndexStorage.CurrentRenderingVersion, Model<DependentForeignKeyContext>("Npgsql").Model[ComplexIndexAnnotations.RenderingVersion]);
    }
}
