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

    [TestMethod(DisplayName = "Roundtrips ordered parts mixing columns and expressions")]
    public void Roundtrips_parts()
    {
        var definitions = new List<CompositeIndexDefinition>
                          {
                              new()
                              {
                                  Parts =
                                  [
                                      new IndexPartDefinition { PropertyPath = "Name" },
                                      new IndexPartDefinition { Expression = "lower(\"Email\")" }
                                  ],
                                  IsUnique = true
                              }
                          };

        var json         = CompositeIndexSerializer.Serialize(definitions);
        var deserialized = CompositeIndexSerializer.Deserialize(json);

        Assert.AreEqual(definitions[0], deserialized[0]);
        Assert.AreEqual(2, deserialized[0].EffectiveParts.Count);
        Assert.IsFalse(deserialized[0].EffectiveParts[0].IsExpression);
        Assert.IsTrue(deserialized[0].EffectiveParts[1].IsExpression);
    }

    [TestMethod(DisplayName = "Legacy paths-only JSON still deserializes via EffectiveParts")]
    public void Legacy_paths_json_deserializes()
    {
        // JSON written before expression support — only the "paths" field is present.
        var deserialized = CompositeIndexSerializer.Deserialize("""[{"paths":["A","B"],"unique":true}]""");

        var def = Assert.ContainsSingle(deserialized);
        Assert.AreEqual(2, def.EffectiveParts.Count);
        Assert.AreEqual("A", def.EffectiveParts[0].PropertyPath);
        Assert.AreEqual("B", def.EffectiveParts[1].PropertyPath);
        Assert.IsFalse(def.EffectiveParts[0].IsExpression);
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
        Assert.DoesNotContain("props",  json);
    }
}