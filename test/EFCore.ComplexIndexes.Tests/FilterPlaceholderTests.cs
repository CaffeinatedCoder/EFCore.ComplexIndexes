using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Filters were spliced into the DDL verbatim while expression parts already resolved
/// <c>{Property.Path}</c> placeholders, so a filter had to repeat the column name a
/// <c>HasColumnName</c> elsewhere decides — kept in sync by discipline, not by the package. Now a
/// filter resolves the same placeholders, at design time: the resolved text rides on the operation
/// and renders through the stock generator, so no runtime wiring is involved, and source and target
/// compare on it, so nothing churns.
/// </summary>
[TestClass]
public class FilterPlaceholderTests
{
    private class Address
    {
        public string City { get; set; } = "";
    }

    private class Method
    {
        public string Type   { get; set; } = "";
        public string Issuer { get; set; } = "";
    }

    private class Account
    {
        public Guid                  Id        { get; set; }
        public string                Email     { get; set; } = "";
        public DateTime?             RevokedAt { get; set; }
        public Address               Address   { get; set; } = new();
        public Method                Method    { get; set; } = new();
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private class PlaceholderContext(DbContextOptions<PlaceholderContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.Property(x => x.Email).HasColumnName("email");
                b.ComplexProperty(x => x.Address, c => c.Property(a => a.City)
                                                        .HasComplexIndex(filter: "{RevokedAt} IS NULL", indexName: "ix_city_active"));
                b.ComplexProperty(x => x.Method, c => c.ToJson("method"));
                b.HasComplexIndex(x => x.Email, isUnique: true, filter: "{RevokedAt} IS NULL", indexName: "ux_email_active");
                b.HasComplexCompositeIndex(x => new { x.Email, x.RevokedAt },
                                           filter:    "{Method.Type} = 'federated' AND {Method} IS NOT NULL",
                                           indexName: "ix_federated");
                b.HasComplexIndex(x => x.RevokedAt,
                                  filter:    "tags @> '{urgent}' AND '{Email}' <> '' AND doc @> '{\"a\": 1}' AND grid = '{{1,2},{3,4}}' AND {Email} <> ''",
                                  indexName: "ix_literals");
                b.HasExclusionConstraint(x => x.Email, x => x.Period, filter: "{RevokedAt} IS NULL", name: "ex_email_active");
            });
    }

    private class UnknownPlaceholderContext(DbContextOptions<UnknownPlaceholderContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address);
                b.ComplexProperty(x => x.Method);
                b.HasComplexIndex(x => x.Email, filter: "{Revoked} IS NULL", indexName: "ix_typo");
            });
    }

    // No JSON, no exclusion constraint: diffable by every provider.
    private class PortableContext(DbContextOptions<PortableContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Period);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.ComplexProperty(x => x.Address);
                b.ComplexProperty(x => x.Method);
                b.HasComplexIndex(x => x.Email, isUnique: true, filter: "{RevokedAt} IS NULL", indexName: "ux_email_active");
            });
    }

    private static string FilterOf(IEnumerable<MigrationOperation> operations, string index)
        => operations.OfType<CreateIndexOperation>().Single(o => o.Name == index).Filter!;

    [TestMethod(DisplayName = "Column placeholders resolve to the mapped column, on property-level and entity-level indexes")]
    public void Column_placeholders_resolve()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PlaceholderContext>());

        Assert.AreEqual("\"revoked_at\" IS NULL", FilterOf(operations, "ux_email_active"));
        Assert.AreEqual("\"revoked_at\" IS NULL", FilterOf(operations, "ix_city_active"));
    }

    [TestMethod(DisplayName = "JSON members resolve to extractions, the document to its container column")]
    public void Json_placeholders_resolve()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PlaceholderContext>());

        Assert.AreEqual("(\"method\" ->> 'Type') = 'federated' AND \"method\" IS NOT NULL", FilterOf(operations, "ix_federated"));
    }

    [TestMethod(DisplayName = "Braces inside string literals and braces that are not a path stay verbatim")]
    public void Literals_stay_verbatim()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PlaceholderContext>());

        Assert.AreEqual(
            "tags @> '{urgent}' AND '{Email}' <> '' AND doc @> '{\"a\": 1}' AND grid = '{{1,2},{3,4}}' AND \"email\" <> ''",
            FilterOf(operations, "ix_literals"));
    }

    [TestMethod(DisplayName = "A placeholder that names no property fails at migrations add")]
    public void Unknown_placeholder_throws()
    {
        var target = MigrationHarness.NpgsqlModel<UnknownPlaceholderContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "'Revoked'");
        StringAssert.Contains(exception.Message, "filter");
    }

    [TestMethod(DisplayName = "Exclusion constraint filters resolve placeholders into the design-time DDL")]
    public void Exclusion_filter_resolves()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PlaceholderContext>());

        var add = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_email_active\" EXCLUDE"));
        StringAssert.Contains(add.Sql, "WHERE (\"revoked_at\" IS NULL)");
    }

    [TestMethod(DisplayName = "The stock generator renders the resolved filter — no runtime wiring involved")]
    public void Stock_generator_renders_resolved_filter()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PortableContext>());

        var sql = MigrationHarness.NpgsqlSql(operations, complexIndexWiring: false);

        StringAssert.Contains(sql, "CREATE UNIQUE INDEX ux_email_active ON accounts (\"Email\") WHERE \"revoked_at\" IS NULL;");
    }

    [TestMethod(DisplayName = "SQL Server delimits resolved columns with brackets")]
    public void SqlServer_quotes_with_brackets()
    {
        var operations = MigrationHarness.SqlServerDiff(null, MigrationHarness.SqlServerModel<PortableContext>());

        Assert.AreEqual("[revoked_at] IS NULL", FilterOf(operations, "ux_email_active"));
        StringAssert.Contains(MigrationHarness.SqlServerSql(operations), "WHERE [revoked_at] IS NULL");
    }

    [TestMethod(DisplayName = "A provider without a satellite resolves with ANSI quotes")]
    public void Core_quotes_ansi()
    {
        var operations = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<PortableContext>());

        Assert.AreEqual("\"revoked_at\" IS NULL", FilterOf(operations, "ux_email_active"));
    }

    [TestMethod(DisplayName = "A filter with placeholders does not churn between snapshot and model")]
    public void Placeholder_filter_does_not_churn()
    {
        var source = MigrationHarness.NpgsqlModel<PlaceholderContext>();
        var target = MigrationHarness.NpgsqlModel<PlaceholderContext>();

        Assert.IsEmpty(MigrationHarness.NpgsqlDiff(source, target));
    }
}
