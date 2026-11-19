using System.Text.Json.Serialization;

namespace CraneoBarbero.Api.Domain.Enums;

public enum PaymentMethod
{
    Cash,
    Card,
    Transfer,
    Wallet,
    [JsonStringEnumMemberName("mercadopago")]
    MercadoPago,
}
