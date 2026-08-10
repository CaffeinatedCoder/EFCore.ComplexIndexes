using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class NpgsqlExpressionIndexSqlTests
{
    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private static string GenerateSql(CreateIndexOperation op)
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

    private static CreateIndexOperation ExpressionIndex(string name, string table, params ResolvedIndexPart[] parts)
    {
        var op = new CreateIndexOperation
                 {
                     Name    = name,
                     Table   = table,
                     Columns = [.. parts.Where(p => !p.IsExpression).Select(p => p.Value)]
                 };
        op.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize(parts));
        return op;
    }

    [TestMethod(DisplayName = "Renders a single expression index")]
    public void Renders_single_expression_index()
    {
        var sql = GenerateSql(ExpressionIndex("IX_person_lowername", "person", new ResolvedIndexPart(true, "lower(name)")));

        StringAssert.Contains(sql, "CREATE INDEX");
        StringAssert.Contains(sql, "\"IX_person_lowername\"");
        StringAssert.Contains(sql, "ON person");
        StringAssert.Contains(sql, "(lower(name))");
    }

    [TestMethod(DisplayName = "Renders mixed unique GIN index with a filter")]
    public void Renders_mixed_unique_gin_with_filter()
    {
        var op = ExpressionIndex("ix", "person",
                                 new ResolvedIndexPart(false, "name"),
                                 new ResolvedIndexPart(true,  "lower(email)"));
        op.Schema   = "public";
        op.IsUnique = true;
        op.Filter   = "deleted_at IS NULL";
        op.AddAnnotation("Npgsql:IndexMethod", "gin");

        var sql = GenerateSql(op);

        StringAssert.Contains(sql, "CREATE UNIQUE INDEX");
        StringAssert.Contains(sql, "USING gin");
        StringAssert.Contains(sql, "(name, (lower(email)))");
        StringAssert.Contains(sql, "WHERE deleted_at IS NULL");
    }

    [TestMethod(DisplayName = "Renders INCLUDE columns and NULLS NOT DISTINCT")]
    public void Renders_include_and_nulls_not_distinct()
    {
        var op = ExpressionIndex("ix3", "person", new ResolvedIndexPart(true, "lower(email)"));
        op.IsUnique = true;
        op.AddAnnotation("Npgsql:IndexInclude",  new[] { "name" });
        op.AddAnnotation("Npgsql:NullsDistinct", false);

        var sql = GenerateSql(op);

        StringAssert.Contains(sql, "INCLUDE (name)");
        StringAssert.Contains(sql, "NULLS NOT DISTINCT");
    }

    [TestMethod(DisplayName = "Renders DESC on descending column and expression parts")]
    public void Renders_descending_parts()
    {
        var op = ExpressionIndex("ix4", "person",
                                 new ResolvedIndexPart(false, "name", Descending: true),
                                 new ResolvedIndexPart(true,  "lower(email)", Descending: true),
                                 new ResolvedIndexPart(false, "id"));

        var sql = GenerateSql(op);

        StringAssert.Contains(sql, "(name DESC, (lower(email)) DESC, id)");
    }

    [TestMethod(DisplayName = "ExpressionIndexBuilder.Descending marks the most recent part")]
    public void Builder_descending_marks_last_part()
    {
        var builder = new EFCore.ComplexIndexes.PostgreSQL.ExpressionIndexBuilder();
        builder.Expression("country").Expression("lower(email)").Descending();

        var parts = builder.Parts;
        Assert.HasCount(2, parts);
        Assert.IsFalse(parts[0].Descending);
        Assert.IsTrue(parts[1].Descending);
    }
}