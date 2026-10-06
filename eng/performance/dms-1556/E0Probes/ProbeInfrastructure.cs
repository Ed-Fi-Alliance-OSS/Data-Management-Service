// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dms1556.E0Probes;

/// <summary>
/// Stand-in for the design's AuthenticationDependencyUnavailableException: the exception a
/// failing key-store dependency would throw from configuration retrieval.
/// </summary>
public sealed class ProbeDependencyException(string message) : Exception(message);

/// <summary>
/// Thrown by the scratch provider's boundary check when no usable snapshot exists
/// (stand-in for the design's SigningKeysUnavailableException, D-4/D-5 shape).
/// </summary>
public sealed class ScratchKeysUnavailableException(string message) : Exception(message);

/// <summary>
/// SCRATCH-PROVIDER semantics, not framework behavior: the states the boundary check
/// distinguishes. ColdFailing = never loaded and the store fails; Expired = last snapshot
/// older than MaxStaleness; Usable = serve from the snapshot.
/// </summary>
public enum ScratchProviderState
{
    ColdFailing,
    Expired,
    Usable,
}

/// <summary>
/// One RSA signing key for the whole probe run: tokens are minted with the private key and
/// the scratch manager publishes the public key, exactly the shape the CMS database-key
/// mode produces.
/// </summary>
public static class ScratchSigning
{
    public const string KeyId = "e0-probe-key";

    private static readonly RSA _rsa = RSA.Create(2048);

    public static readonly RsaSecurityKey PrivateKey = new(_rsa) { KeyId = KeyId };

    public static readonly RsaSecurityKey PublicKey = new(_rsa.ExportParameters(false)) { KeyId = KeyId };

    public static OpenIdConnectConfiguration BuildConfiguration(string issuer)
    {
        OpenIdConnectConfiguration configuration = new() { Issuer = issuer };
        configuration.SigningKeys.Add(PublicKey);
        return configuration;
    }

    public static string MintToken(string issuer, string audience, TimeSpan? lifetime = null)
    {
        JsonWebTokenHandler handler = new();
        return handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(30)),
                SigningCredentials = new SigningCredentials(PrivateKey, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object> { ["sub"] = "e0-probe-client" },
            }
        );
    }
}

/// <summary>
/// Plain IConfigurationManager (never BaseConfigurationManager, per the round-1 decision):
/// records every call and its cancellation token, and behaves per the configured mode -
/// return the configuration, throw, or await a shared gate where each caller waits with
/// its OWN token. The per-waiter detachment on cancellation is SCRATCH-PROVIDER design
/// (the D-5 single-flight shape); the framework fact under probe is only which token the
/// JwtBearer handler passes in.
/// </summary>
public sealed class ScratchConfigurationManager(string issuer)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    private int _callCount;
    private int _refreshCount;
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount => Volatile.Read(ref _callCount);

    public int RefreshCount => Volatile.Read(ref _refreshCount);

    public Exception? ExceptionToThrow { get; set; }

    public bool AwaitGate { get; set; }

    public ScratchProviderState State { get; set; } = ScratchProviderState.Usable;

    public ConcurrentQueue<CancellationToken> ReceivedTokens { get; } = new();

    public ConcurrentQueue<string> WaiterOutcomes { get; } = new();

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        Interlocked.Increment(ref _callCount);
        ReceivedTokens.Enqueue(cancel);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (AwaitGate)
        {
            try
            {
                // Each waiter awaits the shared load with its own token: canceling one
                // request detaches only that waiter (scratch-provider design).
                await _gate.Task.WaitAsync(cancel);
                WaiterOutcomes.Enqueue("completed");
            }
            catch (OperationCanceledException)
            {
                WaiterOutcomes.Enqueue("canceled");
                throw;
            }
        }

        return ScratchSigning.BuildConfiguration(issuer);
    }

    public void RequestRefresh()
    {
        Interlocked.Increment(ref _refreshCount);
    }

    public void ReleaseGate()
    {
        _gate.TrySetResult();
    }
}

