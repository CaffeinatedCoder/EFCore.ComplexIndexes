namespace EFCore.ComplexIndexes;

/// <summary>
/// Builder for configuring a complex-type index.
/// Provider satellites extend this with their own methods.
/// </summary>
public class ComplexIndexBuilder
{
    internal Dictionary<string, object?> Annotations { get; } = new();

    /// <summary>Marks this index as unique.</summary>
    public ComplexIndexBuilder IsUnique(bool unique = true)
    {
        Annotations[ComplexIndexAnnotations.IsUnique] = unique;
        return this;
    }

    /// <summary>Applies a SQL filter expression to this index.</summary>
    public ComplexIndexBuilder HasFilter(string filter)
    {
        Annotations[ComplexIndexAnnotations.Filter] = filter;
        return this;
    }

    /// <summary>Sets a custom name for this index.</summary>
    public ComplexIndexBuilder HasName(string name)
    {
        Annotations[ComplexIndexAnnotations.IndexName] = name;
        return this;
    }

    /// <summary>
    /// Sets a provider-specific annotation. Intended for use by provider satellite packages.
    /// </summary>
    internal ComplexIndexBuilder HasAnnotation(string key, object? value)
    {
        Annotations[key] = value;
        return this;
    }
}