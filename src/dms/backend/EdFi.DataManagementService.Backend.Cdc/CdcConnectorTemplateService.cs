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
    CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult);

    CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    );

    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);
}

internal sealed class CdcConnectorTemplateService(
    ICdcConnectorTemplateInputValidator inputValidator,
    ICdcConnectorTemplateRenderer renderer
) : ICdcConnectorTemplateService
{
    public CdcProviderSetupReadiness GetProviderSetupReadiness(CdcProviderSetupResult providerSetupResult)
    {
        ArgumentNullException.ThrowIfNull(providerSetupResult);

        return new CdcProviderSetupReadiness(
            Provider: providerSetupResult.Provider,
            Outcome: providerSetupResult.Outcome,
            CanRenderTemplate: providerSetupResult.Outcome
                is CdcProviderSetupOutcome.CreatedOrMatched
                    or CdcProviderSetupOutcome.ExactMatch
        );
    }

    public CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    ) => inputValidator.ValidateRequest(request, sourcePhase);

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request) => renderer.Render(request);
}

public sealed record CdcProviderSetupReadiness(
    CdcProvider Provider,
    CdcProviderSetupOutcome Outcome,
    bool CanRenderTemplate
);

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
            ServiceDescriptor.Scoped<ICdcConnectorTemplateService, CdcConnectorTemplateService>()
        );

        return services;
    }
}
