// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NUnit.Framework;
using Serilog;
using static EdFi.DataManagementService.Backend.PartitionUtility;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.CoreBatch;

/// <summary>
/// Provides an end-to-end harness that wires ApiService to the real PostgreSQL backend so tests can
/// execute batch requests through the same middleware pipeline used in production.
/// </summary>
public abstract class CoreBatchIntegrationTestBase : DatabaseTest
{
    protected const string ProjectName = "Test";
    protected const string ProjectEndpoint = "test";
    protected const string StudentResource = "Student";
    protected const string StudentEndpoint = "students";
    protected const string ClaimSetName = "BatchIntegrationClaims";

    private static readonly Guid ReferentialNamespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    private readonly string _issuer = "https://batch.test";
    private readonly string _audience = "batch-audience";

    protected IApiService ApiService = null!;
    protected ServiceProvider ServiceProvider = null!;
    protected AppSettings AppSettings = null!;
    protected TestClaimSetProvider ClaimSetProvider = null!;

    private SigningCredentials _signingCredentials = null!;
    private string _authorizationToken = null!;

    [SetUp]
    public async Task SetUpCoreBatchHarness()
    {
        ClaimSetProvider = new TestClaimSetProvider();
        ClaimSetProvider.SetClaimSets(
            new List<ClaimSet>
            {
                CreateClaimSet(ClaimSetName, StudentResource, "Create", "Read", "Update", "Delete"),
            }
        );

        JsonNode apiSchema = BuildApiSchema();
        var staticApiSchemaProvider = new StaticApiSchemaProvider(apiSchema);

        AppSettings = new AppSettings
        {
            AllowIdentityUpdateOverrides = string.Empty,
            MaskRequestBodyInLogs = false,
            BatchMaxOperations = 10,
            MaximumPageSize = 200,
            UseApiSchemaPath = false,
            AuthenticationService = "mock",
            EnableManagementEndpoints = true,
        };

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });

            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton<IOptions<AppSettings>>(Options.Create(AppSettings));
        services.AddSingleton(AppSettings);
        RegisterJwtAuthenticationMiddleware(services);
        services.AddSingleton<IClaimSetProvider>(ClaimSetProvider);
        services.AddMemoryCache();
        services.AddSingleton<ClaimSetsCache>(sp =>
        {
            var memoryCache = sp.GetRequiredService<IMemoryCache>();
            return new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(30));
        });

        ConfigureJwtAuthentication(services);

        LoggerConfiguration configurationLogger = new LoggerConfiguration().MinimumLevel.Warning();
        Serilog.ILogger serilogLogger = configurationLogger.CreateLogger();

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CircuitBreaker:FailureRatio"] = "0.5",
                    ["CircuitBreaker:SamplingDurationSeconds"] = "30",
                    ["CircuitBreaker:MinimumThroughput"] = "20",
                    ["CircuitBreaker:BreakDurationSeconds"] = "5",
                }
            )
            .Build();

        services.AddDmsDefaultConfiguration(
            serilogLogger,
            configuration.GetSection("CircuitBreaker"),
            AppSettings.MaskRequestBodyInLogs
        );

        services.AddSingleton<IApiSchemaProvider>(staticApiSchemaProvider);

        services.AddSingleton<IOptions<DatabaseOptions>>(
            Options.Create(
                new DatabaseOptions
                {
                    IsolationLevel = ConfiguredIsolationLevel,
                    DocumentUpdateStrategy = DocumentUpdateStrategy.FullDocument,
                }
            )
        );

        services.AddPostgresqlDatastore(Configuration.DatabaseConnectionString ?? string.Empty);
        services.AddPostgresqlQueryHandler();

        ServiceProvider = services.BuildServiceProvider();
        ApiService = ServiceProvider.GetRequiredService<IApiService>();

        _authorizationToken = CreateBearerToken();

        // Force schema load from the temporary directory so every test starts with a clean schema snapshot.
        await ApiService.ReloadApiSchemaAsync();
    }

    [TearDown]
    public async Task TearDownCoreBatchHarness()
    {
        if (ServiceProvider != null)
        {
            await ServiceProvider.DisposeAsync();
        }
    }

    protected static ClaimSet CreateClaimSet(
        string claimSetName,
        string resourceName,
        params string[] actions
    )
    {
        List<ResourceClaim> resourceClaims = new();
        foreach (string action in actions)
        {
            resourceClaims.Add(
                new ResourceClaim(
                    Name: $"{Conventions.EdFiOdsResourceClaimBaseUri}/{ProjectEndpoint}/{resourceName}",
                    Action: action,
                    AuthorizationStrategies:
                    [
                        new AuthorizationStrategy(
                            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                        ),
                    ]
                )
            );
        }

        return new ClaimSet(claimSetName, resourceClaims);
    }

    protected void SetAuthorizedActions(params string[] actions)
    {
        ClaimSetProvider.SetClaimSets([CreateClaimSet(ClaimSetName, StudentResource, actions)]);
    }

    protected async Task<IFrontendResponse> ExecuteBatchAsync(params JsonObject[] operations)
    {
        JsonArray payload = new();
        foreach (JsonObject operation in operations)
        {
            payload.Add(operation);
        }

        var request = new FrontendRequest(
            Path: "/batch",
            Body: payload.ToJsonString()
                ?? throw new InvalidOperationException("Unable to serialize batch payload."),
            Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {_authorizationToken}" },
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId(Guid.NewGuid().ToString())
        );

        return await ApiService.ExecuteBatchAsync(request);
    }

    protected static JsonObject CreateCreateOperation(string studentUniqueId, string givenName)
    {
        return CreateBatchOperation(
            op: "create",
            resource: StudentEndpoint,
            document: CreateStudentDocument(studentUniqueId, givenName, etag: null)
        );
    }

    protected static JsonObject CreateUpdateOperation(
        Guid documentId,
        string etag,
        string studentUniqueId,
        string givenName
    )
    {
        return CreateBatchOperation(
            op: "update",
            resource: StudentEndpoint,
            documentId: documentId,
            document: CreateStudentDocument(studentUniqueId, givenName, etag),
            ifMatch: etag
        );
    }

    protected static JsonObject CreateUpdateByNaturalKeyOperation(
        string naturalKeyValue,
        string? etag,
        string studentUniqueId,
        string givenName
    )
    {
        var naturalKey = new JsonObject { ["studentUniqueId"] = naturalKeyValue };
        return CreateBatchOperation(
            op: "update",
            resource: StudentEndpoint,
            naturalKey: naturalKey,
            document: CreateStudentDocument(studentUniqueId, givenName, etag),
            ifMatch: etag
        );
    }

    protected static JsonObject CreateDeleteOperation(Guid documentId)
    {
        return CreateBatchOperation(op: "delete", resource: StudentEndpoint, documentId: documentId);
    }

    protected static JsonObject CreateDeleteByNaturalKeyOperation(string naturalKeyValue)
    {
        var naturalKey = new JsonObject { ["studentUniqueId"] = naturalKeyValue };
        return CreateBatchOperation(op: "delete", resource: StudentEndpoint, naturalKey: naturalKey);
    }

    private static JsonObject CreateBatchOperation(
        string op,
        string resource,
        JsonObject? document = null,
        Guid? documentId = null,
        JsonObject? naturalKey = null,
        string? ifMatch = null
    )
    {
        var operation = new JsonObject { ["op"] = op, ["resource"] = resource };

        if (document != null)
        {
            operation["document"] = document;
        }

        if (documentId.HasValue)
        {
            operation["documentId"] = documentId.Value.ToString();
        }

        if (naturalKey != null)
        {
            operation["naturalKey"] = naturalKey;
        }

        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            operation["ifMatch"] = ifMatch;
        }

        return operation;
    }

    protected static JsonObject CreateStudentDocument(string studentUniqueId, string givenName, string? etag)
    {
        var document = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["givenName"] = givenName };

        if (!string.IsNullOrWhiteSpace(etag))
        {
            document["_etag"] = etag;
        }

        return document;
    }

    protected async Task<Guid> InsertStudentAsync(string studentUniqueId, string givenName)
    {
        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation(studentUniqueId, givenName)
        );
        response.StatusCode.Should().Be(200);

        JsonArray body = response.Body!.AsArray();
        body.Should().HaveCount(1);

        return Guid.Parse(body[0]!["documentId"]!.GetValue<string>());
    }

    protected async Task<JsonObject?> GetStudentDocumentAsync(Guid documentUuid)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            ConfiguredIsolationLevel
        );

        IGetRequest getRequest = CreateGetRequestForProject(documentUuid);
        var result = await CreateGetById().GetById(getRequest, connection, transaction);
        if (result is GetResult.GetSuccess success)
        {
            JsonNode? parsed = success.EdfiDoc.DeepClone();
            return parsed?.AsObject();
        }

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        return null;
    }

    protected async Task<string> GetStudentEtagAsync(Guid documentUuid)
    {
        JsonObject? document = await GetStudentDocumentAsync(documentUuid);
        document.Should().NotBeNull();
        return document!["_etag"]!.GetValue<string>();
    }

    protected async Task<bool> StudentExistsAsync(string studentUniqueId)
    {
        Guid referentialId = CreateReferentialId(studentUniqueId);
        PartitionKey partitionKey = PartitionKeyFor(new ReferentialId(referentialId));

        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            ConfiguredIsolationLevel
        );

        var document = await CreateSqlAction()
            .FindDocumentByReferentialId(
                new ReferentialId(referentialId),
                partitionKey,
                connection,
                transaction,
                traceId: new TraceId(Guid.NewGuid().ToString())
            );

        return document != null;
    }

    protected static Guid CreateReferentialId(string studentUniqueId)
    {
        string resourceSegment = $"{ProjectName}{StudentResource}";
        string identitySegment = $"$.studentUniqueId={studentUniqueId}";
        return Deterministic.Create(ReferentialNamespace, resourceSegment + identitySegment);
    }

    private static IGetRequest CreateGetRequestForProject(Guid documentUuid, TraceId? traceId = null)
    {
        traceId ??= new TraceId("NotProvided");

        return (
            new
            {
                ResourceInfo = CreateResourceInfo(StudentResource, projectName: ProjectName),
                TraceId = traceId,
                DocumentUuid = new DocumentUuid(documentUuid),
                ResourceAuthorizationHandler = new ResourceAuthorizationHandler(
                    [],
                    [],
                    new NoAuthorizationServiceFactory(),
                    NullLogger.Instance
                ),
            }
        ).ActLike<IGetRequest>();
    }

    private void ConfigureJwtAuthentication(IServiceCollection services)
    {
        byte[] signingKeyBytes = RandomNumberGenerator.GetBytes(32);
        var signingKey = new SymmetricSecurityKey(signingKeyBytes);
        _signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        OpenIdConnectConfiguration oidcConfiguration = new() { Issuer = _issuer };
        oidcConfiguration.SigningKeys.Add(signingKey);

        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(
            new StaticOpenIdConfigurationManager(oidcConfiguration)
        );

        services.Configure<JwtAuthenticationOptions>(options =>
        {
            options.Audience = _audience;
            options.Authority = _issuer;
            options.MetadataAddress = $"{_issuer}/.well-known/openid-configuration";
            options.RequireHttpsMetadata = false;
            options.RoleClaimType = "role";
            options.ClockSkewSeconds = 0;
        });

        RegisterJwtValidationService(services);
    }

    private static void RegisterJwtAuthenticationMiddleware(IServiceCollection services)
    {
        Type middlewareType = GetCoreType(
            "EdFi.DataManagementService.Core.Middleware.JwtAuthenticationMiddleware"
        );
        services.AddSingleton(middlewareType, sp => ActivatorUtilities.CreateInstance(sp, middlewareType));
    }

    private static void RegisterJwtValidationService(IServiceCollection services)
    {
        Type jwtValidationServiceType = GetCoreType(
            "EdFi.DataManagementService.Core.Security.JwtValidationService"
        );
        Type jwtValidationInterfaceType = GetCoreType(
            "EdFi.DataManagementService.Core.Security.IJwtValidationService"
        );

        services.AddSingleton(
            jwtValidationInterfaceType,
            sp => ActivatorUtilities.CreateInstance(sp, jwtValidationServiceType)
        );
    }

    private string CreateBearerToken()
    {
        var now = DateTime.UtcNow;
        List<Claim> claims = [new Claim("scope", ClaimSetName), new Claim("jti", Guid.NewGuid().ToString())];

        var descriptor = new SecurityTokenDescriptor
        {
            Audience = _audience,
            Issuer = _issuer,
            Expires = now.AddHours(1),
            NotBefore = now.AddMinutes(-1),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = _signingCredentials,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    private static JsonNode BuildApiSchema()
    {
        JsonObject studentSchema = BuildStudentResourceSchema();

        JsonObject projectSchema = new()
        {
            ["projectName"] = ProjectName,
            ["projectVersion"] = "1.0.0",
            ["description"] = "Batch integration schema",
            ["projectEndpointName"] = ProjectEndpoint,
            ["isExtensionProject"] = false,
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject { ["students"] = "students" },
            ["resourceNameMapping"] = new JsonObject { [StudentResource] = "students" },
            ["resourceSchemas"] = new JsonObject { ["students"] = studentSchema },
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
        };

        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }

    private static JsonObject BuildStudentResourceSchema()
    {
        JsonObject jsonSchemaForInsert = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                ["givenName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 60 },
            },
            ["required"] = new JsonArray { "studentUniqueId", "givenName" },
            ["additionalProperties"] = false,
        };

        JsonObject documentPathsMapping = new()
        {
            ["StudentUniqueId"] = new JsonObject
            {
                ["isReference"] = false,
                ["isDescriptor"] = false,
                ["path"] = "$.studentUniqueId",
            },
            ["GivenName"] = new JsonObject
            {
                ["isReference"] = false,
                ["isDescriptor"] = false,
                ["path"] = "$.givenName",
            },
        };

        JsonObject queryFieldMapping = new()
        {
            ["studentUniqueId"] = new JsonArray
            {
                new JsonObject { ["path"] = "$.studentUniqueId", ["type"] = "string" },
            },
        };

        JsonObject securableElements = new()
        {
            ["Namespace"] = new JsonArray(),
            ["EducationOrganization"] = new JsonArray(),
            ["Student"] = new JsonArray { "$.studentUniqueId" },
            ["Contact"] = new JsonArray(),
            ["Staff"] = new JsonArray(),
        };

        return new JsonObject
        {
            ["resourceName"] = StudentResource,
            ["allowIdentityUpdates"] = false,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSchoolYearEnumeration"] = false,
            ["isSubclass"] = false,
            ["subclassType"] = string.Empty,
            ["superclassResourceName"] = string.Empty,
            ["superclassProjectName"] = string.Empty,
            ["superclassIdentityJsonPath"] = string.Empty,
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
            ["equalityConstraints"] = new JsonArray(),
            ["booleanJsonPaths"] = new JsonArray(),
            ["numericJsonPaths"] = new JsonArray(),
            ["dateJsonPaths"] = new JsonArray(),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = documentPathsMapping,
            ["queryFieldMapping"] = queryFieldMapping,
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["authorizationPathways"] = new JsonArray(),
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["securableElements"] = securableElements,
        };
    }

    protected sealed class TestClaimSetProvider : IClaimSetProvider
    {
        private IList<ClaimSet> _claimSets = new List<ClaimSet>();

        public Task<IList<ClaimSet>> GetAllClaimSets()
        {
            return Task.FromResult(_claimSets);
        }

        public void SetClaimSets(IList<ClaimSet> claimSets)
        {
            _claimSets = claimSets;
        }
    }

    private sealed class StaticOpenIdConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        public StaticOpenIdConfigurationManager(OpenIdConnectConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            return Task.FromResult(_configuration);
        }

        public void RequestRefresh() { }
    }

    private sealed class StaticApiSchemaProvider : IApiSchemaProvider
    {
        private ApiSchemaDocumentNodes _schemaNodes;

        public StaticApiSchemaProvider(JsonNode schema)
        {
            _schemaNodes = new ApiSchemaDocumentNodes(schema, []);
            ApiSchemaFailures = [];
        }

        public ApiSchemaDocumentNodes GetApiSchemaNodes() => _schemaNodes;

        public Guid ReloadId { get; private set; } = Guid.NewGuid();

        public bool IsSchemaValid => true;

        public List<ApiSchemaFailure> ApiSchemaFailures { get; }

        public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync()
        {
            ReloadId = Guid.NewGuid();
            return Task.FromResult(new ApiSchemaLoadStatus(true, []));
        }

        public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(
            JsonNode coreSchema,
            JsonNode[] extensionSchemas
        )
        {
            _schemaNodes = new ApiSchemaDocumentNodes(coreSchema, extensionSchemas);
            ReloadId = Guid.NewGuid();
            return Task.FromResult(new ApiSchemaLoadStatus(true, []));
        }
    }

    private static Type GetCoreType(string fullName)
    {
        Assembly coreAssembly = AppDomain
            .CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "EdFi.DataManagementService.Core");
        return coreAssembly.GetType(fullName, throwOnError: true)!
            ?? throw new InvalidOperationException($"Unable to find type '{fullName}'.");
    }
}
