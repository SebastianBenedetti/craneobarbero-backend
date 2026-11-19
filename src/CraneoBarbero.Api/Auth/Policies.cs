using Microsoft.AspNetCore.Authorization;

namespace CraneoBarbero.Api.Auth;

public static class Policies
{
    public const string Client = "client";
    public const string Barber = "barber";
    public const string Admin = "admin";
    public const string BarberOrAdmin = "barber_or_admin";
    public const string OwnerOrAdmin = "owner_or_admin";
    public const string OwnerOnly = "owner_only";

    public static void AddApplicationPolicies(this AuthorizationOptions options)
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        options.AddPolicy(Client, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Client));
        options.AddPolicy(Barber, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Barber));
        options.AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
        options.AddPolicy(BarberOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Barber, Roles.Admin));
        options.AddPolicy(OwnerOrAdmin, policy => policy.RequireAuthenticatedUser()
            .AddRequirements(new ResourceOwnerRequirement(allowAdmin: true)));
        options.AddPolicy(OwnerOnly, policy => policy.RequireAuthenticatedUser()
            .AddRequirements(new ResourceOwnerRequirement(allowAdmin: false)));
    }
}
