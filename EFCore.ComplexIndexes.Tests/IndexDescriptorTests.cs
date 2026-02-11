namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class IndexDescriptorTests
{
    [TestMethod(DisplayName = "Equal descriptors match")]
    public void Equal_descriptors_match()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", ["email"], "IX_email", true, "x IS NULL");
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", ["email"], "IX_email", true, "x IS NULL");

        Assert.AreEqual(a,               b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod(DisplayName = "Different columns do not match")]
    public void Different_columns_do_not_match()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", ["email"], "IX_email", true, null);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", "public", ["name"],  "IX_email", true, null);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod(DisplayName = "Column order matters")]
    public void Column_order_matters()
    {
        var a = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, ["a", "b"], "IX_ab", false, null);
        var b = new CustomMigrationsModelDiffer.IndexDescriptor("person", null, ["b", "a"], "IX_ab", false, null);

        Assert.AreNotEqual(a, b);
    }
}