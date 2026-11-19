using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Data.Import;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace CraneoBarbero.Api.Tests.Data;

public sealed class LegacyImporterTests
{
    private static readonly string ClientHash = BCrypt.Net.BCrypt.HashPassword("Clave-cliente-1", BCrypt.Net.BCrypt.GenerateSalt(10, 'a'));
    private static readonly string BarberHash = BCrypt.Net.BCrypt.HashPassword("Clave-barbero-1", BCrypt.Net.BCrypt.GenerateSalt(10, 'b'));
    private static readonly string AdminHash = BCrypt.Net.BCrypt.HashPassword("Clave-admin-1", BCrypt.Net.BCrypt.GenerateSalt(10, 'b'));

    private static string Dump(string appointmentRows = "") => $"""
        --
        -- PostgreSQL database dump
        --
        COPY auth.users (id, email) FROM stdin;
        1	interno@example.com
        \.
        COPY public."Branches" (id, name, address, phone, "imageUrl", "createdAt", "updatedAt") FROM stdin;
        1	Sucursal Uno	Calle Falsa 123	\N	\N	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00
        2	Sucursal Dos	Avenida\tSiempre Viva 742	3410000000	https://example.com/b.jpg	2026-06-18 18:32:54.413+00	2026-06-19 10:00:00+00
        \.
        COPY public."Users" (id, name, email, password, phone, "imageUrl", "branchId", "yearsOfExperience", role, "createdAt", "updatedAt", "startDate") FROM stdin;
        1	Admin Ficticio	Admin@Example.com	{AdminHash}	\N	\N	\N	\N	admin	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00	\N
        5	Barbero Ficticio	barbero@example.com	{BarberHash}	3410000001	\N	2	5	barber	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00	2020-03-01
        7	Cliente Ficticio	 Cliente@Example.COM 	{ClientHash}	3410000002	\N	\N	\N	client	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00	\N
        \.
        COPY public.cuts (id, name, price, "createdAt", "updatedAt") FROM stdin;
        1	Corte + Barba	15000	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00
        2	Corte	10000.50	2026-06-18 18:32:54.413+00	2026-06-18 18:32:54.413+00
        \.
        COPY public."Appointments" (id, "appointmentDate", status, "createdAt", "updatedAt", "clientId", "barberId", "cutId") FROM stdin;
        10	2026-03-10 13:00:00+00	completed	2026-03-01 10:00:00+00	2026-03-01 10:00:00+00	7	5	1
        11	2026-07-15 14:30:00+00	pending	2026-07-01 10:00:00+00	2026-07-01 10:00:00+00	7	5	2
        12	2026-07-20 15:00:00+00	cancelled	2026-07-02 10:00:00+00	2026-07-05 12:00:00+00	7	5	1
        13	2026-12-20 15:00:00+00	pending	2026-11-01 10:00:00+00	2026-11-01 10:00:00+00	7	5	1
        {appointmentRows}\.
        """;

    [Fact]
    public async Task Imports_branches_cuts_and_users_with_the_agreed_rules()
    {
        await using var db = TestDb.Create();

        var result = await Import(db, Dump());

        Assert.Equal((2, 2, 3, 4), (result.Branches, result.Cuts, result.Users, result.Appointments));
        var branch = await db.Branches.SingleAsync(b => b.Id == 2);
        Assert.Equal("Avenida\tSiempre Viva 742", branch.Address);
        Assert.Equal(new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero), branch.UpdatedAt);
        Assert.True(branch.IsActive);

