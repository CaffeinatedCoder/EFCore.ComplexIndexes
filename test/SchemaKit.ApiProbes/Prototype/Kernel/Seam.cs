using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// The open seam on the declaration side: every declaration of every contributor implements it.
/// The kernel stores, reads and routes declarations through this interface and never switches over
/// a contributor's types.
/// </summary>
public interface ISchemaObject
{
    /// <summary>The contributor that owns the declaration, and the annotation key's middle segment.</summary>
    string Owner { get; }

    /// <summary>The explicit name, or null for a default-named declaration.</summary>
    string? Name { get; }

    /// <summary>
    /// Store identity. Declaring a second object with the same identity on an entity replaces the
    /// first, as re-calling <c>HasIndex</c> does.
    /// </summary>
    string Identity { get; }

    /// <summary>The v2 annotation value: canonical JSON of the declaration (#58).</summary>
    string ToAnnotationValue();
}

/// <summary>
/// A contributor's declaration union. The static members let the read model deserialize a
/// contributor's annotations without a service provider, which <c>OnModelCreating</c> does not have.
/// </summary>
public interface ISchemaDeclaration<TSelf> : ISchemaObject where TSelf : class, ISchemaDeclaration<TSelf>
{
    static abstract string OwnerId { get; }

    static abstract TSelf FromAnnotationValue(string json);
}

/// <summary>Marks a declaration PostgreSQL can render. Open: CodoMetis.Hierarchies' ltree objects implement it too.</summary>
public interface IPostgresObject : ISchemaObject;

/// <summary>Marks a declaration SQL Server can render.</summary>
public interface ISqlServerObject : ISchemaObject;

/// <summary>Applying, reading and amending declarations: one call each, on EF's own metadata.</summary>
public static class SchemaDeclarationExtensions
{
    /// <summary>Every v2 annotation is <c>CodoMetis:{Owner}:{Key}</c> (#58).</summary>
    public const string Prefix = "CodoMetis:";

    extension(IMutableEntityType entityType)
    {
        /// <summary>
        /// Stores each declaration as its own annotation. The key is derived from the identity, so a
        /// second declaration with the same identity overwrites the first without a lookup.
        /// </summary>
        public void Declare(IEnumerable<ISchemaObject> declarations)
        {
            foreach (var declaration in declarations)
                entityType.SetAnnotation(AnnotationKey(declaration), declaration.ToAnnotationValue());
        }

        /// <summary>
        /// Replaces each of a contributor's declarations with <paramref name="amend"/>'s result: the
        /// functional form of 5.x <c>AddComplexIndexFilter</c> and <c>AddExclusionConstraintFilter</c>.
        /// </summary>
        public int Redeclare<TDeclaration>(Func<TDeclaration, TDeclaration> amend)
            where TDeclaration : class, ISchemaDeclaration<TDeclaration>
        {
            var changed = 0;
            foreach (var declaration in ((IReadOnlyEntityType)entityType).GetDeclarations<TDeclaration>())
            {
                var amended = amend(declaration);
                if (amended.Equals(declaration)) continue;
                entityType.RemoveAnnotation(AnnotationKey(declaration));
                entityType.Declare([amended]);
                changed++;
            }
            return changed;
        }
    }

    extension(IReadOnlyEntityType entityType)
    {
        /// <summary>The read model: the same values the declarations were, and the ones the differ reads.</summary>
        public IReadOnlyList<TDeclaration> GetDeclarations<TDeclaration>()
            where TDeclaration : class, ISchemaDeclaration<TDeclaration>
            => [.. entityType.GetAnnotations()
                .Where(a => a.Name.StartsWith($"{Prefix}{TDeclaration.OwnerId}:", StringComparison.Ordinal))
                .Select(a => TDeclaration.FromAnnotationValue((string)a.Value!))];
    }

    /// <summary>
    /// <c>CodoMetis:{Owner}:{hash}</c>, the hash being the first 16 hex digits of SHA-256 over the
    /// identity. The name lives in the value, so a rename rewrites one annotation in place.
    /// </summary>
    public static string AnnotationKey(ISchemaObject declaration) =>
        $"{Prefix}{declaration.Owner}:{Hash(declaration.Identity)}";

    private static string Hash(string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
}
