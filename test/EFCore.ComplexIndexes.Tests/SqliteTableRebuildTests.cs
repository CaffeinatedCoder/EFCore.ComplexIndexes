using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Sqlite.Design.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.Tests;

#pragma warning disable EF1001

/// <summary>
/// SQLite runs an <c>ALTER COLUMN</c>, a <c>DROP COLUMN</c> and every key or constraint change by
/// rebuilding the table, and EF Core re-creates only the indexes in its own model. Every complex
/// index on the table vanished, unique ones included, and the migration applied cleanly (#86). EF's
/// SQLite generator emits rebuilds after every other operation, so only a generator wrapped around it
/// can create an index after the rebuild: <c>UseComplexIndexes()</c> registers one (#95). Without it,
/// <c>migrations add</c> fails, naming the indexes and the ways out.
/// </summary>
/// <remarks>
/// Migrations are scaffolded with the differ the design-time services resolve, against the context
/// <c>dotnet ef</c> would use, and applied for real on in-memory SQLite through EF's own command
/// executor, which keeps the transactions <c>Migrate()</c> keeps.
/// </remarks>
[TestClass]
public class SqliteTableRebuildTests
{
    public class EmailAddress
    {
        public string Value  { get; set; } = "";
        public string Domain { get; set; } = "";
    }

    public class Member
    {
        public int          Id       { get; set; }
        public int          Code     { get; set; }
        public string?      Nickname { get; set; }
        public string?      Note     { get; set; }
        public int?         VisitId  { get; set; }
        public EmailAddress Email    { get; set; } = new();
    }

    public class Visit
    {
        public int     Id    { get; set; }
        public string? Notes { get; set; }
    }

    // EF Core 10 keeps a TPC type's complex properties out of its derived tables, so the index
    // a TPC base declares for its derived tables is over a plain property.
    public class Account
    {
        public int    Id   { get; set; }
        public string Code { get; set; } = "";
    }

    public class Partner : Account
    {
        public string? Region { get; set; }
    }

