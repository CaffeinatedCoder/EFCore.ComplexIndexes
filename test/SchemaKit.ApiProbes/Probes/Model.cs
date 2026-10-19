using NpgsqlTypes;

namespace SchemaKit.ApiProbes.Model;

// The entities every probe declares against. Public, because each probe is compiled as its own
// assembly referencing this one.

public interface IWithdrawable
{
    DateTimeOffset? RevokedAt { get; }
}

public sealed class RoleGrant : IWithdrawable
{
    public int Id { get; set; }
    public int GranteeId { get; set; }
    public int RoleId { get; set; }
    public NpgsqlRange<DateTime> Period { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string Kind { get; set; } = "";
}

public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Email Email { get; set; } = new();
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public int Rank { get; set; }
    public string[] Tags { get; set; } = [];
    public Payload Payload { get; set; } = new();
    public Status Status { get; set; }
}

public sealed class Email
{
    public string Value { get; set; } = "";
}

public sealed class Payload
{
    public string Kind { get; set; } = "";
}

public enum Status { Active, Retired }

public sealed class Order
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public bool Cancelled { get; set; }
}

/// <summary>Stands in for an ltree path type a CodoMetis.Hierarchies package would ship.</summary>
public readonly record struct TreePath(string Value);

public sealed class Category
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public TreePath Path { get; set; }
}

/// <summary>Stands in for a CodoMetis.ValueRanges range type SchemaKit knows nothing about.</summary>
public readonly record struct DayRange(DateOnly Start, DateOnly End);

public sealed class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public DayRange Validity { get; set; }
}
