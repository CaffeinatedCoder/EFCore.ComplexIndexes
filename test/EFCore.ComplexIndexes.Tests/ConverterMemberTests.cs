using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A value object mapped through a value converter is one scalar property, so <c>x.Email.Value</c>
/// produced the path <c>Email.Value</c> and the walk died at <c>Email</c>: there is no complex
/// property to descend into. The member now unwraps to the property when the property has a
/// converter and the member's type is the converter's provider type — the column holds exactly
/// that member. Everything else keeps failing, loudly: <c>CreatedAt.Year</c> must not resolve to the
/// whole column.
/// </summary>
[TestClass]
public class ConverterMemberTests
{
    private readonly record struct EmailAddress(string Value);

    private class Money
    {
        public decimal Amount   { get; init; }
        public string  Currency { get; init; } = "EUR";
    }

    private class Customer
    {
        public Guid                  Id        { get; set; }
        public EmailAddress          Email     { get; set; }
        public Money                 Price     { get; set; } = new();
        public DateTime              CreatedAt { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private sealed class EmailConverter() : ValueConverter<EmailAddress, string>(e => e.Value, v => new EmailAddress(v));

    private abstract class CustomerContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Customer>(b =>
            {
                b.ToTable("customers");
                b.HasKey(x => x.Id);
                b.Property(x => x.Email).HasConversion(e => e.Value, v => new EmailAddress(v)).HasColumnName("email");
                b.Property(x => x.Price).HasConversion(m => m.Amount, a => new Money { Amount = a }).HasColumnName("price");
                Declare(b);
            });

        protected abstract void Declare(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Customer> b);
    }

    private class ConvertedMemberContext(DbContextOptions<ConvertedMemberContext> options) : CustomerContext(options)
    {
        protected override void Declare(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_customer_email");
            b.HasExpressionIndex(x => x.Email.Value.ToLower(), indexName: "ix_customer_email_lower");
            b.HasComplexIndex(x => x.Price.Amount, indexName: "ix_customer_amount");
            b.HasExclusionConstraint(ex => ex.WithEquality(x => x.Email.Value).WithOverlaps(x => x.Period).HasName("ex_customer_email_period"));
        }
    }

    private class MismatchedMemberContext(DbContextOptions<MismatchedMemberContext> options) : CustomerContext(options)
    {
        protected override void Declare(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Customer> b)
            => b.HasComplexIndex(x => x.Price.Currency, indexName: "ix_customer_currency");
    }

    private class UnconvertedMemberContext(DbContextOptions<UnconvertedMemberContext> options) : CustomerContext(options)
    {
        protected override void Declare(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Customer> b)
            => b.HasComplexIndex(x => x.CreatedAt.Year, indexName: "ix_customer_year");
    }

    // The converter comes from ConfigureConventions rather than the property: the walk must see it too.
    private class PreConventionContext(DbContextOptions<PreConventionContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.Properties<EmailAddress>().HaveConversion<EmailConverter>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Customer>(b =>
            {
                b.ToTable("customers");
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Price);
                b.Property(x => x.Email).HasColumnName("email");
                b.HasComplexIndex(x => x.Email.Value, indexName: "ix_customer_email");
            });
    }

    [TestMethod(DisplayName = "A converter's member resolves to the converted column")]
    public void Converted_member_resolves_to_the_column()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ConvertedMemberContext>());

        var email = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ux_customer_email");
        CollectionAssert.AreEqual(new[] { "email" }, email.Columns);

        var amount = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ix_customer_amount");
        CollectionAssert.AreEqual(new[] { "price" }, amount.Columns);
    }

    [TestMethod(DisplayName = "Typed expression indexes and exclusion elements unwrap it as well")]
    public void Templates_and_exclusion_elements_unwrap()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ConvertedMemberContext>());

        StringAssert.Contains(MigrationHarness.NpgsqlSql(operations), "CREATE INDEX ix_customer_email_lower ON customers ((lower(\"email\")));");

        var exclusion = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_customer_email_period\" EXCLUDE"));
        StringAssert.Contains(exclusion.Sql, "(\"email\" WITH =, \"Period\" WITH &&)");
    }

    [TestMethod(DisplayName = "A member of another type than the converter's provider type does not resolve")]
    public void Mismatched_member_throws()
    {
        var target = MigrationHarness.NpgsqlModel<MismatchedMemberContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "Price.Currency");
    }

    [TestMethod(DisplayName = "A member of a property without a converter does not resolve")]
    public void Unconverted_member_throws()
    {
        var target = MigrationHarness.NpgsqlModel<UnconvertedMemberContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "CreatedAt.Year");
    }

    [TestMethod(DisplayName = "A converter configured through ConfigureConventions counts too")]
    public void Pre_convention_converter_is_seen()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PreConventionContext>());

        var email = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ix_customer_email");
        CollectionAssert.AreEqual(new[] { "email" }, email.Columns);
    }
}
