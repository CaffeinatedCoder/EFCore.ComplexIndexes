namespace EFCore.ComplexIndexes.SqlServer;

/// <summary>
/// SQL Server annotation key constants for index features.
/// These mirror <c>SqlServerAnnotationNames</c> from the SQL Server provider.
/// </summary>
internal static class SqlServerAnnotations
{
    public const string Clustered       = "SqlServer:Clustered";
    public const string Include         = "SqlServer:Include";
    public const string CreatedOnline   = "SqlServer:Online";
    public const string FillFactor      = "SqlServer:FillFactor";
    public const string SortInTempDb    = "SqlServer:SortInTempDb";
    public const string DataCompression = "SqlServer:DataCompression";
}
