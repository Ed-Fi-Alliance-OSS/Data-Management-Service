// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

public record JobEnqueueResult
{
    /// <summary>The job was stored as <c>Pending</c> with the returned public identifier.</summary>
    public record Success(string JobId) : JobEnqueueResult;

    public record FailureUnsupportedType() : JobEnqueueResult;

    public record FailureUnsupportedVersion() : JobEnqueueResult;

    /// <summary>The payload broke its contract; <paramref name="ReasonCode"/> is a fixed code, not input text.</summary>
    public record FailurePayloadInvalid(string ReasonCode) : JobEnqueueResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobEnqueueResult;
}
