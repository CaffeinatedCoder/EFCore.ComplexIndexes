using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SchemaKit.ApiProbes.Probes;

/// <summary>
/// Compiles each probe of <see cref="ProbeCatalogue"/> with Roslyn against the prototype, as a
/// consumer assembly would: its own assembly, the prototype referenced as metadata, warnings as
/// errors (this repository's <c>Directory.Build.props</c> setting, and the one that makes CS8509 an
/// error).
/// </summary>
[TestClass]
public sealed class CompileProbeTests
{
    private const string Template = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;
        using NpgsqlTypes;
        using CodoMetis.EFCore.SchemaKit;
        using CodoMetis.EFCore.SchemaKit.PostgreSQL;
        using CodoMetis.EFCore.SchemaKit.SqlServer;
        using SchemaKit.ApiProbes.Model;

        /*TYPES*/

        public static class Configuration
        {
            public static void Configure(EntityTypeBuilder<Person> people, EntityTypeBuilder<RoleGrant> grants)
            {
                /*BODY*/
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    public static IEnumerable<object[]> Probes => ProbeCatalogue.All.Select(p => new object[] { p });

    [TestMethod]
    [DynamicData(nameof(Probes))]
    public void Allowed_form_compiles_clean(Probe probe)
    {
        var errors = Compile(probe, probe.Allowed);

        Assert.IsEmpty(errors, $"{probe.Id}: the allowed form must compile clean.\n{Describe(errors)}");
    }

    [TestMethod]
    [DynamicData(nameof(Probes))]
    public void Forbidden_form_fails_with_the_claimed_diagnostic(Probe probe)
    {
        var errors = Compile(probe, probe.Forbidden);

        Assert.IsTrue(errors.Any(d => d.Id == probe.Diagnostic),
            $"{probe.Id}: expected {probe.Diagnostic}.\n{Describe(errors)}");
    }

    /// <summary>Prints every forbidden form's first diagnostic, for the design document's table.</summary>
    [TestMethod]
    public void Print_diagnostics()
    {
        foreach (var probe in ProbeCatalogue.All)
        {
            var first = Compile(probe, probe.Forbidden).FirstOrDefault(d => d.Id == probe.Diagnostic);
            Console.WriteLine($"| `{probe.Id}` | {first?.Id} | {first?.GetMessage(CultureInfo.InvariantCulture)} |");
        }
    }

    private static IReadOnlyList<Diagnostic> Compile(Probe probe, string snippet)
    {
        var source = probe.Shape switch
        {
            ProbeShape.Statement => Template.Replace("/*TYPES*/", "").Replace("/*BODY*/", snippet),
            ProbeShape.TopLevel  => Template.Replace("/*TYPES*/", snippet).Replace("/*BODY*/", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(probe)),
        };

        var compilation = CSharpCompilation.Create(
            $"Probe_{probe.Id.Replace('.', '_').Replace('-', '_')}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"  {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}"));

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");
        return [.. platform.Concat(local)
            .Where(path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .GroupBy(Path.GetFileName)
            .Select(group => MetadataReference.CreateFromFile(group.First()))];
    }
}
