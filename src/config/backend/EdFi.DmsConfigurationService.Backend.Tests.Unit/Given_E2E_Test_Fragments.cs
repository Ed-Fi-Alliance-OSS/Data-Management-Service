// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Composes the embedded base claims with the default extension fragments (004/005) and the
/// test-owned E2E fragments, as the staged E2E claims workspace does, and proves the result matches
/// the E2E grants the embedded Claims.json carried before the E2E claim sets were removed from it.
/// </summary>
[TestFixture("ds52")]
[TestFixture("ds61")]
public class Given_E2E_Test_Fragments(string standardFolder)
{
    private static readonly string[] E2EClaimSetNames =
    [
        "E2E-NameSpaceBasedClaimSet",
        "E2E-NoFurtherAuthRequiredClaimSet",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
        "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet",
        "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet",
        "E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet",
    ];

    // Snapshot of every E2E- grant in the embedded ds52 and ds61 Claims.json (identical in both)
    // before the E2E claim sets moved to test-owned fragments, formatted as
    // "claimSet|claim|action[strategy+strategy];...". MixedStrategy is Read only.
    private static readonly string[] PreChangeEmbeddedE2EGrants =
    [
        "E2E-NameSpaceBasedClaimSet|http://ed-fi.org/identity/claims/ed-fi/absenceEventCategoryDescriptor|Create[NamespaceBased];Read[NamespaceBased];Update[NamespaceBased];Delete[NamespaceBased];ReadChanges[NamespaceBased]",
        "E2E-NameSpaceBasedClaimSet|http://ed-fi.org/identity/claims/ed-fi/schoolYearType|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NameSpaceBasedClaimSet|http://ed-fi.org/identity/claims/ed-fi/survey|Create[NamespaceBased];Read[NamespaceBased];Update[NamespaceBased];Delete[NamespaceBased];ReadChanges[NamespaceBased]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/absenceEventCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicSubjectDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicWeek|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/addressTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/administrationEnvironmentDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentIdentificationSystemDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentItemCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentItemResultDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentItem|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessmentReportingMethodDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/assessment|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/bellSchedule|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/calendarDate|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/calendarEventDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/calendarTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/calendar|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/classPeriod|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/contact|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/contentClassDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/courseIdentificationSystemDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/courseOffering|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/course|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/credentialTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/credential|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/disabilityDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/educationContent|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/educationOrganizationCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/gradeLevelDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/gradeTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/grade|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/gradingPeriodDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/gradingPeriod|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/graduationPlanTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/graduationPlan|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/immunizationTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/languageDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/learningStandardScopeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/learningStandard|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgencyCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgency|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/performanceLevelDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/postSecondaryEvent|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitution|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/programTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/program|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/reportCard|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/responseIndicatorDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/resultDatatypeTypeDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/retestIndicatorDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/schoolYearType|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/school|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/section|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/session|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/staff|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/stateAbbreviationDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentAssessment|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentCTEProgramAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentContactAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentHealth|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentProgramAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/studentSectionAssociation|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/student|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/survey|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-NoFurtherAuthRequiredClaimSet|http://ed-fi.org/identity/claims/ed-fi/termDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicWeek|Create[RelationshipsWithEdOrgsOnly];Read[RelationshipsWithEdOrgsOnly];Update[RelationshipsWithEdOrgsOnly];Delete[RelationshipsWithEdOrgsOnly];ReadChanges[RelationshipsWithEdOrgsOnly]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/bellSchedule|Create[RelationshipsWithEdOrgsOnly];Read[RelationshipsWithEdOrgsOnly];Update[RelationshipsWithEdOrgsOnly];Delete[RelationshipsWithEdOrgsOnly];ReadChanges[RelationshipsWithEdOrgsOnly]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/classPeriod|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/educationOrganizationCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/gradeLevelDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgencyCategoryDescriptor|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgency|Create[RelationshipsWithEdOrgsOnly];Read[RelationshipsWithEdOrgsOnly];Update[RelationshipsWithEdOrgsOnly];Delete[RelationshipsWithEdOrgsOnly];ReadChanges[RelationshipsWithEdOrgsOnly]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/school|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet|http://ed-fi.org/identity/claims/ed-fi/stateEducationAgency|Create[NoFurtherAuthorizationRequired];Read[NoFurtherAuthorizationRequired];Update[NoFurtherAuthorizationRequired];Delete[NoFurtherAuthorizationRequired];ReadChanges[NoFurtherAuthorizationRequired]",
        "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicWeek|Create[RelationshipsWithEdOrgsOnlyInverted];Read[RelationshipsWithEdOrgsOnlyInverted];Update[RelationshipsWithEdOrgsOnlyInverted];Delete[RelationshipsWithEdOrgsOnlyInverted];ReadChanges[RelationshipsWithEdOrgsOnlyInverted]",
        "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgency|Create[RelationshipsWithEdOrgsOnlyInverted];Read[RelationshipsWithEdOrgsOnlyInverted];Update[RelationshipsWithEdOrgsOnlyInverted];Delete[RelationshipsWithEdOrgsOnlyInverted];ReadChanges[RelationshipsWithEdOrgsOnlyInverted]",
        "E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicWeek|Read[RelationshipsWithEdOrgsOnly+OwnershipBased]",
        "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet|http://ed-fi.org/identity/claims/ed-fi/academicWeek|Create[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Read[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Update[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Delete[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];ReadChanges[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted]",
        "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet|http://ed-fi.org/identity/claims/ed-fi/localEducationAgency|Create[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Read[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Update[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];Delete[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted];ReadChanges[RelationshipsWithEdOrgsOnly+RelationshipsWithEdOrgsOnlyInverted]",
    ];

