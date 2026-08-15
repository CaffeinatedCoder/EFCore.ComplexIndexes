using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class NpgsqlTemporalConstraintSqlTests
{
    // Matches the (internal) NpgsqlTemporalAnnotations.WithoutOverlaps key. Tests use the literal,
    // consistent with how NpgsqlExpressionIndexSqlTests references "Npgsql:IndexMethod" directly.
    private const string WithoutOverlaps = "CustomTemporal:WithoutOverlaps";

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

    private static AddUniqueConstraintOperation TemporalUnique(string name, string table, string? schema, string period, params string[] columns)
    {
        var op = new AddUniqueConstraintOperation { Name = name, Table = table, Schema = schema, Columns = columns };
        op.AddAnnotation(WithoutOverlaps, period);
        return op;
    }

    [TestMethod(DisplayName = "Renders a temporal UNIQUE constraint with WITHOUT OVERLAPS")]
    public void Renders_temporal_unique_constraint()
    {
        var sql = GenerateSql(TemporalUnique(
            "AK_room_bookings_room_id_booked_during", "room_bookings", schema: null,
            period: "booked_during", columns: ["room_id", "booked_during"]));

        // Npgsql's DelimitIdentifier quotes only identifiers that require it (uppercase/special),
        // so all-lowercase table/column names render unquoted; the constraint name is quoted.
        StringAssert.Contains(sql, "ALTER TABLE room_bookings");
        StringAssert.Contains(sql, "ADD CONSTRAINT \"AK_room_bookings_room_id_booked_during\" UNIQUE");
        StringAssert.Contains(sql, "(room_id, booked_during WITHOUT OVERLAPS)");
    }

    [TestMethod(DisplayName = "Renders a temporal UNIQUE constraint in a schema")]
    public void Renders_temporal_unique_constraint_with_schema()
    {
        var sql = GenerateSql(TemporalUnique(
            "AK_room_inventory_room_id_valid_during", "room_inventory", schema: "public",
            period: "valid_during", columns: ["room_id", "valid_during"]));

        StringAssert.Contains(sql, "ALTER TABLE public.room_inventory");
        StringAssert.Contains(sql, "(room_id, valid_during WITHOUT OVERLAPS)");
    }

    [TestMethod(DisplayName = "Period column is rendered last regardless of column order")]
    public void Period_column_rendered_last()
    {
        var sql = GenerateSql(TemporalUnique("AK_t", "t", schema: null, period: "period", columns: ["period", "a", "b"]));

        StringAssert.Contains(sql, "(a, b, period WITHOUT OVERLAPS)");
    }

    [TestMethod(DisplayName = "Non-temporal unique constraint delegates to base generator")]
    public void Non_temporal_unique_delegates_to_base()
    {
        var op = new AddUniqueConstraintOperation { Name = "AK_plain", Table = "plain", Columns = ["code"] };

        var sql = GenerateSql(op);

        StringAssert.Contains(sql, "UNIQUE (code)");
        Assert.IsFalse(sql.Contains("WITHOUT OVERLAPS"));
    }
}
