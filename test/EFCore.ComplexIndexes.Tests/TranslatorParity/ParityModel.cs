using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

// The entity every sweep row is written against. Public, because the rows are C# compiled at run
// time by Roslyn, so the lambdas have the exact shapes the C# compiler gives them (a char operand
// promoted to int, a lifted comparison, string.Concat for +), which a hand-built expression tree
// would only imitate.
//
// One property per operand shape the translator has to handle: each scalar type a filter may compare
// non-nullable and nullable, the same set inside a ToJson() document, and a value object mapped
// through a converter, whose member the core resolves to the converted column.

public sealed class ParityRow
{
    public int      Id    { get; set; }
    public string   Str   { get; set; } = "";
    public string   Str2  { get; set; } = "";
    public int      Int   { get; set; }
    public long     Long  { get; set; }
    public short    Short { get; set; }
    public byte     Byte  { get; set; }
    public decimal  Dec   { get; set; }
    public double   Dbl   { get; set; }
    public float    Flt   { get; set; }
    public bool     Bool  { get; set; }
    public char     Chr   { get; set; }
    public Guid     Guid  { get; set; }
    public DateTime Date  { get; set; }

    public string?   NStr   { get; set; }
    public string?   NStr2  { get; set; }
    public int?      NInt   { get; set; }
    public long?     NLong  { get; set; }
    public short?    NShort { get; set; }
    public byte?     NByte  { get; set; }
    public decimal?  NDec   { get; set; }
    public double?   NDbl   { get; set; }
    public float?    NFlt   { get; set; }
    public bool?     NBool  { get; set; }
    public char?     NChr   { get; set; }
    public Guid?     NGuid  { get; set; }
    public DateTime? NDate  { get; set; }

    public ParityDoc   Doc   { get; set; } = new();
    public ParityEmail Email { get; set; } = new();
}

public sealed class ParityDoc
{
    public string   Str   { get; set; } = "";
    public int      Int   { get; set; }
    public long     Long  { get; set; }
    public short    Short { get; set; }
    public decimal  Dec   { get; set; }
    public double   Dbl   { get; set; }
    public bool     Bool  { get; set; }
    public char     Chr   { get; set; }
    public Guid     Guid  { get; set; }
    public DateTime Date  { get; set; }

    public string? NStr  { get; set; }
    public int?    NInt  { get; set; }
    public bool?   NBool { get; set; }
}

public sealed class ParityEmail
{
    public string Value { get; set; } = "";
}

internal static class ParityModel
{
    public const string Table = "parity_rows";

    public static void Map(EntityTypeBuilder<ParityRow> b)
    {
        b.ToTable(Table);
        b.HasKey(x => x.Id);
        b.ComplexProperty(x => x.Doc, d => d.ToJson());
        b.Property(x => x.Email).HasConversion(e => e.Value, v => new ParityEmail { Value = v });
    }
}

/// <summary>EF Core's side of the sweep: the same mapping, no indexes, a cached model.</summary>
internal sealed class ParityQueryContext(DbContextOptions<ParityQueryContext> options) : DbContext(options)
{
    public DbSet<ParityRow> Rows => Set<ParityRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<ParityRow>(ParityModel.Map);
}

/// <summary>
/// The package's side: the same mapping with one index per declaration. Built without service
/// provider caching, so each instance runs its own <see cref="OnModelCreating"/>, which is what lets
/// a failing set of declarations be split until the one that fails at <c>migrations add</c> is found.
/// </summary>
internal sealed class ParityIndexContext(DbContextOptions<ParityIndexContext> options, IReadOnlyList<Action<EntityTypeBuilder<ParityRow>>> declarations)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ParityRow>(b =>
        {
            ParityModel.Map(b);
            foreach (var declare in declarations)
                declare(b);
        });
}
