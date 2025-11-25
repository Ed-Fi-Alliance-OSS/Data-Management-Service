// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Threading;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using ImpromptuInterface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Minimal <see cref="IHostApplicationLifetime"/> implementation for tests so the cache
/// can observe lifecycle callbacks without bootstrapping a full host.
/// </summary>
file sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
        // No-op for integration tests
    }
}

/// <summary>
/// Simple logger used inside integration tests to surface backend errors in console output.
/// </summary>
file sealed class NUnitTestLogger<T> : ILogger<T>
{
    IDisposable ILogger.BeginScope<TState>(TState state)
    {
        return NullLoggerScope.Instance;
    }

    bool ILogger.IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Error;
    }

    void ILogger.Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (logLevel < LogLevel.Error)
        {
            return;
        }

        string message = formatter(state, exception);
        Console.Error.WriteLine($"[{typeof(T).Name}] {logLevel}: {message}");

        if (exception != null)
        {
            Console.Error.WriteLine(exception.ToString());
        }
    }
}

/// <summary>
/// Lightweight scope placeholder so ILogger scope calls remain no-ops in tests.
/// </summary>
file sealed class NullLoggerScope : IDisposable
{
    public static readonly NullLoggerScope Instance = new();

    private NullLoggerScope() { }

    public void Dispose() { }
}

public abstract class DatabaseTest : DatabaseTestBase
{
    protected NpgsqlConnection? Connection { get; set; }
    protected NpgsqlTransaction? Transaction { get; set; }
    private NpgsqlDataSourceCache? _dataSourceCache; // Lazily instantiated cache reused for per-test providers

    private static readonly JsonNode _apiSchemaRootNode =
        JsonNode.Parse(
            """
            {
              "projectSchema": {
                  "caseInsensitiveEndpointNameMapping": {
                    "courseofferings": "courseOfferings",
                    "locations": "locations",
                    "sessions": "sessions",
                    "sections": "sections",
                    "xyzs": "xyzs"
                  },
                  "projectEndpointName": "projectName",
                  "projectName": "ProjectName",
                  "resourceNameMapping": {
                    "CourseOffering": "courseOfferings",
                    "Location": "locations",
                    "Section": "sections",
                    "Session": "sessions",
                    "XYZ": "xyzs"
                  },
                  "resourceSchemas": {
                    "courseOfferings": {
                      "documentPathsMapping": {
                        "LocalCourseCode": {
                          "isReference": false,
                          "path": "$.localCourseCode"
                        },
                        "Session": {
                            "isDescriptor": false,
                            "isReference": true,
                            "projectName": "ProjectName",
                            "referenceJsonPaths": [
                              {
                                "identityJsonPath": "$.sessionName",
                                "referenceJsonPath": "$.sessionReference.sessionName"
                              }
                            ],
                            "resourceName": "Session"
                          }
                      },
                      "identityJsonPaths": [
                        "$.localCourseCode",
                        "$.sessionReference.sessionName"
                      ]
                    },
                    "locations": {
                      "documentPathsMapping": {
                       "School": {
                        "isDescriptor": false,
                        "isReference": true,
                        "isRequired": true,
                        "projectName": "ProjectName",
                        "referenceJsonPaths": [
                          {
                            "identityJsonPath": "$.schoolId",
                            "referenceJsonPath": "$.schoolReference.schoolId",
                            "type": "number"
                          }
                        ],
                        "resourceName": "School"
                       }
                      },
                      "identityJsonPaths": [
                        "$.schoolReference.schoolId"
                      ]
                    },
                    "sections": {
                      "documentPathsMapping": {
                        "CourseOffering": {
                          "isReference": true,
                          "projectName": "ProjectName",
                          "referenceJsonPaths": [
                            {
                              "identityJsonPath": "$.localCourseCode",
                              "referenceJsonPath": "$.courseOfferingReference.localCourseCode"
                            },
                            {
                              "identityJsonPath": "$.sessionReference.sessionName",
                              "referenceJsonPath": "$.courseOfferingReference.sessionName"
                            }
                          ],
                          "resourceName": "CourseOffering"
                        },
                        "SectionName": {
                          "isReference": false,
                          "path": "$.sectionName"
                        }
                      },
                      "identityJsonPaths": [
                        "$.courseOfferingReference.sessionName"
                      ]
                    },
                    "sessions": {
                      "documentPathsMapping": {
                        "SessionName": {
                          "isReference": false,
                          "path": "$.sessionName"
                        }
                      },
                      "identityJsonPaths": [
                        "$.sessionName"
                      ]
                    },
                    "xyzs": {
                      "documentPathsMapping": {
                       "Location": {
                        "isDescriptor": false,
                        "isReference": true,
                        "isRequired": true,
                        "projectName": "ProjectName",
                        "referenceJsonPaths": [
                          {
                            "identityJsonPath": "$.schoolReference.schoolId",
                            "referenceJsonPath": "$.locationReference.schoolId",
                            "type": "number"
                          }
                        ],
                        "resourceName": "Location"
                       }
                      },
                      "identityJsonPaths": [
                        "$.locationReference.schoolId"
                      ]
                    }
                  }
              }
            }
            """
        ) ?? new JsonObject();

