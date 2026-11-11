using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A temporal foreign key stores its principal as the CLR FullName, and a snapshot only knows EF's
/// entity-type name. <c>EntityTypeNames.FromClrFullName</c> converts the one into the other; EF's
/// own model is the oracle for every shape (#197).
/// </summary>
[TestClass]
public class EntityTypeNameTests
{
    internal class Plain;
    internal class Generic<T>;
    internal class Pair<TA, TB>;
    internal class Outer<T>
    {
        internal class Inner;
    }

    // The name EF Core gives the entity type of a CLR type.
    private static string EntityTypeName(Type type)
    {
        var builder = new ModelBuilder();
        builder.Entity(type).Property<int>("Id");
        return builder.Model.GetEntityTypes().Single().Name;
    }

    [TestMethod]
    [DataRow(typeof(Plain))]
    [DataRow(typeof(Generic<int>))]
    [DataRow(typeof(Generic<string>))]
    [DataRow(typeof(Generic<Guid>))]
    [DataRow(typeof(Generic<int?>))]
    [DataRow(typeof(Generic<int[]>))]
    [DataRow(typeof(Generic<List<long>>))]
    [DataRow(typeof(Generic<Generic<bool>>))]
    [DataRow(typeof(Pair<int, string>))]
    [DataRow(typeof(Pair<DateOnly?, Plain>))]
    [DataRow(typeof(Outer<int>.Inner))]
    public void The_clr_full_name_converts_to_the_name_ef_gives_the_entity_type(Type type)
        => Assert.AreEqual(EntityTypeName(type), EntityTypeNames.FromClrFullName(type.FullName!));
}
