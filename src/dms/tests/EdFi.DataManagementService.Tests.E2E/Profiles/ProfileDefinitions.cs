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
    /// Profile for School with extension filtering using IncludeOnly mode within the extension.
    /// Only includes isExemplary from the Sample extension, excludes cteProgramService.
    /// </summary>
    public const string SchoolExtensionIncludeOnlyName = "E2E-Test-School-Extension-IncludeOnly";

    public const string SchoolExtensionIncludeOnlyXml = """
        <Profile name="E2E-Test-School-Extension-IncludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll">
                    <Extension name="sample" memberSelection="IncludeOnly">
                        <Property name="isExemplary"/>
                    </Extension>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with extension filtering using ExcludeOnly mode within the extension.
    /// Excludes isExemplary from the Sample extension, keeps cteProgramService.
    /// </summary>
    public const string SchoolExtensionExcludeOnlyName = "E2E-Test-School-Extension-ExcludeOnly";

    public const string SchoolExtensionExcludeOnlyXml = """
        <Profile name="E2E-Test-School-Extension-ExcludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll">
                    <Extension name="sample" memberSelection="ExcludeOnly">
                        <Property name="isExemplary"/>
                    </Extension>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with IncludeOnly at parent level but no extension rule.
    /// Per design doc section 7.5: When parent uses IncludeOnly, extensions without
    /// explicit rules are EXCLUDED (not explicitly included).
    /// </summary>
    public const string SchoolIncludeOnlyNoExtensionRuleName = "E2E-Test-School-IncludeOnly-NoExtensionRule";

    public const string SchoolIncludeOnlyNoExtensionRuleXml = """
        <Profile name="E2E-Test-School-IncludeOnly-NoExtensionRule">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeOnly">
                    <Property name="nameOfInstitution"/>
                    <Property name="educationOrganizationCategories"/>
                    <Property name="gradeLevels"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with ExcludeOnly at parent level but no extension rule.
    /// Per design doc section 7.5: When parent uses ExcludeOnly, extensions without
    /// explicit rules are INCLUDED (not explicitly excluded).
    /// </summary>
    public const string SchoolExcludeOnlyNoExtensionRuleName = "E2E-Test-School-ExcludeOnly-NoExtensionRule";

    public const string SchoolExcludeOnlyNoExtensionRuleXml = """
        <Profile name="E2E-Test-School-ExcludeOnly-NoExtensionRule">
            <Resource name="School">
                <ReadContentType memberSelection="ExcludeOnly">
                    <Property name="shortNameOfInstitution"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    // ================================================================================
    // WRITE FILTERING PROFILES
    // These profiles have restricted WriteContentType for testing write-side filtering.
    // Fields not allowed by the write profile are silently stripped from the request.
    // ================================================================================

    /// <summary>
    /// Profile for School with IncludeOnly WriteContentType - only allows writing
    /// nameOfInstitution, shortNameOfInstitution, and required collections.
    /// Other fields like webSite will be silently stripped from POST/PUT requests.
    /// </summary>
    public const string SchoolWriteIncludeOnlyName = "E2E-Test-School-Write-IncludeOnly";

    public const string SchoolWriteIncludeOnlyXml = """
        <Profile name="E2E-Test-School-Write-IncludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeOnly">
                    <Property name="nameOfInstitution"/>
                    <Property name="shortNameOfInstitution"/>
                    <Collection name="educationOrganizationCategories" memberSelection="IncludeAll"/>
                    <Collection name="gradeLevels" memberSelection="IncludeAll"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with ExcludeOnly WriteContentType - excludes webSite and
    /// shortNameOfInstitution from being written. These fields will be silently stripped.
    /// </summary>
    public const string SchoolWriteExcludeOnlyName = "E2E-Test-School-Write-ExcludeOnly";

    public const string SchoolWriteExcludeOnlyXml = """
        <Profile name="E2E-Test-School-Write-ExcludeOnly">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="ExcludeOnly">
                    <Property name="webSite"/>
                    <Property name="shortNameOfInstitution"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with collection item filter on WriteContentType.
    /// Only allows writing grade levels matching "Ninth grade" descriptor.
    /// Other grade levels will be silently stripped from the request.
    /// </summary>
    public const string SchoolWriteGradeLevelFilterName = "E2E-Test-School-Write-GradeLevelFilter";

    public const string SchoolWriteGradeLevelFilterXml = """
        <Profile name="E2E-Test-School-Write-GradeLevelFilter">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll">
                    <Collection name="gradeLevels" memberSelection="IncludeAll">
                        <Filter propertyName="gradeLevelDescriptor" filterMode="IncludeOnly">
                            <Value>uri://ed-fi.org/GradeLevelDescriptor#Ninth grade</Value>
                        </Filter>
                    </Collection>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    // ================================================================================
    // CREATABILITY VALIDATION PROFILES
    // These profiles have WriteContentType that excludes required fields.
    // POST requests with these profiles should fail with data-policy-enforced error.
    // ================================================================================

    /// <summary>
    /// Profile for School with WriteContentType that excludes the required nameOfInstitution field.
    /// POST requests with this profile should fail with a data-policy-enforced error.
    /// PUT requests should succeed because existing resources already have the value.
    /// </summary>
    public const string SchoolWriteExcludeRequiredName = "E2E-Test-School-Write-ExcludeRequired";

    public const string SchoolWriteExcludeRequiredXml = """
        <Profile name="E2E-Test-School-Write-ExcludeRequired">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="ExcludeOnly">
                    <Property name="nameOfInstitution"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with WriteContentType that excludes the required educationOrganizationCategories collection.
    /// Uses Property element (not Collection element) to fully exclude the collection.
    /// POST requests with this profile should fail with a data-policy-enforced error.
    /// </summary>
    public const string SchoolWriteExcludeRequiredCollectionName =
        "E2E-Test-School-Write-ExcludeRequiredCollection";

    public const string SchoolWriteExcludeRequiredCollectionXml = """
        <Profile name="E2E-Test-School-Write-ExcludeRequiredCollection">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="ExcludeOnly">
                    <Property name="educationOrganizationCategories"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with WriteContentType using IncludeOnly that omits the required nameOfInstitution field.
    /// POST requests with this profile should fail with a data-policy-enforced error.
    /// </summary>
    public const string SchoolWriteIncludeOnlyMissingRequiredName =
        "E2E-Test-School-Write-IncludeOnlyMissingRequired";

    public const string SchoolWriteIncludeOnlyMissingRequiredXml = """
        <Profile name="E2E-Test-School-Write-IncludeOnlyMissingRequired">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeOnly">
                    <Property name="shortNameOfInstitution"/>
                    <Property name="webSite"/>
                    <Collection name="educationOrganizationCategories" memberSelection="IncludeAll"/>
                    <Collection name="gradeLevels" memberSelection="IncludeAll"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with WriteContentType using IncludeAll.
    /// POST requests should succeed because no required fields are excluded.
    /// </summary>
    public const string SchoolWriteIncludeAllName = "E2E-Test-School-Write-IncludeAll";

    public const string SchoolWriteIncludeAllXml = """
        <Profile name="E2E-Test-School-Write-IncludeAll">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with WriteContentType that has a CollectionRule on the required gradeLevels collection.
    /// The collection is filtered (not excluded), so POST requests should succeed.
    /// </summary>
    public const string SchoolWriteRequiredCollectionWithRuleName =
        "E2E-Test-School-Write-RequiredCollectionWithRule";

    public const string SchoolWriteRequiredCollectionWithRuleXml = """
        <Profile name="E2E-Test-School-Write-RequiredCollectionWithRule">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll">
                    <Collection name="gradeLevels" memberSelection="IncludeAll">
                        <Filter propertyName="gradeLevelDescriptor" filterMode="IncludeOnly">
                            <Value>uri://ed-fi.org/GradeLevelDescriptor#Ninth grade</Value>
                        </Filter>
                    </Collection>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    // ================================================================================
    // PUT MERGE PROFILES
    // These profiles test that excluded fields are preserved from existing documents
    // during PUT operations (recursive merging functionality).
    // ================================================================================

    /// <summary>
    /// Profile for School with WriteContentType that excludes a non-key property within collection items.
    /// The addresses collection excludes nameOfCounty - PUT requests should preserve nameOfCounty from existing doc.
    /// Note: Only non-key properties can be preserved during PUT. Key properties (addressTypeDescriptor, city,
    /// postalCode, stateAbbreviationDescriptor, streetNumberName) form the collection item identity and cannot
    /// be excluded - attempting to do so would cause a DataPolicyException.
    /// </summary>
    public const string SchoolWriteAddressExcludeNameOfCountyName =
        "E2E-Test-School-Write-AddressExcludeNameOfCounty";

    public const string SchoolWriteAddressExcludeNameOfCountyXml = """
        <Profile name="E2E-Test-School-Write-AddressExcludeNameOfCounty">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll">
                    <Collection name="addresses" memberSelection="ExcludeOnly">
                        <Property name="nameOfCounty"/>
                    </Collection>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    /// <summary>
    /// Profile for School with WriteContentType using collection item filter.
    /// Only allows modifying Ninth grade items - other grade levels should be preserved on PUT.
    /// </summary>
    public const string SchoolWriteGradeLevelFilterPreserveName =
        "E2E-Test-School-Write-GradeLevelFilterPreserve";

    public const string SchoolWriteGradeLevelFilterPreserveXml = """
        <Profile name="E2E-Test-School-Write-GradeLevelFilterPreserve">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
                <WriteContentType memberSelection="IncludeAll">
                    <Collection name="gradeLevels" memberSelection="IncludeAll">
                        <Filter propertyName="gradeLevelDescriptor" filterMode="IncludeOnly">
                            <Value>uri://ed-fi.org/GradeLevelDescriptor#Ninth grade</Value>
                        </Filter>
                    </Collection>
                </WriteContentType>
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
            (SchoolExtensionIncludeOnlyName, SchoolExtensionIncludeOnlyXml),
            (SchoolExtensionExcludeOnlyName, SchoolExtensionExcludeOnlyXml),
            (SchoolIncludeOnlyNoExtensionRuleName, SchoolIncludeOnlyNoExtensionRuleXml),
            (SchoolExcludeOnlyNoExtensionRuleName, SchoolExcludeOnlyNoExtensionRuleXml),
            // Write filtering profiles
            (SchoolWriteIncludeOnlyName, SchoolWriteIncludeOnlyXml),
            (SchoolWriteExcludeOnlyName, SchoolWriteExcludeOnlyXml),
            (SchoolWriteGradeLevelFilterName, SchoolWriteGradeLevelFilterXml),
            // Creatability validation profiles
            (SchoolWriteExcludeRequiredName, SchoolWriteExcludeRequiredXml),
            (SchoolWriteExcludeRequiredCollectionName, SchoolWriteExcludeRequiredCollectionXml),
            (SchoolWriteIncludeOnlyMissingRequiredName, SchoolWriteIncludeOnlyMissingRequiredXml),
            (SchoolWriteIncludeAllName, SchoolWriteIncludeAllXml),
            (SchoolWriteRequiredCollectionWithRuleName, SchoolWriteRequiredCollectionWithRuleXml),
            // PUT merge profiles
            (SchoolWriteAddressExcludeNameOfCountyName, SchoolWriteAddressExcludeNameOfCountyXml),
            (SchoolWriteGradeLevelFilterPreserveName, SchoolWriteGradeLevelFilterPreserveXml),
        ];
}