    private sealed class ModelContext(DbContextOptions options, Action<ModelBuilder> build) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => build(modelBuilder);
    }

    // Every model here is built by the same context type from a different delegate.
    private sealed class NoModelCache : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new object();
    }

    /// <summary>A generator another package might register: it leaves a table behind to prove it ran.</summary>
    public class AuditingGenerator(MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider annotations)
        : SqliteMigrationsSqlGenerator(dependencies, annotations)
    {
        public override IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations, IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
            => base.Generate([new SqlOperation { Sql = "CREATE TABLE IF NOT EXISTS \"audited\" (\"x\" INTEGER);" }, .. operations],
                             model, options);
    }

    /// <summary>Another package's options extension registering its generator in <c>ApplyServices</c>.</summary>
    private sealed class AuditingExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
            => services.AddScoped<IMigrationsSqlGenerator, AuditingGenerator>();

        public void Validate(IDbContextOptions options) { }

        private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool   IsDatabaseProvider => false;
            public override string LogFragment        => "";
            public override int    GetServiceProviderHashCode() => 0;
            public override bool   ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
            public override void   PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }

    // ── Models ──

    private static Action<ModelBuilder> Members(Action<EntityTypeBuilder<Member>> vary, bool withIndexes = true)
        => modelBuilder =>
        {
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.HasIndex(x => x.Nickname);
                b.ComplexProperty(x => x.Email);
                if (withIndexes)
                {
                    b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
                    b.HasComplexIndex(x => x.Email.Domain, filter: "\"Email_Domain\" <> ''", indexName: "ix_members_domain");
                }
                vary(b);
            });

            // A second table with no complex index, whose rebuild this package has nothing to say about.
            modelBuilder.Entity<Visit>().ToTable("visits");
        };

    private static readonly Action<EntityTypeBuilder<Member>> V1 = b => b.Property(x => x.Nickname).IsRequired();

    /// <summary>
    /// One change per operation kind that makes SQLite rebuild <c>members</c>, from V1. Applied up and
    /// back down, so the reverse kinds (drop unique, drop check, drop foreign key, …) run too.
    /// </summary>
    public static IEnumerable<object[]> RebuildKinds =>
    [
        ["AlterColumn",                 "members", (Action<EntityTypeBuilder<Member>>)(_ => { })],
        ["DropColumn",                  "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.Ignore(x => x.Note); })],
        ["AddUniqueConstraint",         "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.HasAlternateKey(x => x.Code); })],
        ["AddCheckConstraint",          "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.ToTable(t => t.HasCheckConstraint("ck_members_code", "\"Code\" > 0")); })],
        ["AddForeignKey",               "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId); })],
        ["AddPrimaryKey",               "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.HasKey(x => x.Code); })],
        ["AlterTable",                  "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.ToTable(t => t.HasComment("Club members")); })],
        ["AddColumn with a comment",    "members", (Action<EntityTypeBuilder<Member>>)(b => { V1(b); b.Property<string>("Extra").HasComment("Added later"); })],
        ["RenameTable and AlterColumn", "people",  (Action<EntityTypeBuilder<Member>>)(b => b.ToTable("people"))],
    ];

    private static IRelationalModel Model(SqliteConnection connection, Action<ModelBuilder> build)
    {
        var options = new DbContextOptionsBuilder().UseSqlite(connection).ReplaceService<IModelCacheKeyFactory, NoModelCache>().Options;
        using var context = new ModelContext(options, build);
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    // ── The context dotnet ef uses, and the one that applies the migration ──

    private static DbContextOptions Wired(SqliteConnection connection)
        => new DbContextOptionsBuilder().UseSqlite(connection).UseComplexIndexes().Options;

    private static DbContextOptions Unwired(SqliteConnection connection)
        => new DbContextOptionsBuilder().UseSqlite(connection).Options;

    /// <summary>
    /// <c>dotnet ef migrations add</c>: the design-time services built the way EF's
    /// <c>DesignTimeServicesBuilder</c> builds them, with this package's registration in them.
    /// </summary>
    private static IReadOnlyList<MigrationOperation> Scaffold(DbContextOptions options, IRelationalModel? source, IRelationalModel target)
    {
        using var context = new MigrationHarness.EmptyContext(options);
        using var provider = DesignTimeServices(context);
        return provider.GetRequiredService<IMigrationsModelDiffer>().GetDifferences(source, target);
    }

    private static ServiceProvider DesignTimeServices(DbContext context)
    {
        var services = new ServiceCollection();
        services.AddDbContextDesignTimeServices(context);
        new CustomDesignTimeServices().ConfigureDesignTimeServices(services);
        new SqliteDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddEntityFrameworkDesignTimeServices();
        return services.BuildServiceProvider();
    }

    /// <summary><c>Migrate()</c>: the context's generator, and EF's executor with its transactions.</summary>
    private static void Apply(DbContextOptions options, IReadOnlyList<MigrationOperation> operations, IRelationalModel target)
    {
        using var context = new MigrationHarness.EmptyContext(options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, target.Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    // ── What the database holds ──

    private static string? IndexSql(SqliteConnection connection, string index)
    {
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        sql.Parameters.AddWithValue("$name", index);
        return sql.ExecuteScalar() as string;
    }

    private static string? IndexTable(SqliteConnection connection, string index)
    {
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type = 'index' AND name = $name";
        sql.Parameters.AddWithValue("$name", index);
        return sql.ExecuteScalar() as string;
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var sql = connection.CreateCommand();
        sql.CommandText = commandText;
        sql.ExecuteNonQuery();
    }

    private static void InsertMember(SqliteConnection connection, string table, int id, string email)
        => Execute(connection, $"INSERT INTO \"{table}\" (\"Id\", \"Code\", \"Email_Value\", \"Email_Domain\", \"Nickname\") "
                             + $"VALUES ({id}, {id}, '{email}', 'example.org', 'n{id}')");

    /// <summary>Both complex indexes exist on <paramref name="table"/>, as declared, and the unique one enforces.</summary>
    private static void AssertComplexIndexesHold(SqliteConnection connection, string table, int probeId)
    {
        Assert.AreEqual(table, IndexTable(connection, "ux_members_email"), "ux_members_email");
        Assert.AreEqual(table, IndexTable(connection, "ix_members_domain"), "ix_members_domain");
        StringAssert.EndsWith(IndexSql(connection, "ix_members_domain"), "WHERE \"Email_Domain\" <> ''");

        var duplicate = Assert.ThrowsExactly<SqliteException>(() => InsertMember(connection, table, probeId, "first@example.org"));
        StringAssert.Contains(duplicate.Message, "UNIQUE constraint failed");
    }

    private static void EnableDoubleQuotedStringLiterals(SqliteConnection connection)
    {
        const int dqsDdl = 1014; // SQLITE_DBCONFIG_DQS_DDL
        Assert.AreEqual(0, SQLitePCL.raw.sqlite3_db_config(connection.Handle!, dqsDdl, 1, out int enabled));
        Assert.AreEqual(1, enabled);
    }

    // ── With UseComplexIndexes(): the indexes come back ──

    [TestMethod(DisplayName = "A rebuild re-creates the complex indexes, up and down, for every kind of rebuild")]
    [DynamicData(nameof(RebuildKinds))]
    public void Rebuild_recreates_complex_indexes(string kind, string table, Action<EntityTypeBuilder<Member>> v2)
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Wired(connection);

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(v2));
        Apply(options, Scaffold(options, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");

        var up = Scaffold(options, source, target);
        Assert.IsTrue(up.OfType<CreateIndexOperation>().Any(c => c.Name == "ux_members_email"), kind);
        Apply(options, up, target);
        AssertComplexIndexesHold(connection, table, 2);

        Apply(options, Scaffold(options, target, source), source);
        AssertComplexIndexesHold(connection, "members", 3);
    }

    [TestMethod(DisplayName = "A complex index added or changed in the migration that rebuilds its table is created after the rebuild")]
    public void Added_and_changed_indexes_survive_the_rebuild()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Wired(connection);

        var source = Model(connection, Members(V1, withIndexes: false));
        var target = Model(connection, Members(_ => { }));
        Apply(options, Scaffold(options, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");

        Apply(options, Scaffold(options, source, target), target);
        AssertComplexIndexesHold(connection, "members", 2);

        // The filter changes, the column alteration goes back: a drop, a rebuild, a create.
        var changed = Model(connection, modelBuilder =>
        {
            Members(V1, withIndexes: false)(modelBuilder);
            modelBuilder.Entity<Member>(b =>
            {
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
                b.HasComplexIndex(x => x.Email.Domain, filter: "\"Email_Domain\" <> 'x'", indexName: "ix_members_domain");
            });
        });
        Apply(options, Scaffold(options, target, changed), changed);
        StringAssert.EndsWith(IndexSql(connection, "ix_members_domain"), "WHERE \"Email_Domain\" <> 'x'");
        Assert.ThrowsExactly<SqliteException>(() => InsertMember(connection, "members", 3, "first@example.org"));
    }

    [TestMethod(DisplayName = "A rebuild of a derived TPC type's table re-creates the copy of its base's index")]
    public void Rebuild_recreates_tpc_copies()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Wired(connection);

        Action<ModelBuilder> accounts(bool regionRequired) => modelBuilder =>
        {
            modelBuilder.Entity<Account>(b =>
            {
                b.UseTpcMappingStrategy().ToTable("accounts");
                b.HasComplexIndex(x => x.Code, isUnique: true);
            });
            modelBuilder.Entity<Partner>(b =>
            {
                b.ToTable("partners");
                if (regionRequired)
                    b.Property(x => x.Region).IsRequired();
            });
        };

        var source = Model(connection, accounts(false));
        var target = Model(connection, accounts(true));
        Apply(options, Scaffold(options, null, source), source);
        Assert.AreEqual("partners", IndexTable(connection, "IX_partners_Code"));

        var up = Scaffold(options, source, target);
        Assert.IsTrue(up.OfType<CreateIndexOperation>().Any(c => c.Name == "IX_partners_Code"));
        Apply(options, up, target);

        Assert.AreEqual("partners", IndexTable(connection, "IX_partners_Code"));
        Assert.AreEqual("accounts", IndexTable(connection, "IX_accounts_Code"));
        Execute(connection, "INSERT INTO \"partners\" (\"Id\", \"Code\", \"Region\") VALUES (1, 'a', 'r')");
        Assert.ThrowsExactly<SqliteException>(
            () => Execute(connection, "INSERT INTO \"partners\" (\"Id\", \"Code\", \"Region\") VALUES (2, 'a', 'r')"));
    }

    [TestMethod(DisplayName = "The scaffolded C# carries the marker and the annotation the wrapped generator reads")]
    public void Scaffolded_migration_carries_the_marker()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Wired(connection);

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(_ => { }));
        var up     = Scaffold(options, source, target);

        using var context  = new MigrationHarness.EmptyContext(options);
        using var provider = DesignTimeServices(context);
        var code = provider.GetRequiredService<IMigrationsCodeGenerator>()
                           .GenerateMigration("Generated", "Rebuild", up, []);

        StringAssert.Contains(code, "filter: \"__requires_UseComplexIndexes__()\"");
        StringAssert.Contains(code, "filter: \"(\\\"Email_Domain\\\" <> '') AND __requires_UseComplexIndexes__()\"");
        StringAssert.Contains(code, ".Annotation(\"CustomIndex:AfterTableRebuild\", true)");
    }

    [TestMethod(DisplayName = "Creating the database from scratch is untouched by the wrapper")]
    public void Ensure_created_creates_the_indexes()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var options = new DbContextOptionsBuilder().UseSqlite(connection).UseComplexIndexes()
                                                   .ReplaceService<IModelCacheKeyFactory, NoModelCache>().Options;
        using var context = new ModelContext(options, Members(V1));
        Assert.IsTrue(context.Database.EnsureCreated());
        InsertMember(connection, "members", 1, "first@example.org");

        AssertComplexIndexesHold(connection, "members", 2);
    }

    // ── Without it: refused at migrations add, as in 5.5.0 before #95 ──

    [TestMethod(DisplayName = "Without UseComplexIndexes(), a rebuild of a table with a complex index fails at migrations add")]
    public void Rebuild_without_the_wiring_is_refused()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Scaffold(Unwired(connection), Model(connection, Members(V1)), Model(connection, Members(_ => { }))));

        StringAssert.Contains(exception.Message, "SQLite rebuilds table 'members'");
        StringAssert.Contains(exception.Message, "'ux_members_email', 'ix_members_domain'");
        StringAssert.Contains(exception.Message, "Call UseComplexIndexes() after UseSqlite()");
        StringAssert.Contains(exception.Message, "three migrations");
    }

    [TestMethod(DisplayName = "A differ constructed without its context refuses the rebuild")]
    public void Differ_without_a_context_refuses()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.CoreDiff(connection, Model(connection, Members(V1)), Model(connection, Members(_ => { }))));

        StringAssert.Contains(exception.Message, "SQLite rebuilds table 'members'");
    }

    [TestMethod(DisplayName = "The three migrations the refusal proposes apply and keep the index")]
    public void The_way_out_applies_and_keeps_the_index()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Unwired(connection);

        var v1           = Model(connection, Members(V1));
        var withoutIndex = Model(connection, Members(V1, withIndexes: false));
        var changed      = Model(connection, Members(_ => { }, withIndexes: false));
        var v2           = Model(connection, Members(_ => { }));

        Apply(options, MigrationHarness.CoreDiff(connection, null, v1), v1);
        InsertMember(connection, "members", 1, "first@example.org");
        Apply(options, MigrationHarness.CoreDiff(connection, v1, withoutIndex), withoutIndex);
        Apply(options, MigrationHarness.CoreDiff(connection, withoutIndex, changed), changed);
        Apply(options, MigrationHarness.CoreDiff(connection, changed, v2), v2);

        AssertComplexIndexesHold(connection, "members", 2);
    }

    [TestMethod(DisplayName = "A rebuild of a table without a complex index is left alone")]
    public void Rebuild_of_another_table_is_allowed()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var source = Model(connection, Members(V1));
        var target = Model(connection, modelBuilder =>
        {
            Members(V1)(modelBuilder);
            modelBuilder.Entity<Visit>().Property(x => x.Notes).IsRequired();
        });

        var operations = Scaffold(Unwired(connection), source, target);

        Assert.IsNotEmpty(operations);
        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Circular foreign keys between tables the migration creates are no rebuild")]
    public void Foreign_keys_on_created_tables_are_allowed()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Unwired(connection);

        // A cycle leaves EF Core one foreign key it cannot put in a CREATE TABLE, emitted as
        // AddForeignKey. Both tables carry a complex index, whichever one the cycle is broken on.
        var target = Model(connection, modelBuilder =>
        {
            Members(b => { V1(b); b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId); })(modelBuilder);
            modelBuilder.Entity<Visit>(b =>
            {
                b.HasOne<Member>().WithMany().HasForeignKey("MemberId");
                b.HasComplexIndex(x => x.Notes, indexName: "ix_visits_notes");
            });
        });

        var operations = Scaffold(options, null, target);

        Assert.IsTrue(operations.OfType<AddForeignKeyOperation>().Any(), string.Join(", ", operations.Select(o => o.GetType().Name)));
        Apply(options, operations, target);
        Assert.AreEqual("members", IndexTable(connection, "ux_members_email"));
        Assert.AreEqual("visits", IndexTable(connection, "ix_visits_notes"));
    }

    [TestMethod(DisplayName = "An unchanged model diffs to nothing")]
    public void Unchanged_model_is_empty()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        Assert.IsEmpty(Scaffold(Wired(connection), Model(connection, Members(V1)), Model(connection, Members(V1))));
    }

    // ── Misconfigurations: each fails loudly, none loses an index ──

    [TestMethod(DisplayName = "A migration scaffolded with the wrapper fails at apply without it, and keeps the indexes")]
    [DataRow(false, DisplayName = "Double-quoted string literals off (the bundled SQLite)")]
    [DataRow(true,  DisplayName = "Double-quoted string literals on (a default SQLite build)")]
    public void Missing_wrapper_fails_at_apply_before_anything_is_lost(bool doubleQuotedStrings)
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        if (doubleQuotedStrings)
            EnableDoubleQuotedStringLiterals(connection);

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(b => { V1(b); b.Ignore(x => x.Note); }));
        Apply(Unwired(connection), MigrationHarness.CoreDiff(connection, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");

        var up = Scaffold(Wired(connection), source, target);
        var exception = Assert.ThrowsExactly<SqliteException>(() => Apply(Unwired(connection), up, target));

        StringAssert.Contains(exception.Message, "no such function: __requires_UseComplexIndexes__");
        AssertComplexIndexesHold(connection, "members", 2);
        Execute(connection, "SELECT \"Note\" FROM \"members\"");
    }

    [TestMethod(DisplayName = "Without the wrapper, a create on a column renamed into a dropped one's name still fails")]
    public void Missing_wrapper_fails_when_the_rebuild_would_defer_the_create()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        // Only the index whose column the edit below renames, so no other marker fails first.
        Action<EntityTypeBuilder<Member>> email = b => b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
        var source = Model(connection, Members(b => { V1(b); email(b); }, withIndexes: false));
        var target = Model(connection, Members(b => { V1(b); email(b); b.Ignore(x => x.Note); }, withIndexes: false));
        Apply(Unwired(connection), MigrationHarness.CoreDiff(connection, null, source), source);

        // EF's SQLite generator holds back a create over a column renamed into a dropped column's
        // name, and creates it after the rebuild only if EF's model has the index. A migration edited
        // to drop the email column and rename Note into it puts the complex index's column there.
        var up    = Scaffold(Wired(connection), source, target).ToList();
        var after = up.FindIndex(o => o is DropColumnOperation) + 1;
        up.InsertRange(after,
        [
            new DropColumnOperation   { Table = "members", Name = "Email_Value" },
            new RenameColumnOperation { Table = "members", Name = "Note", NewName = "Email_Value" },
        ]);

        var exception = Assert.ThrowsExactly<SqliteException>(() => Apply(Unwired(connection), up, target));

        StringAssert.Contains(exception.Message, "__requires_UseComplexIndexes__");
    }

    [TestMethod(DisplayName = "Another package's ReplaceService of the generator makes migrations add refuse, naming it")]
    [DataRow(true,  DisplayName = "Replaced after UseComplexIndexes()")]
    [DataRow(false, DisplayName = "Replaced before UseComplexIndexes()")]
    public void Replaced_generator_is_refused(bool after)
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var builder = new DbContextOptionsBuilder().UseSqlite(connection);
        if (after)
            builder.UseComplexIndexes().ReplaceService<IMigrationsSqlGenerator, AuditingGenerator>();
        else
            builder.ReplaceService<IMigrationsSqlGenerator, AuditingGenerator>().UseComplexIndexes();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Scaffold(builder.Options, Model(connection, Members(V1)), Model(connection, Members(_ => { }))));

        StringAssert.Contains(exception.Message, $"the one in effect is {typeof(AuditingGenerator).FullName}");
        StringAssert.Contains(exception.Message, "EF Core keeps only one");
    }

    [TestMethod(DisplayName = "A ReplaceService keyed on SQLite's generator is wrapped, so both packages' work runs")]
    public void Replacement_of_sqlites_generator_is_wrapped()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var options = new DbContextOptionsBuilder().UseSqlite(connection).UseComplexIndexes()
                                                   .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator, AuditingGenerator>()
                                                   .Options;
        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(_ => { }));
        Apply(options, Scaffold(options, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");

        Apply(options, Scaffold(options, source, target), target);

        AssertComplexIndexesHold(connection, "members", 2);
        Execute(connection, "SELECT * FROM \"audited\"");
    }

    [TestMethod(DisplayName = "Another package's extension registering a generator is wrapped before UseComplexIndexes(), refused after")]
    [DataRow(false)]
    [DataRow(true)]
    public void Generator_registered_by_another_extension(bool after)
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var builder = new DbContextOptionsBuilder().UseSqlite(connection);
        if (!after)
            ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new AuditingExtension());
        builder.UseComplexIndexes();
        if (after)
            ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new AuditingExtension());

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(_ => { }));

        if (after)
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Scaffold(builder.Options, source, target));
            StringAssert.Contains(exception.Message, $"the one in effect is {typeof(AuditingGenerator).FullName}");
            return;
        }

        Apply(builder.Options, Scaffold(builder.Options, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");
        Apply(builder.Options, Scaffold(builder.Options, source, target), target);

        AssertComplexIndexesHold(connection, "members", 2);
        Execute(connection, "SELECT * FROM \"audited\"");
    }

    [TestMethod(DisplayName = "UseComplexIndexes() before UseSqlite() leaves the refusal, saying why")]
    public void Wiring_before_the_provider_is_refused()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var options   = new DbContextOptionsBuilder().UseComplexIndexes().UseSqlite(connection).Options;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Scaffold(options, Model(connection, Members(V1)), Model(connection, Members(_ => { }))));

        StringAssert.Contains(exception.Message, "UseComplexIndexes() was called before UseSqlite()");
    }

    [TestMethod(DisplayName = "AddComplexIndexes() on an internal service provider wraps SQLite's generator")]
    public void Internal_service_provider_is_wrapped()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        using var internalProvider = new ServiceCollection().AddEntityFrameworkSqlite().AddComplexIndexes().BuildServiceProvider();
        var options = new DbContextOptionsBuilder().UseSqlite(connection).UseInternalServiceProvider(internalProvider).Options;

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(_ => { }));
        Apply(options, Scaffold(options, null, source), source);
        InsertMember(connection, "members", 1, "first@example.org");
        Apply(options, Scaffold(options, source, target), target);

        AssertComplexIndexesHold(connection, "members", 2);
    }

    [TestMethod(DisplayName = "A marked create whose filter was edited fails at apply instead of guessing")]
    public void Edited_marker_fails()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        var options = Wired(connection);

        var source = Model(connection, Members(V1));
        var target = Model(connection, Members(_ => { }));
        var up     = Scaffold(options, source, target);
        up.OfType<CreateIndexOperation>().Single(c => c.Name == "ux_members_email").Filter = "\"Email_Value\" <> ''";

        using var context = new MigrationHarness.EmptyContext(options);
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => context.GetService<IMigrationsSqlGenerator>().Generate(up, target.Model));

        StringAssert.Contains(exception.Message, "'ux_members_email'");
    }
}

#pragma warning restore EF1001
