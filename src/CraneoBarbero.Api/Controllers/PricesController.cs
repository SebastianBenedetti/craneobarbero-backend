using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("prices")]
[Authorize(Policy = Policies.Admin)]
public sealed class PricesController(InflationService inflation, PriceAdjustments adjustments) : ControllerBase
{
    [HttpGet("inflation-suggestion")]
    public Task<InflationSuggestionResponse> InflationSuggestion([FromQuery] string? from, [FromQuery] string? to, CancellationToken cancellationToken) =>
        inflation.SuggestAsync(InflationService.ParseMonth(from, "inicial"), InflationService.ParseMonth(to, "final"), cancellationToken);

    [HttpPost("adjustments/preview")]
    public Task<PriceAdjustmentPreview> Preview(PriceAdjustmentRequest request, CancellationToken cancellationToken) =>
        adjustments.PreviewAsync(request, cancellationToken);

    [HttpPost("adjustments")]
    public async Task<ActionResult<PriceAdjustmentResponse>> Apply(PriceAdjustmentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await adjustments.ApplyAsync(request, User.GetRequiredUserId(), cancellationToken));

    [HttpGet("adjustments")]
    public Task<IReadOnlyList<PriceAdjustmentResponse>> List(CancellationToken cancellationToken) =>
        adjustments.ListAsync(cancellationToken);

    [HttpPost("adjustments/{id}/revert")]
    public Task<PriceAdjustmentRevertResponse> Revert(int id, CancellationToken cancellationToken) =>
        adjustments.RevertAsync(id, User.GetRequiredUserId(), cancellationToken);

    [HttpGet("history")]
    public Task<PagedResponse<PriceHistoryResponse>> History([FromQuery] PriceHistoryQuery query, CancellationToken cancellationToken) =>
        adjustments.HistoryAsync(query, cancellationToken);
}
