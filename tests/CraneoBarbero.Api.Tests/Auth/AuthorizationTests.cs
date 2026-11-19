using System.Net;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Auth;

public sealed class AuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData(UserRole.Admin, "/test/admin", HttpStatusCode.OK)]
    [InlineData(UserRole.Barber, "/test/admin", HttpStatusCode.Forbidden)]
    [InlineData(UserRole.Client, "/test/admin", HttpStatusCode.Forbidden)]
    [InlineData(UserRole.Client, "/test/client", HttpStatusCode.OK)]
    [InlineData(UserRole.Admin, "/test/client", HttpStatusCode.Forbidden)]
    [InlineData(UserRole.Barber, "/test/barber-or-admin", HttpStatusCode.OK)]
    [InlineData(UserRole.Admin, "/test/barber-or-admin", HttpStatusCode.OK)]
    [InlineData(UserRole.Client, "/test/barber-or-admin", HttpStatusCode.Forbidden)]
    public async Task Role_policies_are_enforced(UserRole role, string path, HttpStatusCode expected)
    {
        var client = await factory.ClientForAsync(role);

        var response = await client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Owner_can_access_own_resource()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        var client = factory.ClientWithToken(factory.TokenFor(user));

        var response = await client.GetAsync($"/test/owned/{user.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task User_cannot_access_resource_of_someone_else()
    {
        var owner = await factory.AddUserAsync(UserRole.Client);
        var client = await factory.ClientForAsync(UserRole.Client);

        var response = await client.GetAsync($"/test/owned/{owner.Id}");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Barber_cannot_access_resource_of_another_barber()
    {
        var owner = await factory.AddUserAsync(UserRole.Barber);
        var client = await factory.ClientForAsync(UserRole.Barber);

        var response = await client.GetAsync($"/test/owned/{owner.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_can_access_any_resource_when_policy_allows_it()
    {
        var owner = await factory.AddUserAsync(UserRole.Client);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/test/owned/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/test/owned-only/{owner.Id}")).StatusCode);
    }
}
