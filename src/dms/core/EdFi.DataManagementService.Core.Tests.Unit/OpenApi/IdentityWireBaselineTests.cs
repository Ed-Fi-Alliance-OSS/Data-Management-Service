// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.ChangeQueriesOpenApiDocumentTestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Story D6/D13: serves the identity OpenAPI document with a fixed <c>servers</c> entry and token URL,
/// normalizes it (recursively sorted object keys, <c>servers[*].url</c> and the client-credentials
/// <c>tokenUrl</c> replaced by stable placeholders, the serve-time contract-version stamp removed), and
/// compares the result byte for byte with the committed <c>identity-v2-wire-baseline.json</c> golden.
/// Follows the golden-fixture call pair <c>PlanContractsManifestGoldenFixtureTests.cs:51-71</c> uses:
/// <see cref="GoldenFixtureTestHelpers.ShouldUpdateGoldens" /> and
/// <see cref="GoldenFixtureTestHelpers.RunGitDiff(string, string)" />, called directly rather than
/// copied into a local helper.
/// </summary>
[TestFixture]
public class IdentityWireBaselineTests
{
    private const string ServerUrl = "http://a/identity/v2";
    private const string TokenUrl = "https://auth.a/token";
    private const string BaselineFileName = "identity-v2-wire-baseline.json";

    private static string ProjectRoot =>
        GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Core.Tests.Unit.csproj"
        );

    private static string ExpectedPath => Path.Combine(ProjectRoot, "OpenApi", "Fixtures", BaselineFileName);

    private static string ActualPath =>
        Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "identity-v2-wire-baseline",
            "actual",
            BaselineFileName
        );

    [Test]
    public void The_normalized_served_document_matches_the_wire_baseline()
    {
        string actualJson = SerializeCanonical(BuildNormalizedServedDocument(ServerUrl, TokenUrl));

        Directory.CreateDirectory(Path.GetDirectoryName(ActualPath)!);
        File.WriteAllText(ActualPath, actualJson);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExpectedPath)!);
            File.WriteAllText(ExpectedPath, actualJson);
            Assert.Fail(
                $"Regenerated {ExpectedPath}. Re-run without UPDATE_GOLDENS to confirm it now matches."
            );
        }

        File.Exists(ExpectedPath)
            .Should()
            .BeTrue($"wire baseline missing at {ExpectedPath}. Set UPDATE_GOLDENS=1 to generate it.");

        string diffOutput = GoldenFixtureTestHelpers.RunGitDiff(ExpectedPath, ActualPath);
        if (!string.IsNullOrWhiteSpace(diffOutput))
        {
            Assert.Fail(diffOutput);
        }
    }

    [Test]
    public void The_contract_version_stamp_equals_the_identity_assembly_informational_version_and_is_absent_from_the_baseline()
    {
        JsonNode served = BuildServedDocument(ServerUrl, TokenUrl);
        string expectedVersion = ResolveIdentityContractInformationalVersion();

        served["x-edfi-identity-contract-version"]!.GetValue<string>().Should().Be(expectedVersion);

        JsonNode baseline = JsonNode.Parse(File.ReadAllText(ExpectedPath))!;
        baseline["x-edfi-identity-contract-version"].Should().BeNull();
    }

    [Test]
    public void Two_serves_with_different_servers_and_tokenUrl_normalize_identically()
    {
        string first = SerializeCanonical(
            BuildNormalizedServedDocument(
                "http://first.example.org/identity/v2",
                "https://first.example.org/token"
            )
        );
        string second = SerializeCanonical(
            BuildNormalizedServedDocument(
                "http://second.example.org/tenant/identity/v2",
                "https://second.example.org/oauth/token"
            )
        );

        first.Should().Be(second);
    }

    /// <summary>
    /// Mirrors <c>IdentityOpenApiDocument.ResolveContractVersion</c>'s stripping of a trailing
    /// <c>+commit</c> metadata suffix, so this test's expectation matches what is actually stamped.
    /// </summary>
    private static string ResolveIdentityContractInformationalVersion()
    {
        string informationalVersion =
            typeof(IIdentityService)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0.0.0";

        int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex < 0 ? informationalVersion : informationalVersion[..plusIndex];
    }

    private static JsonNode BuildNormalizedServedDocument(string serverUrl, string tokenUrl)
    {
        return Normalize(BuildServedDocument(serverUrl, tokenUrl));
    }

    private static JsonNode BuildServedDocument(string serverUrl, string tokenUrl)
    {
        ApiSchemaDocumentNodes apiSchemaDocumentNodes = new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: MinimalOpenApiDocument("Ed-Fi Resources API"),
                descriptorsDoc: MinimalOpenApiDocument("Ed-Fi Descriptors API")
            )
            .WithEndProject()
            .AsApiSchemaNodes();

        ApiService apiService = ApiServiceOpenApiTests.CreateApiService(
            apiSchemaDocumentNodes,
            appSettings: new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                AuthenticationService = tokenUrl,
                MaximumPageSize = OpenApiPagingSettings.MaximumPageSizeDefault,
            }
        );

        return apiService.GetIdentityOpenApiSpecification([new JsonObject { ["url"] = serverUrl }]);
    }

    /// <summary>
    /// Recursively sorts object keys, replaces every <c>servers[*].url</c> with <c>{server}</c>,
    /// replaces the client-credentials <c>tokenUrl</c> with <c>{tokenUrl}</c>, and removes the
    /// serve-time <c>x-edfi-identity-contract-version</c> stamp.
    /// </summary>
    private static JsonNode Normalize(JsonNode served)
    {
        JsonObject document = served.DeepClone().AsObject();
        document.Remove("x-edfi-identity-contract-version");

        if (document["servers"] is JsonArray servers)
        {
            foreach (JsonNode? server in servers)
            {
                if (server is JsonObject serverObject && serverObject.ContainsKey("url"))
                {
                    serverObject["url"] = "{server}";
                }
            }
        }

        if (
            document["components"]
                ?["securitySchemes"]
                ?["oauth2_client_credentials"]
                ?["flows"]
                ?["clientCredentials"]
                is JsonObject clientCredentials
            && clientCredentials.ContainsKey("tokenUrl")
        )
        {
            clientCredentials["tokenUrl"] = "{tokenUrl}";
        }

        return SortKeys(document);
    }

    private static JsonNode SortKeys(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];
                foreach (
                    string key in obj.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal)
                )
                {
                    JsonNode? value = obj[key];
                    sorted[key] = value is null ? null : SortKeys(value.DeepClone());
                }
                return sorted;
            case JsonArray array:
                JsonArray result = [];
                foreach (JsonNode? item in array)
                {
                    result.Add(item is null ? null : SortKeys(item.DeepClone()));
                }
                return result;
            default:
                return node.DeepClone();
        }
    }

    private static string SerializeCanonical(JsonNode node)
    {
        string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return json.Replace("\r\n", "\n") + "\n";
    }
}
