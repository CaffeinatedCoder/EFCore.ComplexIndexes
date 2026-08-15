using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A satellite's annotation whitelist and its builder extension methods are two halves of one
/// feature, edited in different files. When they drift, nothing fails: a whitelisted key with no
/// builder method is simply unreachable, and the option looks supported while being impossible to
/// set. <c>SqlServer:DataCompression</c> sat whitelisted with no API for an entire release.
/// </summary>
[TestClass]
public class BuilderApiParityTests
{
    [TestMethod(DisplayName = "Every whitelisted Npgsql index option is reachable from the builder")]
    public void Npgsql_whitelist_is_reachable()
        => AssertWhitelistIsReachable(
               typeof(NpgsqlComplexIndexMigrationsModelDiffer),
               "SupportedNpgsqlAnnotations",
               typeof(NpgsqlComplexIndexBuilderExtensions));

    [TestMethod(DisplayName = "Every whitelisted SQL Server index option is reachable from the builder")]
    public void SqlServer_whitelist_is_reachable()
        => AssertWhitelistIsReachable(
               typeof(SqlServerComplexIndexMigrationsModelDiffer),
               "SupportedSqlServerAnnotations",
               typeof(SqlServerComplexIndexBuilderExtensions));

    private static void AssertWhitelistIsReachable(Type differ, string whitelistField, Type builderExtensions)
    {
        var whitelist = (HashSet<string>)differ
                       .GetField(whitelistField, BindingFlags.NonPublic | BindingFlags.Static)!
                       .GetValue(null)!;

        Assert.IsNotEmpty(whitelist, $"{differ.Name}.{whitelistField} is empty — has it been renamed?");

        var reachable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in builderExtensions.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            // Every option method is an extension generic over the builder interface.
            if (!method.IsGenericMethodDefinition)
                continue;

            var builder   = new ComplexIndexBuilder();
            var concrete  = method.MakeGenericMethod(typeof(ComplexIndexBuilder));
            var arguments = concrete.GetParameters()
                                    .Skip(1)
                                    .Select(p => SampleArgument(p, method.Name))
                                    .Prepend((object)builder)
                                    .ToArray();

            concrete.Invoke(null, arguments);

            foreach (var key in builder.Annotations.Keys)
                reachable.Add(key);
        }

        var unreachable = whitelist.Except(reachable).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.IsEmpty(
            unreachable,
            $"{differ.Name} forwards {string.Join(", ", unreachable)} onto index operations, but "
          + $"{builderExtensions.Name} offers no way to set them. Either add the builder method or drop "
          + "the key from the whitelist.");
    }

    private static object SampleArgument(ParameterInfo parameter, string methodName)
    {
        var type = parameter.ParameterType;

        if (type == typeof(bool))     return true;
        if (type == typeof(int))      return 1;               // fill factor is range-checked to 1..100
        if (type == typeof(string))   return "sample";
        if (type == typeof(string[])) return new[] { "sample" };
        if (type.IsEnum)              return Enum.GetValues(type).GetValue(0)!;

        throw new NotSupportedException(
            $"Cannot synthesize a '{type}' argument for {methodName}({parameter.Name}). Extend "
          + $"{nameof(SampleArgument)} so this option is covered by the parity check.");
    }
}
