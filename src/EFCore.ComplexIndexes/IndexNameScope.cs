namespace EFCore.ComplexIndexes;

/// <summary>
/// The namespace in which a provider requires index names to be unique. A differ checks every
/// complex index name against this scope at <c>migrations add</c>, because a name reused inside it
/// scaffolds cleanly and only fails when the migration is applied.
/// </summary>
public enum IndexNameScope
{
    /// <summary>Unique among the indexes of one table — SQL Server, MySQL.</summary>
    Table,

    /// <summary>Unique among all relations of one schema — PostgreSQL.</summary>
    Schema,

    /// <summary>
    /// Unique across the whole database, whatever schema a table is configured with — SQLite, whose
    /// provider keeps a configured schema in the model but leaves it out of the DDL.
    /// </summary>
    Database
}
