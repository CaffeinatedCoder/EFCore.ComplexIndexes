using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SchemaKit.ApiProbes.Model;

namespace SchemaKit.ApiProbes.Probes;

/// <summary>
/// The few run-time claims the design document makes about the chosen surface: one annotation per
/// declaration under <c>CodoMetis:</c>, the read model returning the declared values, the
/// functional amend, and DDL in the 5.x shape.
/// </summary>
[TestClass]
public sealed partial class PrototypeBehaviourTests
{
    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    private static readonly PgExclusion.Gist ActivePeriod = Pg.Exclusion(
        Pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
        where: x => x.RevokedAt == null,
        name: "ex_role_grant_active_period");

    [TestMethod]
    public void Each_declaration_is_its_own_annotation_and_reads_back_as_the_same_value()
    {
        var model = new ModelBuilder();
        model.Entity<RoleGrant>().DeclarePostgres(pg => [ActivePeriod, pg.BTree(x => x.Kind)]);
        var entity = model.Model.FindEntityType(typeof(RoleGrant))!;

        var keys = entity.GetAnnotations().Select(a => a.Name).Where(n => n.StartsWith("CodoMetis:")).ToList();
        Assert.HasCount(2, keys);
        Assert.IsTrue(keys.All(k => KeyShape().IsMatch(k)), string.Join(", ", keys));

        Assert.AreEqual(ActivePeriod, entity.GetDeclarations<PgExclusion>().Single());
        Assert.AreEqual(Pg.BTree(x => x.Kind), entity.GetDeclarations<PgIndex>().Single());
    }

    /// <summary>The samples in the design document's section 9, verbatim.</summary>
    [TestMethod]
    public void Annotations_have_the_documented_v2_shape()
    {
        var unique = PgSchema.For<Person>().Unique(x => x.Email.Value,
            where: x => x.DeletedAt == null, include: [x => x.Name], nullsDistinct: false, name: "ux_people_email");
        var deferred = ActivePeriod with { Deferral = Deferral.InitiallyDeferred };

        Assert.AreEqual("CodoMetis:Pg.Exclusion:dbfab59b70e9737b", SchemaDeclarationExtensions.AnnotationKey(deferred));
        Assert.AreEqual(
            """
            {"$kind":"gist","elements":[{"target":{"$t":"column","path":"GranteeId"},"operator":{"sql":"="}},{"target":{"$t":"column","path":"RoleId"},"operator":{"sql":"="}},{"target":{"$t":"column","path":"Period"},"operator":{"sql":"&&"}}],"name":"ex_role_grant_active_period","where":{"template":"{RevokedAt} IS NULL"},"deferral":"InitiallyDeferred"}
            """,
            deferred.ToAnnotationValue());

        Assert.AreEqual("CodoMetis:Pg.Index:3793116cc5c1e71b", SchemaDeclarationExtensions.AnnotationKey(unique));
        Assert.AreEqual(
            """
            {"$kind":"btree","keys":[{"target":{"$t":"column","path":"Email.Value"}}],"unique":{"nullsDistinct":false},"include":["Name"],"name":"ux_people_email","where":{"template":"{DeletedAt} IS NULL"}}
            """,
            unique.ToAnnotationValue());

        Assert.AreEqual(unique, PgIndex.FromAnnotationValue(unique.ToAnnotationValue()));
        Assert.AreEqual(deferred, PgExclusion.FromAnnotationValue(deferred.ToAnnotationValue()));
    }

    [TestMethod]
    public void Declaring_the_same_identity_again_replaces_it()
    {
        var model = new ModelBuilder();
        model.Entity<RoleGrant>().DeclarePostgres(pg => [pg.BTree(x => x.Kind)]);
        model.Entity<RoleGrant>().DeclarePostgres(pg => [pg.BTree(x => x.Kind, fillFactor: 70, name: "ix_kind")]);

        var index = (PgIndex.BTree)model.Model.FindEntityType(typeof(RoleGrant))!.GetDeclarations<PgIndex>().Single();
        Assert.AreEqual("ix_kind", index.Name);
        Assert.AreEqual(70, index.FillFactor);
    }

