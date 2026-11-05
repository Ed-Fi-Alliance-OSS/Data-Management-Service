// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal delegate Task<UpdateResult> MissingReferenceRootChildUpdate(
    AuthorizationRootChildSeed seed,
    DocumentUuid documentUuid,
    IReadOnlyList<long> claimEducationOrganizationIds,
    IReadOnlyList<string> strategyNames,
    BackendProfileWriteContext? backendProfileWriteContext = null,
    JsonNode? requestBody = null
);

internal delegate Task<UpsertResult> MissingReferenceRootChildUpsert(
    AuthorizationRootChildSeed seed,
    IReadOnlyList<long> claimEducationOrganizationIds,
    IReadOnlyList<string> strategyNames,
    BackendProfileWriteContext? backendProfileWriteContext = null,
    JsonNode? requestBody = null
);

/// <summary>
/// Missing-reference PUT and profiled POST regressions shared by both database providers.
/// Provider fixtures own database setup and adapt only request execution and persisted-state readback.
/// </summary>
internal sealed class MissingReferenceWriteAuthorizationScenarios(
    MappingSet mappingSet,
    Func<AuthorizationRootChildSeed, Task<UpsertResult>> createRootChild,
    MissingReferenceRootChildUpdate updateRootChild,
    MissingReferenceRootChildUpsert upsertRootChild,
    Func<DocumentUuid, Task<AuthorizationWriteSideEffectState>> readState,
    Func<DocumentUuid, Task<long>> countDocuments,
    Func<AuthorizationRootChildSeed, Task<long>> countIdentities
)
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    public async Task It_denies_a_missing_proposed_school_on_put_and_preserves_stored_state()
    {
        // DMS-1659 AC4: the stored School 100 authorizes, so only the proposed School decides. A School that
        // does not exist answers exactly like the existing unrelated School 300.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000021",
            421,
            "missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);

        var missingResult = await updateRootChild(
            existingSeed with
            {
                Name = "missing-proposed-change",
                SchoolId = 999,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var unrelatedResult = await updateRootChild(
            existingSeed with
            {
                Name = "unrelated-proposed-change",
                SchoolId = 300,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task It_denies_profiled_put_before_collection_reference_failures_and_preserves_stored_state(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        // Before DMS-1659 a profiled write returned the missing-reference 409 ahead of proposed authorization.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000022",
            422,
            "profile-missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);

        var missingResult = await PutWithVisibleProfileAsync(
            existingSeed with
            {
                Name = "profile-missing-proposed-change",
                SchoolId = proposedSchoolId,
                ClassPeriods = [new ClassPeriodReferenceSeed(classPeriodName, 100)],
            },
            existingSeed.DocumentUuid
        );
        var unrelatedResult = await PutWithVisibleProfileAsync(
            existingSeed with
            {
                Name = "profile-unrelated-proposed-change",
                SchoolId = 300,
                ClassPeriods = [new ClassPeriodReferenceSeed("P1", 100)],
            },
            existingSeed.DocumentUuid
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_put_authorization_without_changing_rows(
        bool schoolVisible
    )
    {
        // Both visible and hidden-preserved School 100 authorize before the child reference conflict.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000023",
            423,
            "profile-hidden-missing-child-existing",
            100,
            []
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);
        var proposedSeed = existingSeed with
        {
            Name = "profile-hidden-missing-child-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("Missing", 100)],
        };
        var writeBody = schoolVisible
            ? RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(proposedSeed)
            : CreateRootSchoolHiddenBodyWithClassPeriods(proposedSeed);

        var result = await updateRootChild(
            proposedSeed,
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: schoolVisible
                ? AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                    mappingSet,
                    writeBody.DeepClone()
                )
                : AuthorizationRootChildProfileTestSupport.CreateRootSchoolHiddenProfileContext(
                    mappingSet,
                    writeBody.DeepClone()
                ),
            requestBody: writeBody
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task Given_A_Custom_View_Strategy_It_Denies_An_Update_Whose_Proposed_School_Is_Unresolved_Like_An_Excluded_School(
        IReadOnlyList<string> customViewStrategy
    )
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000604",
            604,
            "custom-view-missing-existing",
            100,
            []
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);

        var missingResult = await updateRootChild(
            existingSeed with
            {
                Name = "custom-view-missing-change",
                SchoolId = 999,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            customViewStrategy
        );
        var excludedResult = await updateRootChild(
            existingSeed with
            {
                Name = "custom-view-excluded-change",
                SchoolId = 200,
            },
            existingSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            customViewStrategy
        );

        var missingFailure = missingResult
            .Should()
            .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        missingFailure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        missingFailure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
        missingFailure
            .Should()
            .BeEquivalentTo(
                excludedResult
                    .Should()
                    .BeOfType<UpdateResult.UpdateFailureCustomViewNotAuthorized>()
                    .Subject.CustomViewFailure,
                options => options.WithStrictOrdering()
            );
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task It_denies_profiled_put_identically_before_non_creatable_collection_inserts()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000027",
            427,
            "profile-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            Name = "profile-policy-change",
            SchoolId = 999,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var missingResult = await PutWithVisibleProfileAsync(
            candidate,
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );
        var afterMissing = await readState(storedSeed.DocumentUuid);
        afterMissing.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var unrelatedResult = await PutWithVisibleProfileAsync(
            candidate with
            {
                SchoolId = 300,
            },
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );

        AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
            AssertSingleProposedNoRelationshipSubject(missingResult),
            AssertSingleProposedNoRelationshipSubject(unrelatedResult)
        );
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task It_enforces_non_creatable_collection_policy_after_authorized_profiled_put()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000028",
            428,
            "profile-authorized-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);

        var result = await PutWithVisibleProfileAsync(
            storedSeed with
            {
                Name = "profile-authorized-policy-change",
                ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
            },
            storedSeed.DocumentUuid,
            classPeriodsCreatable: false
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>();
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    public async Task It_authorizes_profiled_put_before_multiple_missing_collection_reference_failures(
        int proposedSchoolId
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-1111-0000-0000-000000000029",
            429,
            "profile-multiple-missing-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            Name = "profile-multiple-missing-change",
            SchoolId = proposedSchoolId,
            ClassPeriods =
            [
                new ClassPeriodReferenceSeed("Missing1", 100),
                new ClassPeriodReferenceSeed("Missing2", 200),
            ],
        };

        var result = await PutWithVisibleProfileAsync(candidate, storedSeed.DocumentUuid);

        if (proposedSchoolId == 100)
        {
            result
                .Should()
                .BeOfType<UpdateResult.UpdateFailureReference>()
                .Which.InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
                .Should()
                .Equal(
                    ("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing),
                    ("$.classPeriods[1].classPeriodReference", DocumentReferenceFailureReason.Missing)
                );
        }
        else
        {
            var unrelatedResult = await PutWithVisibleProfileAsync(
                candidate with
                {
                    SchoolId = 300,
                    ClassPeriods =
                    [
                        new ClassPeriodReferenceSeed("P1", 100),
                        new ClassPeriodReferenceSeed("P2", 200),
                    ],
                },
                storedSeed.DocumentUuid
            );
            AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
                AssertSingleProposedNoRelationshipSubject(result),
                AssertSingleProposedNoRelationshipSubject(unrelatedResult)
            );
        }
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private async Task<UpdateResult> PutWithVisibleProfileAsync(
        AuthorizationRootChildSeed proposedSeed,
        DocumentUuid documentUuid,
        bool classPeriodsCreatable = true
    )
    {
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            proposedSeed
        );

        return await updateRootChild(
            proposedSeed,
            documentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                mappingSet,
                writeBody.DeepClone(),
                classPeriodsCreatable
            ),
            requestBody: writeBody
        );
    }

    private static JsonNode CreateRootSchoolHiddenBodyWithClassPeriods(AuthorizationRootChildSeed seed)
    {
        var body = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed);
        body.AsObject().Remove("schoolReference");

        return body;
    }

    private static RelationshipAuthorizationFailure AssertSingleProposedNoRelationshipSubject(
        UpdateResult result
    )
    {
        if (result is UpdateResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        var failedSubject = failure
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.ColumnName.Should().Be("School_SchoolId");

        return failure.RelationshipFailure;
    }

    public async Task It_denies_a_missing_proposed_school_on_post_as_update_and_preserves_stored_state()
    {
        // DMS-1659 AC4: the natural key (AuthorizationRootChildId) is unchanged, so the POST resolves to the
        // existing target; its stored School 100 authorizes and only the proposed School decides.
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000021",
            521,
            "missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);
        var missingCandidate = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000021")),
            Name = "missing-proposed-change",
            SchoolId = 999,
        };
        var unrelatedCandidate = missingCandidate with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000121")),
            Name = "unrelated-proposed-change",
            SchoolId = 300,
        };

        var missingResult = await upsertRootChild(
            missingCandidate,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );
        var unrelatedResult = await upsertRootChild(
            unrelatedCandidate,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames
        );

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(missingCandidate.DocumentUuid)).Should().Be(0);
        (await countDocuments(unrelatedCandidate.DocumentUuid)).Should().Be(0);
    }

    public async Task It_denies_profiled_post_as_update_before_collection_reference_failures_and_preserves_stored_state(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        var existingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000022",
            522,
            "profile-missing-proposed-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(existingSeed));
        var before = await readState(existingSeed.DocumentUuid);
        var candidateSeed = existingSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000022")),
            Name = "profile-missing-proposed-change",
            SchoolId = proposedSchoolId,
            ClassPeriods = [new ClassPeriodReferenceSeed(classPeriodName, 100)],
        };

        var result = await PostWithVisibleProfileAsync(candidateSeed);

        var unrelatedResult = await PostWithVisibleProfileAsync(
            candidateSeed with
            {
                SchoolId = 300,
                ClassPeriods = [new ClassPeriodReferenceSeed("P1", 100)],
            }
        );
        AssertSingleProposedNoRelationshipSubject(result)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        var after = await readState(existingSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(candidateSeed.DocumentUuid)).Should().Be(0);
    }

    public async Task It_denies_profiled_post_create_before_collection_reference_failures_without_side_effects(
        int proposedSchoolId,
        string classPeriodName
    )
    {
        var missingSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000023",
            523,
            "profile-missing-create",
            proposedSchoolId,
            [new ClassPeriodReferenceSeed(classPeriodName, 100)]
        );
        var unrelatedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000024",
            524,
            "profile-unrelated-create",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );

        var missingResult = await PostWithVisibleProfileAsync(missingSeed);
        var unrelatedResult = await PostWithVisibleProfileAsync(unrelatedSeed);

        AssertSingleProposedNoRelationshipSubject(missingResult)
            .Should()
            .BeEquivalentTo(
                AssertSingleProposedNoRelationshipSubject(unrelatedResult),
                options => options.WithStrictOrdering()
            );
        (await countDocuments(missingSeed.DocumentUuid)).Should().Be(0);
        (await countDocuments(unrelatedSeed.DocumentUuid)).Should().Be(0);
    }

    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_post_as_update_authorization_without_side_effects()
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000025",
            525,
            "profile-authorized-child-conflict",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000025")),
            Name = "profile-authorized-child-conflict-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("Missing", 100)],
        };

        var result = await PostWithVisibleProfileAsync(candidate);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(candidate.DocumentUuid)).Should().Be(0);
    }

    public async Task It_returns_a_missing_child_reference_conflict_after_profiled_post_create_authorization_without_side_effects()
    {
        var seed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000026",
            526,
            "profile-authorized-create-child-conflict",
            100,
            [new ClassPeriodReferenceSeed("Missing", 100)]
        );

        var result = await PostWithVisibleProfileAsync(seed);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>()
            .Which.InvalidDocumentReferences.Select(static failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing));
        (await countDocuments(seed.DocumentUuid)).Should().Be(0);
        (await countIdentities(seed)).Should().Be(0);
    }

    public async Task It_denies_profiled_post_identically_before_non_creatable_collection_inserts(
        bool createNew
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000027",
            527,
            "profile-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000027")),
            AuthorizationRootChildId = createNew ? 529 : storedSeed.AuthorizationRootChildId,
            Name = "profile-policy-change",
            SchoolId = 999,
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var missingResult = await PostWithVisibleProfileAsync(candidate, classPeriodsCreatable: false);
        var afterMissing = await readState(storedSeed.DocumentUuid);
        afterMissing.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var unrelatedResult = await PostWithVisibleProfileAsync(
            candidate with
            {
                SchoolId = 300,
            },
            classPeriodsCreatable: false
        );

        AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
            AssertSingleProposedNoRelationshipSubject(missingResult),
            AssertSingleProposedNoRelationshipSubject(unrelatedResult)
        );
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await countIdentities(candidate)).Should().Be(0);
        }
    }

    public async Task It_enforces_non_creatable_collection_policy_after_authorized_profiled_post(
        bool createNew
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000028",
            528,
            "profile-authorized-policy-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000028")),
            AuthorizationRootChildId = createNew ? 530 : storedSeed.AuthorizationRootChildId,
            Name = "profile-authorized-policy-change",
            ClassPeriods = [new ClassPeriodReferenceSeed("P2", 200)],
        };

        var result = await PostWithVisibleProfileAsync(candidate, classPeriodsCreatable: false);

        result.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await countIdentities(candidate)).Should().Be(0);
        }
    }

    public async Task It_authorizes_profiled_post_before_multiple_missing_collection_reference_failures(
        bool createNew,
        int proposedSchoolId
    )
    {
        var storedSeed = CreateRootChildSeed(
            "cccccccc-2222-0000-0000-000000000029",
            531,
            "profile-multiple-missing-existing",
            100,
            [new ClassPeriodReferenceSeed("P1", 100)]
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(await createRootChild(storedSeed));
        var before = await readState(storedSeed.DocumentUuid);
        var candidate = storedSeed with
        {
            DocumentUuid = new DocumentUuid(Guid.Parse("dddddddd-2222-0000-0000-000000000029")),
            AuthorizationRootChildId = createNew ? 532 : storedSeed.AuthorizationRootChildId,
            Name = "profile-multiple-missing-change",
            SchoolId = proposedSchoolId,
            ClassPeriods =
            [
                new ClassPeriodReferenceSeed("Missing1", 100),
                new ClassPeriodReferenceSeed("Missing2", 200),
            ],
        };

        var result = await PostWithVisibleProfileAsync(candidate);

        if (proposedSchoolId == 100)
        {
            result
                .Should()
                .BeOfType<UpsertResult.UpsertFailureReference>()
                .Which.InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
                .Should()
                .Equal(
                    ("$.classPeriods[0].classPeriodReference", DocumentReferenceFailureReason.Missing),
                    ("$.classPeriods[1].classPeriodReference", DocumentReferenceFailureReason.Missing)
                );
        }
        else
        {
            var unrelatedResult = await PostWithVisibleProfileAsync(
                candidate with
                {
                    SchoolId = 300,
                    ClassPeriods =
                    [
                        new ClassPeriodReferenceSeed("P1", 100),
                        new ClassPeriodReferenceSeed("P2", 200),
                    ],
                }
            );
            AuthorizationRootChildProfileTestSupport.AssertNoRelationshipResponseParity(
                AssertSingleProposedNoRelationshipSubject(result),
                AssertSingleProposedNoRelationshipSubject(unrelatedResult)
            );
        }
        var after = await readState(storedSeed.DocumentUuid);
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await countDocuments(candidate.DocumentUuid)).Should().Be(0);
        if (createNew)
        {
            (await countIdentities(candidate)).Should().Be(0);
        }
    }

    private async Task<UpsertResult> PostWithVisibleProfileAsync(
        AuthorizationRootChildSeed seed,
        bool classPeriodsCreatable = true
    )
    {
        var writeBody = RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(
            seed
        );

        return await upsertRootChild(
            seed,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.EdOrgOnlyStrategyNames,
            backendProfileWriteContext: AuthorizationRootChildProfileTestSupport.CreateRootSchoolVisibleProfileContext(
                mappingSet,
                writeBody.DeepClone(),
                classPeriodsCreatable
            ),
            requestBody: writeBody
        );
    }

    private static RelationshipAuthorizationFailure AssertSingleProposedNoRelationshipSubject(
        UpsertResult result
    )
    {
        if (result is UpsertResult.UnknownFailure unknownFailure)
        {
            Assert.Fail(
                $"Expected relationship denial but received unknown failure: {unknownFailure.FailureMessage}"
            );
        }

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>().Subject;
        failure
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        var failedSubject = failure
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.RootBinding.ColumnName.Should().Be("School_SchoolId");

        return failure.RelationshipFailure;
    }

    private static AuthorizationRootChildSeed CreateRootChildSeed(
        string documentUuid,
        int authorizationRootChildId,
        string name,
        int schoolId,
        IReadOnlyList<ClassPeriodReferenceSeed> classPeriods
    ) =>
        new(
            new DocumentUuid(Guid.Parse(documentUuid)),
            authorizationRootChildId,
            name,
            schoolId,
            classPeriods
        );
}