    internal class ApiSchemaProvider : IApiSchemaProvider
    {
        public ApiSchemaDocumentNodes GetApiSchemaNodes()
        {
            return new(_apiSchemaRootNode, []);
        }

        public static Guid SchemaVersion => Guid.Empty;

        public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync()
        {
            // Not needed for tests - just return success
            return Task.FromResult<ApiSchemaLoadStatus>(new(true, []));
        }

        public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(
            JsonNode coreSchema,
            JsonNode[] extensionSchemas
        )
        {
            // Not needed for tests - just return success
            return Task.FromResult<ApiSchemaLoadStatus>(new(true, []));
        }

        public Guid ReloadId => Guid.Empty;

        public bool IsSchemaValid => true;

        public List<ApiSchemaFailure> ApiSchemaFailures => [];
    }

    [SetUp]
    public async Task ConnectionSetup()
    {
        Connection = await DataSource!.OpenConnectionAsync();
        Transaction = await Connection.BeginTransactionAsync(ConfiguredIsolationLevel);
    }

    [TearDown]
    public void ConnectionTeardown()
    {
        Transaction?.Dispose();
        Connection?.Dispose();
        _dataSourceCache?.Dispose();
        _dataSourceCache = null;
        // Note: DataSource disposed in DatabaseTestBase
    }

    /// <summary>
    /// Commits the current fixture transaction so data seeded during setup becomes visible
    /// to operations that execute on freshly opened connections (e.g., QueryDocument).
    /// Optionally starts a new transaction so subsequent assertions can continue to share one.
    /// </summary>
    protected async Task CommitTestTransactionAsync(bool beginNewTransaction = true)
    {
        if (Transaction is null || Connection is null)
        {
            return;
        }

        await Transaction.CommitAsync();
        await Transaction.DisposeAsync();

        Transaction = beginNewTransaction
            ? await Connection.BeginTransactionAsync(ConfiguredIsolationLevel)
            : null;
    }

    protected static SqlAction CreateSqlAction()
    {
        return new SqlAction();
    }

    protected static UpsertDocument CreateUpsert()
    {
        return new UpsertDocument(CreateSqlAction(), new NUnitTestLogger<UpsertDocument>());
    }

    protected static UpdateDocumentById CreateUpdate()
    {
        return new UpdateDocumentById(CreateSqlAction(), NullLogger<UpdateDocumentById>.Instance);
    }

    protected static GetDocumentById CreateGetById()
    {
        return new GetDocumentById(CreateSqlAction(), NullLogger<GetDocumentById>.Instance);
    }

    protected static IOptions<DatabaseOptions> CreateDatabaseOptions()
    {
        return Options.Create(new DatabaseOptions { IsolationLevel = ConfiguredIsolationLevel });
    }

