// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Claims.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using CmsComposer = EdFi.DmsConfigurationService.Backend.Claims.ClaimsFragmentComposer;
using SharedComposer = EdFi.DmsConfigurationService.Claims.Composition.ClaimsFragmentComposer;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixtureSource(nameof(Cases))]
public class Given_ClaimsFragmentComposer_unapplied_input(string table, string scenario)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private static readonly string _fixtures = Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "Fixtures",
        "ClaimsComposition",
        "unapplied-input"
    );

    public static IEnumerable<TestFixtureData> Cases =>
        Directory
            .GetFiles(_fixtures, "*-cases.json")
            .Order(StringComparer.Ordinal)
            .SelectMany(file =>
                JsonNode
                    .Parse(File.ReadAllText(file))!
                    .AsArray()
                    .Select(entry => new TestFixtureData(
                        Path.GetFileName(file),
                        entry!["name"]!.GetValue<string>()
                    ))
            );

    private ClaimsCompositionResult _result = null!;
    private ClaimsDocument _cmsDocument = null!;
    private ClaimsDocument _expected = null!;
    private ClaimsDocument _original = null!;
    private ClaimsCompositionDiagnostic[] _diagnostics = null!;

    [SetUp]
    public void Setup()
    {
        JsonNode entry = JsonNode
            .Parse(File.ReadAllText(Path.Combine(_fixtures, table)))!
            .AsArray()
            .Single(entry => entry!["name"]!.GetValue<string>() == scenario)!;
        string basePath = Path.Combine(_fixtures, entry["base"]?.GetValue<string>() ?? "../base.json");
        BaseDocument = ReadDocument(basePath);
        _original = ReadDocument(basePath);
        File.Copy(basePath, Path.Combine(DirectoryPath, "Claims.json"), true);
        string fragmentPath = Path.Combine(DirectoryPath, "input-claims.json");
        File.WriteAllText(fragmentPath, entry["fragment"]!.ToJsonString());
        _expected = ReadDocument(Path.Combine(_fixtures, entry["expected"]!.GetValue<string>()));
        _diagnostics = entry["diagnostics"]!
            .AsArray()
            .Select(diagnostic => new ClaimsCompositionDiagnostic(
                Enum.Parse<ClaimsCompositionDiagnosticCode>(diagnostic!["code"]!.GetValue<string>()),
                fragmentPath,
                diagnostic["claimName"]!.GetValue<string>(),
                diagnostic["claimSetName"]!.GetValue<string>(),
                diagnostic["jsonPath"]!.GetValue<string>()
            ))
            .ToArray();

        _result = new SharedComposer(NullLogger.Instance).ComposeClaimsFromFragments(
            BaseDocument,
            DirectoryPath
        );
        _cmsDocument = Provider(new CmsComposer(NullLogger<CmsComposer>.Instance)).GetClaimsDocumentNodes();
    }

    [Test]
    public void It_preserves_the_exact_composed_document()
    {
        _result.LoadResult.Failures.Should().BeEmpty();
        AssertDocument(_result.LoadResult.Nodes!, _expected);
    }

    [Test]
    public void It_preserves_CMS_continuation_and_schema_valid_output() =>
        AssertDocument(_cmsDocument, _expected);

    [Test]
    public void It_reports_exact_diagnostic_codes_and_original_indexed_context()
    {
        _result.Diagnostics.Should().Equal(_diagnostics);
        _result
            .IsComplete.Should()
            .Be(
                Array.TrueForAll(
                    _diagnostics,
                    d => d.Code == ClaimsCompositionDiagnosticCode.ClaimSetRegistered
                )
            );
    }

    [Test]
    public void It_leaves_the_base_policy_unchanged() => AssertDocument(BaseDocument, _original);

    private static ClaimsDocument ReadDocument(string path)
    {
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        return new(document["claimSets"]!, document["claimsHierarchy"]!);
    }
}
