using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Domain.Entities;
using FluentValidation;

namespace CraneoBarbero.Api.Reviews;

public sealed record CreateReviewRequest(int? RatingBarber, int? RatingCut, string? Comment);

public sealed record ReviewListQuery(int? BarberId, int? BranchId, int? Page, int? PageSize);

public sealed record ReviewRef(int Id, string Name);

public sealed record ReviewResponse(
    int Id,
    int AppointmentId,
    DateTimeOffset AppointmentDate,
    int RatingBarber,
    int RatingCut,
    string? Comment,
    DateTimeOffset CreatedAt,
    string ClientName,
    ReviewRef Barber,
    ReviewRef Cut,
    ReviewRef Branch)
{
    public static ReviewResponse From(Review review)
    {
        var appointment = review.Appointment!;
        return new ReviewResponse(
            review.Id,
            appointment.Id,
            appointment.AppointmentDate,
            review.RatingBarber,
            review.RatingCut,
            review.Comment,
            review.CreatedAt,
            appointment.Client?.Name ?? appointment.ClientName ?? "Cliente",
            new ReviewRef(appointment.BarberId, appointment.Barber?.Name ?? string.Empty),
            new ReviewRef(appointment.CutId, appointment.Cut?.Name ?? string.Empty),
            new ReviewRef(appointment.BranchId, appointment.Branch?.Name ?? string.Empty));
    }
}

public sealed record BarberRating(int BarberId, string Name, int Count, decimal AverageBarber, decimal AverageCut);

public sealed record ReviewListResponse(
    IReadOnlyList<ReviewResponse> Items,
    int Page,
    int PageSize,
    int Total,
    decimal? AverageBarber,
    decimal? AverageCut,
    IReadOnlyList<BarberRating>? ByBarber);

public sealed record MyReviewResponse(int Id, int AppointmentId, int RatingBarber, int RatingCut, string? Comment, DateTimeOffset CreatedAt);

public static class ReviewLimits
{
    public const int MaxCommentLength = 500;
}

public sealed class CreateReviewRequestValidator : AbstractValidator<CreateReviewRequest>
{
    public CreateReviewRequestValidator()
    {
        RuleFor(r => r.RatingBarber).NotNull().WithMessage("Calificá al barbero.")
            .InclusiveBetween(1, 5).WithMessage("La calificación del barbero va de 1 a 5.");
        RuleFor(r => r.RatingCut).NotNull().WithMessage("Calificá el servicio.")
            .InclusiveBetween(1, 5).WithMessage("La calificación del servicio va de 1 a 5.");
        RuleFor(r => r.Comment).MaximumLength(ReviewLimits.MaxCommentLength)
            .WithMessage($"El comentario puede tener hasta {ReviewLimits.MaxCommentLength} caracteres.");
    }
}

public sealed class ReviewListQueryValidator : AbstractValidator<ReviewListQuery>
{
    public ReviewListQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"La cantidad por página va de 1 a {AppointmentLimits.MaxPageSize}.");
        RuleFor(q => q.BarberId).GreaterThan(0).When(q => q.BarberId is not null).WithMessage("El barbero no es válido.");
        RuleFor(q => q.BranchId).GreaterThan(0).When(q => q.BranchId is not null).WithMessage("La sucursal no es válida.");
    }
}