    protected QueryDocument CreateQueryDocument()
    {
        return new QueryDocument(
            CreateSqlAction(),
            NullLogger<QueryDocument>.Instance,
            CreateDataSourceProvider(),
            CreateDatabaseOptions()
        );
    }

    /// <summary>
    /// Builds a concrete <see cref="NpgsqlDataSourceProvider"/> that mirrors production wiring
    /// so queries open their own connections using the configured connection string.
    /// </summary>
    protected NpgsqlDataSourceProvider CreateDataSourceProvider()
    {
        string connectionString =
            Configuration.DatabaseConnectionString
            ?? throw new InvalidOperationException("Database connection string is not configured.");

        var dmsInstanceSelection = new DmsInstanceSelection();
        dmsInstanceSelection.SetSelectedDmsInstance(
            new DmsInstance(
                Id: 1,
                InstanceType: "integration",
                InstanceName: "IntegrationTestInstance",
                ConnectionString: connectionString,
                RouteContext: []
            )
        );

        _dataSourceCache ??= new NpgsqlDataSourceCache(
            new TestHostApplicationLifetime(),
            NullLogger<NpgsqlDataSourceCache>.Instance
        );

        // Returning the concrete provider ensures query code paths
        // open their own connections rather than reusing the fixture’s transaction.
        return new NpgsqlDataSourceProvider(
            dmsInstanceSelection,
            _dataSourceCache,
            NullLogger<NpgsqlDataSourceProvider>.Instance
        );
    }

    protected static DeleteDocumentById CreateDeleteById()
    {
        return new DeleteDocumentById(CreateSqlAction(), NullLogger<DeleteDocumentById>.Instance);
    }

    protected static T AsValueType<T, TU>(TU value)
        where T : class
    {
        return (new { Value = value }).ActLike<T>();
    }

    protected static ResourceInfo CreateResourceInfo(
        string resourceName,
        string projectName = "ProjectName",
        bool allowIdentityUpdates = false,
        bool isInEducationOrganizationHierarchy = false,
        long educationOrganizationId = 0,
        long? parentEducationOrganizationId = default,
        bool isStudentAuthorizationSecurable = false,
        bool isContactAuthorizationSecurable = false,
        bool isStaffAuthorizationSecurable = false,
        string? studentUniqueId = default
    )
    {
        return new(
            ResourceVersion: new("5.0.0"),
            AllowIdentityUpdates: allowIdentityUpdates,
            ProjectName: new(projectName),
            ResourceName: new(resourceName),
            IsDescriptor: false,
            EducationOrganizationHierarchyInfo: new(
                isInEducationOrganizationHierarchy,
                educationOrganizationId,
                parentEducationOrganizationId
            ),
            AuthorizationSecurableInfo: GetAuthorizationSecurableInfos(
                isStudentAuthorizationSecurable,
                isContactAuthorizationSecurable,
                isStaffAuthorizationSecurable
            )
        );
    }

