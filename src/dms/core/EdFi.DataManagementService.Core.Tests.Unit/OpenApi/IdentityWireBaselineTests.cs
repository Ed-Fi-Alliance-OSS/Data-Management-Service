// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
/// Serves the identity OpenAPI document with a fixed <c>servers</c> entry and token URL,
/// normalizes it (recursively sorted object keys, <c>servers[*].url</c> and the client-credentials
/// <c>tokenUrl</c> replaced by stable placeholders, the serve-time contract-version stamp removed), and
/// compares the result byte for byte with the committed <c>identity-v2-wire-baseline.json</c> golden.
/// Follows the golden-fixture call pair <c>PlanContractsManifestGoldenFixtureTests.cs:51-71</c> uses:
/// <see cref="GoldenFixtureTestHelpers.ShouldUpdateGoldens" /> and
/// <see cref="GoldenFixtureTestHelpers.RunGitDiff(string, string)" />, called directly rather than
/// copied into a local helper.
/// </summary>
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

    /// <summary>
    /// Follows the golden-fixture call pair <c>PlanContractsManifestGoldenFixtureTests.cs:51-71</c>
    /// uses: the diff is computed once in <c>SetUp</c> (writing <c>actual</c>, regenerating the golden
    /// under <c>UPDATE_GOLDENS</c>, and asserting the golden file exists), and the single <c>It_</c>
    /// fails on any remaining diff.
    /// </summary>
    [TestFixture]
    public class Given_The_Identity_Wire_Baseline_GoldenFixture
    {
        private string _diffOutput = null!;

        [SetUp]
        public void Setup()
        {
            string actualPath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "identity-v2-wire-baseline",
                "actual",
                BaselineFileName
            );
            string actualJson = SerializeCanonical(BuildNormalizedServedDocument(ServerUrl, TokenUrl));

            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            File.WriteAllText(actualPath, actualJson);

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

            _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(ExpectedPath, actualPath);
        }

        [Test]
        public void It_matches_the_wire_baseline()
        {
            if (!string.IsNullOrWhiteSpace(_diffOutput))
            {
                Assert.Fail(_diffOutput);
            }
        }
    }

    /// <summary>
    /// The contract version comes from the Identity assembly's <c>IdentityContractVersion</c>
    /// <see cref="AssemblyMetadataAttribute" />, whose value the Identity csproj derives from its own
    /// <c>VersionPrefix</c>. Unlike <c>AssemblyInformationalVersion</c>, neither is overridden by a
    /// global <c>/p:Version</c> or <c>/p:InformationalVersion</c>, so the stamp stays the contract's
    /// version inside release-stamped images.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Identity_Contract_Version_Stamp
    {
        private JsonNode _served = null!;
        private JsonNode _baseline = null!;
        private string? _assemblyMetadataVersion;
        private string _csprojVersionPrefix = null!;

        [SetUp]
        public void Setup()
        {
            _served = BuildServedDocument(ServerUrl, TokenUrl);
            _assemblyMetadataVersion = typeof(IIdentityService)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "IdentityContractVersion")
                ?.Value;
            _csprojVersionPrefix = ReadIdentityCsprojVersionPrefix();
            _baseline = JsonNode.Parse(File.ReadAllText(ExpectedPath))!;
        }

        [Test]
        public void It_equals_the_identity_assembly_contract_version_metadata()
        {
            _served["x-edfi-identity-contract-version"]!
                .GetValue<string>()
                .Should()
                .Be(_assemblyMetadataVersion);
        }

        [Test]
        public void It_equals_the_identity_csproj_VersionPrefix()
        {
            _served["x-edfi-identity-contract-version"]!.GetValue<string>().Should().Be(_csprojVersionPrefix);
        }

        [Test]
        public void It_is_absent_from_the_baseline()
        {
            _baseline["x-edfi-identity-contract-version"].Should().BeNull();
        }

        private static string ReadIdentityCsprojVersionPrefix()
        {
            string csprojPath = Path.Combine(
                ProjectRoot,
                "..",
                "EdFi.DataManagementService.Identity",
                "EdFi.DataManagementService.Identity.csproj"
            );

            return XDocument.Load(csprojPath).Descendants("VersionPrefix").Single().Value.Trim();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Serves_With_Different_Servers_And_Token_Urls
    {
        private string _first = null!;
        private string _second = null!;

        [SetUp]
        public void Setup()
        {
            _first = SerializeCanonical(
                BuildNormalizedServedDocument(
                    "http://first.example.org/identity/v2",
                    "https://first.example.org/token"
                )
            );
            _second = SerializeCanonical(
                BuildNormalizedServedDocument(
                    "http://second.example.org/tenant/identity/v2",
                    "https://second.example.org/oauth/token"
                )
            );
        }

        [Test]
        public void It_normalizes_identically()
        {
            _first.Should().Be(_second);
        }
    }
}
