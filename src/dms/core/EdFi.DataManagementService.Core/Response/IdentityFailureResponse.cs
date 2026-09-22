// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// JSON error-body factory for the identity API surface (design.md D9). Reuses
/// <see cref="FailureResponse.CreateBaseJsonObject" /> so the envelope
/// (detail/type/title/status/correlationId/validationErrors/errors) matches every other DMS problem
/// response byte for byte, but lives in its own type because the identity problem-detail namespace
/// (<c>urn:ed-fi:api:identities:*</c>) and its six fixed types are specific to identity operations and
/// otherwise unrelated to the general catalog in <see cref="FailureResponse" />.
/// </summary>
public static class IdentityFailureResponse
{
    /// <summary>
    /// The identity provider's captured <c>Capabilities</c> does not include the flag the requested
    /// operation needs.
    /// </summary>
    public const string OperationNotSupportedType = "urn:ed-fi:api:identities:operation-not-supported";

    /// <summary>
    /// The provider reported <c>NotFound</c> for a get-by-id subject, a results token, or a
    /// provider-owned context refusal.
    /// </summary>
    public const string NotFoundType = "urn:ed-fi:api:identities:not-found";

    /// <summary>
    /// The provider returned a result shape the identity contract does not permit for the operation
    /// that produced it.
    /// </summary>
    public const string ProviderContractViolationType =
        "urn:ed-fi:api:identities:provider-contract-violation";

    /// <summary>
    /// An identity operation invocation threw while the request was live.
    /// </summary>
    public const string UpstreamFailureType = "urn:ed-fi:api:identities:upstream-failure";

    /// <summary>
    /// The provider reported <c>JobFailed</c> from a results poll: the accepted job has failed
    /// permanently and must not be polled again.
    /// </summary>
    public const string JobFailedType = "urn:ed-fi:api:identities:job-failed";

    /// <summary>
    /// Provider activation or its <c>Capabilities</c> getter threw while the request was live, so no
    /// operation could be attempted.
    /// </summary>
    public const string ProviderConfigurationType = "urn:ed-fi:api:identities:provider-configuration";

    public static JsonNode ForIdentityOperationNotSupported(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "The identity provider does not support this operation.",
            type: OperationNotSupportedType,
            title: "Operation Not Supported",
            status: 404,
            correlationId: traceId.Value
        );

    public static JsonNode ForIdentityNotFound(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "The specified data could not be found.",
            type: NotFoundType,
            title: "Not Found",
            status: 404,
            correlationId: traceId.Value
        );

    /// <summary>
    /// The provider returned a result shape the identity contract forbids for the operation that
    /// produced it (for example a missing payload on <c>Success</c>, both a payload and a request
    /// token, an unusable request token, or a terminal status returned from an operation other than
    /// results). <paramref name="detail" /> names which contract rule was violated; it must never repeat
    /// provider exception text or payload content.
    /// </summary>
    public static JsonNode ForIdentityProviderContractViolation(TraceId traceId, string detail) =>
        FailureResponse.CreateBaseJsonObject(
            detail: detail,
            type: ProviderContractViolationType,
            title: "Identity Provider Contract Violation",
            status: 502,
            correlationId: traceId.Value
        );

    /// <summary>
    /// An identity operation invocation threw while the request was live. The title names Identity
    /// Management as the failing subsystem; the raw provider exception is sanitized before this method
    /// is ever called (D9), so the detail carries no provider-specific text.
    /// </summary>
    public static JsonNode ForIdentityUpstreamFailure(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "The Identity Management service encountered an unexpected error and could not complete the request.",
            type: UpstreamFailureType,
            title: "Identity Management Upstream Failure",
            status: 502,
            correlationId: traceId.Value
        );

    public static JsonNode ForIdentityJobFailed(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "The accepted identity request failed permanently. Stop polling this job.",
            type: JobFailedType,
            title: "Identity job failed",
            status: 502,
            correlationId: traceId.Value
        );

    public static JsonNode ForIdentityProviderConfiguration(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "The identity provider could not be initialized or its capabilities evaluated.",
            type: ProviderConfigurationType,
            title: "Identity provider configuration failure",
            status: 500,
            correlationId: traceId.Value
        );
}
