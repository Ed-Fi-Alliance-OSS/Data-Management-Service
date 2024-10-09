// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using System.Security.Claims;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webApplicationBuilder)
    {
        // For FluentValidation
        var executingAssembly = Assembly.GetExecutingAssembly();

        webApplicationBuilder.Services
         .AddValidatorsFromAssembly(executingAssembly)
         .AddFluentValidationAutoValidation();
        ValidatorOptions.Global.DisplayNameResolver = (type, memberInfo, expression)
                    => memberInfo?
                        .GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetName();
        webApplicationBuilder.Services.AddSingleton<IValidateOptions<IdentitySettings>, IdentitySettingsValidator>();

        // For Security(Keycloak)
        IConfiguration config = webApplicationBuilder.Configuration;
        var settings = config.GetSection("IdentitySettings");
        var identitySettings = ReadSettings();

        webApplicationBuilder.Services.Configure<IdentitySettings>(settings);

        webApplicationBuilder.Services.AddScoped(x =>
            new KeycloakContext(
            identitySettings.IdentityServer,
            identitySettings.Realm,
            identitySettings.ClientId,
            identitySettings.ClientSecret,
            identitySettings.RoleClaimType,
            identitySettings.ServiceRole));

        webApplicationBuilder.Services.AddHttpClient();

        webApplicationBuilder.Services.AddTransient<IClientRepository, ClientRepository>();
        webApplicationBuilder.Services.AddTransient<ITokenManager, TokenManager>();

        var metadataAddress = $"{identitySettings.Authority}/.well-known/openid-configuration";

        webApplicationBuilder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = metadataAddress;
                options.Authority = identitySettings.Authority;
                options.Audience = identitySettings.Audience;
                options.RequireHttpsMetadata = identitySettings.RequireHttpsMetadata;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    RoleClaimType = identitySettings.RoleClaimType
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        Console.WriteLine($"Authentication failed: {context.Exception.Message}");
                        return Task.CompletedTask;
                    }
                };
            });
        webApplicationBuilder.Services.AddAuthorization(options => options.AddPolicy(SecurityConstants.ServicePolicy,
            policy => policy.RequireClaim(ClaimTypes.Role, identitySettings.ServiceRole)));

        IdentitySettings ReadSettings()
        {
            return new IdentitySettings
            {
                Authority = config.GetValue<string>("IdentitySettings:Authority")!,
                IdentityServer = config.GetValue<string>("IdentitySettings:IdentityServer")!,
                Realm = config.GetValue<string>("IdentitySettings:Realm")!,
                ClientId = config.GetValue<string>("IdentitySettings:ClientId")!,
                ClientSecret = config.GetValue<string>("IdentitySettings:ClientSecret")!,
                RequireHttpsMetadata = config.GetValue<bool>("IdentitySettings:RequireHttpsMetadata"),
                Audience = config.GetValue<string>("IdentitySettings:Audience")!,
                RoleClaimType = config.GetValue<string>("IdentitySettings:RoleClaimType")!,
                ServiceRole = config.GetValue<string>("IdentitySettings:ServiceRole")!
            };
        }
    }
}