    private JsonArray _composedClaimSets = null!;
    private JsonNode _composedHierarchy = null!;
    private IList<ClaimSetMetadata> _metadata = null!;
    private List<ReadinessCheck> _readinessChecks = null!;

    [SetUp]
    public async Task Setup()
    {
        string repositoryRoot = FindRepositoryRoot();
        string e2eFragmentsPath = Path.Combine(
            repositoryRoot,
            "src",
            "config",
            "tests",
            "EdFi.DmsConfigurationService.Tests.E2E",
            "TestData",
            "Claims",
            "Fragments"
        );
        string defaultFragmentsPath = Path.Combine(
            repositoryRoot,
            "src",
            "config",
            "backend",
            "EdFi.DmsConfigurationService.Backend",
            "Deploy",
            "AdditionalClaimsets"
        );

        ClaimsDocument composed = Compose(defaultFragmentsPath, e2eFragmentsPath);
        _composedClaimSets = composed.ClaimSetsNode.AsArray();
        _composedHierarchy = composed.ClaimsHierarchyNode;

        _metadata = await CreateAuthorizationMetadata();

        _readinessChecks =
            JsonSerializer.Deserialize<List<ReadinessCheck>>(
                await File.ReadAllTextAsync(Path.Combine(e2eFragmentsPath, "e2e-readiness-checks.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("e2e-readiness-checks.json parsed to null.");
    }

    [Test]
    public void It_reproduces_the_pre_change_embedded_E2E_grants()
    {
        E2EGrants(_composedHierarchy).Should().BeEquivalentTo(PreChangeEmbeddedE2EGrants);
    }

    [Test]
    public void It_registers_the_E2E_claim_sets_as_system_reserved()
    {
        List<JsonObject> e2eClaimSets =
        [
            .. _composedClaimSets
                .OfType<JsonObject>()
                .Where(claimSet =>
                    claimSet["claimSetName"]!.GetValue<string>().StartsWith("E2E-", StringComparison.Ordinal)
                ),
        ];

        e2eClaimSets
            .Select(claimSet => claimSet["claimSetName"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(E2EClaimSetNames);
        e2eClaimSets.Should().OnlyContain(claimSet => claimSet["isSystemReserved"]!.GetValue<bool>());
    }

    [Test]
    public void It_has_one_readiness_check_per_E2E_claim_set()
    {
        _readinessChecks.Select(check => check.ClaimSetName).Should().BeEquivalentTo(E2EClaimSetNames);
    }

    [Test]
    public void It_grants_every_readiness_check_in_authorization_metadata()
    {
        using AssertionScope scope = new();

        foreach (ReadinessCheck check in _readinessChecks)
        {
            ClaimSetMetadata claimSet = _metadata
                .Should()
                .ContainSingle(metadata => metadata.ClaimSetName == check.ClaimSetName)
                .Which;
            ClaimSetMetadata.Claim claim = claimSet
                .Claims.Should()
                .ContainSingle(
                    c => c.Name == check.ResourceClaim,
                    $"{check.ClaimSetName} must list the probe claim"
                )
                .Which;

            claimSet
                .Authorizations.Should()
                .ContainSingle(authorization => authorization.Id == claim.AuthorizationId)
                .Which.Actions.Select(action => action.Name)
                .Should()
                .Contain(
                    check.Action,
                    $"{check.ClaimSetName} must grant the probe action on {check.ResourceClaim}"
                );
        }
    }

    private sealed record ReadinessCheck(string ClaimSetName, string ResourceClaim, string Action);

    private ClaimsDocument Compose(string defaultFragmentsPath, string e2eFragmentsPath)
    {
        // Stage both fragment directories into one, as the E2E claims workspace does
        string stagedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(stagedPath);

        try
        {
            foreach (
                string fragment in Directory
                    .GetFiles(defaultFragmentsPath, "*-claimset.json")
                    .Concat(Directory.GetFiles(e2eFragmentsPath, "*-claimset.json"))
            )
            {
                File.Copy(fragment, Path.Combine(stagedPath, Path.GetFileName(fragment)));
            }

            JsonObject baseClaims = LoadEmbeddedClaims();
            ClaimsLoadResult result = new ClaimsFragmentComposer(
                A.Fake<ILogger<ClaimsFragmentComposer>>()
            ).ComposeClaimsFromFragments(
                new ClaimsDocument(baseClaims["claimSets"]!, baseClaims["claimsHierarchy"]!),
                stagedPath
            );

            result.Failures.Should().BeEmpty();
            return result.Nodes!;
        }
        finally
        {
            Directory.Delete(stagedPath, true);
        }
    }

    private async Task<IList<ClaimSetMetadata>> CreateAuthorizationMetadata()
    {
        var claimSetRepository = A.Fake<IClaimSetRepository>();
        A.CallTo(() => claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
            .Returns(
                new ClaimSetQueryResult.Success([
                    .. _composedClaimSets
                        .OfType<JsonObject>()
                        .Select(
                            (claimSet, index) =>
                                new ClaimSetResponse
                                {
                                    Id = index + 1,
                                    Name = claimSet["claimSetName"]!.GetValue<string>(),
                                    IsSystemReserved = claimSet["isSystemReserved"]!.GetValue<bool>(),
                                }
                        ),
                ])
            );

        var hierarchy =
            JsonSerializer.Deserialize<List<Claim>>(
                _composedHierarchy.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("Composed claimsHierarchy parsed to null.");

        AuthorizationMetadataResponse response = await new AuthorizationMetadataResponseFactory(
            claimSetRepository
        ).Create(null, hierarchy);

        return response.ClaimSets;
    }

    private static IEnumerable<string> E2EGrants(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.SelectMany(E2EGrants);
        }

        if (node is not JsonObject claim)
        {
            return [];
        }

        string claimName = claim["name"]!.GetValue<string>();
        IEnumerable<string> grants = (claim["claimSets"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Where(claimSet =>
                claimSet["name"]!.GetValue<string>().StartsWith("E2E-", StringComparison.Ordinal)
            )
            .Select(claimSet =>
                $"{claimSet["name"]!.GetValue<string>()}|{claimName}|{FormatActions(claimSet["actions"]!.AsArray())}"
            );

        return grants.Concat(E2EGrants(claim["claims"]));
    }

    private static string FormatActions(JsonArray actions) =>
        string.Join(";", actions.OfType<JsonObject>().Select(FormatAction));

    private static string FormatAction(JsonObject action)
    {
        IEnumerable<string> strategies = (action["authorizationStrategyOverrides"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Select(strategy => strategy["name"]!.GetValue<string>());

        return $"{action["name"]!.GetValue<string>()}[{string.Join("+", strategies)}]";
    }

    private JsonObject LoadEmbeddedClaims()
    {
        Assembly assembly = typeof(ClaimsProvider).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Claims.Standards.{standardFolder}.Claims.json";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load embedded resource '{resourceName}'.");
        using StreamReader reader = new(stream);

        JsonNode claims =
            JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Embedded Claims.json parsed to null.");

        return claims.AsObject();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                File.Exists(Path.Combine(directory.FullName, "LICENSE"))
                && File.Exists(
                    Path.Combine(directory.FullName, "src", "config", "EdFi.DmsConfigurationService.sln")
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
