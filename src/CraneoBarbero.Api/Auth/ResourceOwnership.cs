using CraneoBarbero.Api.Common.Errors;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace CraneoBarbero.Api.Auth;

public interface IOwnedResource
{
    IEnumerable<int> OwnerIds { get; }
}

public sealed record OwnedResource(params int?[] Owners) : IOwnedResource
{
    public IEnumerable<int> OwnerIds => Owners.Where(id => id.HasValue).Select(id => id!.Value);
}

public sealed class ResourceOwnerRequirement(bool allowAdmin) : IAuthorizationRequirement
{
    public bool AllowAdmin { get; } = allowAdmin;
}

public sealed class ResourceOwnerHandler : AuthorizationHandler<ResourceOwnerRequirement, IOwnedResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOwnerRequirement requirement,
        IOwnedResource resource)
    {
        if (requirement.AllowAdmin && context.User.IsInRole(Roles.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var userId = context.User.GetUserId();
        if (userId is not null && resource.OwnerIds.Contains(userId.Value))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationServiceExtensions
{
    public static async Task EnsureAuthorizedAsync(
        this IAuthorizationService authorization,
        ClaimsPrincipal user,
        IOwnedResource resource,
        string policy = Policies.OwnerOrAdmin)
    {
        var result = await authorization.AuthorizeAsync(user, resource, policy);
        if (!result.Succeeded)
        {
            throw new ForbiddenException();
        }
    }
}