    [TestMethod]
    public void A_convention_amends_declarations_with_a_pure_function()
    {
        var model = new ModelBuilder();
        model.Entity<RoleGrant>().DeclarePostgres(pg => [pg.Unique(x => x.Kind, name: "ux_kind")]);
        var entity = model.Model.FindEntityType(typeof(RoleGrant))!;

        var notRevoked = Predicate.Of<IWithdrawable>(x => x.RevokedAt == null);
        Assert.AreEqual(1, entity.Redeclare<PgIndex>(ix => ix.AndWhere(notRevoked)));
        Assert.AreEqual(0, entity.Redeclare<PgIndex>(ix => ix.AndWhere(notRevoked)), "Amending twice changes nothing.");

        Assert.AreEqual("{RevokedAt} IS NULL", entity.GetDeclarations<PgIndex>().Single().Where!.Template);
    }

    [TestMethod]
    public void Exclusion_renders_the_5x_ddl()
    {
        var sql = Render(new PgExclusionContributor(), ActivePeriod);

        Assert.AreEqual(
            "ALTER TABLE \"role_grants\" DROP CONSTRAINT IF EXISTS \"ex_role_grant_active_period\";\n"
            + "ALTER TABLE \"role_grants\" ADD CONSTRAINT \"ex_role_grant_active_period\" EXCLUDE USING gist "
            + "(\"grantee_id\" WITH =, \"role_id\" WITH =, \"period\" WITH &&) WHERE (\"revoked_at\" IS NULL);",
            sql);
    }

    [TestMethod]
    public void Unique_index_renders_the_5x_ddl()
    {
        var index = PgSchema.For<Person>().Unique(
            [PgSchema.For<Person>().Key(x => x.Email.Value.ToLower()), PgSchema.For<Person>().Key(x => x.ReviewedAt).Desc().NullsLast()],
            where: x => x.DeletedAt == null,
            nullsDistinct: false,
            name: "ux_people_email");

        Assert.AreEqual(
            "CREATE UNIQUE INDEX \"ux_people_email\" ON \"role_grants\" "
            + "((lower(\"email_value\")), \"reviewed_at\" DESC NULLS LAST) NULLS NOT DISTINCT WHERE \"deleted_at\" IS NULL;",
            Render(new PgIndexContributor(), index));
    }

    [TestMethod]
    public void A_with_expression_that_bypasses_the_stage_types_is_refused_at_run_time()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ActivePeriod with
        {
            Elements = [.. ActivePeriod.Elements.Where(e => e.Operator == ExclusionOperator.Equal)],
        });
    }

    private static string Render(ISchemaContributor contributor, ISchemaObject declaration)
    {
        var table = new SchemaTable("role_grants", null, RenderTarget, p => Placeholder().Replace(p.Template, m => $"\"{Snake(m.Groups[1].Value)}\""));
        return string.Join("\n", contributor.Create(declaration, table, new PostgresDialect()).Cast<SqlOperation>().Select(o => o.Sql));
    }

    private static string RenderTarget(KeyTarget target) => target switch
    {
        KeyTarget.Column c   => $"\"{Snake(c.Path)}\"",
        KeyTarget.Template t => Placeholder().Replace(t.Sql, m => $"\"{Snake(m.Groups[1].Value)}\""),
    };

    private static string Snake(string path) =>
        Regex.Replace(path.Replace('.', '_'), "(?<=[a-z])([A-Z])", "_$1").ToLowerInvariant();

    [GeneratedRegex(@"\{([A-Za-z0-9_.]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^CodoMetis:Pg\.(Index|Exclusion):[0-9a-f]{16}$")]
    private static partial Regex KeyShape();
}
