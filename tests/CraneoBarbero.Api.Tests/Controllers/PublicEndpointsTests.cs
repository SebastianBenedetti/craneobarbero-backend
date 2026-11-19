using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CraneoBarbero.Api.Tests.Controllers;

public sealed class PublicEndpointsTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Password = "Clave-de-prueba-1";
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private User _client1 = null!;
    private User _inactive = null!;
    private User _barber = null!;
    private User _inactiveBarber = null!;
    private User _closedBranchBarber = null!;

    public PublicEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var active = new Branch { Name = "Sucursal Activa", Address = "Calle 1", Phone = "341000" };
        var closed = new Branch { Name = "Sucursal Cerrada", Address = "Calle 2", IsActive = false };
        db.Branches.AddRange(active, closed);
        db.Cuts.AddRange(
            new Cut { Name = "Corte", Price = 10000m, DurationMinutes = 30 },
            new Cut { Name = "Servicio dado de baja", Price = 1m, DurationMinutes = 30, IsActive = false });
        await db.SaveChangesAsync();

        _client1 = NewUser($"cliente-{suffix}@example.com", UserRole.Client, hash);
        _inactive = NewUser($"inactivo-{suffix}@example.com", UserRole.Client, hash, isActive: false);
        _barber = NewUser($"barbero-{suffix}@example.com", UserRole.Barber, hash, active.Id);
        _barber.Phone = "3415550000";
        _inactiveBarber = NewUser($"barbero-baja-{suffix}@example.com", UserRole.Barber, hash, active.Id, isActive: false);
        _closedBranchBarber = NewUser($"barbero-cerrada-{suffix}@example.com", UserRole.Barber, hash, closed.Id);
        db.Users.AddRange(_client1, _inactive, _barber, _inactiveBarber, _closedBranchBarber);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_reports_ok_without_authentication()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Login_returns_a_token_the_frontend_can_decode()
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new { email = $"  {_client1.Email.ToUpperInvariant()} ", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2", body);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(_client1.Id, json.GetProperty("user").GetProperty("id").GetInt32());
        Assert.Equal("client", json.GetProperty("user").GetProperty("role").GetString());

        var token = json.GetProperty("token").GetString()!;
        var payload = DecodePayload(token);
        Assert.Equal(_client1.Id, payload.GetProperty("id").GetInt32());
        Assert.Equal(_client1.Name, payload.GetProperty("name").GetString());
        Assert.Equal("client", payload.GetProperty("role").GetString());
        Assert.True(payload.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var me = await _factory.ClientWithToken(token).GetAsync("/test/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Theory]
    [InlineData("cliente", "otra-clave")]
    [InlineData("nadie", Password)]
    public async Task Wrong_credentials_get_the_same_answer(string who, string password)
    {
        var email = who == "cliente" ? _client1.Email : $"nadie-{Guid.NewGuid():N}@example.com";

        var response = await _client.PostAsJsonAsync("/auth/login", new { email, password });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal("Email o contraseña incorrectos.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Deactivated_account_cannot_log_in()
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new { email = _inactive.Email, password = Password });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "account_inactive");
        Assert.Equal("Tu cuenta está desactivada, comunicate con el local.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_requires_email_and_password()
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new { email = "" });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.GetProperty("errors").TryGetProperty("email", out _));
        Assert.True(json.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Branches_list_only_active_ones()
    {
        var branches = await _client.GetFromJsonAsync<JsonElement>("/branches");

        var names = branches.EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToList();
        Assert.Contains("Sucursal Activa", names);
        Assert.DoesNotContain("Sucursal Cerrada", names);
    }

    [Fact]
    public async Task Cuts_list_only_active_ones_with_duration_and_decimal_price()
    {
        var cuts = await _client.GetFromJsonAsync<JsonElement>("/cuts");

        var cut = cuts.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Corte");
        Assert.Equal(10000m, cut.GetProperty("price").GetDecimal());
        Assert.Equal(30, cut.GetProperty("durationMinutes").GetInt32());
        Assert.DoesNotContain(cuts.EnumerateArray(), c => c.GetProperty("name").GetString() == "Servicio dado de baja");
    }

    [Fact]
    public async Task Barbers_list_is_public_and_hides_private_data()
    {
        var response = await _client.GetAsync("/barbers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("@example.com", body);
        Assert.DoesNotContain("3415550000", body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);

        var barbers = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();
        var ids = barbers.Select(b => b.GetProperty("id").GetInt32()).ToList();
        Assert.DoesNotContain(_inactiveBarber.Id, ids);
        Assert.DoesNotContain(_closedBranchBarber.Id, ids);
        var barber = barbers.Single(b => b.GetProperty("id").GetInt32() == _barber.Id);
        Assert.Equal(_barber.Id, barber.GetProperty("id").GetInt32());
        Assert.Equal("Sucursal Activa", barber.GetProperty("branch").GetProperty("name").GetString());
    }

    private static User NewUser(string email, UserRole role, string hash, int? branchId = null, bool isActive = true) => new()
    {
        Name = "Persona Ñandú",
        Email = email,
        PasswordHash = hash,
        Role = role,
        BranchId = branchId,
        IsActive = isActive,
    };

    private static JsonElement DecodePayload(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement;
    }
}
