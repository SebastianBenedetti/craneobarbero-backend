using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Payments;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Payments;

public sealed class FakePaymentGateway : IPaymentGateway
{
    public bool IsConfigured => true;
    public List<PreferenceRequest> Preferences { get; } = [];
    public List<string> Expired { get; } = [];
    public List<string> Refunds { get; } = [];
    public Dictionary<string, GatewayPayment> Payments { get; } = [];
    public bool FailRefunds { get; set; }

    public Task<CreatedPreference> CreatePreferenceAsync(PreferenceRequest request, CancellationToken cancellationToken)
    {
        Preferences.Add(request);
        var id = $"pref-{Preferences.Count}";
        return Task.FromResult(new CreatedPreference(id, $"https://checkout.example/{id}"));
    }

    public Task ExpirePreferenceAsync(string preferenceId, CancellationToken cancellationToken)
    {
        Expired.Add(preferenceId);
        return Task.CompletedTask;
    }

    public Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Payments.GetValueOrDefault(paymentId));

    public Task RefundAsync(string paymentId, CancellationToken cancellationToken)
    {
        if (FailRefunds)
        {
            throw new PaymentGatewayException("Mercado Pago respondió 500.");
        }

        Refunds.Add(paymentId);
        return Task.CompletedTask;
    }

    public bool IsValidWebhookSignature(string? signatureHeader, string? requestId, string? dataId) => signatureHeader == "valida";
}

public sealed class PaymentApiFactory : ScheduleApiFactory
{
    public FakePaymentGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(Gateway);
        });
    }
}

