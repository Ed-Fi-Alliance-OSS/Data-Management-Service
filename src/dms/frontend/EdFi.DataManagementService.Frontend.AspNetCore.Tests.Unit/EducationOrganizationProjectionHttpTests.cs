// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using static EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.EducationOrganizationProjectionTestHost;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Projection responses as a client receives them: the real host, the production pipeline and the
/// frontend's own serializer, over HTTP. Only the external boundaries are replaced
/// (<see cref="EducationOrganizationProjectionTestHost"/>).
/// </summary>
internal static class EducationOrganizationProjectionHttp
{
    public const string Route = "/management/education-organizations";

    public static async Task<HttpResponseMessage> Get(
        WebApplicationFactory<Program> factory,
        string query,
        bool authenticated = true
    )
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Route}{query}");

        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "projection-token");
        }

        return await client.SendAsync(request);
    }

    /// <summary>Every Cache-Control field value on the response.</summary>
    public static string[] CacheControlValues(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? values) ? [.. values] : [];
}

/// <summary>
/// The body bound (spec §3.3) under the serializer the frontend actually uses. Every item of a full
/// default-size page is as large as the contract allows: names of the longest serialized form, ids and
/// parent ids of twenty characters (negative int64), and the longest discriminator; the data store id
/// is the largest int32 and a cursor follows the page.
/// </summary>
/// <remarks>
/// The frontend serializes with <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c>, not the default
/// encoder the bound was first derived for. The bound needs only that no UTF-16 code unit serializes to
/// more than six bytes, which holds under both: each fixture's names are checked against it, the
/// supplementary-character fixtures being the longest the two engines can store (PostgreSQL 75 code
/// points, SQL Server 75 UTF-16 code units) and the control-character fixture a shape the relaxed
/// encoder still escapes.
/// </remarks>
[TestFixture("PostgreSQL: 75 supplementary characters")]
[TestFixture("SQL Server: 37 supplementary characters and one BMP character")]
[TestFixture("75 control characters")]
public class Given_A_Full_Page_Of_The_Largest_Items_Served_Over_Http(string fixture)
{
    private const int ItemBound = 2048;

    /// <summary>
    /// The envelope measured here is 270 bytes: 108 fixed bytes and a 162-character cursor carrying a
    /// ten-digit data store id, a twenty-character position and a ten-digit walk timestamp. That is a
    /// measurement, not a maximum: the timestamp's decimal width grows. The bound was first estimated at
    /// 256 bytes.
    /// </summary>
    private const int EnvelopeBound = 512;
    private const long DataStoreId = int.MaxValue;
    private const int PageSize = EducationOrganizationProjectionSettings.MaximumPageSizeDefault;

