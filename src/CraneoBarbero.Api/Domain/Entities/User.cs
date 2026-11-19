using System.Text.Json.Serialization;
using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Domain.Entities;

public class User : IHasTimestamps
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    [JsonIgnore]
    public string PasswordHash { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? ImageUrl { get; set; }
    public int? BranchId { get; set; }
    public DateOnly? StartDate { get; set; }
    public UserRole Role { get; set; } = UserRole.Client;
    public bool IsActive { get; set; } = true;
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public Branch? Branch { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
