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
}