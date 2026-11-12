// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The natural-key resolver throws at request time when a reference's identity paths differ from its target's
/// probe, or the target has no probe. This sweep proves no committed authoritative schema can reach that throw:
/// every document reference resolves through a compiled probe that declares exactly the reference's identity
/// paths.
/// </summary>
[TestFixture("ds-5.2", SqlDialect.Pgsql)]
[TestFixture("ds-5.2", SqlDialect.Mssql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Pgsql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Mssql)]
[TestFixture("sample", SqlDialect.Pgsql)]
[TestFixture("sample", SqlDialect.Mssql)]
[Parallelizable]
public class Given_Authoritative_Document_References_And_Their_Natural_Key_Probes(
    string fixture,
    SqlDialect dialect
)
{
    private List<DocumentReferenceBinding> _bindings = null!;
    private List<string> _targetsWithoutProbe = null!;
    private List<string> _identityMismatches = null!;

    [SetUp]
    public void Setup()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            AppContext.BaseDirectory,
            $"src/dms/backend/Fixtures/authoritative/{fixture}"
        );
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(
            EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory),
            dialect
        );
        var mappingSet = new MappingSetCompiler().Compile(emission.ModelSet);

        _bindings =
        [
            .. mappingSet.Model.ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.DocumentReferenceBindings
            ),
        ];
        _targetsWithoutProbe = [];
        _identityMismatches = [];

        foreach (var binding in _bindings)
        {
            var location = $"{binding.Table}.{binding.FkColumn.Value} -> {binding.TargetResource}";

            if (!mappingSet.NaturalKeyProbeTargets.TryGetValue(binding.TargetResource, out var probe))
            {
                _targetsWithoutProbe.Add(location);
                continue;
            }

            var referencePaths = binding
                .IdentityBindings.Select(identity => identity.IdentityJsonPath.Canonical)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var probePaths = probe
                .KeyColumns.Select(keyColumn => keyColumn.IdentityJsonPath.Canonical)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (!referencePaths.SequenceEqual(probePaths, StringComparer.Ordinal))
            {
                _identityMismatches.Add(
                    $"{location}: reference [{string.Join(", ", referencePaths)}] vs probe "
                        + $"[{string.Join(", ", probePaths)}]"
                );
            }
        }
    }

    [Test]
    public void It_sweeps_the_fixture_document_references()
    {
        _bindings.Should().NotBeEmpty();
    }

    [Test]
    public void It_compiles_a_probe_for_every_document_reference_target()
    {
        _targetsWithoutProbe.Should().BeEmpty();
    }

    [Test]
    public void It_matches_every_document_reference_identity_to_its_probe_key_paths()
    {
        _identityMismatches.Should().BeEmpty();
    }
}
