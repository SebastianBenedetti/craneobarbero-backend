using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
public sealed class ReviewsController(AppDbContext db) : ControllerBase
{
    [Authorize(Policy = Policies.Client)]
    [HttpPost("/appointments/{id}/review")]
    public async Task<ActionResult<ReviewResponse>> Create(int id, CreateReviewRequest request, CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var appointment = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                          ?? throw new NotFoundException("No se encontró el turno.");
        if (appointment.ClientId != clientId)
        {
            throw new ForbiddenException();
        }

        if (appointment.Status != AppointmentStatus.Completed)
        {
            throw new ConflictException("Solo podés calificar un turno que ya se realizó.", ErrorCodes.NotReviewable);
        }

        if (await db.Reviews.AnyAsync(r => r.AppointmentId == id, cancellationToken))
        {
            throw new ConflictException("Ya calificaste este turno.", ErrorCodes.AlreadyReviewed);
        }

        var review = new Review
        {
            AppointmentId = id,
            RatingBarber = (short)request.RatingBarber!.Value,
            RatingCut = (short)request.RatingCut!.Value,
            Comment = AccountRules.TrimToNull(request.Comment),
        };
        db.Reviews.Add(review);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("Ya calificaste este turno.", ErrorCodes.AlreadyReviewed);
        }

        var saved = await WithDetails().SingleAsync(r => r.Id == review.Id, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ReviewResponse.From(saved));
    }

    [Authorize(Policy = Policies.Client)]
    [HttpGet("/reviews/mine")]
    public async Task<IReadOnlyList<MyReviewResponse>> Mine(CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        return await db.Reviews.AsNoTracking()
            .Where(r => r.Appointment!.ClientId == clientId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new MyReviewResponse(r.Id, r.AppointmentId, r.RatingBarber, r.RatingCut, r.Comment, r.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    [Authorize(Policy = Policies.BarberOrAdmin)]
    [HttpGet("/reviews")]
    public async Task<ReviewListResponse> List([FromQuery] ReviewListQuery query, CancellationToken cancellationToken)
    {
        var isAdmin = User.IsInRole(Roles.Admin);
        var reviews = db.Reviews.AsNoTracking();
        if (isAdmin)
        {
            if (query.BarberId is { } barberId) reviews = reviews.Where(r => r.Appointment!.BarberId == barberId);
            if (query.BranchId is { } branchId) reviews = reviews.Where(r => r.Appointment!.BranchId == branchId);
        }
        else
        {
            var barberId = User.GetRequiredUserId();
            reviews = reviews.Where(r => r.Appointment!.BarberId == barberId);
        }

        var page = query.Page ?? 1;
        var size = query.PageSize ?? AppointmentLimits.DefaultPageSize;
        var total = await reviews.CountAsync(cancellationToken);
        var ratings = await reviews
            .Select(r => new { r.Appointment!.BarberId, BarberName = r.Appointment.Barber!.Name, r.RatingBarber, r.RatingCut })
            .ToListAsync(cancellationToken);
        var items = await WithDetails(reviews)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(cancellationToken);

        decimal? Average(IEnumerable<int> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? null : Math.Round((decimal)list.Average(), 2);
        }

        var byBarber = isAdmin
            ? ratings.GroupBy(r => new { r.BarberId, r.BarberName })
                .Select(g => new BarberRating(
                    g.Key.BarberId,
                    g.Key.BarberName,
                    g.Count(),
                    Average(g.Select(r => (int)r.RatingBarber))!.Value,
                    Average(g.Select(r => (int)r.RatingCut))!.Value))
                .OrderByDescending(r => r.AverageBarber).ThenByDescending(r => r.Count).ThenBy(r => r.Name)
                .ToList()
            : null;

        return new ReviewListResponse(
            items.Select(ReviewResponse.From).ToList(),
            page,
            size,
            total,
            Average(ratings.Select(r => (int)r.RatingBarber)),
            Average(ratings.Select(r => (int)r.RatingCut)),
            byBarber);
    }

    private IQueryable<Review> WithDetails(IQueryable<Review>? source = null) =>
        (source ?? db.Reviews.AsNoTracking())
        .Include(r => r.Appointment!).ThenInclude(a => a.Client)
        .Include(r => r.Appointment!).ThenInclude(a => a.Barber)
        .Include(r => r.Appointment!).ThenInclude(a => a.Cut)
        .Include(r => r.Appointment!).ThenInclude(a => a.Branch);
}
