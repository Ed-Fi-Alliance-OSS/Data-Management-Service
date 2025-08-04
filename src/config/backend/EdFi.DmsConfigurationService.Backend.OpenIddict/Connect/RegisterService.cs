// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.Ods.AdminApi.Common.Infrastructure;
using EdFi.Ods.AdminApi.Common.Infrastructure.Security;
using FluentValidation;
using FluentValidation.Results;
using OpenIddict.Abstractions;
using Swashbuckle.AspNetCore.Annotations;

namespace EdFi.Ods.AdminApi.Features.Connect;

public interface IRegisterService
{
    Task<bool> Handle(RegisterService.RegisterClientRequest request);
}

public partial class RegisterService(IConfiguration configuration, RegisterService.Validator validator, IOpenIddictApplicationManager applicationManager) : IRegisterService
{
    private readonly IConfiguration _configuration = configuration;
    private readonly Validator _validator = validator;
    private readonly IOpenIddictApplicationManager _applicationManager = applicationManager;

    public async Task<bool> Handle(RegisterClientRequest request)
    {
        //TODO Revisar
        // if (!await RegistrationIsEnabledOrNecessary())
        //    return false;

        await _validator.GuardAsync(request);

        var existingApp = await _applicationManager.FindByClientIdAsync(request.ClientId!);
        if (existingApp != null)
            throw new ValidationException([new ValidationFailure(nameof(request.ClientId), $"ClientId {request.ClientId} already exists")]);

        var application = new OpenIddictApplicationDescriptor
        {
            ClientId = request.ClientId,
            ClientSecret = request.ClientSecret,
            DisplayName = request.DisplayName,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials
            },
        };
        foreach (var scopeValue in SecurityConstants.Scopes.AllScopes)
        {
            application.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scopeValue.Scope);
        }

        await _applicationManager.CreateAsync(application);
        return true;
    }

    private async Task<bool> RegistrationIsEnabledOrNecessary()
    {
        var registrationIsEnabled = _configuration.GetValue<bool>("Authentication:AllowRegistration");
        return await Task.FromResult(registrationIsEnabled);
    }

    public partial class Validator : AbstractValidator<RegisterClientRequest>
    {
        public Validator()
        {
            RuleFor(m => m.ClientId).NotEmpty();
            RuleFor(m => m.ClientSecret)
                .NotEmpty()
                .Matches(ClientSecretValidatorRegex())
                .WithMessage(FeatureConstants.ClientSecretValidationMessage);
            RuleFor(m => m.DisplayName).NotEmpty();
        }

        [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z\d]).{32,128}$")]
        private static partial Regex ClientSecretValidatorRegex();
    }

    [SwaggerSchema(Title = "RegisterClientRequest")]
    public class RegisterClientRequest
    {
        [SwaggerSchema(Description = "Client id", Nullable = false)]
        public string? ClientId { get; set; }
        [SwaggerSchema(Description = "Client secret", Nullable = false)]
        public string? ClientSecret { get; set; }
        [SwaggerSchema(Description = "Client display name", Nullable = false)]
        public string? DisplayName { get; set; }
    }
}
