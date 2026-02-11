<p align="center">
  <img width="300" height="300" align="center" alt="efcore-complexindexes-logo" src="https://github.com/user-attachments/assets/9b51234a-90e4-44af-91a3-443d159f6d1d" />
</p>

| **NuGet** | [![nuget](https://img.shields.io/nuget/v/EFCore.ComplexIndexes.svg)](https://www.nuget.org/packages/EFCore.ComplexIndexes/)

## Index support for complex type properties in EF Core migrations — the missing piece for value object-driven architectures.
EF Core 8.0 introduced complex properties, but migration tooling doesn't automatically generate indexes for these nested value objects. This NuGet package bridges that gap with a clean, fluent API for defining single-column, composite, unique, and filtered indexes directly on complex type properties.

### Why it matters:
- Value Object Indexing: Seamlessly add database indexes to properties buried inside complex types (e.g., Person.EmailAddress.Value)
- DDD-Friendly: Supports the Domain-Driven Design pattern of encapsulating logic in value objects without sacrificing database performance
- Migration-Aware: Automatically generates proper CREATE INDEX and DROP INDEX operations during EF Core migrations
- Flexible Filtering: Supports SQL WHERE clauses for filtered indexes (e.g., soft deletes)
- Composite Indexes: Define multi-column indexes spanning both scalar and nested properties with a single, intuitive expression

### Quick Example:

``` csharp
builder.ComplexProperty(x => x.EmailAddress, c => 
  c.Property(x => x.Value)
   .HasComplexIndex(isUnique: true, filter: "deleted_at IS NULL")
);

builder.HasComplexCompositeIndex(x => new { x.Name, x.EmailAddress.Value }, isUnique: true);
```

The package integrates seamlessly with EF Core's design-time tooling, requiring zero additional ceremony—just configure and migrate.
