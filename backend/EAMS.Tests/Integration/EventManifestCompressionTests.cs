using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <c>ManifestCompression</c>: the capture manifest is gzipped for a client that asks, and nothing else
/// changes. At 100,000 attendees the manifest is 19.1 MB of JSON and 4.4 MB gzipped (2026-11-24).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventManifestCompressionTests : IntegrationTest
{
    public EventManifestCompressionTests(SqlServerFixture sql) : base(sql) { }

    private const string Gzip = "gzip";

    private async Task<(Guid EventId, string ApiKey)> ArrangeAsync()
    {
        Guid schoolId, eventId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var student = TestData.NewStudent(school.Id);
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, "0012503303"));
            var ev = TestData.NewEvent(school.Id, EventStatus.Open);
            db.Events.Add(ev);
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });
            await db.SaveChangesAsync();
            schoolId = school.Id;
            eventId = ev.Id;
        }

        return (eventId, await IssueDeviceKeyAsync(schoolId));
    }

    private static HttpRequestMessage Get(string path, bool acceptGzip)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptGzip) request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(Gzip));
        return request;
    }

    [Fact]
    public async Task A_client_that_accepts_gzip_gets_a_gzipped_manifest_with_the_same_content_and_etag()
    {
        var (eventId, apiKey) = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);
        var route = $"/api/v1/events/{eventId}/manifest";

        using var identity = await client.SendAsync(Get(route, acceptGzip: false));
        using var gzipped = await client.SendAsync(Get(route, acceptGzip: true));

        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.Equal(HttpStatusCode.OK, gzipped.StatusCode);
        Assert.Empty(identity.Content.Headers.ContentEncoding);
        Assert.Equal([Gzip], gzipped.Content.Headers.ContentEncoding);

        // The validator a device sends back must not depend on how the body travelled.
        Assert.Equal(identity.Headers.ETag, gzipped.Headers.ETag);

        await using var compressed = await gzipped.Content.ReadAsStreamAsync();
        await using var inflated = new GZipStream(compressed, CompressionMode.Decompress);
        using var unzipped = await JsonDocument.ParseAsync(inflated);
        using var plain = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());

        Assert.Equal(
            plain.RootElement.GetProperty("attendees").GetRawText(),
            unzipped.RootElement.GetProperty("attendees").GetRawText());
    }

    /// <summary>
    /// The device's steady state: it holds a manifest, sends its ETag back, and accepts gzip. Compression
    /// must not turn the 304 into a body — an empty 304 is the whole point of the validator at 19 MB.
    /// </summary>
    [Fact]
    public async Task A_matching_if_none_match_with_gzip_accepted_is_an_empty_304()
    {
        var (eventId, apiKey) = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);
        var route = $"/api/v1/events/{eventId}/manifest";

        using var first = await client.SendAsync(Get(route, acceptGzip: true));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = Assert.IsType<EntityTagHeaderValue>(first.Headers.ETag);

        var conditional = Get(route, acceptGzip: true);
        conditional.Headers.IfNoneMatch.Add(etag);
        using var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, second.Headers.ETag);
    }

    /// <summary>
    /// Scoped to the manifest. <c>GET /events</c> is the other route a device key reaches and the
    /// nearest neighbour a loose path match would catch.
    /// </summary>
    [Fact]
    public async Task Other_routes_are_not_compressed_even_when_the_client_accepts_gzip()
    {
        var (_, apiKey) = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient().WithDeviceKey(apiKey);

        using var response = await client.SendAsync(Get("/api/v1/events", acceptGzip: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}