    protected static AuthorizationSecurableInfo[] GetAuthorizationSecurableInfos(
        bool isStudentAuthorizationSecurable,
        bool isContactAuthorizationSecurable,
        bool isStaffAuthorizationSecurable
    )
    {
        var authorizationSecurableInfos = new List<AuthorizationSecurableInfo>();
        if (isStudentAuthorizationSecurable)
        {
            authorizationSecurableInfos.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.StudentUniqueId)
            );
        }
        if (isContactAuthorizationSecurable)
        {
            authorizationSecurableInfos.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.ContactUniqueId)
            );
        }
        if (isStaffAuthorizationSecurable)
        {
            authorizationSecurableInfos.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.StaffUniqueId)
            );
        }
        return [.. authorizationSecurableInfos];
    }

    protected static DocumentInfo CreateDocumentInfo(
        Guid referentialId,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null,
        DocumentIdentityElement[]? documentIdentityElements = null
    )
    {
        return new(
            DocumentIdentity: new(
                documentIdentityElements ?? [new(IdentityValue: "", IdentityJsonPath: new("$"))]
            ),
            ReferentialId: new ReferentialId(referentialId),
            DocumentReferences: documentReferences ?? [],
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences ?? [],
            SuperclassIdentity: superclassIdentity
        );
    }

    public record Reference(string ResourceName, Guid ReferentialIdGuid);

    protected static DocumentReference CreateDocumentReference(Reference reference)
    {
        return new(
            ResourceInfo: CreateResourceInfo(reference.ResourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(reference.ReferentialIdGuid)
        );
    }

    protected static DescriptorReference CreateDescriptorReference(Reference reference)
    {
        return new(
            ResourceInfo: CreateResourceInfo(reference.ResourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(reference.ReferentialIdGuid),
            Path: new JsonPath()
        );
    }

    protected static SuperclassIdentity CreateSuperclassIdentity(string resourceName, Guid referentialIdGuid)
    {
        return new(
            ResourceInfo: CreateResourceInfo(resourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(referentialIdGuid)
        );
    }

    protected static DocumentReference[] CreateDocumentReferences(Reference[] references)
    {
        return references.Select(x => CreateDocumentReference(x)).ToArray();
    }

    protected static DescriptorReference[] CreateDescriptorReferences(Reference[] references)
    {
        return references.Select(x => CreateDescriptorReference(x)).ToArray();
    }

    protected async Task<int> CountDocumentReferencesAsync(Guid documentUuid)
    {
        if (Connection is null)
        {
            throw new InvalidOperationException("Connection has not been initialized.");
        }

        await using NpgsqlCommand command = new(
            """
            SELECT COUNT(*)
            FROM dms.Reference r
            INNER JOIN dms.Document d
                ON d.Id = r.ParentDocumentId
               AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey
            WHERE d.DocumentUuid = $1;
            """,
            Connection,
            Transaction
        );
        command.Parameters.Add(new NpgsqlParameter { Value = documentUuid });

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    protected static IUpsertRequest CreateUpsertRequest(
        string resourceName,
        Guid documentUuidGuid,
        Guid referentialIdGuid,
        string edfiDocString,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null,
        bool allowIdentityUpdates = false,
        DocumentIdentityElement[]? documentIdentityElements = null,
        DocumentSecurityElements? documentSecurityElements = null,
        bool isInEducationOrganizationHierarchy = false,
        long educationOrganizationId = 0,
        long? parentEducationOrganizationId = null,
        TraceId? traceId = null,
        string projectName = "ProjectName",
        bool isStudentAuthorizationSecurable = false,
        bool isContactAuthorizationSecurable = false,
        bool isStaffAuthorizationSecurable = false
    )
    {
        if (documentSecurityElements == null)
        {
            documentSecurityElements = new([], [], [], [], []);
        }
        if (traceId == null)
        {
            traceId = new("NotProvided");
        }
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(
                    resourceName,
                    projectName,
                    allowIdentityUpdates,
                    isInEducationOrganizationHierarchy,
                    educationOrganizationId,
                    parentEducationOrganizationId,
                    isStudentAuthorizationSecurable,
                    isContactAuthorizationSecurable,
                    isStaffAuthorizationSecurable
                ),
                DocumentInfo = CreateDocumentInfo(
                    referentialIdGuid,
                    documentReferences,
                    descriptorReferences,
                    superclassIdentity,
                    documentIdentityElements
                ),
                EdfiDoc = JsonNode.Parse(edfiDocString),
                Headers = new Dictionary<string, string>(),
                TraceId = traceId,
                DocumentUuid = new DocumentUuid(documentUuidGuid),
                UpdateCascadeHandler = new UpdateCascadeHandler(new ApiSchemaProvider(), NullLogger.Instance),
                DocumentSecurityElements = documentSecurityElements,
                ResourceAuthorizationHandler = new ResourceAuthorizationHandler(
                    [],
                    [],
                    new NoAuthorizationServiceFactory(),
                    NullLogger.Instance
                ),
            }
        ).ActLike<IUpsertRequest>();
    }

    protected static List<IUpsertRequest> CreateMultipleInsertRequest(string resourceName, string[] documents)
    {
        List<IUpsertRequest> result = [];
        foreach (var document in documents)
        {
            result.Add(CreateUpsertRequest(resourceName, Guid.NewGuid(), Guid.NewGuid(), document));
        }
        return result;
    }

    protected static IUpdateRequest CreateUpdateRequest(
        string resourceName,
        Guid documentUuidGuid,
        Guid referentialIdGuid,
        string edFiDocString,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null,
        bool allowIdentityUpdates = false,
        DocumentIdentityElement[]? documentIdentityElements = null,
        DocumentSecurityElements? documentSecurityElements = null,
        bool isInEducationOrganizationHierarchy = false,
        long educationOrganizationId = 0,
        long? parentEducationOrganizationId = null,
        TraceId? traceId = null,
        string projectName = "ProjectName",
        bool isStudentAuthorizationSecurable = false,
        bool isContactAuthorizationSecurable = false,
        bool isStaffAuthorizationSecurable = false
    )
    {
        if (documentSecurityElements == null)
        {
            documentSecurityElements = new([], [], [], [], []);
        }

        if (traceId == null)
        {
            traceId = new("NotProvided");
        }
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(
                    resourceName,
                    projectName,
                    allowIdentityUpdates,
                    isInEducationOrganizationHierarchy,
                    educationOrganizationId,
                    parentEducationOrganizationId,
                    isStudentAuthorizationSecurable,
                    isContactAuthorizationSecurable,
                    isStaffAuthorizationSecurable
                ),
                DocumentInfo = CreateDocumentInfo(
                    referentialIdGuid,
                    documentReferences,
                    descriptorReferences,
                    superclassIdentity,
                    documentIdentityElements
                ),
                EdfiDoc = JsonNode.Parse(edFiDocString),
                Headers = new Dictionary<string, string>(),
                TraceId = traceId,
                DocumentUuid = new DocumentUuid(documentUuidGuid),
                UpdateCascadeHandler = new UpdateCascadeHandler(new ApiSchemaProvider(), NullLogger.Instance),
                DocumentSecurityElements = documentSecurityElements,
                ResourceAuthorizationHandler = new ResourceAuthorizationHandler(
                    [],
                    [],
                    new NoAuthorizationServiceFactory(),
                    NullLogger.Instance
                ),
            }
        ).ActLike<IUpdateRequest>();
    }

    protected static IGetRequest CreateGetRequest(
        string resourceName,
        Guid documentUuidGuid,
        TraceId? traceId = null
    )
    {
        if (traceId == null)
        {
            traceId = new("NotProvided");
        }

        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                TraceId = traceId,
                DocumentUuid = new DocumentUuid(documentUuidGuid),
                ResourceAuthorizationHandler = new ResourceAuthorizationHandler(
                    [],
                    [],
                    new NoAuthorizationServiceFactory(),
                    NullLogger.Instance
                ),
            }
        ).ActLike<IGetRequest>();
    }

    protected static IQueryRequest CreateQueryRequest(
        string resourceName,
        Dictionary<string, string>? searchParameters,
        PaginationParameters? paginationParameters,
        TraceId? traceId = null
    )
    {
        if (traceId == null)
        {
            traceId = new("NotProvided");
        }
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                SearchParameters = searchParameters,
                PaginationParameters = paginationParameters,
                TraceId = traceId,
                QueryElements = new QueryElement[] { },
                AuthorizationSecurableInfo = new AuthorizationSecurableInfo[] { },
            }
        ).ActLike<IQueryRequest>();
    }

    protected static IDeleteRequest CreateDeleteRequest(
        string resourceName,
        Guid documentUuidGuid,
        TraceId? traceId = null,
        bool deleteInEdOrgHierarchy = false
    )
    {
        if (traceId == null)
        {
            traceId = new("NotProvided");
        }
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                TraceId = traceId,
                DocumentUuid = new DocumentUuid(documentUuidGuid),
                ResourceAuthorizationHandler = new ResourceAuthorizationHandler(
                    [],
                    [],
                    new NoAuthorizationServiceFactory(),
                    NullLogger.Instance
                ),
                DeleteInEdOrgHierarchy = deleteInEdOrgHierarchy,
                Headers = new Dictionary<string, string>(),
            }
        ).ActLike<IDeleteRequest>();
    }

    /// <summary>
    /// Takes a DB setup function and two DB operation functions. The setup function is given
    /// a managed connection and transaction and runs on the current thread. Transaction
    /// commit is provided and no results are returned to the caller.
    ///
    /// The two functions which perform DB operations have their transactions orchestrated
    /// such that the first transaction executes without committing, then the second transaction
    /// executes and blocks in the DB until the first transaction is committed, at which
    /// point the second transaction either completes or aborts due to a concurrency problem.
    ///
    /// The first operation is given a managed connection and transaction provided
    /// on the current thread, whereas the second operation has a managed connection and
    /// transaction provided to it on a separate thread.
    ///
    /// This method returns the operation results for each operation performed.
    ///
    /// </summary>
    protected async Task<(T?, U?)> OrchestrateOperations<T, U>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> setup,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> dbOperation1,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<U>> dbOperation2
    )
    {
        // Connection and transaction for the setup
        await using var connectionForSetup = await DataSource!.OpenConnectionAsync();
        await using var transactionForSetup = await connectionForSetup.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead
        );

        // Run the setup
        await setup(connectionForSetup, transactionForSetup);

        // Commit the setup
        await transactionForSetup.CommitAsync();

        // Cleanup setup connection/transaction to avoid confusion
        await transactionForSetup.DisposeAsync();
        await connectionForSetup.DisposeAsync();

        // Connection and transaction managed in this method for DB transaction 1
        await using var connection1 = await DataSource!.OpenConnectionAsync();
        await using var transaction1 = await connection1.BeginTransactionAsync(ConfiguredIsolationLevel);

        // Use these for threads to signal each other for coordination
        using EventWaitHandle Transaction1Go = new AutoResetEvent(false);
        using EventWaitHandle Transaction2Go = new AutoResetEvent(false);

        // Connection and transaction managed in this method for DB transaction 2
        NpgsqlConnection? connection2 = null;
        NpgsqlTransaction? transaction2 = null;
        U? result2 = default;

        // DB Transaction 2's thread
        _ = Task.Run(async () =>
        {
            // Step #0: Wait for signal from transaction 1 thread to start
            Transaction2Go?.WaitOne();

            // Step #3: Create new connection and begin DB transaction 2
            connection2 = await DataSource!.OpenConnectionAsync();
            transaction2 = await connection2.BeginTransactionAsync(ConfiguredIsolationLevel);

            // Step #4: Signal to transaction 1 thread to continue in parallel
            Transaction1Go?.Set();

            // Step #6: Execute DB operation 2, which will block in DB until transaction 1 commits
            result2 = await dbOperation2(connection2, transaction2);

            // Step #9: DB operation unblocked by DB transaction 1 commit, so now we can commit
            try
            {
                await transaction2.CommitAsync();
            }
            catch (Exception)
            {
                //This transaction was completed or rolled back as part of a retry, we must
                //swallow this exception
            }

            // Step #10: Tell transaction 1 thread we are done
            Transaction1Go?.Set();
        });

        // Step #1: Execute DB operation 1 without committing
        T? result1 = await dbOperation1(connection1, transaction1);

        // Step #2: Yield to transaction 2 thread
        Transaction2Go?.Set();
        Transaction1Go?.WaitOne();

        // Step #5: Give transaction 2 thread time for it's blocking DB operation to execute on DB
        Thread.Sleep(1000);

        // Step #7: Commit DB transaction 1, which will unblock transaction 2's DB operation
        await transaction1.CommitAsync();

        // Step #8: Wait for transaction 2 thread to finish
        Transaction1Go?.WaitOne();

        // Step #11: Cleanup and return
        if (transaction2 is not null)
        {
            await transaction2.DisposeAsync();
        }
        if (connection2 is not null)
        {
            await connection2.DisposeAsync();
        }

        return (result1, result2);
    }
}
