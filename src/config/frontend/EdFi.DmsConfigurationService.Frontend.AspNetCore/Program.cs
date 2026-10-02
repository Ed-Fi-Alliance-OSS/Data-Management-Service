// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Before AddServices, because AddServices reads configuration and composes against what this returns.
// A deployment that allowlisted nothing gets LoadedPlugins.Empty without the loader touching the
// filesystem, which keeps a plugin-free boot on exactly the path it took before.
//
// The configuration phase runs as soon as loading returns, because AddServices is the first reader
// of a value a plugin can supply: its first line configures logging from the Serilog section.
LoadedPlugins loadedPlugins = PluginLoader.Load(
    builder.Configuration,
    CmsPluginContracts.Registry.ContractAssemblyNames
);
loadedPlugins.ContributeConfiguration(builder.Configuration);

builder.AddServices(loadedPlugins);
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Add CORS policy to allow Swagger UI to access the Configuration Service
string swaggerUiOrigin =
    builder.Configuration.GetValue<string>("Cors:SwaggerUIOrigin") ?? "http://localhost:8082";
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowSwaggerUI",
        policy =>
        {
            policy.WithOrigins(swaggerUiOrigin).AllowAnyHeader().AllowAnyMethod();
        }
    );
});

var reverseProxySettings =
    builder.Configuration.GetSection("AppSettings:ReverseProxy").Get<ReverseProxySettings>()
    ?? new ReverseProxySettings();
var useReverseProxyHeaders = reverseProxySettings.UseForwardedHeaders;
if (useReverseProxyHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeadersConfigurator.Configure(options, reverseProxySettings)
    );
}

// A Minimal API binding failure (malformed JSON body, unparsable route/query/header parameter) can be
// written as a bodiless 400 that never reaches GlobalExceptionHandler. The default behavior varies by
// environment (Development throws via its auto-registered exception page; Test/Production leave it
// bodiless), so this makes binding failures reach the handler consistently in every environment. Safe
// because GlobalExceptionHandler never surfaces exception.Message — only fixed, sanitized text.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

var app = builder.Build();

// Immediately after the container exists and before anything else resolves from it, so a plugin
// registration problem stops startup before any schema deployment and before a request can be served.
await AuditPluginRegistrations(app);

var pathBase = app.Configuration.GetValue<string>("AppSettings:PathBase");
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase($"/{pathBase.Trim('/')}");
}

if (useReverseProxyHeaders)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseMiddleware<RequestLoggingMiddleware>();

// The exception boundary must wrap tenant resolution and the invalid-configuration short-circuit so
// that any unexpected exception in those middlewares is shaped by GlobalExceptionHandler instead of
// surfacing as an unshaped framework 500. RequestLoggingMiddleware stays outermost, so a handled 500
// is still logged exactly once as HttpRequestFailed via IExceptionHandlerFeature.
app.UseExceptionHandler(o => { });

app.UseMiddleware<TenantResolutionMiddleware>();

// Deliberately validated outside ReportInvalidConfiguration: that gate installs reporting middleware
// and lets the host keep running, whereas a rejected connection-string encryption key must stop
// startup before any schema deployment. Resolving it here also means a rejected key stops startup
// even when another configuration section is invalid as well, and it runs every DatabaseSettings
// rule, so a blank DatabaseConnection stops startup too. The unhandled OptionsValidationException
// exits non-zero and carries the validator's failure text. It is left unhandled rather than following
// the LogCritical + Environment.Exit(-1) pattern used further down this file because
// DatabaseOptionsStartupTests asserts on that exception, and terminating the process instead would
// make the behavior untestable under WebApplicationFactory.
_ = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

// After the plugin audit, so a plugin registration the host refuses is reported as one first, and after
// DatabaseOptions, because the self-contained manager's repository reads those options when it is
// constructed: a rejected key must stop startup with the validator's text, not as a manager that could
// not be constructed. Before ReportInvalidConfiguration and the database work, so a host that cannot
// revoke tokens never starts serving (DMS-1327 D-12).
EnsureTokenRevocationSupport(app);

if (!ReportInvalidConfiguration(app))
{
    InitializeDatabase(app);
    await InitializeClaimsData(app);
}

app.UseRouting();

// Shape framework-generated bodiless error responses into the Ed-Fi contract. Placed after routing
// but before CORS/authentication/authorization so it wraps the auth short-circuits and the endpoint
// terminal, independent of route and authentication scheme.
app.UseMiddleware<FrameworkErrorResponseMiddleware>();

app.UseCors("AllowSwaggerUI");
app.UseAuthentication();
app.UseAuthorization();

