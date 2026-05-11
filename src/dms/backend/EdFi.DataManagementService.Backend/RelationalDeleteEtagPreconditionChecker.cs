// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed record RelationalDeleteEtagPreconditionCheckResult(
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    string CurrentEtag,
    bool IsMatch
);

public interface IRelationalDeleteEtagPreconditionChecker
{
    Task<RelationalDeleteEtagPreconditionCheckResult?> CheckAsync(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}
