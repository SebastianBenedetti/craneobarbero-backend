using CraneoBarbero.Api.Auth;

namespace CraneoBarbero.Api.Tests.Auth;

public sealed class PasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_is_bcrypt_and_verifies()
    {
        var hash = _hasher.Hash("Clave-de-prueba-1");

        Assert.StartsWith("$2", hash);
        Assert.DoesNotContain("Clave-de-prueba-1", hash);
        Assert.True(_hasher.Verify("Clave-de-prueba-1", hash));
        Assert.False(_hasher.Verify("otra-clave", hash));
    }

    [Theory]
    [InlineData('a', "$2a$")]
    [InlineData('b', "$2b$")]
    public void Existing_hash_formats_still_verify(char revision, string prefix)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("Clave-importada-1", BCrypt.Net.BCrypt.GenerateSalt(10, revision));

        Assert.StartsWith(prefix, hash);
        Assert.True(_hasher.Verify("Clave-importada-1", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto-plano")]
    public void Invalid_hash_never_verifies(string hash)
    {
        Assert.False(_hasher.Verify("texto-plano", hash));
    }
}
