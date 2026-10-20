using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
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
    private class DependentForeignKeyContext(DbContextOptions<DependentForeignKeyContext> options) : TpcContext(options)
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

    // An explicit name over the limit on a TPC type, all of whose tables share a schema: a shorter
    // explicit name would repeat on every table, which the name checks refuse.
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

    [TestMethod(DisplayName = "An explicit index name repeated on every table of a TPC hierarchy fails where names are per schema")]
    [DataRow("Sqlite")]
    [DataRow("Npgsql")]
    public void Explicit_index_name_on_tpc_base_throws(string provider)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff(provider, null, Model<NamedIndexContext>(provider)));

        StringAssert.Contains(exception.Message, "The complex index 'ix_booking_code' is declared on 'Booking', a TPC type");
        StringAssert.Contains(exception.Message, "'bookings', 'special_bookings', 'vip_bookings'");
        StringAssert.Contains(exception.Message, "Leave the name out");
    }

    [TestMethod(DisplayName = "On SQL Server, where index names are per table, an explicit name repeats on every table as EF's does")]
    public void Explicit_index_name_on_tpc_base_is_per_table_on_sql_server()
    {
        var creates = Creates(Diff("SqlServer", null, Model<NamedIndexContext>("SqlServer")));

        CollectionAssert.AreEqual(Tables, creates.Select(c => c.Table).ToArray());
        Assert.IsTrue(creates.All(c => c.Name == "ix_booking_code"));
    }

    [TestMethod(DisplayName = "An explicit index name on a TPC base is accepted when every table is in a schema of its own")]
    public void Explicit_index_name_across_schemas_is_accepted()
    {
        var creates = Creates(Diff("Npgsql", null, Model<NamedAcrossSchemasContext>("Npgsql")));

        Assert.HasCount(3, creates);
        CollectionAssert.AreEquivalent(new string?[] { null, "special", "vip" }, creates.Select(c => c.Schema).ToArray());
    }

    [TestMethod(DisplayName = "An explicit exclusion constraint name repeated on every table of a TPC hierarchy fails")]
    public void Explicit_exclusion_name_on_tpc_base_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<NamedExclusionContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "The exclusion constraint 'ex_room_overlap' is declared on 'Booking', a TPC type");
        StringAssert.Contains(exception.Message, "42P07");
    }

    [TestMethod(DisplayName = "An explicit temporal constraint name repeated on every table of a TPC hierarchy fails")]
    public void Explicit_temporal_name_on_tpc_base_throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<NamedTemporalContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "The temporal constraint 'ak_room_during' is declared on 'Booking', a TPC type");
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

    [TestMethod(DisplayName = "An explicit name over the limit on a TPC type is refused with advice to leave it out, not to shorten it")]
    public void Long_explicit_name_on_tpc_type_advises_leaving_it_out()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Diff("Npgsql", null, Model<LongExplicitExclusionNameContext>("Npgsql")));

        StringAssert.Contains(exception.Message, "64 bytes long");
        StringAssert.Contains(exception.Message, "Leave the name out");
        Assert.DoesNotContain("explicit, shorter name", exception.Message);
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
