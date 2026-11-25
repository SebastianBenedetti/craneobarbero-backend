using System.Net;
using System.Net.Http.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Pricing;

public sealed class CutPriceHistoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Changing_a_service_price_by_hand_is_kept_in_the_history()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var cut = await factory.WithDbAsync(async db =>
        {
            var row = new Cut { Name = "Corte de prueba", Price = 1000m, DurationMinutes = 30 };
            db.Cuts.Add(row);
            await db.SaveChangesAsync();
            return row;
        });

        var changed = await admin.PutAsJsonAsync($"/cuts/{cut.Id}", new { name = cut.Name, price = 1200m, durationMinutes = 30 });
        var unchanged = await admin.PutAsJsonAsync($"/cuts/{cut.Id}", new { name = "Corte renombrado", price = 1200m, durationMinutes = 30 });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        var history = Assert.Single(await factory.WithDbAsync(db => db.PriceHistory.AsNoTracking()
            .Where(h => h.EntityType == PriceEntityType.Cut && h.EntityId == cut.Id).ToListAsync()));
        Assert.Equal(PriceField.Price, history.Field);
        Assert.Equal(1000m, history.OldPrice);
        Assert.Equal(1200m, history.NewPrice);
        Assert.Equal(PriceChangeReason.Manual, history.Reason);
    }
}
