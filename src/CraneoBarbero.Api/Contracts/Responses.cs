using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Contracts;

public sealed record UserResponse(
    int Id,
    string Name,
    string Email,
    string? Phone,
    string? ImageUrl,
    UserRole Role,
    int? BranchId,
    DateOnly? StartDate,
    bool EmailVerified)
{
    public static UserResponse From(User user) => new(
        user.Id, user.Name, user.Email, user.Phone, user.ImageUrl, user.Role, user.BranchId, user.StartDate, user.EmailVerified);
}

public sealed record MessageResponse(string Message);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserResponse User);

public sealed record BranchResponse(int Id, string Name, string Address, string? Phone, string? ImageUrl);

public sealed record CutResponse(int Id, string Name, decimal Price, string? Description, int DurationMinutes);

public sealed record BranchSummary(int Id, string Name);

public sealed record PublicBarberResponse(int Id, string Name, string? ImageUrl, int BranchId, BranchSummary Branch, DateOnly? StartDate);
