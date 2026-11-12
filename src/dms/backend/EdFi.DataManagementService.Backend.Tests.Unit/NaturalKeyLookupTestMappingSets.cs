// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The authoritative DS 5.2 mapping set, compiled once per dialect, so natural-key lookup tests run against
/// the real compiled probes.
/// </summary>
internal static class NaturalKeyLookupTestMappingSets
{
    private const string Ds52FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    public static readonly QualifiedResourceName School = new("Ed-Fi", "School");
    public static readonly QualifiedResourceName EducationOrganization = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    public static readonly QualifiedResourceName Program = new("Ed-Fi", "Program");
    public static readonly QualifiedResourceName CourseOffering = new("Ed-Fi", "CourseOffering");
    public static readonly QualifiedResourceName GeneralStudentProgramAssociation = new(
        "Ed-Fi",
        "GeneralStudentProgramAssociation"
    );
    public static readonly QualifiedResourceName ProgramTypeDescriptor = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    private static readonly Lazy<MappingSet> _pgsql = new(() => Compile(SqlDialect.Pgsql));
    private static readonly Lazy<MappingSet> _mssql = new(() => Compile(SqlDialect.Mssql));

    public static MappingSet Ds52(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsql.Value,
            SqlDialect.Mssql => _mssql.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };

    /// <summary>
    /// A lookup target for a concrete or abstract reference target, read from the compiled probes.
    /// </summary>
    public static NaturalKeyLookupTarget ProbeTarget(MappingSet mappingSet, QualifiedResourceName resource) =>
        NaturalKeyLookupTarget.ForProbe(
            mappingSet.NaturalKeyProbeTargets[resource],
            mappingSet.ResourceKeyIdByResource[resource]
        );

    /// <summary>
    /// A lookup target for a descriptor resource.
    /// </summary>
    public static NaturalKeyLookupTarget DescriptorTarget(
        MappingSet mappingSet,
        QualifiedResourceName descriptorResource
    ) =>
        NaturalKeyLookupTarget.ForDescriptor(
            descriptorResource,
            mappingSet.ResourceKeyIdByResource[descriptorResource]
        );

    private static MappingSet Compile(SqlDialect dialect)
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            AppContext.BaseDirectory,
            Ds52FixtureRelativePath
        );
        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory);
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(effectiveSchemaSet, dialect);

        return new MappingSetCompiler().Compile(emission.ModelSet);
    }
}
