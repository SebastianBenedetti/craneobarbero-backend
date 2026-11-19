using System.Net;
using System.Text;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Security;

public sealed class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Security_headers_are_sent()
    {
        var response = await _client.GetAsync("/test/public");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Error_responses_also_carry_security_headers()
    {
        var response = await _client.GetAsync("/no-existe");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Cors_allows_the_configured_frontend()
    {
        var response = await _client.SendAsync(Preflight(ApiFactory.AllowedOrigin));

        Assert.Equal(ApiFactory.AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Cors_rejects_other_origins()
    {
        var response = await _client.SendAsync(Preflight("https://sitio-ajeno.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        var content = new StringContent(new string('a', 5000), Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/test/echo", content);

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    [Fact]
    public async Task Auth_rate_limit_blocks_excess_requests()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/test/auth-limited", null)).StatusCode);
        }

        var response = await _client.PostAsync("/test/auth-limited", null);

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Password_hash_is_never_serialized()
    {
        var body = await _client.GetStringAsync("/test/entity");

        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2b$", body);
        Assert.Contains("\"role\":\"barber\"", body);
    }

    [Fact]
    public async Task Enums_are_serialized_with_contract_names()
    {
        var body = await _client.GetStringAsync("/test/payment-methods");

        Assert.Equal("[\"cash\",\"card\",\"transfer\",\"wallet\",\"mercadopago\"]", body);
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/test/public");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }
}