    private WebApplicationFactory<Program> _factory = null!;
    private HttpResponseMessage _response = null!;
    private byte[] _body = [];
    private JsonDocument _document = null!;
    private string _name = "";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _name = fixture switch
        {
            "PostgreSQL: 75 supplementary characters" => string.Concat(Enumerable.Repeat("\U0001F600", 75)),
            "SQL Server: 37 supplementary characters and one BMP character" => string.Concat(
                Enumerable.Repeat("\U0001F600", 37)
            ) + "é",
            "75 control characters" => new string('\u0001', 75),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, null),
        };

        _factory = Create(PostgresqlStore(DataStoreId), _ => new FixedSetReader(LargestRows(_name)));
        _response = await EducationOrganizationProjectionHttp.Get(_factory, $"?dataStoreId={DataStoreId}");
        _body = await _response.Content.ReadAsByteArrayAsync();
        _document = JsonDocument.Parse(_body);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _document.Dispose();
        _response.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// A State Education Agency at the smallest id, then one more Education Service Center than a page
    /// holds, each referencing it, so a cursor follows the first page.
    /// </summary>
    private static List<EducationOrganizationProjectionRow> LargestRows(string name)
    {
        List<EducationOrganizationProjectionRow> rows =
        [
            new(long.MinValue, "Ed-Fi:StateEducationAgency", name, name, null, null, null, null),
        ];

        for (int index = 1; index <= PageSize; index++)
        {
            rows.Add(
                new(
                    long.MinValue + index,
                    "Ed-Fi:EducationServiceCenter",
                    name,
                    name,
                    null,
                    null,
                    null,
                    long.MinValue
                )
            );
        }

        return rows;
    }

    private JsonElement[] Items => [.. _document.RootElement.GetProperty("items").EnumerateArray()];

    private static int Utf8Length(JsonElement element) => Encoding.UTF8.GetByteCount(element.GetRawText());

    [Test]
    public void It_answers_a_full_page_followed_by_a_cursor()
    {
        _response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(_body));
        Items.Should().HaveCount(PageSize);
        _document.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Test]
    public void It_serializes_the_names_unchanged()
    {
        Items.Select(item => item.GetProperty("nameOfInstitution").GetString()).Should().AllBe(_name);
    }

    [Test]
    public void It_spends_at_most_six_bytes_per_utf16_code_unit_of_a_name()
    {
        int largest = Items.Max(item => Utf8Length(item.GetProperty("nameOfInstitution")));

        TestContext.Out.WriteLine($"Serialized name: {largest} bytes for {_name.Length} UTF-16 code units");
        largest.Should().BeLessThanOrEqualTo((6 * _name.Length) + 2);
    }

    [Test]
    public void It_keeps_every_item_within_the_item_bound()
    {
        int largest = Items.Max(Utf8Length);

        TestContext.Out.WriteLine($"Largest item: {largest} bytes");
        largest.Should().BeLessThanOrEqualTo(ItemBound);
    }

    [Test]
    public void It_keeps_the_envelope_within_the_envelope_bound()
    {
        JsonElement[] items = Items;
        int envelope = _body.Length - items.Sum(Utf8Length) - (items.Length - 1);

        TestContext.Out.WriteLine($"Envelope: {envelope} bytes; body: {_body.Length} bytes");
        envelope.Should().BeLessThanOrEqualTo(EnvelopeBound);
    }

    [Test]
    public void It_keeps_the_page_within_the_page_bound() =>
        _body.Length.Should().BeLessThanOrEqualTo((PageSize * ItemBound) + EnvelopeBound);

    [Test]
    public void It_serves_utf8_json()
    {
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        _response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
    }

    [Test]
    public void It_marks_the_page_no_store() =>
        EducationOrganizationProjectionHttp.CacheControlValues(_response).Should().Equal("no-store");
}

/// <summary>
/// The frontend serializer omits null members of serialized objects
/// (<c>JsonIgnoreCondition.WhenWritingNull</c>), while the contract requires <c>nextCursor</c>,
/// <c>shortNameOfInstitution</c> and <c>parentId</c> to be present when null. The pages must arrive
/// exactly as the contract examples show them.
/// </summary>
[TestFixture]
public class Given_The_Contract_Example_Pages_Served_Over_Http
{
    private WebApplicationFactory<Program> _emptyStore = null!;
    private WebApplicationFactory<Program> _oneSchool = null!;
    private JsonNode _emptyBody = null!;
    private string _oneSchoolText = "";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _emptyStore = Create(PostgresqlStore(3789), _ => new FixedSetReader([]));
        using HttpResponseMessage empty = await EducationOrganizationProjectionHttp.Get(
            _emptyStore,
            "?dataStoreId=3789"
        );
        _emptyBody = JsonNode.Parse(await empty.Content.ReadAsStringAsync())!;

