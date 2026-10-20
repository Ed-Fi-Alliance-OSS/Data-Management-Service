// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class PutBodyIdScenario
{
    public static Task It_uses_the_url_id_for_student_puts(ApiIntegrationHarness harness) =>
        VerifyAsync(
            harness,
            "/data/ed-fi/students",
            new JsonObject { ["studentUniqueId"] = "put-id-target", ["firstName"] = "Original" },
            new JsonObject { ["studentUniqueId"] = "put-id-other", ["firstName"] = "Untouched" },
            "firstName"
        );

    public static Task It_uses_the_url_id_for_descriptor_puts(ApiIntegrationHarness harness) =>
        VerifyAsync(
            harness,
            "/data/ed-fi/schoolTypeDescriptors",
            new JsonObject
            {
                ["namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["codeValue"] = "put-id-target",
                ["shortDescription"] = "Original",
            },
            new JsonObject
            {
                ["namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["codeValue"] = "put-id-other",
                ["shortDescription"] = "Untouched",
            },
            "shortDescription"
        );

    private static async Task VerifyAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject target,
        JsonObject other,
        string changedField
    )
    {
        string targetPath = await CreateAsync(harness, endpoint, target);
        string otherPath = await CreateAsync(harness, endpoint, other);
        string targetId = targetPath.Split('/')[^1];
        string otherId = otherPath.Split('/')[^1];
        string[] accepted = ["omitted", "null", "matching", "different", "empty-guid"];
        foreach (string mode in accepted)
        {
            JsonObject body = (JsonObject)target.DeepClone();
            body[changedField] = mode;
            if (mode != "omitted")
            {
                body["id"] = mode switch
                {
                    "null" => null,
                    "matching" => JsonValue.Create(targetId),
                    "different" => JsonValue.Create(otherId),
                    _ => JsonValue.Create(Guid.Empty.ToString()),
                };
            }
            using var content = Content(body.ToJsonString());
            using var response = await harness.HttpClient.PutAsync(targetPath, content);
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
            JsonObject updated = await ReadAsync(harness, targetPath);
            updated["id"]!.GetValue<string>().Should().Be(targetId);
            updated[changedField]!.GetValue<string>().Should().Be(mode);
            JsonObject untouched = await ReadAsync(harness, otherPath);
            untouched["id"]!.GetValue<string>().Should().Be(otherId);
            untouched[changedField]!.GetValue<string>().Should().Be("Untouched");
        }

        foreach (
            string invalid in new[]
            {
                "\"bad-id\"",
                "\"\"",
                "12345678901234567890123456789012",
                "true",
                "[]",
                "{}",
            }
        )
        {
            JsonObject body = (JsonObject)target.DeepClone();
            body["id"] = JsonNode.Parse(invalid);
            using var content = Content(body.ToJsonString());
            using var response = await harness.HttpClient.PutAsync(targetPath, content);
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            JsonObject error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            error["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
            error["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
            error["status"]!.GetValue<int>().Should().Be(400);
            error["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
            error["errors"]!.AsArray().Should().BeEmpty();
            error["validationErrors"]!.AsObject().Count.Should().Be(1);
            error["validationErrors"]!["$.id"]!.AsArray().Count.Should().Be(1);
            error["validationErrors"]!["$.id"]![0]!
                .GetValue<string>()
                .Should()
                .Be("id must be omitted, null, or a valid GUID string.");
        }
        (await ReadAsync(harness, targetPath))[changedField]!.GetValue<string>().Should().Be("empty-guid");

        // Normalization must preserve existing invalid-body handling and must not bypass URL validation.
        foreach (string invalidBody in new[] { "[]", "true", "123", "\"text\"" })
        {
            using var content = Content(invalidBody);
            using var response = await harness.HttpClient.PutAsync(targetPath, content);
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            string error = await response.Content.ReadAsStringAsync();
            error.Should().NotContain("id must be omitted, null, or a valid GUID string.");
        }
        using var badUrlContent = Content(target.ToJsonString());
        using var badUrlResponse = await harness.HttpClient.PutAsync(
            endpoint + "/invalid-guid",
            badUrlContent
        );
        badUrlResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest, await badUrlResponse.Content.ReadAsStringAsync());
    }

    private static StringContent Content(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string> CreateAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    )
    {
        using var content = Content(body.ToJsonString());
        using var response = await harness.HttpClient.PostAsync(endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        Uri location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
    }

    private static async Task<JsonObject> ReadAsync(ApiIntegrationHarness harness, string path)
    {
        using var response = await harness.HttpClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
}
