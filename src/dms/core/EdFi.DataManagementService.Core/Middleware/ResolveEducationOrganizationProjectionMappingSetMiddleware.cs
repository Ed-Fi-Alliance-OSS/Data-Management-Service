// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Resolves the compiled mapping set for an education-organization projection target and attaches it
/// to <see cref="RequestInfo.MappingSet"/>.
/// </summary>
/// <remarks>
/// It resolves the same key as <see cref="ResolveMappingSetMiddleware"/> but answers differently. That
/// step returns the exception's message and diagnostics in a 503; here a mapping set that cannot be
/// produced is a permanent property of the deployed data model, so it is 409
/// <c>projection-unsupported</c> with the fixed body, and neither the message nor the diagnostics
/// reach the response or any log. The registered provider wraps every compilation failure in
/// <see cref="MappingSetUnavailableException"/>; any other exception is a defect and propagates.
/// </remarks>
internal class ResolveEducationOrganizationProjectionMappingSetMiddleware(
    IMappingSetProvider _mappingSetProvider,
    IEffectiveSchemaSetProvider _effectiveSchemaSetProvider,
    IEnumerable<IRuntimeMappingSetCompiler> _runtimeCompilers,
    ILogger<ResolveEducationOrganizationProjectionMappingSetMiddleware> _logger
) : IPipelineStep
{
    private readonly SqlDialect? _dialect = _runtimeCompilers.FirstOrDefault()?.Dialect;

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        FrontendRequest request = requestInfo.FrontendRequest;
        CancellationToken cancellationToken = requestInfo.RequestCancellationToken;

        // Both are composition defects rather than request outcomes: the schema step always sets the
        // fingerprint before this step runs, and target resolution has already required a compiler.
        string effectiveSchemaHash =
            requestInfo.DatabaseFingerprint?.EffectiveSchemaHash
            ?? throw new InvalidOperationException(
                "The database fingerprint was not resolved before projection mapping set resolution. "
                    + "ValidateEducationOrganizationProjectionTargetSchemaMiddleware must run first."
            );

        SqlDialect dialect =
            _dialect
            ?? throw new InvalidOperationException(
                "No runtime mapping set compiler is registered, so the deployment's dialect is unknown."
            );

        MappingSetKey key = new(
            EffectiveSchemaHash: effectiveSchemaHash,
            Dialect: dialect,
            RelationalMappingVersion: _effectiveSchemaSetProvider
                .EffectiveSchemaSet
                .EffectiveSchema
                .RelationalMappingVersion
        );

        try
        {
            requestInfo.MappingSet = await _mappingSetProvider.GetOrCreateAsync(key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MappingSetUnavailableException)
        {
            // The reason only. The message and diagnostics name schema objects and can carry whatever
            // the compiler met, so the exception is not handed to the logger.
#pragma warning disable S6667
            _logger.LogError(
                "Projection mapping set unavailable ({Reason}) for dialect {Dialect} - {TraceId}",
                "MappingSetUnavailable",
                dialect,
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );
#pragma warning restore S6667

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.ProjectionUnsupported,
                request.TraceId
            );
            return;
        }

        await next();
    }
}
