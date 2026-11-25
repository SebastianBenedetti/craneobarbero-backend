using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class SuppliersTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/suppliers" },
        { "GET", "/suppliers/1" },
        { "POST", "/suppliers" },
        { "PUT", "/suppliers/1" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Suppliers_require_a_session(string method, string url)
    {
        var response = await factory.CreateClient().SendAsync(Request(method, url));

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Only_the_admin_manages_suppliers(string method, string url)
    {
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);

            var response = await client.SendAsync(Request(method, url));

            await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Admin_creates_edits_and_deactivates_a_supplier()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/suppliers", new
        {
            name = "  Distribuidora Ficticia ",
            contactName = "Ana Prueba",
            phone = "341 555-0000",
            email = "Ventas@Proveedor.test",
            taxId = "",
            notes = " Entrega los martes ",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var supplier = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = supplier.GetProperty("id").GetInt32();
        Assert.Equal("Distribuidora Ficticia", supplier.GetProperty("name").GetString());
        Assert.Equal("ventas@proveedor.test", supplier.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, supplier.GetProperty("taxId").ValueKind);
        Assert.Equal("Entrega los martes", supplier.GetProperty("notes").GetString());
        Assert.True(supplier.GetProperty("isActive").GetBoolean());

        var updated = await admin.PutAsJsonAsync($"/suppliers/{id}", new { name = "Distribuidora Ficticia", taxId = "20-12345678-9", isActive = false });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == id));
        Assert.Equal("20-12345678-9", stored.TaxId);
        Assert.Null(stored.ContactName);
        Assert.False(stored.IsActive);

        var active = await admin.GetFromJsonAsync<List<JsonElement>>("/suppliers?active=true");
        var inactive = await admin.GetFromJsonAsync<List<JsonElement>>("/suppliers?active=false");
        Assert.DoesNotContain(active!, s => s.GetProperty("id").GetInt32() == id);
        Assert.Contains(inactive!, s => s.GetProperty("id").GetInt32() == id);

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{id}");
        Assert.False(fetched.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Supplier_data_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/suppliers", new
        {
            name = "",
            phone = "llamar",
            email = "no-es-un-mail",
            taxId = new string('1', 21),
            notes = new string('x', 501),
        });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        var errors = json.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("phone", out _));
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("taxId", out _));
        Assert.True(errors.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task Unknown_supplier_returns_not_found()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PutAsJsonAsync("/suppliers/999999", new { name = "Proveedor" });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    private static HttpRequestMessage Request(string method, string url) => new(new HttpMethod(method), url)
    {
        Content = method == "GET" ? null : JsonContent.Create(new { }),
    };
}
