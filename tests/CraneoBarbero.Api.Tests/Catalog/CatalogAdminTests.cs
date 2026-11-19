using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CraneoBarbero.Api.Tests.Catalog;

public sealed class CatalogAdminTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Clave2026segura";

    public static TheoryData<string, string> AdminOnlyEndpoints => new()
    {
        { "POST", "/branches" },
        { "PUT", "/branches/1" },
        { "POST", "/branches/1/deactivate" },
        { "POST", "/branches/1/activate" },
        { "POST", "/cuts" },
        { "PUT", "/cuts/1" },
        { "POST", "/cuts/1/deactivate" },
        { "POST", "/cuts/1/activate" },
        { "POST", "/barbers" },
        { "PUT", "/barbers/1" },
        { "POST", "/barbers/1/deactivate" },
        { "POST", "/barbers/1/activate" },
        { "GET", "/branches?includeInactive=true" },
        { "GET", "/cuts?includeInactive=true" },
        { "GET", "/barbers?includeInactive=true" },
    };

    [Theory]
    [MemberData(nameof(AdminOnlyEndpoints))]
    public async Task Management_endpoints_require_a_session(string method, string url)
    {
        var response = await factory.CreateClient().SendAsync(Request(method, url));

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [MemberData(nameof(AdminOnlyEndpoints))]
    public async Task Clients_and_barbers_cannot_manage_the_catalog(string method, string url)
    {
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);

            var response = await client.SendAsync(Request(method, url));

            await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Admin_creates_and_edits_a_branch()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/branches", new
        {
            name = "  Sucursal Norte ",
            address = "Bv. Rondeau 1234",
            phone = "341 555-1234",
            imageUrl = "",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var branch = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = branch.GetProperty("id").GetInt32();
        Assert.Equal("Sucursal Norte", branch.GetProperty("name").GetString());
        Assert.True(branch.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, branch.GetProperty("imageUrl").ValueKind);

        var updated = await admin.PutAsJsonAsync($"/branches/{id}", new { name = "Sucursal Norte", address = "Otra 99", phone = "" });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Branches.AsNoTracking().SingleAsync(b => b.Id == id));
        Assert.Equal("Otra 99", stored.Address);
        Assert.Null(stored.Phone);
    }

    [Fact]
    public async Task Branch_data_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/branches", new { name = "", address = "x", phone = "llamar", imageUrl = "data:image/png;base64,AAAA" });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        var errors = json.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("address", out _));
        Assert.True(errors.TryGetProperty("phone", out _));
        Assert.True(errors.TryGetProperty("imageUrl", out _));
    }

    [Fact]
    public async Task Deactivated_branch_is_hidden_from_the_public_but_kept_for_the_admin()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var branch = await AddBranchAsync();

        var deactivated = await admin.PostAsync($"/branches/{branch.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False((await deactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());
        var publicIds = await Ids(factory.CreateClient(), "/branches");
        Assert.DoesNotContain(branch.Id, publicIds);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/branches/{branch.Id}")).StatusCode);
        Assert.Contains(branch.Id, await Ids(admin, "/branches?includeInactive=true"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/branches/{branch.Id}")).StatusCode);

        var activated = await admin.PostAsync($"/branches/{branch.Id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Contains(branch.Id, await Ids(factory.CreateClient(), "/branches"));
    }

    [Fact]
    public async Task Branch_with_future_appointments_cannot_be_deactivated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (branch, barber, cut) = await AddCatalogAsync();
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(2), AppointmentStatus.Pending);

        var response = await admin.PostAsync($"/branches/{branch.Id}/deactivate", null);

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "has_future_appointments");
        Assert.Contains("1 turno futuro", json.GetProperty("error").GetString());
        var stored = await factory.WithDbAsync(db => db.Branches.AsNoTracking().SingleAsync(b => b.Id == branch.Id));
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task Past_cancelled_and_expired_appointments_do_not_block_deactivation()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (branch, barber, cut) = await AddCatalogAsync();
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(-3), AppointmentStatus.Pending);
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(-10), AppointmentStatus.Completed);
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(3), AppointmentStatus.Cancelled);
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(3), AppointmentStatus.Expired);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/barbers/{barber.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/cuts/{cut.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/branches/{branch.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Barber_and_cut_with_future_appointments_cannot_be_deactivated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (branch, barber, cut) = await AddCatalogAsync();
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddHours(5), AppointmentStatus.AwaitingPayment);
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(9), AppointmentStatus.Pending);

        var barberResponse = await admin.PostAsync($"/barbers/{barber.Id}/deactivate", null);
        var cutResponse = await admin.PostAsync($"/cuts/{cut.Id}/deactivate", null);

        var json = await HttpAssertions.AssertErrorAsync(barberResponse, HttpStatusCode.Conflict, "has_future_appointments");
        Assert.Contains("2 turnos futuros", json.GetProperty("error").GetString());
        await HttpAssertions.AssertErrorAsync(cutResponse, HttpStatusCode.Conflict, "has_future_appointments");
    }

    [Fact]
    public async Task Admin_creates_a_cut_with_duration_and_decimal_price()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/cuts", new
        {
            name = "Corte y barba",
            price = 15500.50m,
            durationMinutes = 45,
            description = "  Incluye perfilado ",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cut = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15500.50m, cut.GetProperty("price").GetDecimal());
        Assert.Equal(45, cut.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("Incluye perfilado", cut.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Cut_data_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/cuts", new { name = "Corte", price = -1, durationMinutes = 0 });
        var rounding = await admin.PostAsJsonAsync("/cuts", new { name = "Corte", price = 10.555m, durationMinutes = 30 });
        var missing = await admin.PostAsJsonAsync("/cuts", new { name = "Corte" });

        var errors = (await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("price", out _));
        Assert.True(errors.TryGetProperty("durationMinutes", out _));
        await HttpAssertions.AssertErrorAsync(rounding, HttpStatusCode.BadRequest, "validation_error");
        var missingErrors = (await HttpAssertions.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(missingErrors.TryGetProperty("price", out _));
        Assert.True(missingErrors.TryGetProperty("durationMinutes", out _));
    }

    [Fact]
    public async Task Deactivated_cut_is_hidden_from_the_public_list()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (_, _, cut) = await AddCatalogAsync();

        await admin.PostAsync($"/cuts/{cut.Id}/deactivate", null);

        Assert.DoesNotContain(cut.Id, await Ids(factory.CreateClient(), "/cuts"));
        Assert.Contains(cut.Id, await Ids(admin, "/cuts?includeInactive=true"));

        await admin.PostAsync($"/cuts/{cut.Id}/activate", null);

        Assert.Contains(cut.Id, await Ids(factory.CreateClient(), "/cuts"));
    }

    [Fact]
    public async Task Admin_creates_a_verified_barber_who_can_log_in()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var branch = await AddBranchAsync();
        var email = $"Nuevo-{Guid.NewGuid():N}@Example.com";

        var response = await admin.PostAsJsonAsync("/barbers", new
        {
            name = "Barbero Nuevo",
            email,
            password = Password,
            branchId = branch.Id,
            startDate = "2021-04-01",
            imageUrl = "https://example.com/barbero.jpg",
            phone = "3415550000",
            role = "admin",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2", body);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(email.ToLowerInvariant(), json.GetProperty("email").GetString());
        Assert.Equal(branch.Id, json.GetProperty("branch").GetProperty("id").GetInt32());

        var id = json.GetProperty("id").GetInt32();
        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));
        Assert.Equal(UserRole.Barber, stored.Role);
        Assert.True(stored.EmailVerified);
        Assert.True(stored.IsActive);
        Assert.Null(stored.Phone);
        Assert.Equal(new DateOnly(2021, 4, 1), stored.StartDate);
        Assert.Empty(factory.Emails.SentTo(stored.Email));

        var login = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Barber_email_must_be_unique_and_branch_active()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var existing = await factory.AddUserAsync(UserRole.Client);
        var branch = await AddBranchAsync();
        var closed = await AddBranchAsync(isActive: false);

        var duplicated = await admin.PostAsJsonAsync("/barbers", new
        {
            name = "Barbero", email = existing.Email.ToUpperInvariant(), password = Password, branchId = branch.Id,
        });
        var closedBranch = await admin.PostAsJsonAsync("/barbers", new
        {
            name = "Barbero", email = $"b-{Guid.NewGuid():N}@example.com", password = Password, branchId = closed.Id,
        });
        var missingBranch = await admin.PostAsJsonAsync("/barbers", new
        {
            name = "Barbero", email = $"b-{Guid.NewGuid():N}@example.com", password = Password, branchId = 999_999,
        });
        var weakPassword = await admin.PostAsJsonAsync("/barbers", new
        {
            name = "Barbero", email = $"b-{Guid.NewGuid():N}@example.com", password = "corta", branchId = branch.Id,
        });

        await HttpAssertions.AssertErrorAsync(duplicated, HttpStatusCode.Conflict, "email_in_use");
        await HttpAssertions.AssertErrorAsync(closedBranch, HttpStatusCode.BadRequest, "branch_inactive");
        await HttpAssertions.AssertErrorAsync(missingBranch, HttpStatusCode.BadRequest, "branch_inactive");
        var errors = (await HttpAssertions.AssertErrorAsync(weakPassword, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Admin_sets_a_new_password_for_an_existing_barber_without_sending_mail()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (_, barber, _) = await AddCatalogAsync();
        var sentBefore = factory.Emails.Sent.Count;

        var response = await admin.PutAsJsonAsync($"/barbers/{barber.Id}", new
        {
            name = barber.Name,
            branchId = barber.BranchId,
            newPassword = "NuevaClave2026",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { email = barber.Email, password = "NuevaClave2026" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(sentBefore, factory.Emails.Sent.Count);
    }

    [Fact]
    public async Task Editing_a_barber_without_new_password_keeps_the_current_one()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (_, barber, _) = await AddCatalogAsync();
        var otherBranch = await AddBranchAsync();

        var response = await admin.PutAsJsonAsync($"/barbers/{barber.Id}", new
        {
            name = "Nombre Editado",
            branchId = otherBranch.Id,
            startDate = "2019-01-15",
            newPassword = "",
            email = "cambiado@example.com",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == barber.Id));
        Assert.Equal("Nombre Editado", stored.Name);
        Assert.Equal(otherBranch.Id, stored.BranchId);
        Assert.Equal(barber.Email, stored.Email);
        Assert.Equal(barber.PasswordHash, stored.PasswordHash);
    }

    [Fact]
    public async Task Branch_change_is_rejected_while_the_barber_has_future_appointments()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (branch, barber, cut) = await AddCatalogAsync();
        var otherBranch = await AddBranchAsync();
        await AddAppointmentAsync(branch, barber, cut, DateTimeOffset.UtcNow.AddDays(1), AppointmentStatus.Pending);

        var moved = await admin.PutAsJsonAsync($"/barbers/{barber.Id}", new { name = barber.Name, branchId = otherBranch.Id });
        var sameBranch = await admin.PutAsJsonAsync($"/barbers/{barber.Id}", new { name = "Mismo Lugar", branchId = branch.Id });

        await HttpAssertions.AssertErrorAsync(moved, HttpStatusCode.Conflict, "has_future_appointments");
        Assert.Equal(HttpStatusCode.OK, sameBranch.StatusCode);
    }

    [Fact]
    public async Task Barber_endpoints_only_handle_barbers()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var client = await factory.AddUserAsync(UserRole.Client);

        var response = await admin.PostAsync($"/barbers/{client.Id}/deactivate", null);

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Admin_list_shows_inactive_barbers_with_email_and_public_list_hides_them()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (_, barber, _) = await AddCatalogAsync();

        await admin.PostAsync($"/barbers/{barber.Id}/deactivate", null);

        Assert.DoesNotContain(barber.Id, await Ids(factory.CreateClient(), "/barbers"));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/barbers/{barber.Id}")).StatusCode);
        var all = await admin.GetFromJsonAsync<JsonElement>("/barbers?includeInactive=true");
        var row = all.EnumerateArray().Single(b => b.GetProperty("id").GetInt32() == barber.Id);
        Assert.False(row.GetProperty("isActive").GetBoolean());
        Assert.Equal(barber.Email, row.GetProperty("email").GetString());

        await admin.PostAsync($"/barbers/{barber.Id}/activate", null);

        var publicBarber = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/barbers/{barber.Id}");
        Assert.False(publicBarber.TryGetProperty("email", out _));
        Assert.False(publicBarber.TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Barbers_cannot_save_a_phone_in_their_profile()
    {
        var (_, barber, _) = await AddCatalogAsync();
        var client = factory.ClientWithToken(factory.TokenFor(barber));

        var response = await client.PutAsJsonAsync("/me", new { name = "Barbero Perfil", phone = "3415550000" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == barber.Id));
        Assert.Null(stored.Phone);
        Assert.Equal("Barbero Perfil", stored.Name);
    }

    private static HttpRequestMessage Request(string method, string url) => new(new HttpMethod(method), url)
    {
        Content = method == "GET" ? null : JsonContent.Create(new { }),
    };

    private static async Task<List<int>> Ids(HttpClient client, string url)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(url);
        return list.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
    }

    private Task<Branch> AddBranchAsync(bool isActive = true) => factory.WithDbAsync(async db =>
    {
        var branch = new Branch { Name = $"Sucursal {Guid.NewGuid():N}"[..20], Address = "Calle 123", IsActive = isActive };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return branch;
    });

    private Task<(Branch Branch, User Barber, Cut Cut)> AddCatalogAsync() => factory.WithDbAsync(async db =>
    {
        var branch = new Branch { Name = "Sucursal Prueba", Address = "Calle 123" };
        var cut = new Cut { Name = "Corte prueba", Price = 1000m, DurationMinutes = 30 };
        db.AddRange(branch, cut);
        await db.SaveChangesAsync();

        var hasher = factory.Services.GetRequiredService<IPasswordHasher>();
        var barber = new User
        {
            Name = "Barbero Prueba",
            Email = $"barbero-{Guid.NewGuid():N}@example.com",
            PasswordHash = hasher.Hash(Password),
            Role = UserRole.Barber,
            BranchId = branch.Id,
            EmailVerified = true,
        };
        db.Users.Add(barber);
        await db.SaveChangesAsync();
        return (branch, barber, cut);
    });

    private Task AddAppointmentAsync(Branch branch, User barber, Cut cut, DateTimeOffset start, AppointmentStatus status) =>
        factory.WithDbAsync(async db =>
        {
            db.Appointments.Add(new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(cut.DurationMinutes),
                Status = status,
                ClientName = "Cliente de prueba",
                BarberId = barber.Id,
                CutId = cut.Id,
                BranchId = branch.Id,
                Price = cut.Price,
                CreatedByRole = UserRole.Admin,
            });
            return await db.SaveChangesAsync();
        });
}
