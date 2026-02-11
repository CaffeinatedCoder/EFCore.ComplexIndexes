namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class CompositeIndexSerializerTests
{
    [TestMethod(DisplayName = "Roundtrips definitions")]
    public void Roundtrips_definitions()
    {
        var definitions = new List<CompositeIndexDefinition>
                          {
                              new()
                              {
                                  PropertyPaths = ["LastName", "EmailAddress.Value"],
                                  IsUnique      = true,
                                  Filter        = "deleted_at IS NULL",
                                  IndexName     = "IX_custom"
                              },
                              new()
                              {
                                  PropertyPaths = ["FirstName", "LastName"],
                                  IsUnique      = false,
                                  Filter        = null,
                                  IndexName     = null
                              }
                          };

        var json         = CompositeIndexSerializer.Serialize(definitions);
        var deserialized = CompositeIndexSerializer.Deserialize(json);

        Assert.AreEqual(2,              deserialized.Count);
        Assert.AreEqual(definitions[0], deserialized[0]);
        Assert.AreEqual(definitions[1], deserialized[1]);
    }

    [TestMethod(DisplayName = "Deserialize empty returns empty list")]
    public void Deserialize_empty_returns_empty_list()
    {
        var result = CompositeIndexSerializer.Deserialize("[]");
        Assert.IsEmpty(result);
    }

    [TestMethod(DisplayName = "Null fields omitted in JSON")]
    public void Null_fields_omitted_in_json()
    {
        var definitions = new List<CompositeIndexDefinition>
                          {
                              new()
                              {
                                  PropertyPaths = ["A", "B"],
                                  IsUnique      = false,
                                  Filter        = null,
                                  IndexName     = null
                              }
                          };

        var json = CompositeIndexSerializer.Serialize(definitions);

        Assert.DoesNotContain("filter", json);
        Assert.DoesNotContain("name",   json);
    }
}