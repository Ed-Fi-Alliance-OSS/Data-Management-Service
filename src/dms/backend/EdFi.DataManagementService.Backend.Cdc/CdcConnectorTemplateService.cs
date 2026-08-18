// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Cdc;

public interface ICdcConnectorTemplateService
{
    /// <summary>
    /// Reports whether provider setup evidence alone is sufficient for connector-template rendering.
    /// This does not validate binding identity, deployment policy, connection properties, Kafka
    /// security properties, Kafka Connect REST state, provider DDL, broker access rules, source
    /// offsets, or lifecycle operations.
    /// </summary>
    CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult);

    CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    );

    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);

    CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );

    CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );
}

internal sealed class CdcConnectorTemplateService(
    ICdcConnectorTemplateInputValidator inputValidator,
    ICdcConnectorTemplateRenderer renderer,
    ICdcConnectorTemplateEffectiveConfigValidator effectiveConfigValidator
) : ICdcConnectorTemplateService
{
    public CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult)
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        CdcConnectorTemplateValidationResult validationResult = CdcProviderSetupReadinessRules.Validate(
            providerSetupResult
        );

        return new CdcProviderSetupReadiness(
            provider: providerSetupResult.Provider,
            outcome: providerSetupResult.Outcome,
            canRenderTemplate: validationResult.IsValid,
            diagnostics: validationResult.Diagnostics
        );
    }

    public CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    ) => inputValidator.ValidateRequest(request, sourcePhase);

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request) => renderer.Render(request);

    public CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) =>
        effectiveConfigValidator.ValidateEffectiveConfig(
            request,
            CdcConnectorTemplateSourcePhase.RegistrationPreflight
        );

    public CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) =>
        effectiveConfigValidator.ValidateEffectiveConfig(
            request,
            CdcConnectorTemplateSourcePhase.LiveReadBack
        );
}

public sealed record CdcProviderSetupReadiness
{
    public CdcProviderSetupReadiness(
        CdcProvider provider,
        CdcProviderSetupOutcome outcome,
        bool canRenderTemplate,
        IReadOnlyList<CdcConnectorTemplateDiagnostic>? diagnostics = null
    )
    {
        Provider = provider;
        Outcome = outcome;
        CanRenderTemplate = canRenderTemplate;
        Diagnostics = diagnostics?.ToArray() ?? [];
    }

    public CdcProvider Provider { get; }

    public CdcProviderSetupOutcome Outcome { get; }

    /// <summary>
    /// True only when the provider setup result contains the provider evidence needed before a
    /// connector-template request can render. This is not a combined CDC deployment readiness flag.
    /// </summary>
    public bool CanRenderTemplate { get; }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }
}

internal static class CdcProviderSetupReadinessRules
{
    private const string RedactedValue = "[redacted]";

    internal static CdcConnectorTemplateValidationResult Validate(CdcProviderSetupResult providerSetupResult)
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddOutcomeDiagnostic(providerSetupResult, diagnostics);
        AddSourceFingerprintDiagnostics(providerSetupResult, diagnostics);
        AddHeartbeatActionQueryDiagnostic(providerSetupResult, diagnostics);
        diagnostics.AddRange(
            CdcProviderSetupPrerequisiteRules.Validate(
                providerSetupResult,
                safeArtifactOrObjectName: null,
                CdcConnectorTemplateSourcePhase.RequestValidation
            )
        );

        return new CdcConnectorTemplateValidationResult(diagnostics);
    }

    private static void AddOutcomeDiagnostic(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (
            providerSetupResult.Outcome
            is CdcProviderSetupOutcome.CreatedOrMatched
                or CdcProviderSetupOutcome.ExactMatch
        )
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.outcome",
                "CreatedOrMatched or ExactMatch",
                providerSetupResult.Outcome.ToString(),
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void AddSourceFingerprintDiagnostics(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        bool boundFingerprintIsValid = IsValidSourceFingerprint(
            providerSetupResult.BoundPhysicalSourceFingerprint
        );
        if (!boundFingerprintIsValid)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceFingerprintEvidenceRequired,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                    "providerSetup.boundPhysicalSourceFingerprint",
                    "valid bound physical-source fingerprint",
                    RedactedValue,
                    providerSetupResult.Provider,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            providerSetupResult.ObservedSourceFingerprint is not null
            && IsValidSourceFingerprint(providerSetupResult.ObservedSourceFingerprint)
            && boundFingerprintIsValid
            && providerSetupResult.ObservedSourceFingerprint.Equals(
                providerSetupResult.BoundPhysicalSourceFingerprint
            )
        )
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SourceFingerprintEvidenceRequired,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                "providerSetup.observedPhysicalSourceFingerprint",
                "matching observed physical-source fingerprint",
                providerSetupResult.ObservedSourceFingerprint is null ? "missing" : RedactedValue,
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static void AddHeartbeatActionQueryDiagnostic(
        CdcProviderSetupResult providerSetupResult,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (providerSetupResult.HeartbeatActionQuery is not null)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                CdcConnectorTemplateDiagnosticCategory.Heartbeat,
                "providerSetup.heartbeatActionQuery",
                "fresh provider heartbeat action query",
                "missing",
                providerSetupResult.Provider,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcProvider provider,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            safeArtifactOrObjectName: null,
            expectedValue,
            observedValue,
            provider,
            CdcConnectorTemplateSourcePhase.RequestValidation,
            redactionClassification
        );

    private static bool IsValidSourceFingerprint(CdcSourceFingerprint? sourceFingerprint)
    {
        if (sourceFingerprint is null)
        {
            return false;
        }

        try
        {
            CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
                sourceFingerprint,
                nameof(sourceFingerprint)
            );
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class CdcConnectorTemplateServiceCollectionExtensions
{
    public static IServiceCollection AddCdcConnectorTemplates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<
                ICdcConnectorTemplateInputValidator,
                CdcConnectorTemplateInputValidator
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<ICdcConnectorTemplateRenderer, CdcConnectorTemplateRenderer>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                ICdcConnectorTemplateEffectiveConfigValidator,
                CdcConnectorTemplateEffectiveConfigValidator
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<ICdcConnectorTemplateService, CdcConnectorTemplateService>()
        );

        return services;
    }
}
