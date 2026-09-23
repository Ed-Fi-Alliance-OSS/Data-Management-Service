// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.SchemaTools.Tests.Unit;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
public class Given_Cdc_runbook_history_output
{
    [TestCase(DocumentCacheAdministrativeCommand.OfflineActivation, true)]
    [TestCase(DocumentCacheAdministrativeCommand.OfflineDeactivation, true)]
    [TestCase(DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery, true)]
    [TestCase(DocumentCacheAdministrativeCommand.OfflineActivation, false)]
    [TestCase(DocumentCacheAdministrativeCommand.OfflineDeactivation, false)]
    [TestCase(DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery, false)]
    public void It_matches_shared_result_excerpts_for_all_three_guarded_operations(
        DocumentCacheAdministrativeCommand command,
        bool admitted
    )
    {
        DocumentCacheAdministrativeCommandResult result = new(
            command,
            new("", 1),
            admitted
                ? DocumentCacheAdministrativeCommandStatus.Completed
                : DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            admitted
                ? DocumentCacheAdministrativeCommandClassification.Succeeded
                : DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown,
            mutated: admitted
        );
        string json = DocumentCacheAdminJsonSerializer.SerializeContract(
            result,
            typeof(DocumentCacheAdministrativeCommandResult)
        );
        CdcRunbookExamples.AssertExcerpt(
            admitted ? "cdc-output-history-admitted" : "cdc-output-history-rejected",
            json,
            "status",
            "classification",
            "mutated",
            "downstreamPublicationStatus"
        );
        DocumentCacheAdminExitCodeMapper
            .ForAdministrativeCommandResult(result)
            .Should()
            .Be(admitted ? 0 : 10);
    }
}
