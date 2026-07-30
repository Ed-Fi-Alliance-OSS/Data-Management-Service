// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
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
                    SourceSetupPath: "source-setup.json",
                    ExpectedCacheRowPath: "expected-cache-row.json",
                    ExpectedStreamEtagPath: "expected-stream-etag.json",
                    ExpectedPublicCdcDocumentPath: "expected-public-cdc-document.json"
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
                        "2026-01-15T17:18:19.1234567Z",
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
        _fixture.SourceSetup.ReferenceRows.Should().BeEmpty();
        _fixture
            .SourceSetup.ReferentialIdentityRows.Should()
            .ContainSingle(row => row.DocumentId == 1001 && row.ResourceKeyId == 1);
    }

    [Test]
    public void It_keeps_cache_row_json_free_of_etag_and_returns_stream_etag_separately()
    {
        _fixture.ExpectedCacheRow.DocumentId.Should().Be(1001);
        _fixture.ExpectedCacheRow.DocumentUuid.Should().Be("11111111-1111-1111-1111-111111111111");
        _fixture.ExpectedCacheRow.ProjectName.Should().Be("Ed-Fi");
        _fixture.ExpectedCacheRow.ResourceName.Should().Be("School");
        _fixture.ExpectedCacheRow.ResourceVersion.Should().Be("5.3.0");
        _fixture.ExpectedCacheRow.ContentVersion.Should().Be(42);
        _fixture.ExpectedCacheRow.StreamEtag.Should().Be("\"42-cache-projection\"");
        _fixture.ExpectedStreamEtag.Should().Be(_fixture.ExpectedCacheRow.StreamEtag);
        _fixture.ExpectedCacheRow.DocumentJson.ContainsKey("_etag").Should().BeFalse();
    }

    [Test]
    public void It_models_the_public_cdc_document_as_cache_json_plus_stream_etag()
    {
        var expectedPublicDocument = JsonNode
            .Parse(_fixture.ExpectedCacheRow.DocumentJson.ToJsonString())!
            .AsObject();
        expectedPublicDocument["_etag"] = _fixture.ExpectedStreamEtag;

        JsonNode
            .DeepEquals(_fixture.ExpectedPublicCdcDocument!.Document, expectedPublicDocument)
            .Should()
            .BeTrue();
    }

    private static IEnumerable<string> ReferencedManifestPaths(MaterializedDocumentFixtureManifest manifest)
    {
        yield return manifest.SourceSetupPath;
        yield return manifest.ExpectedCacheRowPath;
        yield return manifest.ExpectedStreamEtagPath;

        if (manifest.ExpectedPublicCdcDocumentPath is not null)
        {
            yield return manifest.ExpectedPublicCdcDocumentPath;
        }
    }
}
