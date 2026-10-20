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

    /// <summary>
    /// The other direction: an option the builder sets but the differ does not forward is dropped
    /// from every property-level declaration without a word, and every entity-level test still
    /// passes, since entity-level options skip the whitelist. Removing <c>SqlServer:FillFactor</c>
    /// from the whitelist went unnoticed by the whole suite (#96).
    /// </summary>
    [TestMethod(DisplayName = "Every option a satellite's builder sets is forwarded by its differ")]
    [DataRow(typeof(SqlServerComplexIndexMigrationsModelDiffer), typeof(SqlServerComplexIndexBuilderExtensions))]
    [DataRow(typeof(NpgsqlComplexIndexMigrationsModelDiffer),    typeof(NpgsqlComplexIndexBuilderExtensions))]
    public void Every_builder_option_is_forwarded(Type differType, Type builderExtensions)
    {
        using var context = new MigrationHarness.EmptyContext(MigrationHarness.NpgsqlOptions());
        var differ = MigrationHarness.CreateDiffer(differType, context);

        var isForwarded = typeof(CustomMigrationsModelDiffer).GetMethod("IsForwardedIndexAnnotation", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var dropped = KeysSetBy(builderExtensions)
                     .SelectMany(m => m.Keys.Where(k => !(bool)isForwarded.Invoke(differ, [k])!).Select(k => $"{m.Method} ({k})"))
                     .ToList();

        Assert.IsEmpty(dropped,
            $"{differType.Name} does not forward options its builder sets, so a property-level declaration loses them "
          + $"without a word: {string.Join(", ", dropped)}.");
    }

    private static void AssertWhitelistIsReachable(Type differ, string whitelistField, Type builderExtensions)
    {
        var whitelist = (HashSet<string>)differ
                       .GetField(whitelistField, BindingFlags.NonPublic | BindingFlags.Static)!
                       .GetValue(null)!;

        Assert.IsNotEmpty(whitelist, $"{differ.Name}.{whitelistField} is empty — has it been renamed?");

        var reachable = KeysSetBy(builderExtensions).SelectMany(m => m.Keys).ToHashSet(StringComparer.Ordinal);

        var unreachable = whitelist.Except(reachable).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.IsEmpty(
            unreachable,
            $"{differ.Name} forwards {string.Join(", ", unreachable)} onto index operations, but "
          + $"{builderExtensions.Name} offers no way to set them. Either add the builder method or drop "
          + "the key from the whitelist.");
    }

    /// <summary>
    /// Invokes every option method of <paramref name="builderExtensions"/> on a fresh builder and
    /// reports the annotation keys each one sets.
    /// </summary>
    internal static IEnumerable<(string Method, IReadOnlyCollection<string> Keys)> KeysSetBy(Type builderExtensions)
    {
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

            yield return (method.Name, builder.Annotations.Keys.ToList());
        }
    }

    private static object SampleArgument(ParameterInfo parameter, string methodName)
    {
        var type = parameter.ParameterType;

        if (type == typeof(bool))     return true;
        if (type == typeof(int))      return 1;               // fill factor is range-checked to 1..100
        if (type == typeof(string))   return "sample";
        if (type == typeof(string[])) return new[] { "sample" };
        if (type == typeof(object))   return 1;               // storage parameter value
        if (type.IsEnum)              return Enum.GetValues(type).GetValue(0)!;

        throw new NotSupportedException(
            $"Cannot synthesize a '{type}' argument for {methodName}({parameter.Name}). Extend "
          + $"{nameof(SampleArgument)} so this option is covered by the parity check.");
    }
}
