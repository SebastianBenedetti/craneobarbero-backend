using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace CraneoBarbero.Api.Domain.Enums;

public enum PaymentMethod
{
    Cash,
    Card,
    Transfer,
    Wallet,
    [JsonStringEnumMemberName("mercadopago")]
    [PgName("mercadopago")]
    MercadoPago,
}
