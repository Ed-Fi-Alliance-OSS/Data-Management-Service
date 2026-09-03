// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Runs every implementer-registered custom resource validator that applies to the request's
/// resource, in addition to DMS's own core validation.
/// </summary>
internal class CustomResourceValidationMiddleware(ILogger _logger, CustomValidationOperation _operation)
    : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // TraceId is client-supplied whenever AppSettings:CorrelationIdHeader is configured, so it
        // is sanitized before it reaches any log template here, per the repository's logging rule.
        // Only the log records are sanitized: the trace id handed to a validator, and the one that
        // becomes the 400 body's correlationId, must stay the client's real value.
        string sanitizedTraceId = LoggingSanitizer.SanitizeForLogging(
            requestInfo.FrontendRequest.TraceId.Value
        );

        _logger.LogDebug("Entering CustomResourceValidationMiddleware - {TraceId}", sanitizedTraceId);

        // Resolved from the per-request scoped service provider, not a constructor field: the
        // pipeline itself is built once and cached in a Lazy<PipelineProvider>, so a constructor-held
        // collection would be captive across every request.
        var validators = requestInfo.ScopedServiceProvider.GetServices<ICustomResourceValidator>();

        var resourceInfo = requestInfo.ResourceInfo;

        // Materialized rather than left as a deferred Where. Enumerating lazily inside the loop
        // below would interleave the AppliesTo guard with validator execution, so a validator that
        // violates the contract would only be detected after earlier validators had already run -
        // side effects and outbound I/O included - and whether a misconfigured deployment failed at
        // all would depend on DI registration order.
        List<ICustomResourceValidator> applicableValidators =
        [
            .. validators.Where(validator => AppliesToRequestedResource(validator, resourceInfo)),
        ];

        // Nothing applies, which is every request in a deployment that has registered no validators.
        // Returning here rather than falling through keeps that path free of the per-request
        // projections below, which no one would read.
        if (applicableValidators.Count == 0)
        {
            await next();
            return;
        }

        var validatedResourceInfo = new ValidatedResourceInfo(
            resourceInfo.ProjectName.Value,
            resourceInfo.ResourceName.Value,
            resourceInfo.ResourceVersion.Value
        );

        // Projected into a new Dictionary<string, string> rather than exposed as a wrapper over the
        // live FrontendRequest.RouteQualifiers, so a later mutation of the request's own dictionary
        // (e.g. by a downstream step) is never observed through a scope already handed to a
        // validator. The branded record-struct keys and values are unwrapped here because
        // Dictionary<RouteQualifierName, RouteQualifierValue> does not itself implement
        // IReadOnlyDictionary<string, string>. Wrapped with AsReadOnly() rather than handed out as
        // the raw Dictionary<,> so a validator cannot downcast ValidationScope.RouteQualifiers back
        // to a mutable dictionary and change what a later validator in the same request observes -
        // the same validator-to-validator leak the per-validator DeepClone() below exists to
        // prevent, on this other input.
        var routeQualifiers = requestInfo
            .FrontendRequest.RouteQualifiers.ToDictionary(
                routeQualifier => routeQualifier.Key.Value,
                routeQualifier => routeQualifier.Value.Value
            )
            .AsReadOnly();

        var scope = new ValidationScope(requestInfo.FrontendRequest.Tenant, routeQualifiers);

        // Accumulated across every applicable validator before any response is produced: there is
        // no early exit among custom validators on returned failures. Appended to, never assigned,
        // mirroring the per-path accumulation DocumentValidator.ValidationErrorsFrom already
        // performs, so two validators reporting the same path both survive.
        var validationErrors = new Dictionary<string, string[]>();
        var errors = new List<string>();

        // Invoked sequentially, one at a time - never Task.WhenAll: two validators in one request
        // scope can share a scoped host service, and .NET DI scoped services carry no thread-safety
        // guarantee.
        foreach (var validator in applicableValidators)
        {
            // Each validator gets its own clone rather than one shared clone, so an earlier
            // validator that ignores the read-only contract rule cannot silently change what a
            // later one sees. Cloned from the profile-effective body rather than ParsedBody
            // directly, so a validator sees the profile-shaped writable surface when a writable
            // profile applied, and the profile-effective body as it stands at this point in the
            // pipeline otherwise - after type coercion and after InjectVersionMetadataToEdFiDocumentMiddleware
            // has written "_lastModifiedDate" into it, not the raw submitted body.
            var document = ProfileWriteValidationBody.Effective(requestInfo).DeepClone();

            // The validator's own type name is implementer-authored, so it is routed through the
            // sanitizer before it reaches a log template, for the same reason the trace id is.
            string sanitizedValidatorTypeName = LoggingSanitizer.SanitizeForLogging(validator.GetType().Name);
            long validatorStartTimestamp = Stopwatch.GetTimestamp();

            IReadOnlyList<CustomValidationFailure> failures;

            try
            {
                // A null return is not a substitute for an empty list per ICustomResourceValidator's
                // own contract: without this guard a validator that mistakenly returns null is
                // indistinguishable from one that ran and found nothing. Named with the sanitized
                // type name rather than the raw one, because this exception is caught into
                // RequestInfo.CaughtException and logged, and control characters in a rendered
                // exception message can forge lines in a text sink.
                failures =
                    await validator.ValidateAsync(
                        document,
                        validatedResourceInfo,
                        _operation,
                        scope,
                        requestInfo.FrontendRequest.TraceId.Value,
                        requestInfo.RequestCancellationToken
                    )
                    ?? throw new InvalidOperationException(
                        $"{sanitizedValidatorTypeName}.ValidateAsync returned null. A null return is "
                            + "not a substitute for an empty list and is treated as a hard failure."
                    );
            }
            finally
            {
                // This design puts third-party network I/O on the write path with no timeout the
                // contract controls, so per-validator elapsed time is what a deployment reaches for
                // when a validator is slow. In a finally rather than after the await because the
                // case this record exists for is a validator that hangs, and one that hangs and then
                // throws - an HttpClient timeout, say - is exactly when the elapsed time is worth
                // having. Left at Debug because it is per-request detail. An operator turning it
                // on needs the right category, and it is not this file's namespace: ApiService
                // constructs this step with its own ILogger<ApiService>, so the record lands under
                // "EdFi.DataManagementService.Core.ApiService" and an override scoped to the
                // middleware namespace surfaces nothing.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "{ValidatorTypeName} ran in {ElapsedMilliseconds} ms - {TraceId}",
                        sanitizedValidatorTypeName,
                        Stopwatch.GetElapsedTime(validatorStartTimestamp).TotalMilliseconds,
                        sanitizedTraceId
                    );
                }
            }

            if (failures.Count > 0)
            {
                // Information rather than Debug: this is a client-visible rejection, and at Debug
                // it would never appear in a default deployment, leaving a custom-validation 400
                // with no operator trace at all. That costs one record per rejecting validator per
                // rejected document, which under a bulk load of invalid documents is a real volume -
                // accepted deliberately, because a 400 nobody can see is worse.
                // Names the validator and the failure count only. Failure messages are never
                // logged: they can quote submitted document values.
                _logger.LogInformation(
                    "{ValidatorTypeName} returned {FailureCount} failure(s) - {TraceId}",
                    sanitizedValidatorTypeName,
                    failures.Count,
                    sanitizedTraceId
                );
            }

            foreach (var failure in failures)
            {
                // Written as a switch expression, whose arm is mandatory rather than a matter of
                // taste: Directory.Build.props sets TreatWarningsAsErrors, so CS8509 on an
                // unhandled case in a switch expression is a build failure. The throwing arm is
                // unreachable from any test in this repository - CustomValidationFailure is a
                // closed hierarchy with exactly two constructible cases - but it still has to exist
                // for the switch to compile, and it fails loud rather than silently dropping a
                // third case a future, differently-built assembly could somehow introduce.
                Action recordFailure = failure switch
                {
                    CustomValidationFailure.OnPath onPath => () =>
                        validationErrors[onPath.JsonPath] = validationErrors.TryGetValue(
                            onPath.JsonPath,
                            out var existingMessages
                        )
                            ? [.. existingMessages, onPath.Message]
                            : [onPath.Message],
                    CustomValidationFailure.OnResource onResource => () => errors.Add(onResource.Message),
                    // Ahead of the discard arm because a type pattern never matches null. Without
                    // it a null element falls through to "_" and throws NullReferenceException on
                    // failure.GetType() - inside the one arm whose whole purpose is to fail loud
                    // with a name.
                    null => throw new InvalidOperationException(
                        $"{sanitizedValidatorTypeName} returned a null "
                            + $"{nameof(CustomValidationFailure)}. A null is not a valid failure and "
                            + "is treated as a hard failure."
                    ),
                    _ => throw new InvalidOperationException(
                        $"Unhandled {nameof(CustomValidationFailure)} case: {failure.GetType().Name}."
                    ),
                };
                recordFailure();
            }
        }

        if (errors.Count == 0 && validationErrors.Count == 0)
        {
            await next();
            return;
        }

        // Same factory-selection rule ValidateDocumentMiddleware uses: any errors-arm failure picks
        // ForBadRequest even when validationErrors is also non-empty, otherwise ForDataValidation.
        JsonNode failureResponse =
            errors.Count > 0
                ? FailureResponse.ForBadRequest(
                    FailureResponse.ErrorsArmDetail,
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    [.. errors]
                )
                : FailureResponse.ForDataValidation(
                    FailureResponse.ValidationErrorsArmDetail,
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    [.. errors]
                );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 400,
            Body: failureResponse,
            Headers: []
        );
    }

    /// <summary>
    /// Whether a validator declares itself applicable to the resource this request is for.
    /// </summary>
    /// <remarks>
    /// AppliesTo and its entries are guarded here the same way the ValidateAsync return is guarded
    /// in <see cref="Execute" />, and for a stronger reason. This runs for every registered
    /// validator on every write, so one validator that violates the non-nullable contract on this
    /// property fails writes to every resource rather than only to its own - the widest reach on
    /// this seam. Unguarded it surfaced as a bare NullReferenceException raised inside a lambda,
    /// naming neither the property nor the validator that broke the contract; throwing here names
    /// both.
    /// </remarks>
    private static bool AppliesToRequestedResource(
        ICustomResourceValidator validator,
        ResourceInfo resourceInfo
    )
    {
        IReadOnlyList<ValidatedResource> appliesTo =
            validator.AppliesTo
            ?? throw new InvalidOperationException(
                $"{LoggingSanitizer.SanitizeForLogging(validator.GetType().Name)}.AppliesTo returned "
                    + "null. A null is not a substitute for an empty list and is treated as a hard "
                    + "failure."
            );

        // Checked across the whole list before any matching is attempted, so whether a null entry is
        // reported does not depend on where it sits relative to a matching entry.
        if (appliesTo.Any(entry => entry is null))
        {
            throw new InvalidOperationException(
                $"{LoggingSanitizer.SanitizeForLogging(validator.GetType().Name)}.AppliesTo contains "
                    + "a null entry."
            );
        }

        // Matching against the current request's resource is exact and ordinal, so a typo'd or
        // wrong-cased AppliesTo entry never matches and that validator never runs for this resource.
        return appliesTo.Any(entry =>
            string.Equals(entry.ProjectName, resourceInfo.ProjectName.Value, StringComparison.Ordinal)
            && string.Equals(entry.ResourceName, resourceInfo.ResourceName.Value, StringComparison.Ordinal)
        );
    }
}
