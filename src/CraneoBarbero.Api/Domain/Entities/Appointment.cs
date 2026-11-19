using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Domain.Entities;

public class Appointment : IHasTimestamps
{
    public int Id { get; set; }
    public DateTimeOffset AppointmentDate { get; set; }
    public DateTimeOffset EndDate { get; set; }
    public AppointmentStatus Status { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int BarberId { get; set; }
    public int CutId { get; set; }
    public int BranchId { get; set; }
    public decimal Price { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;
    public PaymentMethod? PaymentMethod { get; set; }
    public DateTimeOffset? PaymentExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public UserRole CreatedByRole { get; set; }
    public int? CreatedById { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public int? CancelledById { get; set; }
    public UserRole? CancelledByRole { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User? Client { get; set; }
    public User? Barber { get; set; }
    public Cut? Cut { get; set; }
    public Branch? Branch { get; set; }
}

public class Payment : IHasTimestamps
{
    public int Id { get; set; }
    public int? OrderId { get; set; }
    public int? AppointmentId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.MercadoPago;
    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;
    public string? ExternalId { get; set; }
    public string? PreferenceId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Order? Order { get; set; }
    public Appointment? Appointment { get; set; }
}
