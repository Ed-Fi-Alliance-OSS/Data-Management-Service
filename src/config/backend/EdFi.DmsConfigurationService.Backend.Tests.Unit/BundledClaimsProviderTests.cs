// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Claims;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture(null, "ds52")]
[TestFixture("", "ds52")]
[TestFixture(" \t\r\n", "ds52")]
[TestFixture("5.2", "ds52")]
[TestFixture(" 5.2 ", "ds52")]
[TestFixture("6.1", "ds61")]
[TestFixture(" 6.1 ", "ds61")]
public class Given_BundledClaimsProvider_version_selection(string? version, string expectedFolder)
{
    private ClaimsLoadResult _sharedResult = null!;
    private ClaimsDocument _cmsDocument = null!;
    private JsonNode _expected = null!;

    [SetUp]
    public void Arrange_and_act()
    {
        string path = Path.Combine(
            Given_E2E_Test_Fragments.FindRepositoryRoot(),
            "src/config/backend/EdFi.DmsConfigurationService.Claims/Claims/Standards",
            expectedFolder,
            "Claims.json"
        );
        _expected = JsonNode.Parse(File.ReadAllText(path))!;
        _sharedResult = BundledClaimsProvider.Load(version);
        ClaimsOptions options = new() { ClaimsSource = ClaimsSource.Embedded };
        if (version is not null)
        {
            options.DataStandardVersion = version;
        }
        ClaimsProvider provider = new(
            NullLogger<ClaimsProvider>.Instance,
            Options.Create(options),
            new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
            A.Fake<IClaimsFragmentComposer>()
        );
        _cmsDocument = provider.GetClaimsDocumentNodes();
    }

    [Test]
    public void It_loads_the_exact_selected_base_without_failures()
    {
        _sharedResult.Failures.Should().BeEmpty();
        _sharedResult.Nodes.Should().NotBeNull();
        JsonNode.DeepEquals(_sharedResult.Nodes!.ClaimSetsNode, _expected["claimSets"]).Should().BeTrue();
        JsonNode
            .DeepEquals(_sharedResult.Nodes.ClaimsHierarchyNode, _expected["claimsHierarchy"])
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_preserves_the_validated_CMS_document()
    {
        JsonNode.DeepEquals(_cmsDocument.ClaimSetsNode, _expected["claimSets"]).Should().BeTrue();
        JsonNode.DeepEquals(_cmsDocument.ClaimsHierarchyNode, _expected["claimsHierarchy"]).Should().BeTrue();
    }
}

[TestFixture]
public class Given_BundledClaimsProvider_unsupported_version
{
    private ClaimsLoadResult _sharedResult = null!;
    private ClaimsProvider _provider = null!;
    private ClaimsLoadResult _cmsResult = null!;

    [SetUp]
    public void Arrange_and_act()
    {
        _sharedResult = BundledClaimsProvider.Load(" 9.9 ");
        _provider = new ClaimsProvider(
            NullLogger<ClaimsProvider>.Instance,
            Options.Create(new ClaimsOptions { DataStandardVersion = " 9.9 " }),
            new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
            A.Fake<IClaimsFragmentComposer>()
        );
        _cmsResult = _provider.LoadClaimsFromSource();
    }

    [Test]
    public void It_returns_the_same_structured_configuration_failure_to_CMS()
    {
        _sharedResult.Nodes.Should().BeNull();
        _sharedResult
            .Failures.Should()
            .Equal(
                new ClaimsFailure(
                    "Configuration",
                    "Could not load embedded Claims.json for data standard version '9.9' "
                        + "(resource 'EdFi.DmsConfigurationService.Claims.Claims.Standards.ds99.Claims.json')"
                )
            );
        _cmsResult.Nodes.Should().BeNull();
        _cmsResult.Failures.Should().Equal(_sharedResult.Failures);
    }

    [Test]
    public void It_preserves_CMS_initial_load_failure_state()
    {
        Action load = () => _provider.GetClaimsDocumentNodes();
        load.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Claims loading failed. Check ClaimsFailures for details.");
        _provider.IsClaimsValid.Should().BeFalse();
        _provider.ClaimsFailures.Should().Equal(_sharedResult.Failures);
    }
}
