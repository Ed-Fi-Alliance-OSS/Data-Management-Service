// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Profiles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

public sealed partial class DocumentCacheHostedHappyPathTests
{
    private const string SchoolProfileName = ProfileDefinitions.SchoolIncludeOnlyName;
    private const string BellScheduleProfileName = ProfileDefinitions.BellScheduleClassPeriodsIncludeOnlyName;
    private const string ClassPeriodResourcePath = "data/ed-fi/classPeriods";
    private const string BellScheduleResourcePath = "data/ed-fi/bellSchedules";
    private const string EducationOrganizationCategoryDescriptor =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School";
    private const string GradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string CacheOnlySchoolNameSentinel = "cache-only hosted school sentinel";
    private const string CacheOnlyBellScheduleClassPeriodNameSentinel =
        "cache-only hosted bell schedule sentinel";
    private const string BellScheduleName = "Cache Profile Bell Schedule";
    private const string ClassPeriodName = "Document Cache Profile Period";

    [Test]
    [Category("DocumentCacheProfileAuthorization")]
    public async Task It_applies_profiles_and_authorization_before_serving_cache_hits()
    {
        await RegisterSystemAdministratorAsync();
        int dataStoreId = await GetConfiguredDataStoreIdAsync();
        dataStoreId
            .Should()
            .Be(
                TargetDataStoreId,
                "the DocumentCache hosted E2E target configured in .env.e2e must match the provisioned CMS data store"
            );

        int schoolProfileId = await CreateOrFindProfileAsync(
            SchoolProfileName,
            ProfileDefinitions.SchoolIncludeOnlyXml
        );
        int bellScheduleProfileId = await CreateOrFindProfileAsync(
            BellScheduleProfileName,
            ProfileDefinitions.BellScheduleClassPeriodsIncludeOnlyXml
        );
        int[] profileIds = [schoolProfileId, bellScheduleProfileId];
        const long schoolId = 990131701;

        ClientCredentials setupCredentials = await CreateClientCredentialsForDataStoreAsync(dataStoreId);
        ClientCredentials authorizedCredentials = await CreateClientCredentialsForDataStoreAsync(
            dataStoreId,
            AuthorizationClaimSetNames.RelationshipsWithEdOrgsOnly,
            [schoolId],
            profileIds
        );
        ClientCredentials deniedCredentials = await CreateClientCredentialsForDataStoreAsync(
            dataStoreId,
            AuthorizationClaimSetNames.RelationshipsWithEdOrgsOnly,
            [schoolId + 1],
            profileIds
        );

        string setupToken = await GetDmsTokenAsync(setupCredentials);
        string authorizedProfileToken = await GetDmsTokenAsync(authorizedCredentials);
        string deniedProfileToken = await GetDmsTokenAsync(deniedCredentials);

        SetDmsBearerToken(setupToken);
        JsonObject activation = await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            [
                "activate-new-empty",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--confirm",
                "newEmptyActivation",
                "--json",
                "--datastore",
                CliDatastoreOptionValue(),
                "--command-timeout-seconds",
                "90",
            ]
        );
        activation["status"]!.GetValue<string>().Should().Be("completed");
        activation["classification"]!.GetValue<string>().Should().Be("succeeded");
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        await PostDescriptorAsync(EducationOrganizationCategoryDescriptor);
        await PostDescriptorAsync(GradeLevelDescriptor);
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        Guid schoolUuid = await PostProfiledSchoolAsync(authorizedProfileToken, schoolId, SchoolProfileName);
        await PostClassPeriodAsync(setupToken, schoolId);
        Guid bellScheduleUuid = await PostProfiledBellScheduleAsync(
            authorizedProfileToken,
            schoolId,
            BellScheduleProfileName
        );
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        DocumentCacheProjection projection = await ReadDocumentCacheProjectionAsync(schoolUuid);
        projection.CacheContentVersion.Should().Be(projection.DocumentContentVersion);
        projection.WorkRows.Should().Be(0);
        projection.ResourceName.Should().Be("School");
        projection.DocumentJson.Should().Contain("Cache Profile School");
        projection.DocumentJson.Should().Contain("shortNameOfInstitution");
        projection.DocumentJson.Should().Contain("educationOrganizationCategories");
        projection.DocumentJson.Should().Contain("gradeLevels");
        projection.DocumentJson.Should().NotContain(CacheOnlySchoolNameSentinel);

