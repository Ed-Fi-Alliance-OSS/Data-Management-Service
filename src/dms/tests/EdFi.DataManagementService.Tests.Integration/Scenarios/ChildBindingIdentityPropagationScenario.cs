// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public HTTP regression for DMS-1166: when an upstream resource's identity is
/// changed via PUT, the SQL Server native ON UPDATE CASCADE reference foreign keys
/// must reach stored child-collection bindings on referencing roots, and the
/// resulting child stamp triggers must bump the owning root document's content
/// version so a subsequent GET returns the propagated identity value and an
/// advanced ETag/_etag.
///
/// Concrete shape: ClassPeriod is the upstream identity holder, BellSchedule is
/// the referencing root, and BellSchedule.classPeriods[*].classPeriodReference is
/// the child binding. Changing the ClassPeriod's classPeriodName via PUT must
/// flow into the BellSchedule's nested classPeriodReference on the very next
/// GET, with the BellSchedule's representation ETag advanced to reflect the
/// stamp-trigger-driven content-version bump.
///
/// Bound to <c>FixtureKey.AuthoritativeDs52</c> because reproducing this end to
/// end requires the production School/ClassPeriod/BellSchedule shapes and the
/// production cascade and stamping behavior those resource shapes drive.
/// </summary>
internal static class ChildBindingIdentityPropagationScenario
{
    private const string StandardJsonContentType = "application/json";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string ClassPeriodsEndpoint = "/data/ed-fi/classPeriods";
    private const string BellSchedulesEndpoint = "/data/ed-fi/bellSchedules";

