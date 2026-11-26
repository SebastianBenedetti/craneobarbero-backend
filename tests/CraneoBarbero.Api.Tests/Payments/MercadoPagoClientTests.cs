using System.Net;
using System.Text;
using CraneoBarbero.Api.Payments;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Tests.Payments;

public sealed class MercadoPagoClientTests
{
    [Fact]
    public async Task Get_payment_converts_the_approval_date_to_utc()
    {
        var handler = new StubHandler("""
            {"id": 123, "status": "approved", "external_reference": "appointment:7",
             "transaction_amount": 15000.5, "date_approved": "2026-11-19T15:30:00.000-04:00"}
            """);
        var client = CreateClient(handler);

        var payment = await client.GetPaymentAsync("123", CancellationToken.None);

        Assert.NotNull(payment);
        Assert.Equal("123", payment.Id);
        Assert.Equal("approved", payment.Status);
        Assert.Equal("appointment:7", payment.ExternalReference);
        Assert.Equal(15000.5m, payment.Amount);
        Assert.Equal(TimeSpan.Zero, payment.ApprovedAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 19, 30, 0, TimeSpan.Zero), payment.ApprovedAt);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.EndsWith("/v1/payments/123", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Get_payment_without_approval_date_returns_null_date()
    {
        var client = CreateClient(new StubHandler("""{"id": 5, "status": "pending"}"""));

        var payment = await client.GetPaymentAsync("5", CancellationToken.None);

        Assert.NotNull(payment);
        Assert.Null(payment.ApprovedAt);
        Assert.Null(payment.ExternalReference);
    }

    private static MercadoPagoClient CreateClient(HttpMessageHandler handler)
    {
        var settings = new MercadoPagoSettings { AccessToken = "TEST-token" };
        var http = new HttpClient(handler) { BaseAddress = new Uri(settings.ApiUrl) };
        return new MercadoPagoClient(http, Options.Create(settings), TimeProvider.System);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