public sealed class PaymentTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Booking_online_returns_a_checkout_link_that_expires_with_the_reservation()
    {
        var s = await SetupAsync();

        var response = await s.Client.PostAsJsonAsync("/appointments", new { barberId = s.Barber.Id, cutId = s.Cut.Id, appointmentDate = Local(2026, 11, 24, 9, 0) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("appointment").GetProperty("id").GetInt32();
        Assert.StartsWith("https://checkout.example/pref-", json.GetProperty("checkoutUrl").GetString());
        var preference = factory.Gateway.Preferences.Last();
        Assert.Equal($"appointment:{id}", preference.ExternalReference);
        Assert.Equal(1500m, preference.Item.UnitPrice);
        Assert.Equal(Now.AddMinutes(30), preference.ExpiresAt);
        Assert.Equal(s.ClientUser.Email, preference.PayerEmail);
        var payment = await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.AppointmentId == id));
        Assert.Equal(TransactionStatus.Pending, payment.Status);
        Assert.NotNull(payment.PreferenceId);
    }

    [Fact]
    public async Task Approved_payment_from_the_webhook_confirms_the_appointment_once()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 10, 0));
        factory.Gateway.Payments["1001"] = new GatewayPayment("1001", "approved", $"appointment:{id}", 1500m, Now.AddMinutes(2));

        var first = await Webhook("1001");
        var second = await Webhook("1001");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var appointment = await LoadAsync(id);
        Assert.Equal(AppointmentStatus.Pending, appointment.Status);
        Assert.Equal(PaymentStatus.Paid, appointment.PaymentStatus);
        Assert.Equal(Now.AddMinutes(2), appointment.PaidAt);
        Assert.Null(appointment.PaymentExpiresAt);
        var payments = await factory.WithDbAsync(db => db.Payments.AsNoTracking().Where(p => p.AppointmentId == id).ToListAsync());
        var approved = Assert.Single(payments);
        Assert.Equal("1001", approved.ExternalId);
        Assert.Equal(TransactionStatus.Approved, approved.Status);
        Assert.DoesNotContain("1001", factory.Gateway.Refunds);
    }

    [Fact]
    public async Task Webhook_requires_a_valid_signature_and_ignores_other_topics()
    {
        var unsigned = await factory.CreateClient().PostAsJsonAsync("/payments/webhook?type=payment&data.id=1", new { });
        var other = await Webhook("55", type: "merchant_order");

        await HttpAssertions.AssertErrorAsync(unsigned, HttpStatusCode.Unauthorized, "invalid_signature");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Returning_from_checkout_confirms_with_the_gateway_and_checks_ownership()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 11, 0));
        factory.Gateway.Payments["2002"] = new GatewayPayment("2002", "approved", $"appointment:{id}", 1500m, Now.AddMinutes(1));
        var stranger = await factory.ClientForAsync(UserRole.Client);
        var url = $"/payments/return?payment_id=2002&status=approved&external_reference=appointment:{id}";

        var foreign = await stranger.GetAsync(url);
        var mine = await s.Client.GetFromJsonAsync<JsonElement>(url);

        await HttpAssertions.AssertErrorAsync(foreign, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("pending", mine.GetProperty("appointment").GetProperty("status").GetString());
        Assert.Equal("paid", mine.GetProperty("appointment").GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task Rejected_payment_keeps_waiting_and_paying_again_replaces_the_checkout()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 12, 0));
        var firstPreference = await factory.WithDbAsync(db => db.Payments.Where(p => p.AppointmentId == id).Select(p => p.PreferenceId).SingleAsync());
        factory.Gateway.Payments["3003"] = new GatewayPayment("3003", "rejected", $"appointment:{id}", 1500m, null);

        await Webhook("3003");
        var retry = await s.Client.PostAsync($"/appointments/{id}/pay", null);

        Assert.Equal(AppointmentStatus.AwaitingPayment, (await LoadAsync(id)).Status);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var json = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://checkout.example/", json.GetProperty("checkoutUrl").GetString());
        var rows = await factory.WithDbAsync(db => db.Payments.AsNoTracking().Where(p => p.AppointmentId == id).ToListAsync());
        Assert.Contains(rows, p => p.ExternalId == "3003" && p.Status == TransactionStatus.Rejected);
        Assert.Contains(rows, p => p.Status == TransactionStatus.Pending && p.PreferenceId != firstPreference);
    }

    [Fact]
    public async Task Late_approval_for_an_expired_reservation_is_refunded()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 15, 0));
        await factory.WithDbAsync(async db =>
        {
            var appointment = await db.Appointments.SingleAsync(a => a.Id == id);
            appointment.Status = AppointmentStatus.Expired;
            return await db.SaveChangesAsync();
        });
        factory.Gateway.Payments["4004"] = new GatewayPayment("4004", "approved", $"appointment:{id}", 1500m, Now);

        await Webhook("4004");

        Assert.Equal(AppointmentStatus.Expired, (await LoadAsync(id)).Status);
        Assert.Contains("4004", factory.Gateway.Refunds);
        var payment = await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.ExternalId == "4004"));
        Assert.Equal(TransactionStatus.Refunded, payment.Status);
    }

    [Fact]
    public async Task Cancelling_a_paid_appointment_refunds_through_the_gateway_and_failures_change_nothing()
    {
        var s = await SetupAsync();
        var ok = await PaidAsync(s, Local(2026, 11, 24, 16, 0), "5005");
        var failing = await PaidAsync(s, Local(2026, 11, 24, 17, 0), "5006");

        var cancelled = await (await s.Client.PostAsync($"/appointments/{ok}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();
        factory.Gateway.FailRefunds = true;
        HttpResponseMessage failed;
        try
        {
            failed = await s.Client.PostAsync($"/appointments/{failing}/cancel", null);
        }
        finally
        {
            factory.Gateway.FailRefunds = false;
        }

        Assert.Equal("refunded", cancelled.GetProperty("paymentStatus").GetString());
        Assert.Contains("5005", factory.Gateway.Refunds);
        await HttpAssertions.AssertErrorAsync(failed, HttpStatusCode.BadGateway, "payment_provider_error");
        var unchanged = await LoadAsync(failing);
        Assert.Equal(AppointmentStatus.Pending, unchanged.Status);
        Assert.Equal(PaymentStatus.Paid, unchanged.PaymentStatus);
    }

    [Fact]
    public async Task Cancelling_a_reservation_waiting_for_payment_voids_its_checkout()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 18, 0));
        var preference = await factory.WithDbAsync(db => db.Payments.Where(p => p.AppointmentId == id).Select(p => p.PreferenceId!).SingleAsync());

        var response = await s.Client.PostAsync($"/appointments/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(preference, factory.Gateway.Expired);
    }

    [Fact]
    public async Task Only_the_owner_can_pay_and_only_while_the_reservation_is_valid()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 19, 0));
        var stranger = await factory.ClientForAsync(UserRole.Client);
        var paid = await PaidAsync(s, Local(2026, 11, 24, 19, 30), "6006");

        await HttpAssertions.AssertErrorAsync(await stranger.PostAsync($"/appointments/{id}/pay", null), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await s.Client.PostAsync($"/appointments/{paid}/pay", null), HttpStatusCode.Conflict, "not_payable");
    }

    [Fact]
    public void Webhook_signature_follows_the_mercado_pago_manifest()
    {
        const string secret = "clave-de-prueba-del-webhook";
        var settings = Options.Create(new MercadoPagoSettings { WebhookSecret = secret, AccessToken = "x" });
        var client = new MercadoPagoClient(new HttpClient(), settings, TimeProvider.System);
        var manifest = "id:123abc;request-id:req-1;ts:1700000000;";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest)));

        Assert.True(client.IsValidWebhookSignature($"ts=1700000000,v1={hash}", "req-1", "123ABC"));
        Assert.False(client.IsValidWebhookSignature($"ts=1700000001,v1={hash}", "req-1", "123ABC"));
        Assert.False(client.IsValidWebhookSignature($"ts=1700000000,v1={hash}", "req-2", "123ABC"));
        Assert.False(client.IsValidWebhookSignature(null, "req-1", "123ABC"));
        Assert.False(new MercadoPagoClient(new HttpClient(), Options.Create(new MercadoPagoSettings()), TimeProvider.System)
            .IsValidWebhookSignature($"ts=1700000000,v1={hash}", "req-1", "123ABC"));
    }

    [Fact]
    public void External_references_are_parsed_strictly()
    {
        Assert.True(AppointmentPayments.TryParseReference("appointment:42", out var id));
        Assert.Equal(42, id);
        Assert.False(AppointmentPayments.TryParseReference("appointment:-1", out _));
        Assert.False(AppointmentPayments.TryParseReference("order:42", out _));
        Assert.False(AppointmentPayments.TryParseReference(null, out _));
    }

    private sealed record Setup(User Barber, Cut Cut, User ClientUser, HttpClient Client);

    private async Task<Setup> SetupAsync()
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        var cut = await factory.WithDbAsync(async db =>
        {
            var c = new Cut { Name = "Corte y barba", Price = 1500m, DurationMinutes = 30 };
            db.Cuts.Add(c);
            await db.SaveChangesAsync();
            return c;
        });
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);
        return new Setup(barber, cut, user, factory.ClientWithToken(factory.TokenFor(user)));
    }

    private async Task<int> BookAsync(Setup s, DateTimeOffset start)
    {
        var response = await s.Client.PostAsJsonAsync("/appointments", new { barberId = s.Barber.Id, cutId = s.Cut.Id, appointmentDate = start });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appointment").GetProperty("id").GetInt32();
    }

    private async Task<int> PaidAsync(Setup s, DateTimeOffset start, string paymentId)
    {
        var id = await BookAsync(s, start);
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"appointment:{id}", 1500m, Now);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(paymentId)).StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> Webhook(string dataId, string type = "payment")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/payments/webhook?type={type}&data.id={dataId}")
        {
            Content = JsonContent.Create(new { type, data = new { id = dataId } }),
        };
        request.Headers.Add("x-signature", "valida");
        request.Headers.Add("x-request-id", "req");
        return factory.CreateClient().SendAsync(request);
    }

    private Task<Appointment> LoadAsync(int id) =>
        factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
}
