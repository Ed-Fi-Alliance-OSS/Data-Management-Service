// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Profiles;

/// <summary>
/// Static profile XML definitions used for E2E testing.
/// These profiles are created once at test run startup and reused across all profile tests.
/// All profiles include WriteContentType with IncludeAll to allow creating test data.
/// </summary>
public static class ProfileDefinitions
{
    /// <summary>
    /// Profile for School with IncludeOnly mode - only includes nameOfInstitution and webSite.
    /// Identity fields (id, schoolId) are always included regardless of profile rules.
    /// </summary>
    public const string SchoolIncludeOnlyName = "E2E-Test-School-IncludeOnly";

    public const string SchoolIncludeOnlyXml = """
        <Profile name="E2E-Test-School-IncludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeOnly">
                    <Property name="nameOfInstitution"/>
                    <Property name="webSite"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with ExcludeOnly mode - excludes shortNameOfInstitution.
    /// All other fields will be included.
    /// </summary>
    public const string SchoolExcludeOnlyName = "E2E-Test-School-ExcludeOnly";

    public const string SchoolExcludeOnlyXml = """
        <Profile name="E2E-Test-School-ExcludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="ExcludeOnly">
                    <Property name="shortNameOfInstitution"/>
                    <Property name="webSite"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with IncludeAll mode - includes all fields.
    /// This is effectively a pass-through profile with no filtering.
    /// </summary>
    public const string SchoolIncludeAllName = "E2E-Test-School-IncludeAll";

    public const string SchoolIncludeAllXml = """
        <Profile name="E2E-Test-School-IncludeAll">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School that filters gradeLevels collection items by descriptor.
    /// Only includes grade levels matching "Ninth grade" descriptor.
    /// </summary>
    public const string SchoolGradeLevelFilterName = "E2E-Test-School-GradeLevelFilter";

    public const string SchoolGradeLevelFilterXml = """
        <Profile name="E2E-Test-School-GradeLevelFilter">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll">
                    <Collection name="gradeLevels" memberSelection="IncludeAll">
                        <Filter propertyName="gradeLevelDescriptor" filterMode="IncludeOnly">
                            <Value>uri://ed-fi.org/GradeLevelDescriptor#Ninth grade</Value>
                        </Filter>
                    </Collection>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School that excludes specific grade levels from the collection.
    /// Excludes grade levels matching "Tenth grade" descriptor.
    /// </summary>
    public const string SchoolGradeLevelExcludeFilterName = "E2E-Test-School-GradeLevelExcludeFilter";

    public const string SchoolGradeLevelExcludeFilterXml = """
        <Profile name="E2E-Test-School-GradeLevelExcludeFilter">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll">
                    <Collection name="gradeLevels" memberSelection="IncludeAll">
                        <Filter propertyName="gradeLevelDescriptor" filterMode="ExcludeOnly">
                            <Value>uri://ed-fi.org/GradeLevelDescriptor#Tenth grade</Value>
                        </Filter>
                    </Collection>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Second IncludeOnly profile for School - used to test multiple profiles scenario.
    /// Includes different fields than the first IncludeOnly profile.
    /// </summary>
    public const string SchoolIncludeOnlyAltName = "E2E-Test-School-IncludeOnly-Alt";

    public const string SchoolIncludeOnlyAltXml = """
        <Profile name="E2E-Test-School-IncludeOnly-Alt">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeOnly">
                    <Property name="nameOfInstitution"/>
                    <Property name="shortNameOfInstitution"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Returns all profile definitions as name-XML pairs for bulk creation.
    /// </summary>
    public static IReadOnlyList<(string Name, string Xml)> AllProfiles =>
        [
            (SchoolIncludeOnlyName, SchoolIncludeOnlyXml),
            (SchoolExcludeOnlyName, SchoolExcludeOnlyXml),
            (SchoolIncludeAllName, SchoolIncludeAllXml),
            (SchoolGradeLevelFilterName, SchoolGradeLevelFilterXml),
            (SchoolGradeLevelExcludeFilterName, SchoolGradeLevelExcludeFilterXml),
            (SchoolIncludeOnlyAltName, SchoolIncludeOnlyAltXml),
        ];
}
