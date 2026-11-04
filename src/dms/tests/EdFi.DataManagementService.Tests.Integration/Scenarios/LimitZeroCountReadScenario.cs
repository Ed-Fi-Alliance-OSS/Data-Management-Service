// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class LimitZeroCountReadScenario
{
    private const string GradeLevelEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const string AcademicSubjectEndpoint = "/data/ed-fi/academicSubjectDescriptors";
    private const string TotalCountHeader = "Total-Count";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string SchoolsEndpoint = "/data/ed-fi/schools";

    public static async Task SeedGradeLevelDescriptorsAsync(ApiIntegrationHarness harness)
    {
        foreach (string codeValue in new[] { "LimitZeroFirst", "LimitZeroSecond" })
        {
            string body = new JsonObject
            {
                ["namespace"] = "uri://ed-fi.org/GradeLevelDescriptor",
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }.ToJsonString();

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await harness.HttpClient.PostAsync(
                GradeLevelEndpoint,
                content
            );
            string responseBody = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        }
    }

    public static Task It_returns_an_empty_page_and_the_total_count_for_limit_zero_with_total_count(
        ApiIntegrationHarness harness
    ) => AssertPageAsync(harness, $"{GradeLevelEndpoint}?limit=0&totalCount=true", 0, "2");

    public static Task It_returns_an_empty_page_without_a_total_count_header_for_limit_zero(
        ApiIntegrationHarness harness
    ) => AssertPageAsync(harness, $"{GradeLevelEndpoint}?limit=0", 0, null);

    public static Task It_returns_a_zero_total_count_for_limit_zero_on_an_empty_descriptor_collection(
        ApiIntegrationHarness harness
    ) => AssertPageAsync(harness, $"{AcademicSubjectEndpoint}?limit=0&totalCount=true", 0, "0");

    public static Task It_returns_an_empty_page_without_a_total_count_header_for_limit_zero_on_an_empty_descriptor_collection(
        ApiIntegrationHarness harness
    ) => AssertPageAsync(harness, $"{AcademicSubjectEndpoint}?limit=0", 0, null);

    public static Task It_returns_one_item_and_the_total_count_for_limit_one_with_total_count(
        ApiIntegrationHarness harness
    ) => AssertPageAsync(harness, $"{GradeLevelEndpoint}?limit=1&totalCount=true", 1, "2");

    // Change queries (/deletes, /keyChanges) are answered from the tracked-change tables and never use the
    // document cache, so only the plain fixtures cover them; the _DocumentCache fixtures would add runtime only.

    /// <summary>
    /// Leaves two Student tracked deletes, two Student tracked key changes, and one descriptor tracked delete.
    /// </summary>
    public static async Task SeedTrackedChangesAsync(ApiIntegrationHarness harness)
    {
        await DerivativeTrackedChangeScenario.SeedTrackedDeletesAsync(harness, 2, "limit-zero");
        await DerivativeTrackedChangeScenario.SeedTrackedKeyChangesAsync(harness, 2, "limit-zero");

        string body = new JsonObject
        {
            ["namespace"] = "uri://ed-fi.org/GradeLevelDescriptor",
            ["codeValue"] = "LimitZeroDeleted",
            ["shortDescription"] = "LimitZeroDeleted",
        }.ToJsonString();

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(GradeLevelEndpoint, content);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        string location = created.Headers.Location!.IsAbsoluteUri
            ? created.Headers.Location!.AbsolutePath
            : created.Headers.Location!.OriginalString;

        using HttpResponseMessage deleted = await harness.HttpClient.DeleteAsync(location);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
    }

    public static async Task It_returns_an_empty_page_and_the_total_count_for_student_deletes_with_limit_zero(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertChangeQueryCountAsync(harness, $"{StudentsEndpoint}/deletes", string.Empty, "2");
    }

    public static async Task It_returns_an_empty_page_without_a_total_count_header_for_student_deletes_with_limit_zero(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertPageAsync(harness, $"{StudentsEndpoint}/deletes?limit=0", 0, null);
    }

    public static async Task It_returns_the_total_count_for_student_deletes_with_limit_zero_in_a_change_version_window_that_excludes_a_change(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        string path = $"{StudentsEndpoint}/deletes";
        await AssertChangeQueryCountAsync(harness, path, string.Empty, "2");

        string window = await GetWindowExcludingTheLatestChangeAsync(harness, path);
        await AssertChangeQueryCountAsync(harness, path, window, "1");
    }

    public static async Task It_returns_an_empty_page_and_the_total_count_for_student_key_changes_with_limit_zero(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertChangeQueryCountAsync(harness, $"{StudentsEndpoint}/keyChanges", string.Empty, "2");
    }

    public static async Task It_returns_an_empty_page_without_a_total_count_header_for_student_key_changes_with_limit_zero(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertPageAsync(harness, $"{StudentsEndpoint}/keyChanges?limit=0", 0, null);
    }

    public static async Task It_returns_the_total_count_for_student_key_changes_with_limit_zero_in_a_change_version_window_that_excludes_a_change(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        string path = $"{StudentsEndpoint}/keyChanges";
        await AssertChangeQueryCountAsync(harness, path, string.Empty, "2");

        string window = await GetWindowExcludingTheLatestChangeAsync(harness, path);
        await AssertChangeQueryCountAsync(harness, path, window, "1");
    }

    public static async Task It_returns_a_zero_total_count_for_limit_zero_on_a_resource_with_no_tracked_deletes(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertChangeQueryCountAsync(harness, $"{SchoolsEndpoint}/deletes", string.Empty, "0");
    }

    public static async Task It_returns_the_total_count_for_descriptor_deletes_with_limit_zero(
        ApiIntegrationHarness harness
    )
    {
        await SeedTrackedChangesAsync(harness);
        await AssertChangeQueryCountAsync(harness, $"{GradeLevelEndpoint}/deletes", string.Empty, "1");
    }

    /// <summary>
    /// Reads the two tracked changes at the endpoint and returns a window that ends at the lower of their change
    /// versions, so exactly one of the two falls inside the window.
    /// </summary>
    private static async Task<string> GetWindowExcludingTheLatestChangeAsync(
        ApiIntegrationHarness harness,
        string path
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync($"{path}?limit=500");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        long[] changeVersions =
        [
            .. JsonDocument
                .Parse(body)
                .RootElement.EnumerateArray()
                .Select(item => item.GetProperty("changeVersion").GetInt64()),
        ];

        changeVersions.Should().HaveCount(2, body);
        changeVersions.Distinct().Should().HaveCount(2, body);

        return $"&minChangeVersion=0&maxChangeVersion={changeVersions.Min()}";
    }

    /// <summary>
    /// Asserts the limit=0 page is empty and carries the expected Total-Count, then that the same request with
    /// limit=1 reports the same Total-Count.
    /// </summary>
    private static async Task AssertChangeQueryCountAsync(
        ApiIntegrationHarness harness,
        string path,
        string windowQuery,
        string expectedTotalCount
    )
    {
        await AssertPageAsync(harness, $"{path}?limit=0&totalCount=true{windowQuery}", 0, expectedTotalCount);

        using HttpResponseMessage limitOne = await harness.HttpClient.GetAsync(
            $"{path}?limit=1&totalCount=true{windowQuery}"
        );
        string limitOneBody = await limitOne.Content.ReadAsStringAsync();

        limitOne.StatusCode.Should().Be(HttpStatusCode.OK, limitOneBody);
        limitOne.Headers.TryGetValues(TotalCountHeader, out IEnumerable<string>? values).Should().BeTrue();
        values.Should().Equal(expectedTotalCount);
    }

    private static async Task AssertPageAsync(
        ApiIntegrationHarness harness,
        string requestPath,
        int expectedItemCount,
        string? expectedTotalCount
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestPath);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!.AsArray().Should().HaveCount(expectedItemCount, body);

        if (expectedTotalCount is null)
        {
            response.Headers.Contains(TotalCountHeader).Should().BeFalse();
            return;
        }

        response.Headers.TryGetValues(TotalCountHeader, out IEnumerable<string>? values).Should().BeTrue();
        values.Should().Equal(expectedTotalCount);
    }
}
