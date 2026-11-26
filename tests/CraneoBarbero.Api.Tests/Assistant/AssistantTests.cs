using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Assistant;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Assistant;

public sealed class FakeInterpreter : ITextInterpreter
{
    public bool Configured { get; set; }
    public Func<string, InterpretedText?> Answer { get; set; } = _ => null;
    public List<(string Text, AssistantCatalog Catalog)> Calls { get; } = [];

    public bool IsConfigured => Configured;

    public Task<InterpretedText?> InterpretAsync(string text, AssistantCatalog catalog, DateOnly today, CancellationToken cancellationToken)
    {
        Calls.Add((text, catalog));
        return Task.FromResult(Answer(text));
    }
}

public sealed class AssistantApiFactory : ScheduleApiFactory
{
    public FakeInterpreter Interpreter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITextInterpreter>();
            services.AddSingleton<ITextInterpreter>(Interpreter);
        });
    }
}

public sealed class AssistantTests(AssistantApiFactory factory) : IClassFixture<AssistantApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Only_verified_clients_can_use_the_assistant()
    {
        var body = new { message = "Quiero un turno" };

        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsJsonAsync("/assistant/message", body), HttpStatusCode.Unauthorized, "unauthorized");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Barber)).PostAsJsonAsync("/assistant/message", body), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Admin)).PostAsJsonAsync("/assistant/message", body), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Client)).PostAsJsonAsync("/assistant/message", body), HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task Booking_is_completed_with_buttons_only()
    {
        var (branch, barber, cut) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();

        var start = await SendAsync(client, null);
        Assert.Equal("start", Step(start));
        var id = start.GetProperty("conversationId").GetString();

        var branches = await SendAsync(client, id, choice: "start");
        Assert.Equal("branch", Step(branches));
        Assert.Contains($"branch:{branch.Id}", Values(branches));

        var barbers = await SendAsync(client, id, choice: $"branch:{branch.Id}");
        Assert.Equal("barber", Step(barbers));
        Assert.Contains($"barber:{barber.Id}", Values(barbers));

        var cuts = await SendAsync(client, id, choice: $"barber:{barber.Id}");
        Assert.Equal("cut", Step(cuts));

        var days = await SendAsync(client, id, choice: $"cut:{cut.Id}");
        Assert.Equal("day", Step(days));
        Assert.Contains($"day:{Tuesday:yyyy-MM-dd}", Values(days));

        var times = await SendAsync(client, id, choice: $"day:{Tuesday:yyyy-MM-dd}");
        Assert.Equal("time", Step(times));
        var tenOClock = TimeChoice(Local(2026, 11, 24, 10, 0));
        Assert.Contains(tenOClock, Values(times));

        var summary = await SendAsync(client, id, choice: tenOClock);
        Assert.Equal("confirm", Step(summary));
        var card = summary.GetProperty("summary");
        Assert.Equal("10:00", card.GetProperty("time").GetString());
        Assert.Equal(1000m, card.GetProperty("price").GetDecimal());
        Assert.Equal(0, await CountAppointmentsAsync(user.Id));

        var done = await SendAsync(client, id, choice: "confirm");
        Assert.Equal("done", Step(done));
        var appointmentId = done.GetProperty("appointmentId").GetInt32();
        Assert.Contains("Pagar", done.GetProperty("reply").GetString());

        var appointment = await factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId));
        Assert.Equal(user.Id, appointment.ClientId);
        Assert.Equal(AppointmentStatus.AwaitingPayment, appointment.Status);
        Assert.Equal(PaymentMethod.MercadoPago, appointment.PaymentMethod);
        Assert.Equal(Local(2026, 11, 24, 10, 0), appointment.AppointmentDate);
        Assert.Equal(UserRole.Client, appointment.CreatedByRole);
        Assert.Equal(barber.Id, appointment.BarberId);
    }

    [Fact]
    public async Task Choices_outside_the_rules_are_rejected()
    {
        var (branch, barber, cut) = await SetupAsync();
        var (otherBranch, otherBarber, _) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        await SendAsync(client, id, choice: $"branch:{branch.Id}");
        var wrongBarber = await SendAsync(client, id, choice: $"barber:{otherBarber.Id}");
        Assert.Equal("barber", Step(wrongBarber));
        Assert.DoesNotContain($"barber:{otherBarber.Id}", Values(wrongBarber));

        await SendAsync(client, id, choice: $"barber:{barber.Id}");
        await SendAsync(client, id, choice: $"cut:{cut.Id}");
        var sunday = await SendAsync(client, id, choice: "day:2026-11-29");
        Assert.Equal("day", Step(sunday));
        var far = await SendAsync(client, id, choice: "day:2027-03-02");
        Assert.Equal("day", Step(far));

        await SendAsync(client, id, choice: $"day:{Tuesday:yyyy-MM-dd}");
        var offGrid = await SendAsync(client, id, choice: TimeChoice(Local(2026, 11, 24, 10, 15)));
        Assert.Equal("time", Step(offGrid));
        var lunch = await SendAsync(client, id, choice: TimeChoice(Local(2026, 11, 24, 13, 0)));
        Assert.Equal("time", Step(lunch));
        var confirmEarly = await SendAsync(client, id, choice: "confirm");
        Assert.Equal("time", Step(confirmEarly));

        Assert.Equal(0, await CountAppointmentsAsync(user.Id));
        Assert.NotEqual(otherBranch.Id, branch.Id);
    }

    [Fact]
    public async Task Taken_slot_offers_other_times_and_does_not_book()
    {
        var (branch, barber, cut) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);
        await ChooseUntilTimeAsync(client, id, branch, barber, cut);
        var start = Local(2026, 11, 24, 11, 0);
        await SendAsync(client, id, choice: TimeChoice(start));

        await factory.AddAppointmentAsync(barber, cut, start);
        var response = await SendAsync(client, id, choice: "confirm");

        Assert.Equal("time", Step(response));
        Assert.Contains("se acaba de ocupar", response.GetProperty("reply").GetString());
        Assert.DoesNotContain(TimeChoice(start), Values(response));
        Assert.Equal(0, await CountAppointmentsAsync(user.Id));
    }

    [Fact]
    public async Task Own_overlapping_appointment_hides_that_time()
    {
        var (branch, barber, cut) = await SetupAsync();
        var (_, otherBarber, _) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();
        var busy = Local(2026, 11, 24, 16, 0);
        await factory.AddAppointmentAsync(otherBarber, cut, busy, clientId: user.Id);
        var id = await ConversationAsync(client);

        var times = await ChooseUntilTimeAsync(client, id, branch, barber, cut);

        Assert.DoesNotContain(TimeChoice(busy), Values(times));
    }

    [Fact]
    public async Task Conversation_of_another_user_is_not_reused()
    {
        var (branch, barber, _) = await SetupAsync();
        var (owner, _) = await VerifiedClientAsync();
        var (intruder, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(owner);
        await SendAsync(owner, id, choice: $"branch:{branch.Id}");
        await SendAsync(owner, id, choice: $"barber:{barber.Id}");

        var response = await SendAsync(intruder, id, choice: "start");

        Assert.NotEqual(id, response.GetProperty("conversationId").GetString());
        Assert.Contains("venció", response.GetProperty("reply").GetString());
        Assert.DoesNotContain(barber.Name, response.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Free_text_is_understood_without_the_model_and_still_asks_to_confirm()
    {
        var (_, barber, cut) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);
        var calls = factory.Interpreter.Calls.Count;

        var response = await SendAsync(client, id,
            message: $"Quiero un {cut.Name.ToLowerInvariant()} el martes a las 10:00 con {barber.Name.Split(' ')[0]}");

        Assert.Equal("confirm", Step(response));
        Assert.Equal("10:00", response.GetProperty("summary").GetProperty("time").GetString());
        Assert.Equal(barber.Name, response.GetProperty("summary").GetProperty("barberName").GetString());
        Assert.Equal(0, await CountAppointmentsAsync(user.Id));
        Assert.Equal(calls, factory.Interpreter.Calls.Count);
    }

    [Fact]
    public async Task Model_values_are_validated_against_real_data()
    {
        var (_, barber, cut) = await SetupAsync();
        var (client, user) = await VerifiedClientAsync();
        factory.Interpreter.Configured = true;
        factory.Interpreter.Answer = text => text.Contains("inventado")
            ? new InterpretedText("book", null, "Barbero Inexistente", "Masaje", "2026-11-24", "10:00", null)
            : new InterpretedText("book", null, barber.Name, cut.Name, "2026-11-24", "15:30", null);
        try
        {
            var id = await ConversationAsync(client);
            var invented = await SendAsync(client, id, message: "zzz inventado qqq");
            Assert.Equal("branch", Step(invented));

            var understood = await SendAsync(client, id, message: "zzz algo con apodo qqq");
            Assert.Equal("confirm", Step(understood));
            Assert.Equal("15:30", understood.GetProperty("summary").GetProperty("time").GetString());

            var call = factory.Interpreter.Calls[^1];
            Assert.Equal("zzz algo con apodo qqq", call.Text);
            Assert.DoesNotContain(call.Catalog.Barbers, b => b.Name.Contains('@'));
            Assert.Equal(0, await CountAppointmentsAsync(user.Id));
        }
        finally
        {
            factory.Interpreter.Configured = false;
            factory.Interpreter.Answer = _ => null;
        }
    }

    [Fact]
    public async Task Model_completes_what_the_local_reading_missed()
    {
        var (_, barber, cut) = await SetupAsync();
        var (client, _) = await VerifiedClientAsync();
        factory.Interpreter.Configured = true;
        factory.Interpreter.Answer = _ => new InterpretedText("book", null, "Otro Nombre", cut.Name, "2026-11-30", null, null);
        try
        {
            var id = await ConversationAsync(client);
            var response = await SendAsync(client, id, message: $"quiero cortarme el pelo el martes con {barber.Name.Split(' ')[0]}");

            Assert.Equal("time", Step(response));
            Assert.Contains(cut.Name, response.GetProperty("reply").GetString());
            Assert.Contains("martes 24/11", response.GetProperty("reply").GetString());
            Assert.Contains(barber.Name, response.GetProperty("reply").GetString());
        }
        finally
        {
            factory.Interpreter.Configured = false;
            factory.Interpreter.Answer = _ => null;
        }
    }

    [Fact]
    public async Task Model_failure_keeps_the_buttons_working()
    {
        var (branch, _, _) = await SetupAsync();
        var (client, _) = await VerifiedClientAsync();
        factory.Interpreter.Configured = true;
        factory.Interpreter.Answer = _ => null;
        try
        {
            var id = await ConversationAsync(client);
            var response = await SendAsync(client, id, message: "zzz qqq www");

            Assert.Equal("branch", Step(response));
            Assert.Contains("No llegué a entenderte", response.GetProperty("reply").GetString());
            Assert.Contains($"branch:{branch.Id}", Values(response));
        }
        finally
        {
            factory.Interpreter.Configured = false;
        }
    }

    [Fact]
    public async Task Barbers_with_the_same_name_are_offered_to_choose()
    {
        var (branch, first, cut) = await SetupAsync();
        var (otherBranch, second, _) = await SetupAsync();
        var name = "Nico" + Guid.NewGuid().ToString("N")[..5];
        await factory.UpdateUserAsync(first.Id, u => u.Name = $"{name} Uno");
        await factory.UpdateUserAsync(second.Id, u => u.Name = $"{name} Dos");
        var (client, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        var choose = await SendAsync(client, id, message: $"un turno con {name}");

        Assert.Equal("barber", Step(choose));
        var options = choose.GetProperty("options").EnumerateArray()
            .Where(o => o.GetProperty("value").GetString()!.StartsWith("barber:"))
            .ToList();
        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.GetProperty("label").GetString() == $"{name} Uno ({branch.Name})");
        Assert.Contains(options, o => o.GetProperty("label").GetString() == $"{name} Dos ({otherBranch.Name})");

        var chosen = await SendAsync(client, id, choice: $"barber:{second.Id}");
        Assert.Equal("cut", Step(chosen));
        await SendAsync(client, id, choice: $"cut:{cut.Id}");
        var times = await SendAsync(client, id, choice: $"day:{Tuesday:yyyy-MM-dd}");
        var summary = await SendAsync(client, id, choice: Values(times).First(v => v.StartsWith("time:")));
        Assert.Equal(otherBranch.Name, summary.GetProperty("summary").GetProperty("branchName").GetString());
        Assert.Equal($"{name} Dos", summary.GetProperty("summary").GetProperty("barberName").GetString());
    }

    [Fact]
    public async Task Barbers_with_the_same_name_in_one_branch_are_listed_without_the_branch()
    {
        var (branch, first, _) = await SetupAsync();
        var second = await factory.AddBarberInBranchAsync(branch.Id);
        await factory.GenerateAsync(second.Id, Tuesday, Tuesday);
        var name = "Juan" + Guid.NewGuid().ToString("N")[..5];
        await factory.UpdateUserAsync(first.Id, u => u.Name = $"{name} Pérez");
        await factory.UpdateUserAsync(second.Id, u => u.Name = $"{name} Gómez");
        var (client, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        var choose = await SendAsync(client, id, message: $"con {name}");

        Assert.Equal("barber", Step(choose));
        var labels = choose.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()).ToList();
        Assert.Contains($"{name} Pérez", labels);
        Assert.Contains($"{name} Gómez", labels);

        var exact = await SendAsync(client, id, message: $"{name} Gómez");
        Assert.Equal("cut", Step(exact));
    }

    [Fact]
    public async Task Nickname_after_con_matches_a_single_barber()
    {
        var (_, barber, _) = await SetupAsync();
        var name = "Gerardito" + Guid.NewGuid().ToString("N")[..4];
        await factory.UpdateUserAsync(barber.Id, u => u.Name = $"{name} Baldus");
        var (client, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        var response = await SendAsync(client, id, message: $"quiero un turno con {name[..6]}");

        Assert.Equal("cut", Step(response));
        Assert.Contains($"{name} Baldus", response.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Greeting_gets_a_friendly_reply()
    {
        await SetupAsync();
        var (client, _) = await VerifiedClientAsync();
        var start = await SendAsync(client, null);

        var response = await SendAsync(client, start.GetProperty("conversationId").GetString(), message: "Hola, ¿cómo estás?");

        Assert.Equal("branch", Step(response));
        Assert.StartsWith("¡Hola!", response.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Day_and_part_of_day_are_kept_until_the_missing_steps_are_chosen()
    {
        var (branch, barber, cut) = await SetupAsync();
        var (client, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        var waiting = await SendAsync(client, id, message: $"el martes a la tarde en {branch.Name}");
        Assert.Equal("barber", Step(waiting));
        await SendAsync(client, id, choice: $"barber:{barber.Id}");
        var times = await SendAsync(client, id, choice: $"cut:{cut.Id}");

        Assert.Equal("time", Step(times));
        var values = Values(times).Where(v => v.StartsWith("time:")).ToList();
        Assert.Contains(TimeChoice(Local(2026, 11, 24, 15, 0)), values);
        Assert.DoesNotContain(TimeChoice(Local(2026, 11, 24, 10, 0)), values);
    }

    [Fact]
    public async Task Only_booking_is_allowed()
    {
        await SetupAsync();
        var (client, _) = await VerifiedClientAsync();
        var id = await ConversationAsync(client);

        var response = await SendAsync(client, id, message: "Quiero cancelar mi turno del viernes");

        Assert.Contains("Mis turnos", response.GetProperty("reply").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("checkoutUrl").ValueKind);
    }

    [Fact]
    public async Task Messages_per_user_are_limited()
    {
        var (client, _) = await VerifiedClientAsync();
        HttpResponseMessage last = new(HttpStatusCode.OK);
        for (var i = 0; i < 21; i++)
        {
            last = await client.PostAsJsonAsync("/assistant/message", new { });
        }

        await HttpAssertions.AssertErrorAsync(last, HttpStatusCode.TooManyRequests, "too_many_requests");
    }

    private async Task<(Branch Branch, User Barber, Cut Cut)> SetupAsync()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await factory.UpdateUserAsync(barber.Id, u => u.Name = $"Barbero{suffix} Prueba");
        await factory.WithDbAsync(async db =>
        {
            var row = await db.Branches.SingleAsync(b => b.Id == branch.Id);
            row.Name = $"Sucursal {suffix}";
            return await db.SaveChangesAsync();
        });
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        var cut = await factory.AddCutAsync(30);
        await factory.WithDbAsync(async db =>
        {
            var row = await db.Cuts.SingleAsync(c => c.Id == cut.Id);
            row.Name = $"Servicio{suffix}";
            return await db.SaveChangesAsync();
        });
        barber.Name = $"Barbero{suffix} Prueba";
        branch.Name = $"Sucursal {suffix}";
        cut.Name = $"Servicio{suffix}";
        return (branch, barber, cut);
    }

    private async Task<(HttpClient Client, User User)> VerifiedClientAsync()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);
        return (factory.ClientWithToken(factory.TokenFor(user)), user);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, string? conversationId, string? choice = null, string? message = null)
    {
        var response = await client.PostAsJsonAsync("/assistant/message", new { conversationId, choice, message });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ConversationAsync(HttpClient client)
    {
        var start = await SendAsync(client, null);
        var id = start.GetProperty("conversationId").GetString()!;
        await SendAsync(client, id, choice: "start");
        return id;
    }

    private static async Task<JsonElement> ChooseUntilTimeAsync(HttpClient client, string id, Branch branch, User barber, Cut cut)
    {
        await SendAsync(client, id, choice: $"branch:{branch.Id}");
        await SendAsync(client, id, choice: $"barber:{barber.Id}");
        await SendAsync(client, id, choice: $"cut:{cut.Id}");
        var times = await SendAsync(client, id, choice: $"day:{Tuesday:yyyy-MM-dd}");
        Assert.Equal("time", Step(times));
        return times;
    }

    private Task<int> CountAppointmentsAsync(int clientId) =>
        factory.WithDbAsync(db => db.Appointments.CountAsync(a => a.ClientId == clientId && a.CreatedByRole == UserRole.Client));

    private static string Step(JsonElement json) => json.GetProperty("step").GetString()!;

    private static List<string> Values(JsonElement json) =>
        json.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()!).ToList();

    private static string TimeChoice(DateTimeOffset start) => "time:" + start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}

public sealed class DayPartsTests
{
    [Theory]
    [InlineData(9, 0, DayPart.Morning)]
    [InlineData(13, 30, DayPart.Morning)]
    [InlineData(14, 0, DayPart.Afternoon)]
    [InlineData(19, 30, DayPart.Afternoon)]
    public void Morning_ends_with_the_13_30_slot(int hour, int minute, DayPart expected) =>
        Assert.Equal(expected, DayParts.Of(new TimeOnly(hour, minute)));

    [Theory]
    [InlineData("el sábado a las 13:30", DayPart.Morning)]
    [InlineData("el sábado a las 14", DayPart.Afternoon)]
    [InlineData("pasado mañana temprano", DayPart.Morning)]
    public void Exact_time_sets_the_part_of_day(string text, DayPart expected)
    {
        var catalog = new AssistantCatalog([], [], []);
        Assert.Equal(expected, TextMatcher.Interpret(text, catalog, new DateOnly(2026, 11, 23), null).Part);
    }
}
