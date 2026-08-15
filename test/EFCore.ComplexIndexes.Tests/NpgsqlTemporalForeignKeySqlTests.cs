using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class NpgsqlTemporalForeignKeySqlTests
{
    private const string DependentPeriod = "CustomTemporal:ForeignKeyDependentPeriod";
    private const string PrincipalPeriod = "CustomTemporal:ForeignKeyPrincipalPeriod";

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private static string GenerateSql(MigrationOperation op)
    {
        var options = new DbContextOptionsBuilder()
                     .UseNpgsql("Host=localhost;Database=test")
                     .UseNpgsqlComplexIndexes()
                     .Options;

        using var context   = new EmptyContext(options);
        var       generator = context.GetService<IMigrationsSqlGenerator>();
        var       commands  = generator.Generate([op], model: null);

        return string.Join("\n", commands.Select(c => c.CommandText));
    }

    private static AddForeignKeyOperation TemporalForeignKey(
        string    name,
        string    table,
        string?   schema,
        string    principalTable,
        string?   principalSchema,
        string    dependentPeriod,
        string    principalPeriod,
        string[]  columns,
        string[]  principalColumns)
    {
        var op = new AddForeignKeyOperation
                 {
                     Name             = name,
                     Table            = table,
                     Schema           = schema,
                     PrincipalTable   = principalTable,
                     PrincipalSchema  = principalSchema,
                     Columns          = columns,
                     PrincipalColumns = principalColumns
                 };
        op.AddAnnotation(DependentPeriod, dependentPeriod);
        op.AddAnnotation(PrincipalPeriod, principalPeriod);
        return op;
    }

    [TestMethod(DisplayName = "Renders a temporal FOREIGN KEY with PERIOD columns")]
    public void Renders_temporal_foreign_key()
    {
        var sql = GenerateSql(TemporalForeignKey(
            "FK_subscription_addons_subscriptions_subscription_id_active_during",
            "subscription_addons", schema: null,
            "subscriptions", principalSchema: null,
            dependentPeriod: "active_during",
            principalPeriod: "valid_during",
            columns: ["subscription_id", "active_during"],
            principalColumns: ["subscription_id", "valid_during"]));

        StringAssert.Contains(sql, "ALTER TABLE subscription_addons");
        StringAssert.Contains(sql, "ADD CONSTRAINT \"FK_subscription_addons_subscriptions_subscription_id_active_during\" FOREIGN KEY");
        StringAssert.Contains(sql, "(subscription_id, PERIOD active_during)");
        StringAssert.Contains(sql, "REFERENCES subscriptions (subscription_id, PERIOD valid_during)");
    }

    [TestMethod(DisplayName = "Renders a temporal FOREIGN KEY in schemas")]
    public void Renders_temporal_foreign_key_with_schemas()
    {
        var sql = GenerateSql(TemporalForeignKey(
            "fk_addons_subscriptions_temporal",
            "subscription_addons", schema: "billing",
            "subscriptions", principalSchema: "billing",
            dependentPeriod: "active_during",
            principalPeriod: "valid_during",
            columns: ["tenant_id", "subscription_id", "active_during"],
            principalColumns: ["tenant_id", "subscription_id", "valid_during"]));

        StringAssert.Contains(sql, "ALTER TABLE billing.subscription_addons");
        StringAssert.Contains(sql, "FOREIGN KEY (tenant_id, subscription_id, PERIOD active_during)");
        StringAssert.Contains(sql, "REFERENCES billing.subscriptions (tenant_id, subscription_id, PERIOD valid_during)");
    }

    [TestMethod(DisplayName = "Temporal FK period columns are rendered last regardless of column order")]
    public void Period_columns_rendered_last()
    {
        var sql = GenerateSql(TemporalForeignKey(
            "fk_t_p",
            "t", schema: null,
            "p", principalSchema: null,
            dependentPeriod: "dependent_period",
            principalPeriod: "principal_period",
            columns: ["dependent_period", "a", "b"],
            principalColumns: ["principal_period", "a", "b"]));

        StringAssert.Contains(sql, "FOREIGN KEY (a, b, PERIOD dependent_period)");
        StringAssert.Contains(sql, "REFERENCES p (a, b, PERIOD principal_period)");
    }

    [TestMethod(DisplayName = "Temporal FK rejects cascade actions")]
    public void Temporal_foreign_key_rejects_cascade_actions()
    {
        var op = TemporalForeignKey(
            "fk_t_p",
            "t", schema: null,
            "p", principalSchema: null,
            dependentPeriod: "dependent_period",
            principalPeriod: "principal_period",
            columns: ["a", "dependent_period"],
            principalColumns: ["a", "principal_period"]);
        op.OnDelete = ReferentialAction.Cascade;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => GenerateSql(op));
        StringAssert.Contains(ex.Message, "only support NO ACTION");
    }

    [TestMethod(DisplayName = "Non-temporal foreign key delegates to base generator")]
    public void Non_temporal_foreign_key_delegates_to_base()
    {
        var op = new AddForeignKeyOperation
                 {
                     Name             = "fk_plain",
                     Table            = "children",
                     Columns          = ["parent_id"],
                     PrincipalTable   = "parents",
                     PrincipalColumns = ["id"]
                 };

        var sql = GenerateSql(op);

        StringAssert.Contains(sql, "FOREIGN KEY (parent_id) REFERENCES parents (id)");
        Assert.IsFalse(sql.Contains("PERIOD"));
    }
}
