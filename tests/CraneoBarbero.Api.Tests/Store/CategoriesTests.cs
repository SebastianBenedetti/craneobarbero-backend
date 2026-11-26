using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class CategoriesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Categories_are_public_to_read()
    {
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/categories")).StatusCode);

        var barber = await factory.ClientForAsync(UserRole.Barber);
        Assert.Equal(HttpStatusCode.OK, (await barber.GetAsync("/categories")).StatusCode);

        var client = await factory.ClientForAsync(UserRole.Client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/categories")).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_creates_or_edits_categories()
    {
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/categories", new { name = "Ceras" }), HttpStatusCode.Forbidden, "forbidden");
            await HttpAssertions.AssertErrorAsync(await client.PutAsJsonAsync("/categories/1", new { name = "Ceras" }), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Admin_creates_and_renames_a_category_with_a_unique_name()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var name = $"Ceras {Guid.NewGuid():N}"[..20];

        var created = await admin.PostAsJsonAsync("/categories", new { name = $"  {name} " });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var category = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = category.GetProperty("id").GetInt32();
        Assert.Equal(name, category.GetProperty("name").GetString());

        var duplicate = await admin.PostAsJsonAsync("/categories", new { name = name.ToUpperInvariant() });
        await HttpAssertions.AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "category_exists");

        var renamed = await admin.PutAsJsonAsync($"/categories/{id}", new { name = name + " y geles" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var sameName = await admin.PutAsJsonAsync($"/categories/{id}", new { name = name + " y geles" });
        Assert.Equal(HttpStatusCode.OK, sameName.StatusCode);

        var list = await admin.GetFromJsonAsync<List<JsonElement>>("/categories");
        Assert.Contains(list!, c => c.GetProperty("id").GetInt32() == id && c.GetProperty("name").GetString() == name + " y geles");
    }

    [Fact]
    public async Task Category_name_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/categories", new { name = "x" });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.GetProperty("errors").TryGetProperty("name", out _));
        await HttpAssertions.AssertErrorAsync(await admin.PutAsJsonAsync("/categories/999999", new { name = "Ceras" }), HttpStatusCode.NotFound, "not_found");
    }
}
