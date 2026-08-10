// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MaterializedDocumentFixtureCatalog
{
    private MaterializedDocumentFixture _fixture = null!;
    private IReadOnlyList<MaterializedDocumentFixture> _allFixtures = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "layout-contract-school"
        );
        _allFixtures = MaterializedDocumentFixtureCatalog.LoadAll(TestContext.CurrentContext.TestDirectory);
    }

    [Test]
    public void It_loads_cases_from_the_shared_repository_layout()
    {
        _allFixtures.Select(fixture => fixture.CaseName).Should().Contain("layout-contract-school");
        _fixture
            .CaseDirectory.Replace('\\', '/')
            .Should()
            .EndWith("src/dms/backend/Fixtures/document-cache/materialized-documents/layout-contract-school");
    }

    [Test]
    public void It_uses_a_manifest_with_relative_paths_to_language_neutral_json_files()
    {
        _fixture
            .Manifest.Should()
            .BeEquivalentTo(
                new MaterializedDocumentFixtureManifest(
                    FixtureVersion: "materialized-document-fixture-v1",
                    CaseName: "layout-contract-school",
                    CoverageTags: ["layout-contract", "layout-only", "ordinary-resource"],
                    SourceSetupPath: "source-setup.json",
                    ExpectedCacheRowPath: "expected-cache-row.json",
                    ExpectedStreamEtagPath: "expected-stream-etag.json",
                    ExpectedPublicCdcDocumentPath: "expected-public-cdc-document.json",
                    ExpectedProjectionFailurePath: null
                )
            );

        ReferencedManifestPaths(_fixture.Manifest)
            .Should()
            .AllSatisfy(path =>
            {
                Path.IsPathRooted(path).Should().BeFalse();
                Path.GetExtension(path).Should().Be(".json");
            });
    }

    [Test]
    public void It_exposes_provider_neutral_source_setup_row_categories()
    {
        _fixture
            .SourceSetup.Documents.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new MaterializedDocumentSourceDocument(
                    DocumentId: 1001,
                    DocumentUuid: "11111111-1111-1111-1111-111111111111",
                    ResourceKeyId: 1,
                    ContentVersion: 42,
                    ContentLastModifiedAt: DateTimeOffset.Parse(
                        "2026-01-15T17:18:19.123456Z",
                        CultureInfo.InvariantCulture
                    )
                )
            );

        _fixture
            .SourceSetup.ConcreteRootRows.Should()
            .ContainSingle(row => row.Schema == "edfi" && row.Table == "School" && row.DocumentId == 1001);
        _fixture.SourceSetup.ChildRows.Should().BeEmpty();
        _fixture.SourceSetup.ExtensionRows.Should().BeEmpty();
        _fixture.SourceSetup.Descriptors.Should().BeEmpty();
        _fixture
            .SourceSetup.ReferentialIdentityRows.Should()
            .ContainSingle(row => row.DocumentId == 1001 && row.ResourceKeyId == 1);
    }

    [Test]
    public void It_keeps_cache_row_json_free_of_etag_and_returns_stream_etag_separately()
    {
        _fixture.ExpectedCacheRow!.DocumentId.Should().Be(1001);
        _fixture.ExpectedCacheRow.DocumentUuid.Should().Be("11111111-1111-1111-1111-111111111111");
        _fixture.ExpectedCacheRow.ProjectName.Should().Be("Ed-Fi");
        _fixture.ExpectedCacheRow.ResourceName.Should().Be("School");
        _fixture.ExpectedCacheRow.ResourceVersion.Should().Be("5.3.0");
        _fixture.ExpectedCacheRow.ContentVersion.Should().Be(42);
        _fixture.ExpectedStreamEtag.Should().Be(_fixture.ExpectedCacheRow.StreamEtag);
        _fixture.ExpectedCacheRow.DocumentJson.ContainsKey("_etag").Should().BeFalse();
    }

    [Test]
    public void It_keeps_the_layout_contract_fixture_out_of_executable_success_expectations()
    {
        HasTags(_fixture, "layout-contract", "layout-only").Should().BeTrue();
        HasTags(_fixture, "success").Should().BeFalse();
    }

    [Test]
    public void It_models_public_cdc_documents_as_cache_json_plus_stream_etag()
    {
        _allFixtures
            .Where(fixture => fixture.ExpectedPublicCdcDocument is not null)
            .Should()
            .AllSatisfy(fixture =>
            {
                var expectedPublicDocument = JsonNode
                    .Parse(fixture.ExpectedCacheRow!.DocumentJson.ToJsonString())!
                    .AsObject();
                expectedPublicDocument["_etag"] = fixture.ExpectedStreamEtag;

                JsonNode
                    .DeepEquals(fixture.ExpectedPublicCdcDocument!.Document, expectedPublicDocument)
                    .Should()
                    .BeTrue();
            });
    }

    [Test]
    public void It_uses_contract_shaped_stream_etags_for_cache_and_cdc_expectations()
    {
        _allFixtures
            .Where(fixture => fixture.ExpectedStreamEtag is not null)
            .Should()
            .AllSatisfy(fixture =>
            {
                var streamEtag = fixture.ExpectedStreamEtag!;
                streamEtag.Should().NotStartWith("\"");
                streamEtag.Should().NotEndWith("\"");

                EtagValue
                    .TryParse(streamEtag, out var contentVersion, out var variantKeyValue)
                    .Should()
                    .BeTrue();
                contentVersion
                    .Should()
                    .Be(fixture.ExpectedCacheRow!.ContentVersion.ToString(CultureInfo.InvariantCulture));

                var variantKey = new VariantKey(variantKeyValue);
                variantKey.TryParseComponents(out var components).Should().BeTrue();
                components.Format.Should().Be("j");
                components.ProfileCode.Should().Be(VariantKey.NoProfileCode);
                components.LinkFlag.Should().Be(HasTags(fixture, "descriptor") ? "n" : "l");
                components.ContentCoding.Should().Be("i");
            });
    }

    [Test]
    public void It_includes_the_representative_DMS_1312_materialized_document_cases()
    {
        _allFixtures
            .Where(fixture => HasTags(fixture, "success", "ordinary-resource", "link-bearing"))
            .Select(fixture => fixture.CaseName)
            .Should()
            .Contain("ordinary-link-bearing-student-school-association");

        _allFixtures
            .Where(fixture => HasTags(fixture, "success", "descriptor", "no-link-stream"))
            .Select(fixture => fixture.CaseName)
            .Should()
            .Contain("descriptor-school-type");

        _allFixtures
            .Where(fixture => HasTags(fixture, "success", "extension", "nested-collection"))
            .Select(fixture => fixture.CaseName)
            .Should()
            .Contain("extension-student-school-association");

        _allFixtures
            .Where(fixture => HasTags(fixture, "success", "property-absence", "nested-collection"))
            .Select(fixture => fixture.CaseName)
            .Should()
            .Contain("school-address-property-absence");

        _allFixtures
            .Where(fixture => HasTags(fixture, "projection-failure", "invariant-failure"))
            .Select(fixture => fixture.CaseName)
            .Should()
            .Contain("invariant-missing-school-body");
    }

    [Test]
    public void It_keeps_each_success_fixture_cache_row_complete_and_free_of_etag()
    {
        _allFixtures
            .Where(fixture => HasTags(fixture, "success"))
            .Should()
            .AllSatisfy(fixture =>
            {
                var cacheRow = fixture.ExpectedCacheRow!;
                cacheRow.DocumentId.Should().BePositive();
                cacheRow.DocumentUuid.Should().NotBeNullOrWhiteSpace();
                cacheRow.ProjectName.Should().NotBeNullOrWhiteSpace();
                cacheRow.ResourceName.Should().NotBeNullOrWhiteSpace();
                cacheRow.ResourceVersion.Should().NotBeNullOrWhiteSpace();
                cacheRow.ContentVersion.Should().BePositive();
                cacheRow.LastModifiedAt.Should().NotBe(default);
                cacheRow.StreamEtag.Should().Be(fixture.ExpectedStreamEtag);
                cacheRow.DocumentJson.Should().ContainKey("id");
                cacheRow.DocumentJson.Should().ContainKey("_lastModifiedDate");
                cacheRow.DocumentJson["_lastModifiedDate"]!
                    .GetValue<string>()
                    .Should()
                    .Be(FormatLastModifiedDate(cacheRow.LastModifiedAt));
                cacheRow.DocumentJson.Should().NotContainKey("_etag");
            });
    }

    [Test]
    public void It_models_projection_failure_fixtures_without_cache_candidates()
    {
        _allFixtures.Should().ContainSingle(fixture => fixture.CaseName == "invariant-missing-school-body");
        var failureFixture = _allFixtures.Single(fixture =>
            fixture.CaseName == "invariant-missing-school-body"
        );

        failureFixture.HasProjectionFailureExpectation.Should().BeTrue();
        failureFixture.ExpectedCacheRow.Should().BeNull();
        failureFixture.ExpectedStreamEtag.Should().BeNull();
        failureFixture.ExpectedPublicCdcDocument.Should().BeNull();

        failureFixture.ExpectedProjectionFailure!.Reason.Should().Be("StableSourceBodyMissing");
        failureFixture.ExpectedProjectionFailure.DocumentId.Should().Be(972101);
        failureFixture.ExpectedProjectionFailure.ResourceName.Should().Be("School");
    }

    [Test]
    public void It_models_collection_property_absence_without_json_null()
    {
        var fixture = MaterializedDocumentFixtureCatalog.LoadCase(
            TestContext.CurrentContext.TestDirectory,
            "school-address-property-absence"
        );

        fixture.SourceSetup.ChildRows.Where(HasNullAddressTypeDescriptorId).Should().ContainSingle();

        var expectedAddresses = fixture.ExpectedCacheRow!.DocumentJson["addresses"]!.AsArray();
        expectedAddresses.Should().HaveCount(2);

        var firstExpectedAddress = expectedAddresses[0]!.AsObject();
        firstExpectedAddress["addressTypeDescriptor"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        var secondExpectedAddress = expectedAddresses[1]!.AsObject();
        secondExpectedAddress.Should().NotContainKey("addressTypeDescriptor");
        secondExpectedAddress["city"]!.GetValue<string>().Should().Be("Dallas");

        var expectedPublicAddresses = fixture.ExpectedPublicCdcDocument!.Document["addresses"]!.AsArray();
        expectedPublicAddresses[1]!.AsObject().Should().NotContainKey("addressTypeDescriptor");
    }

    private static IEnumerable<string> ReferencedManifestPaths(MaterializedDocumentFixtureManifest manifest)
    {
        if (manifest.SourceSetupPath is not null)
        {
            yield return manifest.SourceSetupPath;
        }

        if (manifest.ExpectedCacheRowPath is not null)
        {
            yield return manifest.ExpectedCacheRowPath;
        }

        if (manifest.ExpectedStreamEtagPath is not null)
        {
            yield return manifest.ExpectedStreamEtagPath;
        }

        if (manifest.ExpectedPublicCdcDocumentPath is not null)
        {
            yield return manifest.ExpectedPublicCdcDocumentPath;
        }

        if (manifest.ExpectedProjectionFailurePath is not null)
        {
            yield return manifest.ExpectedProjectionFailurePath;
        }
    }

    private static bool HasTags(MaterializedDocumentFixture fixture, params string[] tags) =>
        Array.TrueForAll(tags, tag => fixture.Manifest.CoverageTags?.Contains(tag) == true);

    private static bool HasNullAddressTypeDescriptorId(MaterializedDocumentSourceTableRow row) =>
        row.Values.TryGetPropertyValue("AddressTypeDescriptor_DescriptorId", out var value) && value is null;

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