        var cut = await db.Cuts.SingleAsync(c => c.Id == 2);
        Assert.Equal(10000.50m, cut.Price);
        Assert.Equal(30, cut.DurationMinutes);
        Assert.True(cut.IsActive);

        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
        Assert.All(users, u => Assert.True(u.IsActive && u.EmailVerified));
        Assert.Equal(["admin@example.com", "barbero@example.com", "cliente@example.com"], users.Select(u => u.Email));
        Assert.Equal([UserRole.Admin, UserRole.Barber, UserRole.Client], users.Select(u => u.Role));
        Assert.Equal([null, 2, null], users.Select(u => u.BranchId));
        Assert.Equal(new DateOnly(2020, 3, 1), users[1].StartDate);
        Assert.Equal("Barbero Ficticio", users[1].Name);
        Assert.Equal([null, null, "3410000002"], users.Select(u => u.Phone));
        Assert.Equal(new DateTimeOffset(2026, 6, 18, 18, 32, 54, 413, TimeSpan.Zero), users[0].CreatedAt);
    }

    [Fact]
    public async Task Existing_password_hashes_are_kept_and_still_verify()
    {
        await using var db = TestDb.Create();
        await Import(db, Dump());
        var hasher = new BcryptPasswordHasher();

        var users = await db.Users.ToDictionaryAsync(u => u.Id);

        Assert.Equal(AdminHash, users[1].PasswordHash);
        Assert.Equal(BarberHash, users[5].PasswordHash);
        Assert.Equal(ClientHash, users[7].PasswordHash);
        Assert.True(hasher.Verify("Clave-admin-1", users[1].PasswordHash));
        Assert.True(hasher.Verify("Clave-barbero-1", users[5].PasswordHash));
        Assert.True(hasher.Verify("Clave-cliente-1", users[7].PasswordHash));
    }

    [Fact]
    public async Task Appointments_are_imported_as_history()
    {
        await using var db = TestDb.Create();
        await Import(db, Dump());

        var appointments = await db.Appointments.ToDictionaryAsync(a => a.Id);

        var completed = appointments[10];
        Assert.Equal(AppointmentStatus.Completed, completed.Status);
        Assert.Equal(PaymentStatus.Paid, completed.PaymentStatus);
        Assert.Equal(PaymentMethod.Cash, completed.PaymentMethod);
        Assert.Equal(completed.AppointmentDate, completed.PaidAt);
        Assert.Equal(15000m, completed.Price);
        Assert.Equal(2, completed.BranchId);
        Assert.Equal(completed.AppointmentDate.AddMinutes(30), completed.EndDate);
        Assert.Equal(UserRole.Client, completed.CreatedByRole);
        Assert.Equal(7, completed.CreatedById);

        var pastPending = appointments[11];
        Assert.Equal(AppointmentStatus.Completed, pastPending.Status);
        Assert.Equal(PaymentStatus.Paid, pastPending.PaymentStatus);
        Assert.Equal(10000.50m, pastPending.Price);

        var cancelled = appointments[12];
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
        Assert.Equal(PaymentStatus.Unpaid, cancelled.PaymentStatus);
        Assert.Null(cancelled.PaymentMethod);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero), cancelled.CancelledAt);

        var futurePending = appointments[13];
        Assert.Equal(AppointmentStatus.Pending, futurePending.Status);
        Assert.Equal(PaymentStatus.Unpaid, futurePending.PaymentStatus);
        Assert.Equal(PaymentMethod.Cash, futurePending.PaymentMethod);
    }

    [Fact]
    public async Task Refuses_to_import_into_a_database_with_data()
    {
        await using var db = TestDb.Create();
        db.Branches.Add(new Branch { Name = "Existente", Address = "Calle" });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(db, Dump()));

        Assert.Contains("base vacía", error.Message);
        Assert.Equal(1, await db.Branches.CountAsync());
    }

    [Theory]
    [InlineData("20\t2026-03-10 13:00:00+00\tabsent\t2026-03-01 10:00:00+00\t2026-03-01 10:00:00+00\t7\t5\t1\n", "estado desconocido")]
    [InlineData("20\t2026-03-10 13:00:00+00\tcompleted\t2026-03-01 10:00:00+00\t2026-03-01 10:00:00+00\t7\t7\t1\n", "barbero válido")]
    [InlineData("20\t2026-03-10 13:00:00+00\tcompleted\t2026-03-01 10:00:00+00\t2026-03-01 10:00:00+00\t99\t5\t1\n", "cliente inexistente")]
    public async Task Invalid_rows_abort_the_whole_import(string row, string expectedMessage)
    {
        await using var db = TestDb.Create();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(db, Dump(row)));

        Assert.Contains(expectedMessage, error.Message);
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task Plain_text_passwords_are_rejected()
    {
        await using var db = TestDb.Create();
        var dump = Dump().Replace(ClientHash, "texto-plano");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Import(db, dump));

        Assert.Contains("hash bcrypt", error.Message);
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task Only_public_tables_are_read()
    {
        var tables = await PostgresDump.ReadPublicTablesAsync(new StringReader(Dump()), CancellationToken.None);

        Assert.Equal(["Appointments", "Branches", "Users", "cuts"], tables.Keys.Order(StringComparer.Ordinal));
        Assert.Null(tables["Branches"].Rows[0]["phone"]);
    }

    [Theory]
    [InlineData("\\N", null)]
    [InlineData("texto", "texto")]
    [InlineData("a\\tb\\nc\\\\d", "a\tb\nc\\d")]
    public void Copy_values_are_unescaped(string raw, string? expected)
    {
        Assert.Equal(expected, PostgresDump.Unescape(raw));
    }

    private static async Task<LegacyImportResult> Import(Api.Data.AppDbContext db, string dump)
    {
        var tables = await PostgresDump.ReadPublicTablesAsync(new StringReader(dump), CancellationToken.None);
        return await new LegacyImporter(db, new FakeTimeProvider(TestDb.Now)).ImportAsync(tables, CancellationToken.None);
    }
}
