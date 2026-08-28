// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
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
        _logger.LogDebug(
            "Entering CustomResourceValidationMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolved from the per-request scoped service provider, not a constructor field: the
        // pipeline itself is built once and cached in a Lazy<PipelineProvider>, so a constructor-held
        // collection would be captive across every request.
        var validators = requestInfo.ScopedServiceProvider.GetServices<ICustomResourceValidator>();

        var resourceInfo = requestInfo.ResourceInfo;

        // Matching against the current request's resource is exact and ordinal, so a typo'd or
        // wrong-cased AppliesTo entry never matches and that validator never runs for this resource.
        var applicableValidators = validators.Where(validator =>
            validator.AppliesTo.Any(appliesTo =>
                string.Equals(appliesTo.ProjectName, resourceInfo.ProjectName.Value, StringComparison.Ordinal)
                && string.Equals(
                    appliesTo.ResourceName,
                    resourceInfo.ResourceName.Value,
                    StringComparison.Ordinal
                )
            )
        );

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

            // The validator's own type name is implementer-authored, unlike every other value on
            // this log line, so it is routed through the sanitizer before it ever reaches a log
            // template.
            string sanitizedValidatorTypeName = LoggingSanitizer.SanitizeForLogging(validator.GetType().Name);
            long validatorStartTimestamp = Stopwatch.GetTimestamp();

            // A null return is not a substitute for an empty list per ICustomResourceValidator's own
            // contract: without this guard a validator that mistakenly returns null is
            // indistinguishable from one that ran and found nothing.
            var failures =
                await validator.ValidateAsync(
                    document,
                    validatedResourceInfo,
                    _operation,
                    scope,
                    requestInfo.FrontendRequest.TraceId.Value,
                    requestInfo.RequestCancellationToken
                )
                ?? throw new InvalidOperationException(
                    $"{validator.GetType().Name}.ValidateAsync returned null. A null return is not "
                        + "a substitute for an empty list and is treated as a hard failure."
                );

            // This design puts third-party network I/O on the write path with no timeout the
            // contract controls, so per-validator elapsed time is the only diagnostic a deployment
            // has when a validator is slow.
            _logger.LogDebug(
                "{ValidatorTypeName} ran in {ElapsedMilliseconds} ms - {TraceId}",
                sanitizedValidatorTypeName,
                Stopwatch.GetElapsedTime(validatorStartTimestamp).TotalMilliseconds,
                requestInfo.FrontendRequest.TraceId.Value
            );

            if (failures.Count > 0)
            {
                // Names the validator and the failure count only. Failure messages are never
                // logged: they can quote submitted document values.
                _logger.LogDebug(
                    "{ValidatorTypeName} returned {FailureCount} failure(s) - {TraceId}",
                    sanitizedValidatorTypeName,
                    failures.Count,
                    requestInfo.FrontendRequest.TraceId.Value
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
                    "The request could not be processed. See 'errors' for details.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    [.. errors]
                )
                : FailureResponse.ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
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
}
