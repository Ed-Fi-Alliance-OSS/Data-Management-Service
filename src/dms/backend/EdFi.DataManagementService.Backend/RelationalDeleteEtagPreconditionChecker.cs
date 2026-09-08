// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed record RelationalDeleteEtagPreconditionCheckResult(
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    bool IsMatch
);

public interface IRelationalDeleteEtagPreconditionChecker
{
    /// <summary>
    /// Evaluates a DELETE If-Match precondition against an already-resolved, already-locked target.
    /// DELETE serves no body and applies no profile lens, so the current etag is composed purely from
    /// the ContentVersion captured when the caller locked the row plus the schema epoch — no re-lock
    /// and no state hydration. Existence and concurrency are the caller's responsibility (it must not
    /// invoke this without a locked target), which is why the result is never null.
    /// </summary>
    RelationalDeleteEtagPreconditionCheckResult Evaluate(
        MappingSet mappingSet,
        RelationalWriteTargetContext.ExistingDocument lockedTargetContext,
        WritePrecondition.IfMatch precondition
    );
}