// Reject JSON requests declaring an unsupported charset with the Ed-Fi 415 contract before body
// binding reads them. Placed after authorization so authentication and authorization failures keep
// their 401/403 responses.
app.UseMiddleware<JsonCharsetValidationMiddleware>();

app.MapRouteEndpoints();
app.MapOpenApi();
await app.RunAsync();

/// <summary>
/// Emits the plugin inventory, then runs the plugin registration checks that can only be made once the
/// container exists, all over the one audit input AddServices registered after every plugin hook had
/// run. Every finding is written to Console.Error and any finding stops startup. This is deliberately
/// not reported through ReportInvalidConfigurationMiddleware: a refused plugin registration is not a
/// configuration section the host can keep running with.
/// </summary>
async Task AuditPluginRegistrations(WebApplication app)
{
    PluginAuditInput auditInput = app.Services.GetRequiredService<PluginAuditInput>();

    // First, so what each plugin brought into the process reaches an operator before any check below
    // can abort startup naming a type rather than the plugin that supplied it. The per-file loaded
    // flags are read at this moment, because an assembly first touched inside a contribution hook
    // loaded after loading returned.
    PluginInventoryLog.Emit(app.Logger, auditInput);

    // Before the shared audit, whose last step activates the declared contracts: a registration this
    // host has already refused on its shape is never constructed.
    await AbortOnPluginRegistrationProblems(
        PluginContractShapeCheck.Check(auditInput),
        activationException: null
    );

    PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(auditInput, app.Services);

    if (result.ScopeCleanupFailure is not null)
    {
        // A warning rather than a failure, and reported separately so it can neither stand in for nor
        // hide an activation failure. Releasing the probe's scope runs implementer code, and the
        // container abandons the rest of a scope's disposables after the first one throws.
        app.Logger.LogWarning(
            result.ScopeCleanupFailure,
            "Releasing the plugin activation scope threw. Any instance the scope had not yet released "
                + "stays unreleased; this did not affect the plugin registration checks"
        );
    }

    await AbortOnPluginRegistrationProblems(
        [.. result.Findings.Select(finding => finding.Message)],
        // The first original activation exception, where there was one. The rest travel in the
        // messages; a wrapper that dropped every one of them would leave an operator with a
        // description of the failure and no stack.
        result
            .Findings.Select(finding => finding.ActivationException)
            .FirstOrDefault(activationException => activationException is not null)
    );

    // After the shared audit, so an activation failure is reported as one rather than surfacing here
    // as a resolve that throws. A factory that returned null passed that activation.
    await AbortOnPluginRegistrationProblems(
        PluginContractShapeCheck.CheckResolvedInstances(auditInput, app.Services),
        activationException: null
    );
}

/// <summary>
/// Writes each plugin registration problem to Console.Error and stops startup if there is one.
/// </summary>
async Task AbortOnPluginRegistrationProblems(IReadOnlyList<string> problems, Exception? activationException)
{
    if (problems.Count == 0)
    {
        return;
    }

    foreach (string problem in problems)
    {
        await Console.Error.WriteLineAsync($"Plugin registration problem: {problem}");
    }

    throw new InvalidOperationException(
        $"Startup aborted: {problems.Count} plugin registration problem(s). Correct the plugin, "
            + "or remove it from Plugins:Allowed, then restart: "
            + string.Join(" | ", problems),
        activationException
    );
}

/// <summary>
/// Stops startup unless an ITokenRevocationManager can be constructed for the configured identity
/// provider, so POST /connect/revoke can never be served by a host that cannot revoke (DMS-1327 D-12).
/// Construction only: neither manager performs I/O in its constructor, so this adds no database or
/// identity provider availability requirement; a provider outage is answered 503 per request instead.
/// </summary>
/// <remarks>
/// A construction failure is reported by exception type names alone, and the caught exception is
/// neither logged nor wrapped: a registration factory can come from a plugin, and its message can carry
/// anything, a connection string or a secret included (D-15).
/// </remarks>
void EnsureTokenRevocationSupport(WebApplication app)
{
    string provider = TokenRevocationProviderName(app.Configuration["AppSettings:IdentityProvider"]);
    Exception? constructionFailure = null;
    ITokenRevocationManager? revocationManager = null;

    try
    {
        // In a scope: the Keycloak manager depends on the scoped KeycloakContext, and the root provider
        // rejects scoped services when scope validation is on. Releasing the scope is inside the try
        // too, because it runs the disposal of whatever the registration constructed.
        using IServiceScope scope = app.Services.CreateScope();
        revocationManager = scope.ServiceProvider.GetService<ITokenRevocationManager>();
    }
    catch (Exception exception)
    {
        constructionFailure = exception;
    }

    if (constructionFailure is not null)
    {
        string exceptionTypes = ExceptionTypeChain(constructionFailure);
        app.Logger.LogCritical(
            "The token revocation manager for AppSettings:IdentityProvider '{Provider}' could not be "
                + "constructed ({ExceptionTypes}). Correct the registration or its dependencies, then restart.",
            provider,
            exceptionTypes
        );
        throw new InvalidOperationException(
            $"The token revocation manager for AppSettings:IdentityProvider '{provider}' could not be "
                + $"constructed ({exceptionTypes}). Correct the registration or its dependencies, then restart."
        );
    }

    if (revocationManager is null)
    {
        app.Logger.LogCritical(
            "No token revocation manager is registered for AppSettings:IdentityProvider '{Provider}'. "
                + "Register one for this provider or correct the setting, then restart.",
            provider
        );
        throw new InvalidOperationException(
            $"No token revocation manager is registered for AppSettings:IdentityProvider '{provider}'. "
                + "Register one for this provider or correct the setting, then restart."
        );
    }
}

