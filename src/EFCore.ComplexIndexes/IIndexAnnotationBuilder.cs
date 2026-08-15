namespace EFCore.ComplexIndexes;

/// <summary>
/// Shared hook implemented by the index option builders (<see cref="ComplexIndexBuilder"/> in core and
/// the provider-specific expression-index builders) so that provider satellite packages can offer a
/// single set of option extension methods (e.g. <c>UseGin</c>) that work on either builder.
/// </summary>
public interface IIndexAnnotationBuilder
{
    internal Dictionary<string, object?> Annotations { get; }
}
