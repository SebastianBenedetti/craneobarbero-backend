using System.Text.RegularExpressions;
using CraneoBarbero.Api.Data.Migrations;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Time.Testing;

namespace CraneoBarbero.Api.Tests.Data;

public sealed class SchemaTests
{
    [Fact]
    public void Model_has_the_tables_of_the_specification_the_monthly_reports_and_the_images()
    {
        using var db = TestDb.CreateNpgsqlModelOnly();

        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).Order().ToArray();

        Assert.Equal(InitialSchemaSql.Tables.Append("MonthlyReports").Append("Images").Order().ToArray(), tables);
        Assert.Equal(20, tables.Length);
    }

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var db = TestDb.CreateNpgsqlModelOnly();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(db.Database.GetMigrations(), m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_script_uses_postgres_enums_with_contract_names()
    {
        using var db = TestDb.CreateNpgsqlModelOnly();

        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TYPE appointment_status AS ENUM ('awaiting_payment', 'cancelled', 'completed', 'expired', 'pending')", script);
        Assert.Contains("CREATE TYPE payment_method AS ENUM ('card', 'cash', 'mercadopago', 'transfer', 'wallet')", script);
        Assert.Contains("CREATE TYPE user_role AS ENUM ('admin', 'barber', 'client')", script);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS btree_gist SCHEMA extensions", script);
        Assert.Equal(18, Regex.Matches(script, "CREATE TYPE ").Count);
        Assert.Contains("price numeric(10,2) NOT NULL", script);
        Assert.Contains("\"appointmentDate\" timestamp with time zone NOT NULL", script);
    }

    [Fact]
    public void Initial_migration_adds_overlap_exclusions_and_row_level_security()
    {
        using var db = TestDb.CreateNpgsqlModelOnly();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations.Single(m => m.Key.EndsWith("_InitialSchema", StringComparison.Ordinal)).Value,
            db.Database.ProviderName!);

        var sql = migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        Assert.Contains(sql, s => s.Contains("EXCLUDE USING gist (\"barberId\" WITH =") && s.Contains("'awaiting_payment', 'pending', 'completed'"));
        Assert.Contains(sql, s => s.Contains("EXCLUDE USING gist (\"clientId\" WITH =") && s.Contains("\"clientId\" IS NOT NULL"));
        Assert.Equal(19, sql.Count(s => s.Contains("ENABLE ROW LEVEL SECURITY")));
    }

    [Fact]
    public void Server_never_migrates_on_startup()
    {
        var source = File.ReadAllText(FindRepositoryFile("src/CraneoBarbero.Api/Program.cs")) +
                     File.ReadAllText(FindRepositoryFile("src/CraneoBarbero.Api/Extensions/WebApplicationExtensions.cs"));

        Assert.DoesNotContain("Migrate(", source);
        Assert.DoesNotContain("EnsureCreated", source);
    }

    [Fact]
    public async Task Timestamps_are_set_on_insert_and_update()
    {
        var time = new FakeTimeProvider(TestDb.Now);
        await using var db = TestDb.Create(time);
        var branch = new Branch { Name = "Sucursal", Address = "Calle 1" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        Assert.Equal(TestDb.Now, branch.CreatedAt);
        Assert.Equal(TestDb.Now, branch.UpdatedAt);

        time.Advance(TimeSpan.FromHours(1));
        branch.Name = "Sucursal renombrada";
        await db.SaveChangesAsync();

        Assert.Equal(TestDb.Now, branch.CreatedAt);
        Assert.Equal(TestDb.Now.AddHours(1), branch.UpdatedAt);
    }

    [Fact]
    public async Task Explicit_timestamps_are_preserved_on_insert()
    {
        await using var db = TestDb.Create();
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cut = new Cut { Name = "Corte", Price = 10, DurationMinutes = 30, CreatedAt = created, UpdatedAt = created };
        db.Cuts.Add(cut);
        await db.SaveChangesAsync();

        Assert.Equal(created, cut.CreatedAt);
        Assert.Equal(created, cut.UpdatedAt);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