/// <summary>
/// Everything the probes observe from inside the pipeline.
/// </summary>
public sealed class ProbeCapture
{
    public ConcurrentQueue<Exception> AuthenticationFailures { get; } = new();

    public ConcurrentQueue<Exception?> ChallengeFailures { get; } = new();

    public ConcurrentQueue<TokenValidationParameters> ValidationParameters { get; } = new();

    public ConcurrentQueue<CancellationToken> RequestAbortedTokens { get; } = new();
}

/// <summary>
/// JsonWebTokenHandler that records the TokenValidationParameters the JwtBearer handler
/// actually hands to token validation, so V-2's claims (ConfigurationManager left null on
/// the clone; manager keys concatenated into IssuerSigningKeys) are observed, not assumed.
/// </summary>
public sealed class RecordingTokenHandler(ProbeCapture capture) : JsonWebTokenHandler
{
    public override Task<TokenValidationResult> ValidateTokenAsync(
        string token,
        TokenValidationParameters validationParameters
    )
    {
        capture.ValidationParameters.Enqueue(validationParameters);
        return base.ValidateTokenAsync(token, validationParameters);
    }
}

/// <summary>
/// A TestServer host whose Bearer scheme mirrors the CMS self-contained configuration
/// (WebApplicationBuilderExtensions.cs:335-384: Authority, MetadataAddress, SaveToken,
/// Audience, RequireHttpsMetadata=false, TVP with ValidateAudience/Issuer/IssuerSigningKey,
/// ValidIssuer=Authority, RoleClaimType) with two probe differences: the blocking
/// IssuerSigningKeyResolver is replaced by a supplied plain IConfigurationManager (D-2),
/// and the probe events are attached.
/// </summary>
public static class ProbeHost
{
    public const string Audience = "e0-probe-audience";

    public const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    public static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        ScratchConfigurationManager manager,
        ProbeCapture capture,
        string authority,
        bool translateOnAuthenticationFailed = false,
        bool boundaryProviderCheck = false,
        Func<MessageReceivedContext, Task>? onMessageReceived = null,
        bool useRecordingTokenHandler = false
    )
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder
            .Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority = authority;
                    options.MetadataAddress = $"{authority}/.well-known/openid-configuration";
                    options.SaveToken = true;
                    options.Audience = Audience;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = true,
                        ValidateIssuer = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = authority,
                        RoleClaimType = RoleClaimType,
                    };

                    // The probe difference vs. today's CMS: a plain IConfigurationManager
                    // instead of the blocking IssuerSigningKeyResolver.
                    options.ConfigurationManager = manager;

                    if (useRecordingTokenHandler)
                    {
                        options.TokenHandlers.Clear();
                        options.TokenHandlers.Add(new RecordingTokenHandler(capture));
                    }

                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = async context =>
                        {
                            if (boundaryProviderCheck && manager.State != ScratchProviderState.Usable)
                            {
                                // Boundary prototype (D-3 shape): classify unavailability
                                // via context.Fail(exception) before any retrieval.
                                context.Fail(
                                    new ScratchKeysUnavailableException(
                                        $"scratch provider state: {manager.State}"
                                    )
                                );
                                return;
                            }
                            if (onMessageReceived is not null)
                            {
                                await onMessageReceived(context);
                            }
                        },
                        OnAuthenticationFailed = context =>
                        {
                            capture.AuthenticationFailures.Enqueue(context.Exception);
                            if (translateOnAuthenticationFailed)
                            {
                                context.Fail(context.Exception);
                            }
                            return Task.CompletedTask;
                        },
                        OnChallenge = context =>
                        {
                            capture.ChallengeFailures.Enqueue(context.AuthenticateFailure);
                            if (context.AuthenticateFailure is ScratchKeysUnavailableException)
                            {
                                context.HandleResponse();
                                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                                context.Response.Headers.RetryAfter = "30";
                            }
                            return Task.CompletedTask;
                        },
                    };
                }
            );
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                capture.RequestAbortedTokens.Enqueue(context.RequestAborted);
                await next();
            }
        );
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/probe", () => Results.Ok(new { ok = true })).RequireAuthorization();

        await app.StartAsync();
        return (app, app.GetTestClient());
    }
}
