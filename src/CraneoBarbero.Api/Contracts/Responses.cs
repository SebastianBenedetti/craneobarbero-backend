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

public sealed record BranchResponse(int Id, string Name, string Address, string? Phone, string? ImageUrl, bool IsActive)
{
    public static BranchResponse From(Branch branch) =>
        new(branch.Id, branch.Name, branch.Address, branch.Phone, branch.ImageUrl, branch.IsActive);
}

public sealed record CutResponse(int Id, string Name, decimal Price, string? Description, int DurationMinutes, bool IsActive)
{
    public static CutResponse From(Cut cut) =>
        new(cut.Id, cut.Name, cut.Price, cut.Description, cut.DurationMinutes, cut.IsActive);
}

public sealed record BranchSummary(int Id, string Name);

public sealed record PublicBarberResponse(int Id, string Name, string? ImageUrl, int BranchId, BranchSummary Branch, DateOnly? StartDate);

public sealed record AdminBarberResponse(
    int Id,
    string Name,
    string Email,
    string? ImageUrl,
    int BranchId,
    BranchSummary Branch,
    DateOnly? StartDate,
    bool IsActive,
    bool BranchIsActive);