        await AssertProfiledSchoolGetByIdAsync(
            authorizedProfileToken,
            schoolUuid,
            schoolId,
            projection,
            expectedNameOfInstitution: "Cache Profile School"
        );
        await AssertProfiledSchoolGetManyAsync(
            authorizedProfileToken,
            schoolId,
            projection,
            expectedNameOfInstitution: "Cache Profile School"
        );
        await AssertCanonicalSchoolNameAsync(schoolUuid, expectedNameOfInstitution: "Cache Profile School");

        DocumentCacheSentinel sentinel = await ApplyDocumentCacheSentinelAsync(
            schoolUuid,
            documentJson => documentJson["nameOfInstitution"] = CacheOnlySchoolNameSentinel
        );
        try
        {
            DocumentCacheProjection sentinelProjection = await ReadDocumentCacheProjectionAsync(schoolUuid);
            await AssertCanonicalSchoolNameAsync(
                schoolUuid,
                expectedNameOfInstitution: "Cache Profile School"
            );
            await AssertProfiledSchoolGetByIdAsync(
                authorizedProfileToken,
                schoolUuid,
                schoolId,
                sentinelProjection,
                expectedNameOfInstitution: CacheOnlySchoolNameSentinel
            );
            await AssertProfiledSchoolGetManyAsync(
                authorizedProfileToken,
                schoolId,
                sentinelProjection,
                expectedNameOfInstitution: CacheOnlySchoolNameSentinel
            );
            await AssertCanonicalSchoolNameAsync(
                schoolUuid,
                expectedNameOfInstitution: "Cache Profile School"
            );
        }
        finally
        {
            await RestoreDocumentCacheSentinelAsync(sentinel);
        }

        DocumentCacheProjection bellScheduleProjection = await ReadDocumentCacheProjectionAsync(
            bellScheduleUuid
        );
        bellScheduleProjection.CacheContentVersion.Should().Be(bellScheduleProjection.DocumentContentVersion);
        bellScheduleProjection.WorkRows.Should().Be(0);
        bellScheduleProjection.ResourceName.Should().Be("BellSchedule");
        bellScheduleProjection.DocumentJson.Should().Contain(BellScheduleName);
        bellScheduleProjection.DocumentJson.Should().Contain("totalInstructionalTime");

        await AssertProfiledBellScheduleGetByIdAsync(
            authorizedProfileToken,
            bellScheduleUuid,
            schoolId,
            bellScheduleProjection,
            expectedClassPeriodName: ClassPeriodName
        );

