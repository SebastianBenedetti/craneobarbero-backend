using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Auth;

public sealed record UserAccess(int Id, UserRole Role, bool IsActive);

public interface IUserAccessStore
{
    Task<UserAccess?> FindAsync(int userId, CancellationToken cancellationToken);
}

public sealed class EfUserAccessStore(AppDbContext db) : IUserAccessStore
{
    public Task<UserAccess?> FindAsync(int userId, CancellationToken cancellationToken) =>
        db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new UserAccess(user.Id, user.Role, user.IsActive))
            .FirstOrDefaultAsync(cancellationToken);
}
