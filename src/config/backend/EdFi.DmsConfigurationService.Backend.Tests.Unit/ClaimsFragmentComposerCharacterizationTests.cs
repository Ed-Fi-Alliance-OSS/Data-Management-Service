// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedClaimsFragmentComposer = EdFi.DmsConfigurationService.Claims.Composition.ClaimsFragmentComposer;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture("empty")]
[TestFixture("missing")]
[TestFixture("retired")]
[TestFixture("first-wins")]
[TestFixture("reverse-wins")]
[TestFixture("base-wins")]
[TestFixture("invalid-registration")]
[TestFixture("empty-registration")]
[TestFixture("unmatched")]
[TestFixture("discarded-children")]
[TestFixture("malformed")]
[TestFixture("no-resource-claims")]
public class Given_ClaimsFragmentComposer_characterization(string scenario)
{
    private string _directory = null!;
    private ClaimsDocument _base = null!;
    private ClaimsDocument _expected = null!;
    private ClaimsLoadResult _result = null!;
    private ClaimsLoadResult _sharedResult = null!;
    private ClaimsProvider _provider = null!;
    private ClaimsDocument _cmsResult = null!;
    private Action _load = null!;

    [SetUp]
    public void Setup()
    {
        string fixtures = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "ClaimsComposition"
        );
        _directory = Path.Combine(Path.GetTempPath(), $"claims-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _base = ReadDocument(Path.Combine(fixtures, "base.json"));
        _expected = ReadDocument(Path.Combine(fixtures, scenario, "expected.json"));
        string fragments = Path.Combine(fixtures, scenario, "fragments");
        if (Directory.Exists(fragments))
        {
            foreach (string source in Directory.GetFiles(fragments, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(_directory, Path.GetRelativePath(fragments, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        ClaimsFragmentComposer composer = new(NullLogger<ClaimsFragmentComposer>.Instance);
        _result = composer.ComposeClaimsFromFragments(
            _base,
            scenario == "missing" ? Path.Combine(_directory, "absent") : _directory
        );
        _sharedResult = new SharedClaimsFragmentComposer(
            NullLogger<SharedClaimsFragmentComposer>.Instance
        ).ComposeClaimsFromFragments(
            _base,
            scenario == "missing" ? Path.Combine(_directory, "absent") : _directory
        );
        File.Copy(Path.Combine(fixtures, "base.json"), Path.Combine(_directory, "Claims.json"), true);
        _provider = new ClaimsProvider(
            NullLogger<ClaimsProvider>.Instance,
            Options.Create(
                new ClaimsOptions
                {
                    ClaimsSource = ClaimsSource.Filesystem,
                    ClaimsDirectory = scenario == "missing" ? Path.Combine(_directory, "absent") : _directory,
                }
            ),
            new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
            composer
        );
        _load = () => _cmsResult = _provider.GetClaimsDocumentNodes();
        // Keep validation failures observable without preventing the composition assertions.
        if (scenario is not ("empty-registration" or "missing"))
        {
            _load();
        }
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void It_preserves_the_complete_composition_or_fatal_failure()
    {
        if (scenario == "malformed")
        {
            _result.Nodes.Should().BeNull();
            _result.Failures.Select(f => f.FailureType).Should().Equal("JsonError");
        }
        else
        {
            _result.Failures.Should().BeEmpty();
            AssertDocument(_result.Nodes!, _expected);
        }
    }

    [Test]
    public void It_preserves_the_complete_CMS_result_and_validation_behavior()
    {
        if (scenario == "missing")
        {
            _load
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("Claims loading failed. Check ClaimsFailures for details.");
            _provider.IsClaimsValid.Should().BeFalse();
            _provider
                .ClaimsFailures.Should()
                .BeEquivalentTo(
                    new[]
                    {
                        new ClaimsFailure(
                            "FileSystem",
                            $"The directory {Path.Combine(_directory, "absent")} does not exist"
                        ),
                    }
                );
        }
        else if (scenario == "empty-registration")
        {
            _load
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("Claims validation failed. Cannot proceed with invalid claims.");
            _provider.IsClaimsValid.Should().BeFalse();
            _provider
                .ClaimsFailures.Select(f => f.FailureType)
                .Should()
                .OnlyContain(kind => kind == "Validation");
            _provider
                .ClaimsFailures.Should()
                .Contain(f => f.Path == "$.claimsHierarchy.0.claims.0.claimSets.1.name");
        }
        else
        {
            AssertDocument(_cmsResult, _expected);
            _provider.IsClaimsValid.Should().BeTrue();
            _provider.ClaimsFailures.Should().BeEmpty();
        }
    }

    [Test]
    public void It_uses_the_same_shared_result_and_failure_contract()
    {
        _sharedResult
            .Failures.Should()
            .BeEquivalentTo(_result.Failures, options => options.Excluding(f => f.Exception));
        if (scenario == "malformed")
        {
            _sharedResult.Nodes.Should().BeNull();
        }
        else
        {
            AssertDocument(_sharedResult.Nodes!, _expected);
        }
    }

    [Test]
    public void It_leaves_the_base_document_unchanged()
    {
        ClaimsDocument original = ReadDocument(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "ClaimsComposition",
                "base.json"
            )
        );
        AssertDocument(_base, original);
    }

    private static ClaimsDocument ReadDocument(string path)
    {
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        return new(document["claimSets"]!, document["claimsHierarchy"]!);
    }

    private static void AssertDocument(ClaimsDocument actual, ClaimsDocument expected)
    {
        JsonNode
            .DeepEquals(actual.ClaimSetsNode, expected.ClaimSetsNode)
            .Should()
            .BeTrue("the complete catalog must match the literal fixture");
        JsonNode
            .DeepEquals(actual.ClaimsHierarchyNode, expected.ClaimsHierarchyNode)
            .Should()
            .BeTrue(
                "all actions, strategies, ordering and discarded children must match the literal fixture"
            );
    }
}