        DocumentCacheSentinel bellScheduleSentinel = await ApplyDocumentCacheSentinelAsync(
            bellScheduleUuid,
            ApplyBellScheduleClassPeriodNameSentinel
        );
        try
        {
            DocumentCacheProjection bellScheduleSentinelProjection = await ReadDocumentCacheProjectionAsync(
                bellScheduleUuid
            );
            await AssertProfiledBellScheduleGetByIdAsync(
                authorizedProfileToken,
                bellScheduleUuid,
                schoolId,
                bellScheduleSentinelProjection,
                expectedClassPeriodName: CacheOnlyBellScheduleClassPeriodNameSentinel
            );
            await AssertProfiledBellScheduleReadDeniedAsync(
                deniedProfileToken,
                bellScheduleUuid,
                CacheOnlyBellScheduleClassPeriodNameSentinel
            );
        }
        finally
        {
            await RestoreDocumentCacheSentinelAsync(bellScheduleSentinel);
        }
    }

    private async Task<int> CreateOrFindProfileAsync(string profileName, string profileXml)
    {
        using StringContent content = JsonContent(new { name = profileName, definition = profileXml });
        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v3/profiles/",
            content
        );
        string body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Created)
        {
            Uri location = RequireLocation(response, _configurationServiceClient.BaseAddress!);
            return int.Parse(location.Segments[^1].TrimEnd('/'), CultureInfo.InvariantCulture);
        }

        if (
            response.StatusCode == HttpStatusCode.BadRequest
            && body.Contains("exists", StringComparison.OrdinalIgnoreCase)
        )
        {
            return await FindProfileIdAsync(profileName);
        }

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"CMS profile creation failed: {body}");
        throw new AssertionException($"CMS profile creation failed: {body}");
    }

    private async Task<int> FindProfileIdAsync(string profileName)
    {
        using HttpResponseMessage response = await _configurationServiceClient.GetAsync(
            "v3/profiles/?limit=1000&offset=0"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"CMS profile query failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        foreach (JsonElement profile in document.RootElement.EnumerateArray())
        {
            if (
                profile.TryGetProperty("name", out JsonElement name)
                && string.Equals(name.GetString(), profileName, StringComparison.Ordinal)
                && profile.TryGetProperty("id", out JsonElement id)
            )
            {
                return id.GetInt32();
            }
        }

        throw new AssertionException($"CMS profile '{profileName}' was not found after duplicate response.");
    }

    private async Task PostDescriptorAsync(string descriptorValue)
    {
        (string descriptorEndpoint, JsonObject descriptorBody) = DescriptorRequest(descriptorValue);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"data/ed-fi/{descriptorEndpoint}")
        {
            Content = new StringContent(descriptorBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await _dmsClient.SendAsync(request);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], $"POST descriptor failed: {body}");
    }

    private static (string Endpoint, JsonObject Body) DescriptorRequest(string descriptorValue)
    {
        string namespaceName = descriptorValue.Split('#')[0];
        string codeValue = descriptorValue.Split('#')[1];
        string descriptorTypeName = namespaceName[(namespaceName.LastIndexOf('/') + 1)..];

        return (
            $"{ToCamelCase(descriptorTypeName)}s",
            new JsonObject
            {
                ["codeValue"] = codeValue,
                ["description"] = codeValue,
                ["namespace"] = namespaceName,
                ["shortDescription"] = codeValue,
            }
        );
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int lowercaseStart = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsLower(value[index]))
            {
                lowercaseStart = index;
                break;
            }
        }

        if (lowercaseStart == 0)
        {
            lowercaseStart = 1;
        }

        if (lowercaseStart > 1)
        {
            lowercaseStart--;
        }

        return value[..lowercaseStart].ToLowerInvariant() + value[lowercaseStart..];
    }

    private async Task<Guid> PostProfiledSchoolAsync(string bearerToken, long schoolId, string profileName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SchoolResourcePath)
        {
            Content = SchoolContent(schoolId, profileName),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using HttpResponseMessage response = await _dmsClient.SendAsync(request);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST profiled school failed: {body}");

        return ExtractResourceId(response);
    }

    private async Task PostClassPeriodAsync(string bearerToken, long schoolId)
    {
        var classPeriod = new JsonObject
        {
            ["classPeriodName"] = ClassPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ClassPeriodResourcePath)
        {
            Content = new StringContent(classPeriod.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using HttpResponseMessage response = await _dmsClient.SendAsync(request);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST class period failed: {body}");
    }

    private async Task<Guid> PostProfiledBellScheduleAsync(
        string bearerToken,
        long schoolId,
        string profileName
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BellScheduleResourcePath)
        {
            Content = BellScheduleContent(schoolId, profileName),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using HttpResponseMessage response = await _dmsClient.SendAsync(request);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, $"POST profiled bell schedule failed: {body}");

        return ExtractResourceId(response);
    }

    private static StringContent SchoolContent(long schoolId, string profileName)
    {
        var school = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = "Cache Profile School",
            ["shortNameOfInstitution"] = "DCPS",
            ["webSite"] = "https://document-cache-profile.example.com",
            ["educationOrganizationCategories"] = new JsonArray
            {
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] = EducationOrganizationCategoryDescriptor,
                },
            },
            ["gradeLevels"] = new JsonArray
            {
                new JsonObject { ["gradeLevelDescriptor"] = GradeLevelDescriptor },
            },
        };

        return new StringContent(
            school.ToJsonString(),
            Encoding.UTF8,
            WritableSchoolProfileContentType(profileName)
        );
    }

    private static StringContent BellScheduleContent(long schoolId, string profileName)
    {
        var bellSchedule = new JsonObject
        {
            ["bellScheduleName"] = BellScheduleName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["totalInstructionalTime"] = 325,
            ["classPeriods"] = new JsonArray
            {
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = ClassPeriodName,
                        ["schoolId"] = schoolId,
                    },
                },
            },
            ["dates"] = new JsonArray(),
            ["gradeLevels"] = new JsonArray(),
        };

        return new StringContent(
            bellSchedule.ToJsonString(),
            Encoding.UTF8,
            WritableBellScheduleProfileContentType(profileName)
        );
    }

    private async Task AssertProfiledSchoolGetByIdAsync(
        string bearerToken,
        Guid schoolUuid,
        long schoolId,
        DocumentCacheProjection projection,
        string expectedNameOfInstitution
    )
    {
        using HttpResponseMessage response = await SendProfiledSchoolGetAsync(
            bearerToken,
            SchoolPath(schoolUuid),
            SchoolProfileName
        );
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET profiled school by id failed: {body}");
        AssertProfiledSchoolContentType(response);

        JsonObject school =
            JsonNode.Parse(body)?.AsObject()
            ?? throw new AssertionException($"GET profiled school by id returned non-object JSON: {body}");
        AssertProfiledSchoolShape(school, schoolUuid, schoolId, expectedNameOfInstitution);
        AssertProfiledEtag(response, school, projection);
    }

    private async Task AssertProfiledSchoolGetManyAsync(
        string bearerToken,
        long schoolId,
        DocumentCacheProjection projection,
        string expectedNameOfInstitution
    )
    {
        using HttpResponseMessage response = await SendProfiledSchoolGetAsync(
            bearerToken,
            $"{SchoolResourcePath}?schoolId={schoolId.ToString(CultureInfo.InvariantCulture)}&totalCount=true",
            SchoolProfileName
        );
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET profiled school query failed: {body}");
        AssertProfiledSchoolContentType(response);

        JsonArray schools =
            JsonNode.Parse(body)?.AsArray()
            ?? throw new AssertionException($"GET profiled school query returned non-array JSON: {body}");
        schools.Should().ContainSingle();
        JsonObject school =
            schools[0]?.AsObject() ?? throw new AssertionException("School query row was null.");
        AssertProfiledSchoolShape(school, null, schoolId, expectedNameOfInstitution);
        AssertProfiledBodyEtag(school, projection);
    }

    private async Task AssertProfiledBellScheduleGetByIdAsync(
        string bearerToken,
        Guid bellScheduleUuid,
        long schoolId,
        DocumentCacheProjection projection,
        string expectedClassPeriodName
    )
    {
        using HttpResponseMessage response = await SendProfiledBellScheduleGetAsync(
            bearerToken,
            BellSchedulePath(bellScheduleUuid),
            BellScheduleProfileName
        );
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, $"GET profiled bell schedule by id failed: {body}");
        AssertProfiledBellScheduleContentType(response);

        JsonObject bellSchedule =
            JsonNode.Parse(body)?.AsObject()
            ?? throw new AssertionException(
                $"GET profiled bell schedule by id returned non-object JSON: {body}"
            );
        AssertProfiledBellScheduleShape(bellSchedule, bellScheduleUuid, schoolId, expectedClassPeriodName);
        AssertProfiledEtag(response, bellSchedule, projection);
    }

    private async Task AssertProfiledBellScheduleReadDeniedAsync(
        string bearerToken,
        Guid bellScheduleUuid,
        string forbiddenSentinel
    )
    {
        using HttpResponseMessage response = await SendProfiledBellScheduleGetAsync(
            bearerToken,
            BellSchedulePath(bellScheduleUuid),
            BellScheduleProfileName
        );
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden, $"GET should be authorization denied: {body}");

        JsonObject problem =
            JsonNode.Parse(body)?.AsObject()
            ?? throw new AssertionException($"Authorization failure returned non-object JSON: {body}");
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");
        body.Should().NotContain(BellScheduleName);
        body.Should().NotContain("totalInstructionalTime");
        body.Should().NotContain("classPeriods");
        body.Should().NotContain(forbiddenSentinel);
    }

    private async Task<HttpResponseMessage> SendProfiledSchoolGetAsync(
        string bearerToken,
        string path,
        string profileName
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(
            MediaTypeWithQualityHeaderValue.Parse(ReadableSchoolProfileContentType(profileName))
        );

        return await _dmsClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendProfiledBellScheduleGetAsync(
        string bearerToken,
        string path,
        string profileName
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(
            MediaTypeWithQualityHeaderValue.Parse(ReadableBellScheduleProfileContentType(profileName))
        );

        return await _dmsClient.SendAsync(request);
    }

    private static void AssertProfiledSchoolContentType(HttpResponseMessage response)
    {
        response
            .Content.Headers.ContentType?.MediaType.Should()
            .Be(ReadableSchoolProfileContentType(SchoolProfileName));
    }

    private static void AssertProfiledBellScheduleContentType(HttpResponseMessage response)
    {
        response
            .Content.Headers.ContentType?.MediaType.Should()
            .Be(ReadableBellScheduleProfileContentType(BellScheduleProfileName));
    }

    private static void AssertProfiledSchoolShape(
        JsonObject school,
        Guid? schoolUuid,
        long schoolId,
        string expectedNameOfInstitution
    )
    {
        school
            .Select(field => field.Key)
            .Should()
            .BeEquivalentTo("id", "schoolId", "nameOfInstitution", "webSite", "_etag", "_lastModifiedDate");

        if (schoolUuid is not null)
        {
            school["id"]!.GetValue<string>().Should().Be(schoolUuid.Value.ToString());
        }

        school["schoolId"]!.GetValue<long>().Should().Be(schoolId);
        school["nameOfInstitution"]!.GetValue<string>().Should().Be(expectedNameOfInstitution);
        school["webSite"]!.GetValue<string>().Should().Be("https://document-cache-profile.example.com");
    }

    private static void AssertProfiledBellScheduleShape(
        JsonObject bellSchedule,
        Guid bellScheduleUuid,
        long schoolId,
        string expectedClassPeriodName
    )
    {
        bellSchedule["id"]!.GetValue<string>().Should().Be(bellScheduleUuid.ToString());
        bellSchedule["bellScheduleName"]!.GetValue<string>().Should().Be(BellScheduleName);
        bellSchedule["schoolReference"]!["schoolId"]!.GetValue<long>().Should().Be(schoolId);
        bellSchedule.Should().NotContainKey("totalInstructionalTime");
        bellSchedule.Should().NotContainKey("dates");
        bellSchedule.Should().NotContainKey("gradeLevels");

        JsonArray classPeriods =
            bellSchedule["classPeriods"]?.AsArray()
            ?? throw new AssertionException("Profiled bell schedule omitted classPeriods.");
        classPeriods.Should().ContainSingle();
        JsonObject classPeriod =
            classPeriods[0]?.AsObject() ?? throw new AssertionException("Class period row was null.");
        classPeriod["classPeriodReference"]!["schoolId"]!.GetValue<long>().Should().Be(schoolId);
        classPeriod["classPeriodReference"]!["classPeriodName"]!
            .GetValue<string>()
            .Should()
            .Be(expectedClassPeriodName);
    }

    private static void ApplyBellScheduleClassPeriodNameSentinel(JsonObject documentJson)
    {
        JsonArray classPeriods =
            documentJson["classPeriods"]?.AsArray()
            ?? throw new AssertionException("Cached bell schedule omitted classPeriods.");
        classPeriods.Should().ContainSingle();
        JsonObject classPeriod =
            classPeriods[0]?.AsObject() ?? throw new AssertionException("Cached class period row was null.");
        JsonObject classPeriodReference =
            classPeriod["classPeriodReference"]?.AsObject()
            ?? throw new AssertionException("Cached class period row omitted classPeriodReference.");
        classPeriodReference["classPeriodName"] = CacheOnlyBellScheduleClassPeriodNameSentinel;
    }

    private static void AssertProfiledEtag(
        HttpResponseMessage response,
        JsonObject school,
        DocumentCacheProjection projection
    )
    {
        string headerEtag = ReadUnquotedEtagHeader(response);
        string bodyEtag = AssertProfiledBodyEtag(school, projection);
        headerEtag
            .Should()
            .Be(
                bodyEtag,
                "profiled GET-by-id should expose the same readable-profile etag in header and body"
            );
    }

    private static string AssertProfiledBodyEtag(JsonObject school, DocumentCacheProjection projection)
    {
        string bodyEtag = school["_etag"]!.GetValue<string>();
        bodyEtag.Should().NotBeNullOrWhiteSpace();
        bodyEtag
            .Should()
            .StartWith($"{projection.CacheContentVersion.ToString(CultureInfo.InvariantCulture)}-");
        bodyEtag
            .Should()
            .NotBe(
                projection.StreamEtag,
                "readable-profile etags must vary from the caller-agnostic cache stream etag"
            );
        string[] etagParts = bodyEtag.Split('.');
        etagParts.Should().HaveCount(5);
        etagParts[1].Should().Be("j");
        etagParts[2].Should().NotBe("_", "a readable-profile etag must carry a profile variant");
        etagParts[3].Should().Be("l");
        etagParts[4].Should().Be("i");
        return bodyEtag;
    }

    private static string ReadUnquotedEtagHeader(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("ETag", out IEnumerable<string>? values).Should().BeTrue();
        string rawEtag = values!.Single();
        rawEtag.Should().StartWith("\"").And.EndWith("\"");
        return rawEtag[1..^1];
    }

    private static string SchoolPath(Guid schoolUuid) => $"{SchoolResourcePath}/{schoolUuid}";

    private static string BellSchedulePath(Guid bellScheduleUuid) =>
        $"{BellScheduleResourcePath}/{bellScheduleUuid}";

    private static string ReadableSchoolProfileContentType(string profileName) =>
        $"application/vnd.ed-fi.school.{profileName.ToLowerInvariant()}.readable+json";

    private static string WritableSchoolProfileContentType(string profileName) =>
        $"application/vnd.ed-fi.school.{profileName.ToLowerInvariant()}.writable+json";

    private static string ReadableBellScheduleProfileContentType(string profileName) =>
        $"application/vnd.ed-fi.bellschedule.{profileName.ToLowerInvariant()}.readable+json";

    private static string WritableBellScheduleProfileContentType(string profileName) =>
        $"application/vnd.ed-fi.bellschedule.{profileName.ToLowerInvariant()}.writable+json";
}
