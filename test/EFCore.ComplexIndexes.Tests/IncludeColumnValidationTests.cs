using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// An INCLUDE entry that names no property falls back to the entry verbatim, as a column name.
/// When the property it named is removed, both sides of the diff resolve to the same text, no
/// index operation is emitted, and the migration is a bare <c>DROP COLUMN</c>: PostgreSQL drops the
/// unique index with the column and applies cleanly, SQL Server fails with Msg 5074 (#142). The
/// target model's fallback entries must therefore name a column of the table.
/// </summary>
[TestClass]
public class IncludeColumnValidationTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Member
    {
        public Guid         Id    { get; set; }
        public EmailAddress Email { get; set; } = new();
        public string?      Note  { get; set; }
    }

    private static void MapMember(EntityTypeBuilder<Member> b)
    {
        b.ToTable("members");
        b.HasKey(x => x.Id);
        b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email").HasMaxLength(256));
    }

    // Both satellites declare IncludeProperties, so the models pick the one for the provider they are built for.
    private static TBuilder Include<TBuilder>(DbContext context, TBuilder builder, params string[] entries)
        where TBuilder : IIndexAnnotationBuilder
        => context.Database.ProviderName!.Contains("SqlServer", StringComparison.Ordinal)
               ? SqlServerComplexIndexBuilderExtensions.IncludeProperties(builder, entries)
               : NpgsqlComplexIndexBuilderExtensions.IncludeProperties(builder, entries);

    // The declaration both models share.
    private static void DeclareIndex(DbContext context, EntityTypeBuilder<Member> b)
        => b.HasComplexIndex(x => x.Email.Value, ix => Include(context, ix.HasName("ux_email").IsUnique(), "Note"));

    private class WithNoteContext(DbContextOptions<WithNoteContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                DeclareIndex(this, b);
            });
    }

    // The issue's model: the property the INCLUDE entry named is removed, the declaration is kept.
    private class NoteRemovedContext(DbContextOptions<NoteRemovedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                b.Ignore(x => x.Note);
                DeclareIndex(this, b);
            });
    }

    // The property-level form of the same declaration.
    private class PropertyLevelNoteRemovedContext(DbContextOptions<PropertyLevelNoteRemovedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Note);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value)
                                                      .HasColumnName("email")
                                                      .HasComplexIndex(ix => Include(this, ix.HasName("ux_email").IsUnique(), "Note")));
            });
    }

    // The fix: the entry leaves the declaration with the property.
    private class NoteRemovedFromIncludeContext(DbContextOptions<NoteRemovedFromIncludeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                b.Ignore(x => x.Note);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ux_email").IsUnique());
            });
    }

    // The documented verbatim fallback: an entry that is no property path but is a column name.
    private class RawColumnContext(DbContextOptions<RawColumnContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                b.Ignore(x => x.Note);
                b.Property<string>("Raw").HasColumnName("raw_col");
                b.HasComplexIndex(x => x.Email.Value, ix => Include(this, ix.HasName("ux_email").IsUnique(), "raw_col"));
            });
    }

    [TestMethod(DisplayName = "PostgreSQL: an INCLUDE entry whose property was removed fails at migrations add")]
    public void Npgsql_include_naming_a_removed_property_throws()
    {
        var source = MigrationHarness.NpgsqlModel<WithNoteContext>();
        var target = MigrationHarness.NpgsqlModel<NoteRemovedContext>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(source, target));

        StringAssert.Contains(error.Message, "'Note'");
        StringAssert.Contains(error.Message, "'ux_email'");
        StringAssert.Contains(error.Message, "'members'");
    }

    [TestMethod(DisplayName = "PostgreSQL: a property-level INCLUDE entry whose property was removed fails at migrations add")]
    public void Npgsql_property_level_include_naming_a_removed_property_throws()
    {
        var source = MigrationHarness.NpgsqlModel<WithNoteContext>();
        var target = MigrationHarness.NpgsqlModel<PropertyLevelNoteRemovedContext>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(source, target));

        StringAssert.Contains(error.Message, "'Note'");
    }

    [TestMethod(DisplayName = "SQL Server: an INCLUDE entry whose property was removed fails at migrations add")]
    public void SqlServer_include_naming_a_removed_property_throws()
    {
        var source = MigrationHarness.SqlServerModel<WithNoteContext>();
        var target = MigrationHarness.SqlServerModel<NoteRemovedContext>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.SqlServerDiff(source, target));

        StringAssert.Contains(error.Message, "'Note'");
        StringAssert.Contains(error.Message, "'ux_email'");
    }

    [TestMethod(DisplayName = "An INCLUDE entry naming no column fails on a fresh model too")]
    public void Include_naming_no_column_throws_without_a_snapshot()
    {
        var target = MigrationHarness.NpgsqlModel<NoteRemovedContext>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(error.Message, "'Note'");
    }

    [TestMethod(DisplayName = "With UseNpgsqlComplexIndexes(), GenerateCreateScript refuses it too, as EnsureCreated would")]
    public void Runtime_differ_refuses_it_in_GenerateCreateScript()
    {
        var options = new DbContextOptionsBuilder<NoteRemovedContext>()
                     .UseNpgsql(MigrationHarness.NpgsqlConnection)
                     .UseNpgsqlComplexIndexes()
                     .Options;
        using var context = new NoteRemovedContext(options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.Database.GenerateCreateScript());

        StringAssert.Contains(error.Message, "'Note'");
    }

    [TestMethod(DisplayName = "A verbatim INCLUDE entry naming a real column still passes")]
    [DataRow("Npgsql:IndexInclude")]
    [DataRow("SqlServer:Include")]
    public void Raw_column_entry_still_passes(string annotation)
    {
        var operations = annotation.StartsWith("Npgsql", StringComparison.Ordinal)
                             ? MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<RawColumnContext>())
                             : MigrationHarness.SqlServerDiff(null, MigrationHarness.SqlServerModel<RawColumnContext>());

        var create = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ux_email");
        CollectionAssert.AreEqual(new[] { "raw_col" }, (string[])create[annotation]!);
    }

    [TestMethod(DisplayName = "SQL Server: a verbatim INCLUDE entry matches its column without regard to case")]
    public void SqlServer_raw_entry_matches_column_ignoring_case()
    {
        // "NOTE" against a column "note": SQL Server's default collation compares them alike.
        var model = MigrationHarness.SqlServerModel<LowerCaseNoteColumnContext>();

        var create = MigrationHarness.SqlServerDiff(null, model).OfType<CreateIndexOperation>().Single(o => o.Name == "ux_email");
        CollectionAssert.AreEqual(new[] { "NOTE" }, (string[])create["SqlServer:Include"]!);
    }

    private class LowerCaseNoteColumnContext(DbContextOptions<LowerCaseNoteColumnContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                b.Property(x => x.Note).HasColumnName("note");
                b.HasComplexIndex(x => x.Email.Value, ix => Include(this, ix.HasName("ux_email").IsUnique(), "NOTE"));
            });
    }

    // Both satellites' INCLUDE lists on one model, as a model shared between providers carries them.
    private class SharedModelContext(DbContextOptions<SharedModelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                MapMember(b);
                b.Ignore(x => x.Note);
                b.HasComplexIndex(x => x.Email.Value, ix =>
                    SqlServerComplexIndexBuilderExtensions.IncludeProperties(
                        NpgsqlComplexIndexBuilderExtensions.IncludeProperties(ix.HasName("ux_email"), "Note"), "Note"));
            });
    }

    [TestMethod(DisplayName = "Another provider's INCLUDE list is not checked: SQLite ignores it")]
    public void Sqlite_ignores_other_providers_include_lists()
    {
        var operations = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<SharedModelContext>());

        Assert.IsTrue(operations.OfType<CreateIndexOperation>().Any(o => o.Name == "ux_email"));
    }

    [TestMethod(DisplayName = "A snapshot holding an INCLUDE entry that names no column stays diffable")]
    [DataRow("Npgsql")]
    [DataRow("SqlServer")]
    public void Snapshot_with_unresolvable_include_stays_diffable(string provider)
    {
        // The source is history: the model can only be fixed if it diffs.
        var operations = provider == "Npgsql"
                             ? MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<NoteRemovedContext>(),
                                                           MigrationHarness.NpgsqlModel<NoteRemovedFromIncludeContext>())
                             : MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<NoteRemovedContext>(),
                                                              MigrationHarness.SqlServerModel<NoteRemovedFromIncludeContext>());

        Assert.IsTrue(operations.OfType<DropIndexOperation>().Any(o => o.Name == "ux_email"));
        Assert.IsTrue(operations.OfType<CreateIndexOperation>().Any(o => o.Name == "ux_email"));
    }

    [TestMethod(DisplayName = "Removing the property and its INCLUDE entry together re-creates the index without it")]
    public void Removing_property_and_entry_together_recreates_the_index()
    {
        var source = MigrationHarness.NpgsqlModel<WithNoteContext>();
        var target = MigrationHarness.NpgsqlModel<NoteRemovedFromIncludeContext>();

        var operations = MigrationHarness.NpgsqlDiff(source, target);

        var drop       = operations.OfType<DropIndexOperation>().Single(o => o.Name == "ux_email");
        var dropColumn = operations.OfType<DropColumnOperation>().Single(o => o.Name == "Note");
        var create     = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ux_email");

        var order = operations.ToList();
        Assert.IsLessThan(order.IndexOf(dropColumn), order.IndexOf(drop));
        Assert.IsGreaterThan(order.IndexOf(dropColumn), order.IndexOf(create));
        Assert.IsNull(create.FindAnnotation("Npgsql:IndexInclude"));
    }
}
