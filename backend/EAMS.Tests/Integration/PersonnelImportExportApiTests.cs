using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The Personnel tab's bulk import and export over real HTTP — the CSV round-trips through import, the
/// upsert keys on personnel number, and a bad row is tallied rather than failing the whole batch.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelImportExportApiTests : IntegrationTest
{
    public PersonnelImportExportApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/personnel";

    private async Task ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        db.Schools.Add(TestData.NewSchool());
        await db.SaveChangesAsync();
    }

    private static object Row(
        string number, string first = "Jose", string last = "Rizal",
        string? rfidUid = null, string? department = "CICT", string? status = "Active") => new
    {
        personnelNumber = number,
        firstName = first,
        middleName = (string?)null,
        lastName = last,
        email = $"{number}@usa.edu.ph",
        classification = "ACAD",
        department,
        organization = (string?)null,
        position = "Instructor",
        rfidUid,
        status,
    };

    [Fact]
    public async Task An_import_creates_new_records_and_updates_by_number()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var create = await client.PostAsJsonAsync($"{Route}/import", new
        {
            rows = new[] { Row("EMP-0001", last: "Alpha"), Row("EMP-0002", last: "Bravo") },
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using (var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            Assert.Equal(2, body.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("updated").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("failed").GetInt32());
        }

        // Re-import EMP-0001 with a changed last name → updated, not a second row.
        var update = await client.PostAsJsonAsync($"{Route}/import", new
        {
            rows = new[] { Row("EMP-0001", last: "Alphonse") },
        });
        using (var body = JsonDocument.Parse(await update.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, body.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("updated").GetInt32());
        }

        await using var read = NewDbContext();
        Assert.Equal(2, await read.Personnel.CountAsync(p => !p.IsDeleted));
        Assert.Equal("Alphonse",
            (await read.Personnel.AsNoTracking().SingleAsync(p => p.PersonnelNumber == "EMP-0001")).LastName);
    }

    [Fact]
    public async Task A_bad_row_is_tallied_and_does_not_stop_the_others()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync($"{Route}/import", new
        {
            rows = new[]
            {
                Row("EMP-0001", rfidUid: "04A1B2C3"),          // ok
                Row("EMP-0002", rfidUid: "04A1B2C3"),          // duplicate card → fails
                Row("", last: "Nameless"),                      // blank id → fails
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("failed").GetInt32());

        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal(2, errors.GetArrayLength());
        // Errors carry the 1-based row number so the operator can find them.
        Assert.Contains(errors.EnumerateArray(), e => e.GetProperty("row").GetInt32() == 2);
        Assert.Contains(errors.EnumerateArray(), e => e.GetProperty("row").GetInt32() == 3);

        await using var read = NewDbContext();
        Assert.Equal(1, await read.Personnel.CountAsync(p => !p.IsDeleted));
    }

    [Fact]
    public async Task An_empty_import_is_a_400()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync($"{Route}/import", new { rows = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(PersonnelWriteOutcome.ValidationFailed),
            body.RootElement.GetProperty(PersonnelController.ErrorCodeProperty).GetString());
    }

    [Fact]
    public async Task An_oversized_import_is_a_400()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var rows = Enumerable.Range(1, PersonnelController.MaxImportRows + 1)
            .Select(i => Row($"EMP-{i:00000}"))
            .ToArray();

        var response = await client.PostAsJsonAsync($"{Route}/import", new { rows });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_export_is_a_csv_that_round_trips_through_import()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsJsonAsync($"{Route}/import", new
        {
            rows = new[] { Row("EMP-0001", last: "Alpha"), Row("EMP-0002", last: "Bravo") },
        });

        var export = await client.GetAsync($"{Route}/export.csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);

        var text = await export.Content.ReadAsStringAsync();
        Assert.Contains("Personnel ID", text);
        Assert.Contains("EMP-0001", text);
        Assert.Contains("EMP-0002", text);

        // Re-importing the same people updates rather than duplicates.
        var reimport = await client.PostAsJsonAsync($"{Route}/import", new
        {
            rows = new[] { Row("EMP-0001", last: "Alpha"), Row("EMP-0002", last: "Bravo") },
        });
        using var body = JsonDocument.Parse(await reimport.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task The_export_header_is_the_spec_columns_in_spec_order()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var export = await client.GetAsync($"{Route}/export.csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        var text = await export.Content.ReadAsStringAsync();
        // Drop a possible leading UTF-8 BOM, then take the header line (cells here need no CSV quoting).
        var firstLine = text.Split("\r\n")[0].TrimStart('﻿');
        Assert.Equal(
            "Personnel ID,RFID UID,Last Name,First Name,Middle Name,Email,Classification,Department," +
            "Organization,Status,Position",
            firstLine);
    }
}
