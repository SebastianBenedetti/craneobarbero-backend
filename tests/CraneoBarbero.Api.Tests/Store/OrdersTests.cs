using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Payments;
using CraneoBarbero.Api.Store;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class OrdersTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    [Fact]
    public async Task Only_verified_clients_place_orders()
    {
        var s = await SetupAsync();
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsJsonAsync("/orders", Cart(s)), HttpStatusCode.Unauthorized, "unauthorized");
        foreach (var role in new[] { UserRole.Barber, UserRole.Admin })
        {
            var other = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await other.PostAsJsonAsync("/orders", Cart(s)), HttpStatusCode.Forbidden, "forbidden");
        }

        var unverified = await factory.ClientForAsync(UserRole.Client);
        await HttpAssertions.AssertErrorAsync(await unverified.PostAsJsonAsync("/orders", Cart(s)), HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task Placing_an_order_keeps_prices_and_does_not_reserve_stock()
    {
        var s = await SetupAsync();

        var response = await s.Client.PostAsJsonAsync("/orders", Cart(s, 2, 1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var order = json.GetProperty("order");
        var id = order.GetProperty("id").GetInt32();
        Assert.Equal("pending_payment", order.GetProperty("status").GetString());
        Assert.Equal(2 * 1000m + 500m, order.GetProperty("total").GetDecimal());
        Assert.Equal(Now.AddMinutes(30), order.GetProperty("paymentExpiresAt").GetDateTimeOffset());
        Assert.Equal(s.Branch.Id, order.GetProperty("branch").GetProperty("id").GetInt32());
        var items = order.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2000m, items.Single(i => i.GetProperty("productId").GetInt32() == s.Wax.Id).GetProperty("subtotal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("unitCost").ValueKind);
        Assert.StartsWith("https://checkout.example/", json.GetProperty("checkoutUrl").GetString());
        var preference = factory.Gateway.Preferences.Last();
        Assert.Equal($"order:{id}", preference.ExternalReference);
        Assert.Equal(2500m, preference.Item.UnitPrice);
        Assert.Equal(10, await StockAsync(s.Wax.Id));
        Assert.Equal(TransactionStatus.Pending, (await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id))).Status);
    }

    [Fact]
    public async Task Orders_need_active_products_with_enough_stock_and_an_active_branch()
    {
        var s = await SetupAsync();
        var inactive = await factory.AddProductAsync(p => p.IsActive = false);
        var closed = await factory.WithDbAsync(async db =>
        {
            var branch = new Branch { Name = "Sucursal cerrada", Address = "Calle 2", IsActive = false };
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            return branch;
        });

        await HttpAssertions.AssertErrorAsync(
            await s.Client.PostAsJsonAsync("/orders", new { branchId = s.Branch.Id, items = new[] { new { productId = s.Wax.Id, quantity = 11 } } }),
            HttpStatusCode.Conflict, "insufficient_stock");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PostAsJsonAsync("/orders", new { branchId = s.Branch.Id, items = new[] { new { productId = inactive.Id, quantity = 1 } } }),
            HttpStatusCode.BadRequest, "product_unavailable");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PostAsJsonAsync("/orders", new { branchId = closed.Id, items = new[] { new { productId = s.Wax.Id, quantity = 1 } } }),
            HttpStatusCode.BadRequest, "branch_inactive");
        var invalid = await s.Client.PostAsJsonAsync("/orders", new
        {
            items = new[] { new { productId = s.Wax.Id, quantity = 0 }, new { productId = s.Wax.Id, quantity = 1 } },
        });
        var errors = (await HttpAssertions.AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("branchId", out _));
        Assert.True(errors.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task Approved_payment_discounts_stock_once_and_keeps_the_cost_at_that_moment()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 3, 1);
        await factory.WithDbAsync(async db =>
        {
            var wax = await db.Products.SingleAsync(p => p.Id == s.Wax.Id);
            wax.Cost = 650m;
            return await db.SaveChangesAsync();
        });
        var paymentId = NewPaymentId();
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"order:{id}", 3500m, Now.AddMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await Webhook(paymentId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(paymentId)).StatusCode);

        var order = await LoadAsync(id);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Null(order.PaymentExpiresAt);
        Assert.Equal(650m, order.Items.Single(i => i.ProductId == s.Wax.Id).UnitCost);
        Assert.Equal(200m, order.Items.Single(i => i.ProductId == s.Gel.Id).UnitCost);
        Assert.Equal(7, await StockAsync(s.Wax.Id));
        Assert.Equal(4, await StockAsync(s.Gel.Id));
        var sales = await factory.WithDbAsync(db => db.StockMovements.AsNoTracking().Where(m => m.OrderId == id).ToListAsync());
        Assert.Equal(2, sales.Count);
        Assert.All(sales, m => Assert.Equal(StockMovementType.Sale, m.Type));
        Assert.Equal(-3, sales.Single(m => m.ProductId == s.Wax.Id).Quantity);
        Assert.Equal(7, sales.Single(m => m.ProductId == s.Wax.Id).BalanceAfter);
        Assert.Equal(s.ClientUser.Id, sales[0].UserId);
        var payment = Assert.Single(await factory.WithDbAsync(db => db.Payments.AsNoTracking().Where(p => p.OrderId == id).ToListAsync()));
        Assert.Equal(TransactionStatus.Approved, payment.Status);
        Assert.Equal(paymentId, payment.ExternalId);
        Assert.DoesNotContain(paymentId, factory.Gateway.Refunds);
    }

    [Fact]
    public async Task Payment_without_enough_stock_cancels_and_refunds_the_order()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 1, 4);
        await factory.WithDbAsync(async db =>
        {
            var gel = await db.Products.SingleAsync(p => p.Id == s.Gel.Id);
            gel.Stock = 2;
            return await db.SaveChangesAsync();
        });
        var paymentId = NewPaymentId();
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"order:{id}", 3000m, Now);

        await Webhook(paymentId);

        var order = await LoadAsync(id);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(RefundStatus.Refunded, order.RefundStatus);
        Assert.Equal(OrderLimits.OutOfStockReason, order.CancellationReason);
        Assert.Contains(paymentId, factory.Gateway.Refunds);
        Assert.Equal(10, await StockAsync(s.Wax.Id));
        Assert.Equal(2, await StockAsync(s.Gel.Id));
        Assert.Equal(0, await factory.WithDbAsync(db => db.StockMovements.CountAsync(m => m.OrderId == id)));
        var payment = Assert.Single(await factory.WithDbAsync(db => db.Payments.AsNoTracking().Where(p => p.OrderId == id).ToListAsync()));
        Assert.Equal(TransactionStatus.Refunded, payment.Status);
    }

    [Fact]
    public async Task Late_payment_for_an_expired_order_is_refunded()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 1, 0);
        await factory.WithDbAsync(async db =>
        {
            var row = await db.Orders.SingleAsync(o => o.Id == id);
            row.Status = OrderStatus.Expired;
            return await db.SaveChangesAsync();
        });
        var paymentId = NewPaymentId();
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"order:{id}", 1000m, Now);

        await Webhook(paymentId);

        Assert.Equal(OrderStatus.Expired, (await LoadAsync(id)).Status);
        Assert.Contains(paymentId, factory.Gateway.Refunds);
        Assert.Equal(10, await StockAsync(s.Wax.Id));
    }

    [Fact]
    public async Task Unpaid_orders_expire_after_thirty_minutes()
    {
        var s = await SetupAsync();
        var due = await PlaceAsync(s, 1, 0);
        var fresh = await PlaceAsync(s, 1, 0);
        await factory.WithDbAsync(async db =>
        {
            var row = await db.Orders.SingleAsync(o => o.Id == due);
            row.PaymentExpiresAt = Now.AddMinutes(-1);
            return await db.SaveChangesAsync();
        });

        var expired = await RunExpiryAsync();
        var again = await RunExpiryAsync();

        Assert.True(expired >= 1);
        Assert.Equal(0, again);
        Assert.Equal(OrderStatus.Expired, (await LoadAsync(due)).Status);
        Assert.Equal(OrderStatus.PendingPayment, (await LoadAsync(fresh)).Status);
        var mine = await s.Client.GetFromJsonAsync<JsonElement>("/orders/mine");
        var ids = mine.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.DoesNotContain(due, ids);
        Assert.Contains(fresh, ids);
    }

    [Fact]
    public async Task Admin_moves_a_paid_order_to_ready_and_delivered()
    {
        var s = await SetupAsync();
        var id = await PaidOrderAsync(s);

        await HttpAssertions.AssertErrorAsync(await s.Admin.PutAsJsonAsync($"/orders/{id}/status", new { status = "delivered" }), HttpStatusCode.Conflict, "invalid_status_transition");
        var ready = await s.Admin.PutAsJsonAsync($"/orders/{id}/status", new { status = "ready" });
        Assert.Equal("ready", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var delivered = await s.Admin.PutAsJsonAsync($"/orders/{id}/status", new { status = "delivered" });
        Assert.Equal("delivered", (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        await HttpAssertions.AssertErrorAsync(await s.Admin.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = true }), HttpStatusCode.Conflict, "not_cancellable");
        await HttpAssertions.AssertErrorAsync(await s.Admin.PutAsJsonAsync($"/orders/{id}/status", new { status = "paid" }), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await s.Client.PutAsJsonAsync($"/orders/{id}/status", new { status = "ready" }), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await s.Client.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = true }), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Cancelling_with_refund_returns_money_and_restores_stock_at_the_sold_cost()
    {
        var s = await SetupAsync();
        var id = await PaidOrderAsync(s, 2);
        var paymentId = (await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id))).ExternalId!;
        await factory.WithDbAsync(async db =>
        {
            var wax = await db.Products.SingleAsync(p => p.Id == s.Wax.Id);
            wax.Cost = 900m;
            return await db.SaveChangesAsync();
        });

        var response = await s.Admin.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = true, reason = " Pedido del cliente " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
        Assert.Equal("refunded", body.GetProperty("refundStatus").GetString());
        Assert.Equal("Pedido del cliente", body.GetProperty("cancellationReason").GetString());
        Assert.Contains(paymentId, factory.Gateway.Refunds);
        Assert.Equal(10, await StockAsync(s.Wax.Id));
        var refund = await factory.WithDbAsync(db => db.StockMovements.AsNoTracking().SingleAsync(m => m.OrderId == id && m.Type == StockMovementType.Refund));
        Assert.Equal(2, refund.Quantity);
        Assert.Equal(600m, refund.UnitCost);
        Assert.Equal(TransactionStatus.Refunded, (await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id))).Status);
    }

    [Fact]
    public async Task Cancelling_without_refund_restores_stock_but_keeps_the_money()
    {
        var s = await SetupAsync();
        var id = await PaidOrderAsync(s);
        var refundsBefore = factory.Gateway.Refunds.Count;

        var response = await s.Admin.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = false });

        Assert.Equal("not_refunded", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refundStatus").GetString());
        Assert.Equal(refundsBefore, factory.Gateway.Refunds.Count);
        Assert.Equal(10, await StockAsync(s.Wax.Id));
        Assert.Equal(TransactionStatus.Approved, (await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id))).Status);
    }

    [Fact]
    public async Task A_failed_refund_leaves_the_order_untouched()
    {
        var s = await SetupAsync();
        var id = await PaidOrderAsync(s);
        factory.Gateway.FailRefunds = true;
        try
        {
            var response = await s.Admin.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = true });

            await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadGateway, "payment_provider_error");
        }
        finally
        {
            factory.Gateway.FailRefunds = false;
        }

        Assert.Equal(OrderStatus.Paid, (await LoadAsync(id)).Status);
        Assert.Equal(9, await StockAsync(s.Wax.Id));
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_releases_its_payment_link()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 1, 0);
        var preference = (await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id))).PreferenceId!;

        var response = await s.Admin.PostAsJsonAsync($"/orders/{id}/cancel", new { refund = false });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
        Assert.Equal("none", body.GetProperty("refundStatus").GetString());
        Assert.Contains(preference, factory.Gateway.Expired);
    }

    [Fact]
    public async Task Clients_see_only_their_orders_and_can_retry_the_payment()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 1, 0);
        var stranger = await VerifiedClientAsync();

        await HttpAssertions.AssertErrorAsync(await stranger.GetAsync($"/orders/{id}"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await stranger.PostAsync($"/orders/{id}/pay", null), HttpStatusCode.Forbidden, "forbidden");
        var strangerOrders = await stranger.GetFromJsonAsync<JsonElement>("/orders/mine");
        Assert.Equal(0, strangerOrders.GetProperty("total").GetInt32());

        var retry = await s.Client.PostAsync($"/orders/{id}/pay", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.StartsWith("https://checkout.example/", (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checkoutUrl").GetString());
        var payments = await factory.WithDbAsync(db => db.Payments.AsNoTracking().Where(p => p.OrderId == id).OrderBy(p => p.Id).ToListAsync());
        Assert.Equal(TransactionStatus.Rejected, payments[0].Status);
        Assert.Equal(TransactionStatus.Pending, payments[1].Status);

        var adminView = await s.Admin.GetFromJsonAsync<JsonElement>($"/orders?status=pending_payment&clientId={s.ClientUser.Id}");
        var listed = adminView.GetProperty("items").EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == id);
        Assert.Equal(1000m, listed.GetProperty("items")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(s.ClientUser.Email, listed.GetProperty("client").GetProperty("email").GetString());
        await HttpAssertions.AssertErrorAsync(await s.Admin.GetAsync("/orders?status=desconocido"), HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Returning_from_the_checkout_confirms_the_order()
    {
        var s = await SetupAsync();
        var id = await PlaceAsync(s, 1, 0);
        var paymentId = NewPaymentId();
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"order:{id}", 1000m, Now);

        var response = await s.Client.GetAsync($"/payments/return?payment_id={paymentId}&external_reference=order:{id}&status=approved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("appointment").ValueKind);
        Assert.Equal("paid", body.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(9, await StockAsync(s.Wax.Id));
    }

    private sealed record Setup(
        HttpClient Client,
        User ClientUser,
        HttpClient Admin,
        Branch Branch,
        Product Wax,
        Product Gel);

    private async Task<Setup> SetupAsync()
    {
        var clientUser = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(clientUser.Id, u => u.EmailVerified = true);
        var branch = await factory.WithDbAsync(async db =>
        {
            var row = new Branch { Name = "Sucursal Tienda", Address = "Calle 1" };
            db.Branches.Add(row);
            await db.SaveChangesAsync();
            return row;
        });
        var wax = await factory.AddProductAsync(p => { p.SalePrice = 1000m; p.Cost = 600m; p.Stock = 10; });
        var gel = await factory.AddProductAsync(p => { p.SalePrice = 500m; p.Cost = 200m; p.Stock = 5; });
        return new Setup(
            factory.ClientWithToken(factory.TokenFor(clientUser)),
            clientUser,
            await factory.ClientForAsync(UserRole.Admin),
            branch,
            wax,
            gel);
    }

    private async Task<HttpClient> VerifiedClientAsync()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);
        return factory.ClientWithToken(factory.TokenFor(user));
    }

    private static object Cart(Setup s, int wax = 1, int gel = 0)
    {
        var items = new List<object> { new { productId = s.Wax.Id, quantity = wax } };
        if (gel > 0)
        {
            items.Add(new { productId = s.Gel.Id, quantity = gel });
        }

        return new { branchId = s.Branch.Id, items };
    }

    private async Task<int> PlaceAsync(Setup s, int wax, int gel)
    {
        var response = await s.Client.PostAsJsonAsync("/orders", Cart(s, wax, gel));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("order").GetProperty("id").GetInt32();
    }

    private async Task<int> PaidOrderAsync(Setup s, int wax = 1)
    {
        var id = await PlaceAsync(s, wax, 0);
        var paymentId = NewPaymentId();
        factory.Gateway.Payments[paymentId] = new GatewayPayment(paymentId, "approved", $"order:{id}", wax * 1000m, Now);
        await Webhook(paymentId);
        Assert.Equal(OrderStatus.Paid, (await LoadAsync(id)).Status);
        return id;
    }

    private static string NewPaymentId() => Random.Shared.NextInt64(10_000_000, 99_999_999).ToString();

    private Task<HttpResponseMessage> Webhook(string dataId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/payments/webhook?type=payment&data.id={dataId}")
        {
            Content = JsonContent.Create(new { type = "payment", data = new { id = dataId } }),
        };
        request.Headers.Add("x-signature", "valida");
        request.Headers.Add("x-request-id", "req");
        return factory.CreateClient().SendAsync(request);
    }

    private async Task<int> RunExpiryAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderTransitions>().ExpireUnpaidAsync(CancellationToken.None);
    }

    private Task<Order> LoadAsync(int id) =>
        factory.WithDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == id));

    private Task<int> StockAsync(int productId) =>
        factory.WithDbAsync(db => db.Products.Where(p => p.Id == productId).Select(p => p.Stock).SingleAsync());
}
