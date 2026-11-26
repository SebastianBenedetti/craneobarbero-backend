using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Images;

public sealed class ImagesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private static MultipartFormDataContent Form(byte[] data, string name = "foto.jpg", string type = "image/jpeg")
    {
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    [Fact]
    public async Task A_signed_in_user_uploads_an_image_and_anyone_can_read_it()
    {
        var client = await factory.ClientForAsync(UserRole.Client);
        var response = await client.PostAsync("/images", Form(Jpeg));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadBody>();
        Assert.NotNull(body);
        Assert.EndsWith($"/images/{body.Id}", body.Url);
        Assert.StartsWith("http", body.Url);

        var read = await factory.CreateClient().GetAsync($"/images/{body.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/jpeg", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await read.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", read.Headers.CacheControl?.ToString());
        Assert.Equal("cross-origin", read.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("*", read.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task The_type_is_taken_from_the_content_not_from_the_name()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var response = await admin.PostAsync("/images", Form(Png, "foto.jpg", "image/jpeg"));
        var body = await response.Content.ReadFromJsonAsync<UploadBody>();
        var read = await factory.CreateClient().GetAsync($"/images/{body!.Id}");
        Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Rejects_files_that_are_not_images_or_are_too_big()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        await HttpAssertions.AssertErrorAsync(await admin.PostAsync("/images", Form("hola"u8.ToArray(), "foto.jpg")), HttpStatusCode.BadRequest, "invalid_image");

        var big = new byte[950_000];
        Jpeg.CopyTo(big, 0);
        await HttpAssertions.AssertErrorAsync(await admin.PostAsync("/images", Form(big)), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/images", new MultipartFormDataContent())).StatusCode);
    }

    [Fact]
    public async Task Uploading_requires_a_session_and_unknown_images_are_not_found()
    {
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsync("/images", Form(Jpeg)), HttpStatusCode.Unauthorized, "unauthorized");
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync("/images/999999"), HttpStatusCode.NotFound, "not_found");
    }

    private sealed record UploadBody(int Id, string Url);
}
