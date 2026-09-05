using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// <c>EnsureCreated()</c> and <c>GenerateCreateScript()</c> build the schema through the
/// <em>runtime</em> <c>IMigrationsModelDiffer</c>, not the design-time one the <c>.targets</c> wire
/// up. Without a runtime registration they use EF's stock differ, which cannot see this package's
/// declarations: the tables are created and the indexes are simply absent — no error, nothing in a
/// log. These tests pin both halves: the registration makes the declarations appear, and its absence
/// is exactly the silent omission the feature exists to close.
/// </summary>
[TestClass]
public class RuntimeRegistrationTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid                 Id     { get; set; }
        public string               Name   { get; set; } = "";
        public EmailAddress         Email  { get; set; } = new();
        public NpgsqlRange<DateOnly> Period { get; set; }
    }

    private class PersonContext(DbContextOptions<PersonContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Period);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_people_email");
            });
    }

    // The PostgreSQL shape adds the two things only the Npgsql differ produces: an exclusion
    // constraint (design-time DDL) and an expression index (rendered by the custom generator).
    private class NpgsqlPersonContext(DbContextOptions<NpgsqlPersonContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_people_email");
                b.HasExclusionConstraint(x => x.Name, x => x.Period, name: "ex_people_name_period");
                b.HasExpressionIndex("lower(email)", indexName: "ix_people_email_ci");
            });
    }

    // ── SQLite: EnsureCreated against a real (in-memory) database ──

    private static List<string> SqliteIndexesAfterEnsureCreated(Func<SqliteConnection, DbContextOptions<PersonContext>> configure)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var context = new PersonContext(configure(connection)))
            Assert.IsTrue(context.Database.EnsureCreated(), "EnsureCreated should have created the schema.");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'people' AND sql IS NOT NULL";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [TestMethod(DisplayName = "EnsureCreated builds the complex index once the differ is registered at runtime")]
    public void EnsureCreated_includes_complex_index_with_UseComplexIndexes()
    {
        var indexes = SqliteIndexesAfterEnsureCreated(connection =>
            new DbContextOptionsBuilder<PersonContext>().UseSqlite(connection).UseComplexIndexes().Options);

        Assert.Contains("ux_people_email", indexes);
    }

    [TestMethod(DisplayName = "AddComplexIndexes registers the differ on a custom internal service provider")]
    public void EnsureCreated_includes_complex_index_with_AddComplexIndexes()
    {
        var provider = new ServiceCollection()
                      .AddEntityFrameworkSqlite()
                      .AddComplexIndexes()
                      .BuildServiceProvider();

        var indexes = SqliteIndexesAfterEnsureCreated(connection =>
            new DbContextOptionsBuilder<PersonContext>()
               .UseSqlite(connection)
               .UseInternalServiceProvider(provider)
               .Options);

        Assert.Contains("ux_people_email", indexes);
    }

    /// <summary>
    /// The premise, kept as a test so that the day EF's own differ starts seeing these declarations
    /// the registration can be retired knowingly rather than left as ceremony.
    /// </summary>
    [TestMethod(DisplayName = "Without the runtime registration, EnsureCreated silently omits the complex index")]
    public void EnsureCreated_omits_complex_index_without_registration()
    {
        var indexes = SqliteIndexesAfterEnsureCreated(connection =>
            new DbContextOptionsBuilder<PersonContext>().UseSqlite(connection).Options);

        Assert.DoesNotContain("ux_people_email", indexes);
    }

    // ── PostgreSQL and SQL Server: GenerateCreateScript, the same code path without a server ──

    private static string NpgsqlCreateScript(bool wired)
    {
        var builder = new DbContextOptionsBuilder<NpgsqlPersonContext>().UseNpgsql(MigrationHarness.NpgsqlConnection);
        if (wired)
            builder.UseNpgsqlComplexIndexes();

        using var context = new NpgsqlPersonContext(builder.Options);
        return context.Database.GenerateCreateScript();
    }

    [TestMethod(DisplayName = "UseNpgsqlComplexIndexes makes GenerateCreateScript include indexes, exclusion constraints and expression indexes")]
    public void Npgsql_create_script_includes_declarations_when_wired()
    {
        var script = NpgsqlCreateScript(wired: true);

        // Npgsql leaves identifiers that need no quoting bare.
        StringAssert.Contains(script, "CREATE UNIQUE INDEX ux_people_email ON people (email)");
        StringAssert.Contains(script, "EXCLUDE USING gist");
        StringAssert.Contains(script, "ex_people_name_period");
        // Rendered by the custom generator, which the same call registers — no sentinel column leaks.
        StringAssert.Contains(script, "CREATE INDEX ix_people_email_ci ON people ((lower(email)))");
        Assert.DoesNotContain(CustomMigrationsModelDiffer.RuntimeWiringSentinel, script);
    }

    [TestMethod(DisplayName = "Without UseNpgsqlComplexIndexes, GenerateCreateScript has none of the declarations")]
    public void Npgsql_create_script_omits_declarations_when_not_wired()
    {
        var script = NpgsqlCreateScript(wired: false);

        StringAssert.Contains(script, "CREATE TABLE people");
        Assert.DoesNotContain("ux_people_email", script);
        Assert.DoesNotContain("EXCLUDE", script);
        Assert.DoesNotContain("ix_people_email_ci", script);
    }

    private static string SqlServerCreateScript(bool wired)
    {
        var builder = new DbContextOptionsBuilder<PersonContext>().UseSqlServer(MigrationHarness.SqlServerConnection);
        if (wired)
            builder.UseSqlServerComplexIndexes();

        using var context = new PersonContext(builder.Options);
        return context.Database.GenerateCreateScript();
    }

    [TestMethod(DisplayName = "UseSqlServerComplexIndexes makes GenerateCreateScript include the complex index")]
    public void SqlServer_create_script_includes_index_when_wired()
        => StringAssert.Contains(SqlServerCreateScript(wired: true), "CREATE UNIQUE INDEX [ux_people_email] ON [people] ([email])");

    [TestMethod(DisplayName = "Without UseSqlServerComplexIndexes, GenerateCreateScript omits the complex index")]
    public void SqlServer_create_script_omits_index_when_not_wired()
    {
        var script = SqlServerCreateScript(wired: false);

        StringAssert.Contains(script, "CREATE TABLE [people]");
        Assert.DoesNotContain("ux_people_email", script);
    }
}
