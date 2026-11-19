namespace CraneoBarbero.Api.Domain.Entities;

public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

public interface IHasTimestamps : IHasCreatedAt
{
    DateTimeOffset UpdatedAt { get; set; }
}
