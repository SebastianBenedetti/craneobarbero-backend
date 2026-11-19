using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Errors;

public sealed class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public async Task Invalid_route_ids_are_rejected(string id)
    {
        var response = await _client.GetAsync($"/test/items/{id}");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_id");
    }

    [Fact]
    public async Task Positive_route_id_is_accepted()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/test/items/7");

        Assert.Equal(7, body.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Invalid_body_returns_field_errors()
    {
        var response = await _client.PostAsJsonAsync("/test/echo", new { email = "no-es-un-mail", name = "" });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        var errors = json.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Valid_body_passes_validation()
    {
        var response = await _client.PostAsJsonAsync("/test/echo", new { email = "persona@example.com", name = "Ana" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_without_internal_details()
    {
        var content = new StringContent("{\"email\": ", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/test/echo", content);

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.DoesNotContain("System.", json.GetRawText());
        Assert.DoesNotContain("LineNumber", json.GetRawText());
    }

    [Fact]
    public async Task Unexpected_errors_hide_internal_details()
    {
        var response = await _client.GetAsync("/test/boom");

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("Npgsql", json.GetRawText());
        Assert.DoesNotContain("42P01", json.GetRawText());
    }

    [Fact]
    public async Task Application_exceptions_map_to_their_status()
    {
        await HttpAssertions.AssertErrorAsync(await _client.GetAsync("/test/missing"), HttpStatusCode.NotFound, "not_found");
        var conflict = await HttpAssertions.AssertErrorAsync(await _client.GetAsync("/test/conflict"), HttpStatusCode.Conflict, "slot_taken");
        Assert.Equal("El horario ya está ocupado.", conflict.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_routes_return_json_not_found()
    {
        var response = await _client.GetAsync("/no-existe");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Wrong_method_returns_json_error()
    {
        var response = await _client.DeleteAsync("/test/public");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }
}