/// <summary>
/// The configured identity provider as a fixed literal, so the startup check never logs configuration
/// text. AddServices has already refused any other value by the time the check runs.
/// </summary>
static string TokenRevocationProviderName(string? configured) =>
    configured?.ToLowerInvariant() switch
    {
        "keycloak" => "keycloak",
        "self-contained" => "self-contained",
        _ => "unrecognized",
    };

/// <summary>
/// The full type names of an exception and each inner exception, outermost first. Only type names are
/// read: no message, data or stack trace.
/// </summary>
static string ExceptionTypeChain(Exception exception)
{
    List<string> names = [];
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        names.Add(current.GetType().FullName ?? current.GetType().Name);
    }

    return string.Join(" -> ", names);
}

/// <summary>
/// Triggers configuration validation. If configuration is invalid, injects a short-circuit middleware to report.
/// Returns true if the middleware was injected.
/// </summary>
bool ReportInvalidConfiguration(WebApplication app)
{
    try
    {
        // Accessing IOptions<T> forces validation
        _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<IdentitySettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ReverseProxySettings>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<ReportInvalidConfigurationMiddleware>(ex.Failures);
        return true;
    }
    return false;
}

void InitializeDatabase(WebApplication app)
{
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Running initial database deploy");
        try
        {
            var result = app
                .Services.GetRequiredService<IDatabaseDeploy>()
                .DeployDatabase(
                    app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.DatabaseConnection
                );
            if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
            {
                app.Logger.LogCritical(failure.Error, "Database Deploy Failure");
                Environment.Exit(-1);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Database Deploy Failure");
            Environment.Exit(-1);
        }
    }
}

/// <summary>
/// Initializes claims data at application startup if database deployment is enabled and tables are empty,
/// loading initial claim sets and hierarchy from the configured claims provider.
/// </summary>
async Task InitializeClaimsData(WebApplication app)
{
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Checking if initial claims data needs to be loaded");
        try
        {
            // IClaimsDataLoader is scoped (it reaches the scoped ITenantContextProvider through its
            // repositories), so startup resolution needs its own scope: the root provider rejects
            // scoped services when scope validation is on.
            using IServiceScope claimsLoaderScope = app.Services.CreateScope();
            IClaimsDataLoader claimsLoader =
                claimsLoaderScope.ServiceProvider.GetRequiredService<IClaimsDataLoader>();
            ClaimsDataLoadResult result = await claimsLoader.LoadInitialClaimsAsync();

            switch (result)
            {
                case ClaimsDataLoadResult.Success success:
                    app.Logger.LogInformation(
                        "Successfully loaded {ClaimSetCount} claim sets and hierarchy data",
                        success.ClaimSetsLoaded
                    );
                    break;
                case ClaimsDataLoadResult.AlreadyLoaded:
                    app.Logger.LogInformation("Claims data already exists, skipping initial load");
                    break;
                case ClaimsDataLoadResult.ValidationFailure validationFailure:
                    app.Logger.LogCritical(
                        "Claims data validation failed: {Errors}",
                        string.Join("; ", validationFailure.Errors)
                    );
                    Environment.Exit(-1);
                    break;
                case ClaimsDataLoadResult.DatabaseFailure databaseFailure:
                    app.Logger.LogCritical(
                        "Database error loading claims: {Error}",
                        databaseFailure.ErrorMessage
                    );
                    Environment.Exit(-1);
                    break;
                case ClaimsDataLoadResult.UnexpectedFailure unexpectedFailure:
                    app.Logger.LogCritical(
                        unexpectedFailure.Exception,
                        "Unexpected error loading claims: {Error}",
                        unexpectedFailure.ErrorMessage
                    );
                    Environment.Exit(-1);
                    break;
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Failed to initialize claims data");
            Environment.Exit(-1);
        }
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