    public static async Task It_propagates_ClassPeriod_identity_update_into_BellSchedule_child_binding(
        ApiIntegrationHarness harness
    )
    {
        // Per-test unique values keep the scenario isolated even if the leased
        // database baseline is shared with future scenarios bound to this fixture.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 6_001_166L + Math.Abs(suffix.GetHashCode() % 100_000);
        string namespaceUri = $"uri://ed-fi.org/DMS-1166/{suffix}";
        string eocDescriptorUri = $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School";
        string gradeLevelDescriptorUri = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade";
        string bellScheduleName = $"DMS-1166 BS {suffix}";
        string oldClassPeriodName = $"DMS-1166 Period A {suffix}";
        string newClassPeriodName = $"DMS-1166 Period B {suffix}";

        await SeedDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/EducationOrganizationCategoryDescriptor",
            codeValue: "School",
            shortDescription: "School"
        );
        await SeedDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            namespaceUri: $"{namespaceUri}/GradeLevelDescriptor",
            codeValue: "Tenth grade",
            shortDescription: "Tenth grade"
        );

        var schoolPayload = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"DMS-1166 School {suffix}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject { ["educationOrganizationCategoryDescriptor"] = eocDescriptorUri }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = gradeLevelDescriptorUri }
            ),
        };
        await PostExpectingCreatedAsync(harness, SchoolsEndpoint, schoolPayload);

        var classPeriodPayload = new JsonObject
        {
            ["classPeriodName"] = oldClassPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
        };
        (string classPeriodLocationPath, string classPeriodInitialEtag) = await PostExpectingCreatedAsync(
            harness,
            ClassPeriodsEndpoint,
            classPeriodPayload
        );

        var bellSchedulePayload = new JsonObject
        {
            ["bellScheduleName"] = bellScheduleName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
            ["classPeriods"] = new JsonArray(
                new JsonObject
                {
                    ["classPeriodReference"] = new JsonObject
                    {
                        ["classPeriodName"] = oldClassPeriodName,
                        ["schoolId"] = schoolId,
                    },
                }
            ),
        };
        (string bellScheduleLocationPath, string bellScheduleCreateEtag) = await PostExpectingCreatedAsync(
            harness,
            BellSchedulesEndpoint,
            bellSchedulePayload
        );

        JsonObject bellScheduleBeforePut = await GetJsonObjectAsync(harness, bellScheduleLocationPath);
        JsonArray classPeriodsBefore = bellScheduleBeforePut["classPeriods"]!.AsArray();
        classPeriodsBefore
            .Count.Should()
            .Be(
                1,
                "the seeded BellSchedule must return exactly one classPeriods child binding before the identity change"
            );
        string? nestedNameBefore = classPeriodsBefore[0]!.AsObject()["classPeriodReference"]!.AsObject()[
            "classPeriodName"
        ]!.GetValue<string>();
        nestedNameBefore
            .Should()
            .Be(
                oldClassPeriodName,
                "the BellSchedule's projected child-binding identity must match the upstream ClassPeriod before the identity change"
            );
        string? bellScheduleEtagBefore = bellScheduleBeforePut["_etag"]?.GetValue<string>();
        bellScheduleEtagBefore
            .Should()
            .NotBeNullOrWhiteSpace("the BellSchedule GET response must expose an _etag for change detection");
        bellScheduleCreateEtag
            .Should()
            .Be(
                bellScheduleEtagBefore,
                "the POST-create ETag header must match the subsequent GET _etag before any propagation occurs"
            );

        string classPeriodResourceId = classPeriodLocationPath.Split('/')[^1];
        var classPeriodUpdatePayload = new JsonObject
        {
            ["id"] = classPeriodResourceId,
            ["classPeriodName"] = newClassPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
        };
        using var putContent = new StringContent(
            classPeriodUpdatePayload.ToJsonString(),
            Encoding.UTF8,
            StandardJsonContentType
        );
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, classPeriodLocationPath)
        {
            Content = putContent,
        };
        putRequest.Headers.TryAddWithoutValidation("If-Match", classPeriodInitialEtag);

        using HttpResponseMessage putResponse = await harness.HttpClient.SendAsync(putRequest);
        string putBody = await putResponse.Content.ReadAsStringAsync();
        putResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NoContent,
                $"changing the ClassPeriod's classPeriodName via PUT must succeed (allowIdentityUpdates=true). Body: {putBody}"
            );

        JsonObject bellScheduleAfterPut = await GetJsonObjectAsync(harness, bellScheduleLocationPath);
        JsonArray classPeriodsAfter = bellScheduleAfterPut["classPeriods"]!.AsArray();
        classPeriodsAfter
            .Count.Should()
            .Be(
                1,
                "identity propagation must update the existing child-binding row, never insert/delete child rows"
            );
        string nestedNameAfter = classPeriodsAfter[0]!.AsObject()["classPeriodReference"]!.AsObject()[
            "classPeriodName"
        ]!.GetValue<string>();
        nestedNameAfter
            .Should()
            .Be(
                newClassPeriodName,
                "the BellSchedule's projected child-binding identity must reflect the updated ClassPeriod identity on the very next GET"
            );

        string? bellScheduleEtagAfter = bellScheduleAfterPut["_etag"]?.GetValue<string>();
        bellScheduleEtagAfter
            .Should()
            .NotBeNullOrWhiteSpace(
                "the BellSchedule GET response must continue to expose an _etag after propagation"
            );
        bellScheduleEtagAfter
            .Should()
            .NotBe(
                bellScheduleEtagBefore,
                "the trigger-driven content-version bump on BellSchedule must advance the GET _etag so clients can invalidate cached representations"
            );
    }

    private static async Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string namespaceUri,
        string codeValue,
        string shortDescription
    )
    {
        var payload = new JsonObject
        {
            ["namespace"] = namespaceUri,
            ["codeValue"] = codeValue,
            ["shortDescription"] = shortDescription,
        };
        await PostExpectingCreatedAsync(harness, endpoint, payload);
    }

    private static async Task<(string LocationPath, string Etag)> PostExpectingCreatedAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
        response.Headers.Location.Should().NotBeNull($"POST {endpoint} must return a Location header");
        response.TryReadRawEtag(out string etag).Should().BeTrue($"POST {endpoint} must emit an ETag header");

        Uri location = response.Headers.Location!;
        string locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        return (locationPath, etag);
    }

    private static async Task<JsonObject> GetJsonObjectAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {endpoint} body: {body}");
        JsonNode? node = JsonNode.Parse(body);
        node.Should().NotBeNull($"GET {endpoint} must return a JSON document");
        return node!.AsObject();
    }
}