        _oneSchool = Create(
            PostgresqlStore(3788),
            _ => new FixedSetReader([
                new(900001, "Ed-Fi:School", "Independent Academy", null, null, null, null, null),
            ])
        );
        using HttpResponseMessage oneSchool = await EducationOrganizationProjectionHttp.Get(
            _oneSchool,
            "?dataStoreId=3788&limit=2"
        );
        _oneSchoolText = await oneSchool.Content.ReadAsStringAsync();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _emptyStore.DisposeAsync();
        await _oneSchool.DisposeAsync();
    }

    [Test]
    public void It_serves_the_empty_store_example_exactly() =>
        JsonNode
            .DeepEquals(_emptyBody, ContractExample("empty.json"))
            .Should()
            .BeTrue(_emptyBody.ToJsonString());

    [Test]
    public void It_serves_the_last_page_example_exactly() =>
        JsonNode
            .DeepEquals(JsonNode.Parse(_oneSchoolText), ContractExample("success-last-page.json"))
            .Should()
            .BeTrue(_oneSchoolText);

    [Test]
    public void It_writes_the_null_members_rather_than_omitting_them()
    {
        using JsonDocument document = JsonDocument.Parse(_oneSchoolText);
        JsonElement root = document.RootElement;
        JsonElement item = root.GetProperty("items")[0];

        root.EnumerateObject()
            .Select(member => member.Name)
            .Should()
            .Equal("contractVersion", "dataStoreId", "nextCursor", "items");
        root.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        item.EnumerateObject()
            .Select(member => member.Name)
            .Should()
            .Equal(
                "educationOrganizationId",
                "nameOfInstitution",
                "shortNameOfInstitution",
                "discriminator",
                "parentId"
            );
        item.GetProperty("shortNameOfInstitution").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

/// <summary>
/// Repeated parameters reach Core from the HTTP query string, whatever the letter case of each copy,
/// and are refused before any value is judged; failures arrive as uncacheable problem documents.
/// </summary>
[TestFixture]
public class Given_Projection_Requests_That_Fail_Before_Reading_Served_Over_Http
{
    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void Setup() =>
        _factory = Create(
            PostgresqlStore(3788),
            _ => new FixedSetReader([
                new(900001, "Ed-Fi:School", "Independent Academy", null, null, null, null, null),
            ])
        );

    [OneTimeTearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    private static IEnumerable<TestCaseData> RepeatedParameterCases()
    {
        yield return new TestCaseData(
            "?dataStoreId=3788&DATASTOREID=3788",
            new[] { "DataStoreId must not be supplied more than once." }
        ).SetName("dataStoreId repeated in another letter case");
        yield return new TestCaseData(
            "?dataStoreId=3788&cursor=a&Cursor=b",
            new[] { "Cursor must not be supplied more than once." }
        ).SetName("cursor repeated in another letter case, refused before the cursor is decoded");
        yield return new TestCaseData(
            "?dataStoreId=3788&limit=1&limit=1&contractVersion=educationOrganizationProjection.v1&CONTRACTVERSION=educationOrganizationProjection.v1",
            new[]
            {
                "Limit must not be supplied more than once.",
                "ContractVersion must not be supplied more than once.",
            }
        ).SetName("limit repeated exactly and contractVersion in another letter case");
    }

    [TestCaseSource(nameof(RepeatedParameterCases))]
    public async Task It_refuses_a_repeated_parameter(string query, string[] expectedErrors)
    {
        using HttpResponseMessage response = await EducationOrganizationProjectionHttp.Get(_factory, query);
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedErrors);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        EducationOrganizationProjectionHttp.CacheControlValues(response).Should().Equal("no-store");
    }

    [Test]
    public async Task It_reads_a_single_parameter_in_any_letter_case()
    {
        using HttpResponseMessage response = await EducationOrganizationProjectionHttp.Get(
            _factory,
            "?DATASTOREID=3788&Limit=1"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task It_answers_an_unauthenticated_request_with_an_uncacheable_problem()
    {
        using HttpResponseMessage response = await EducationOrganizationProjectionHttp.Get(
            _factory,
            "?dataStoreId=3788",
            authenticated: false
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        EducationOrganizationProjectionHttp.CacheControlValues(response).Should().Equal("no-store");
    }
}
