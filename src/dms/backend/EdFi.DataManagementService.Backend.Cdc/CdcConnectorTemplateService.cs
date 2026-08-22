// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Cdc;

public interface ICdcConnectorTemplateService
{
    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);

    CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );

    CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    );
}

internal sealed class CdcConnectorTemplateService(
    ICdcConnectorTemplateRenderer renderer,
    ICdcConnectorTemplateEffectiveConfigValidator effectiveConfigValidator
) : ICdcConnectorTemplateService
{
    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request) => renderer.Render(request);

    public CdcConnectorTemplateResult ValidateRegistrationPreflight(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) => effectiveConfigValidator.ValidateEffectiveConfig(request, CdcConnectorTemplateSourcePhase.Preflight);

    public CdcConnectorTemplateResult ValidateLiveReadBack(
        CdcConnectorTemplateEffectiveConfigValidationRequest request
    ) =>
        effectiveConfigValidator.ValidateEffectiveConfig(
            request,
            CdcConnectorTemplateSourcePhase.LiveReadBack
        );
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
