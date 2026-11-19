using System.Net;
using System.Text.Json;

namespace CraneoBarbero.Api.Tests.Infrastructure;

public static class HttpAssertions
{
    public static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()));
        return json;
    }
}
