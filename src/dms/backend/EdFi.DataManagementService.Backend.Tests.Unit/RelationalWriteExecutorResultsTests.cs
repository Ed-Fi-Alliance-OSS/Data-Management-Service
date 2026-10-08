// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Direct unit tests of <see cref="RelationalWriteExecutorResults.BuildStaleNoOpCompareResult"/>. It is a
/// pure static mapping from (operationKind, writePrecondition) to a result, so locking its branching here
/// is cheaper and more precise than driving every precondition combination through the full executor.
///
/// Scope (B7 regression lock): a stale guarded no-op (the merge synthesizer produced a no-op candidate,
/// but the freshness check found the row has moved on since it was read) 412s ONLY for a specific-tag
/// If-Match. A wildcard If-Match is an existence precondition, so it uses the ordinary
/// write-conflict/retry outcome.
/// </summary>
[TestFixture]
public class Given_Relational_Write_Executor_Results_Build_Stale_No_Op_Compare_Result
{
    [Test]
    public void It_returns_a_post_etag_mismatch_for_a_specific_tag_if_match()
    {
        var result = RelationalWriteExecutorResults.BuildStaleNoOpCompareResult(
            RelationalWriteOperationKind.Post,
            new WritePrecondition.IfMatch("\"5-abc\"")
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
    }

    [Test]
    public void It_returns_a_put_etag_mismatch_for_a_specific_tag_if_match()
    {
        var result = RelationalWriteExecutorResults.BuildStaleNoOpCompareResult(
            RelationalWriteOperationKind.Put,
            new WritePrecondition.IfMatch("\"5-abc\"")
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
    }

    [Test]
    public void It_returns_a_post_write_conflict_not_etag_mismatch_for_a_wildcard_if_match()
    {
        var result = RelationalWriteExecutorResults.BuildStaleNoOpCompareResult(
            RelationalWriteOperationKind.Post,
            new WritePrecondition.IfMatch("*", IsWildcard: true)
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
    }

    [Test]
    public void It_returns_a_put_write_conflict_not_etag_mismatch_for_a_wildcard_if_match()
    {
        var result = RelationalWriteExecutorResults.BuildStaleNoOpCompareResult(
            RelationalWriteOperationKind.Put,
            new WritePrecondition.IfMatch("*", IsWildcard: true)
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureWriteConflict(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
    }
}
