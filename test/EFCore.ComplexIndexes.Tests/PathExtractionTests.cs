namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class PathExtractionTests
{
    private class Person
    {
        public string       FirstName    { get; set; } = "";
        public string       LastName     { get; set; } = "";
        public EmailAddress EmailAddress { get; set; } = new();
        public Address      Address      { get; set; } = new();
    }

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Address
    {
        public string  Street  { get; set; } = "";
        public ZipCode ZipCode { get; set; } = new();
    }

    private class ZipCode
    {
        public string Value { get; set; } = "";
    }

    [TestMethod(DisplayName = "Extracts single level property")]
    public void Extracts_single_level_property()
    {
        var paths = ComplexIndexExtensions
           .ExtractPropertyPaths<Person, object>(x => new { x.FirstName, x.LastName });
        List<string> expectedPaths = ["FirstName", "LastName"];

        Assert.IsTrue(expectedPaths.SequenceEqual(paths));
    }

    [TestMethod(DisplayName = "Extracts nested complex property")]
    public void Extracts_nested_complex_property()
    {
        var paths = ComplexIndexExtensions
           .ExtractPropertyPaths<Person, object>(x => new { x.LastName, x.EmailAddress.Value });
        List<string> expectedPaths = ["LastName", "EmailAddress.Value"];

        Assert.IsTrue(expectedPaths.SequenceEqual(paths));
    }

    [TestMethod(DisplayName = "Extracts deeply nested complex property")]
    public void Extracts_deeply_nested_complex_property()
    {
        var paths = ComplexIndexExtensions
           .ExtractPropertyPaths<Person, object>(x => new
                                                      {
                                                          ZipCode      = x.Address.ZipCode.Value,
                                                          EmailAddress = x.EmailAddress.Value
                                                      });

        List<string> expectedPaths = ["Address.ZipCode.Value", "EmailAddress.Value"];

        Assert.IsTrue(expectedPaths.SequenceEqual(paths));
    }

    [TestMethod(DisplayName = "Extracts per-column direction from DbOrder markers")]
    public void Extracts_direction_from_dborder_markers()
    {
        var parts = ComplexIndexExtensions
           .ExtractIndexParts<Person, object>(x => new { x.FirstName, Email = DbOrder.Desc(x.EmailAddress.Value) });

        Assert.AreEqual("FirstName", parts[0].PropertyPath);
        Assert.IsFalse(parts[0].Descending);

        Assert.AreEqual("EmailAddress.Value", parts[1].PropertyPath);
        Assert.IsTrue(parts[1].Descending);
    }

    [TestMethod(DisplayName = "DbOrder markers do not affect extracted paths")]
    public void DbOrder_markers_do_not_affect_paths()
    {
        var paths = ComplexIndexExtensions
           .ExtractPropertyPaths<Person, object>(x => new { x.FirstName, Email = DbOrder.Desc(x.EmailAddress.Value) });

        List<string> expectedPaths = ["FirstName", "EmailAddress.Value"];
        Assert.IsTrue(expectedPaths.SequenceEqual(paths));
    }

    [TestMethod(DisplayName = "Throws for non anonymous type")]
    public void Throws_for_non_anonymous_type()
    {
        Assert.Throws<ArgumentException>(() => ComplexIndexExtensions.ExtractPropertyPaths<Person, string>(x => x.FirstName));
    }

    [TestMethod(DisplayName = "Throws for single property")]
    public void Throws_for_single_property()
    {
        // This would be caught by HasComplexCompositeIndex, but good to verify
        var paths = ComplexIndexExtensions.ExtractPropertyPaths<Person, object>(x => new { x.FirstName });

        Assert.ContainsSingle(paths);
    }

    // A member chain that does not start at the lambda parameter yields a well-formed dotted path
    // no property lookup can ever match, so it has to be rejected where it is written.

    private static readonly Person Other = new() { FirstName = "static" };

    [TestMethod(DisplayName = "Throws when a selector reads a captured variable instead of the parameter")]
    public void Throws_for_captured_variable()
    {
        var captured = new Person { FirstName = "captured" };

        var ex = Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractPropertyPaths<Person, object>(x => new { x.LastName, captured.FirstName }));

        StringAssert.Contains(ex.Message, "does not start from the lambda parameter 'x'");
    }

    [TestMethod(DisplayName = "Throws when a selector reads a static member")]
    public void Throws_for_static_member()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractPropertyPaths<Person, object>(x => new { x.LastName, Other.FirstName }));

        StringAssert.Contains(ex.Message, "does not start from the lambda parameter");
    }

    [TestMethod(DisplayName = "Throws when a single-member selector reads a captured variable")]
    public void Throws_for_captured_variable_in_single_selector()
    {
        var captured = new Person { FirstName = "captured" };

        var ex = Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractSinglePath((System.Linq.Expressions.Expression<Func<Person, object?>>)
                                                           (x => captured.EmailAddress.Value)));

        StringAssert.Contains(ex.Message, "captured.EmailAddress.Value");
        StringAssert.Contains(ex.Message, "do not map to a column");
    }

    [TestMethod(DisplayName = "DbOrder markers do not bypass the parameter check")]
    public void Markers_do_not_bypass_parameter_check()
    {
        var captured = new Person { FirstName = "captured" };

        Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractPropertyPaths<Person, object>(
                x => new { x.LastName, First = DbOrder.Desc(captured.FirstName) }));
    }

    // ── Marker composition ──

    [TestMethod(DisplayName = "DbOrder.Asc marks a column ascending")]
    public void Asc_marks_ascending()
    {
        var parts = ComplexIndexExtensions
           .ExtractIndexParts<Person, object>(x => new { First = DbOrder.Asc(x.FirstName), Last = DbOrder.Desc(x.LastName) });

        Assert.IsFalse(parts[0].Descending);
        Assert.IsTrue(parts[1].Descending);
    }

    [TestMethod(DisplayName = "Composing different marker kinds works in any order")]
    public void Different_marker_kinds_compose()
    {
        var parts = ComplexIndexExtensions
           .ExtractIndexParts<Person, object>(x => new
                                                   {
                                                       A = DbOrder.NullsLast(DbOrder.Desc(x.FirstName)),
                                                       B = DbOrder.Desc(DbOrder.NullsFirst(x.LastName))
                                                   });

        Assert.IsTrue(parts[0].Descending);
        Assert.AreEqual(DbNullSort.Last, parts[0].NullSort);
        Assert.IsTrue(parts[1].Descending);
        Assert.AreEqual(DbNullSort.First, parts[1].NullSort);
    }

    [TestMethod(DisplayName = "Asc combined with Desc is rejected instead of silently picking one")]
    public void Conflicting_direction_markers_throw()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractIndexParts<Person, object>(
                x => new { x.LastName, First = DbOrder.Asc(DbOrder.Desc(x.FirstName)) }));

        StringAssert.Contains(ex.Message, "Conflicting DbOrder.Asc/DbOrder.Desc markers");
    }

    [TestMethod(DisplayName = "NullsFirst combined with NullsLast is rejected")]
    public void Conflicting_null_sort_markers_throw()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ComplexIndexExtensions.ExtractIndexParts<Person, object>(
                x => new { x.LastName, First = DbOrder.NullsFirst(DbOrder.NullsLast(x.FirstName)) }));

        StringAssert.Contains(ex.Message, "Conflicting DbOrder.NullsFirst/DbOrder.NullsLast markers");
    }

    [TestMethod(DisplayName = "Repeating the same marker is harmless")]
    public void Repeated_marker_is_allowed()
    {
        var parts = ComplexIndexExtensions
           .ExtractIndexParts<Person, object>(x => new { x.LastName, First = DbOrder.Desc(DbOrder.Desc(x.FirstName)) });

        Assert.IsTrue(parts[1].Descending);
    }

    // ── Part copying ──

    [TestMethod(DisplayName = "WithSortOptions preserves every part member")]
    public void WithSortOptions_preserves_all_members()
    {
        var template = new IndexPartDefinition { Template = "lower({Email.Value})" };

        var descending = template.WithSortOptions(descending: true);
        Assert.AreEqual("lower({Email.Value})", descending.Template);
        Assert.IsTrue(descending.Descending);

        var path = new IndexPartDefinition { PropertyPath = "Email.Value", Descending = true };
        var sorted = path.WithSortOptions(nullSort: DbNullSort.Last);
        Assert.AreEqual("Email.Value", sorted.PropertyPath);
        Assert.IsTrue(sorted.Descending, "Unspecified options must be carried over.");
        Assert.AreEqual(DbNullSort.Last, sorted.NullSort);
    }
}