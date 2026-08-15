namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class IndexDescriptorTests
{
    private static ResolvedIndexPart Col(string name) => new(false, name);

    [TestMethod(DisplayName = "Equal descriptors match")]
    public void Equal_descriptors_match()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", [Col("email")], "IX_email", true, "x IS NULL", []);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", [Col("email")], "IX_email", true, "x IS NULL", []);

        Assert.AreEqual(a,               b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod(DisplayName = "Different columns do not match")]
    public void Different_columns_do_not_match()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", [Col("email")], "IX_email", true, null, []);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", [Col("name")],  "IX_email", true, null, []);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod(DisplayName = "Column order matters")]
    public void Column_order_matters()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, [Col("a"), Col("b")], "IX_ab", false, null, []);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, [Col("b"), Col("a")], "IX_ab", false, null, []);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod(DisplayName = "Expression and column parts are distinct")]
    public void Expression_and_column_parts_are_distinct()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, [new ResolvedIndexPart(true, "email")], "IX_email", false, null, []);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, [Col("email")],                        "IX_email", false, null, []);

        Assert.AreNotEqual(a, b);
    }
}
