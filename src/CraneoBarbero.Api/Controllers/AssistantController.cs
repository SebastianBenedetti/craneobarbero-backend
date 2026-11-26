using CraneoBarbero.Api.Assistant;
using CraneoBarbero.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("assistant")]
[Authorize(Policy = Policies.Client)]
public sealed class AssistantController(BookingAssistant assistant) : ControllerBase
{
    [HttpPost("message")]
    public Task<AssistantMessageResponse> Message(AssistantMessageRequest request, CancellationToken cancellationToken) =>
        assistant.HandleAsync(User.GetRequiredUserId(), request, cancellationToken);
}
