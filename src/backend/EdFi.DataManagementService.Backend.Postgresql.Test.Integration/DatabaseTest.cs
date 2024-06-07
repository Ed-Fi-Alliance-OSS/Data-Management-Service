// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using ImpromptuInterface;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Respawn;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration
{
    public abstract class DatabaseTest
    {
        public NpgsqlDataSource? DataSource { get; set; }

        private static readonly string _connectionString =
            Configuration.DatabaseConnectionString ?? string.Empty;
        private NpgsqlConnection? _respawnerConnection;
        private Respawner? _respawner;

        public static T AsValueType<T, U>(U value)
            where T : class
        {
            return (new { Value = value }).ActLike<T>();
        }

        public static readonly IResourceInfo _resourceInfo = (
            new
            {
                ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                AllowIdentityUpdates = false,
                ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
                IsDescriptor = false
            }
        ).ActLike<IResourceInfo>();

        public static readonly IDocumentInfo _documentInfo = (
            new
            {
                DocumentIdentity = (
                    new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
                ).ActLike<IResourceInfo>(),
                ReferentialId = new ReferentialId(Guid.Empty),
                DocumentReferences = new List<IDocumentReference>(),
                DescriptorReferences = new List<IDocumentReference>(),
                SuperclassIdentity = null as ISuperclassIdentity
            }
        ).ActLike<IDocumentInfo>();

        public static IUpsertRequest CreateUpsertRequest(Guid documentUuidGuid, string edFiDocString)
        {
            return (
                new
                {
                    ResourceInfo = _resourceInfo,
                    DocumentInfo = _documentInfo,
                    EdfiDoc = JsonNode.Parse(edFiDocString),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(documentUuidGuid)
                }
            ).ActLike<IUpsertRequest>();
        }

        public static IUpdateRequest CreateUpdateRequest(
            Guid documentUuidGuid,
            Guid referentialIdGuid,
            string edFiDocString
        )
        {
            return (
                new
                {
                    ResourceInfo = _resourceInfo,
                    DocumentInfo = (
                        new
                        {
                            DocumentIdentity = (
                                new
                                {
                                    IdentityValue = "",
                                    IdentityJsonPath = AsValueType<IJsonPath, string>("$")
                                }
                            ).ActLike<IResourceInfo>(),
                            ReferentialId = new ReferentialId(referentialIdGuid),
                            DocumentReferences = new List<IDocumentReference>(),
                            DescriptorReferences = new List<IDocumentReference>(),
                            SuperclassIdentity = null as ISuperclassIdentity
                        }
                    ).ActLike<IDocumentInfo>(),
                    EdfiDoc = JsonNode.Parse(edFiDocString),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(documentUuidGuid)
                }
            ).ActLike<IUpdateRequest>();
        }

        public static IGetRequest CreateGetRequest(Guid documentUuidGuid)
        {
            return (
                new
                {
                    ResourceInfo = _resourceInfo,
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(documentUuidGuid)
                }
            ).ActLike<IGetRequest>();
        }

        public static IQueryRequest CreateGetRequestbyKey(Dictionary<string, string>? searchParameters, IPaginationParameters? paginationParameters)
        {
            return (
                new
                {
                    resourceInfo = _resourceInfo,
                    searchParameters,
                    paginationParameters,
                    TraceId = new TraceId("123")
                }
            ).ActLike<IQueryRequest>();
        }

        public static IDeleteRequest CreateDeleteRequest(Guid documentUuidGuid)
        {
            return (
                new
                {
                    ResourceInfo = _resourceInfo,
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(documentUuidGuid)
                }
            ).ActLike<IDeleteRequest>();
        }

        public static UpsertDocument CreateUpsert(NpgsqlDataSource dataSource)
        {
            return new UpsertDocument(dataSource, new SqlAction(), NullLogger<UpsertDocument>.Instance);
        }

        public static UpdateDocumentById CreateUpdate(NpgsqlDataSource dataSource)
        {
            return new UpdateDocumentById(
                dataSource,
                new SqlAction(),
                NullLogger<UpdateDocumentById>.Instance
            );
        }

        public static GetDocumentById CreateGetById(NpgsqlDataSource dataSource)
        {
            return new GetDocumentById(dataSource, new SqlAction(), NullLogger<GetDocumentById>.Instance);
        }

        public static GetDocumentByKey GetDocumentByKey(NpgsqlDataSource dataSource)
        {
            return new GetDocumentByKey(dataSource, new SqlAction(), NullLogger<GetDocumentByKey>.Instance);
        }

        public static DeleteDocumentById CreateDeleteById(NpgsqlDataSource dataSource)
        {
            return new DeleteDocumentById(
                dataSource,
                new SqlAction(),
                NullLogger<DeleteDocumentById>.Instance
            );
        }

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            new Deploy.DatabaseDeploy().DeployDatabase(_connectionString);
        }

        [SetUp]
        public async Task SetUp()
        {
            DataSource = NpgsqlDataSource.Create(_connectionString);
            _respawnerConnection = DataSource.OpenConnectionAsync().Result;

            _respawner = await Respawner.CreateAsync(
                _respawnerConnection,
                new RespawnerOptions
                {
                    TablesToInclude = [new("public", "documents"), new("public", "aliases")],
                    DbAdapter = DbAdapter.Postgres
                }
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await _respawner!.ResetAsync(_respawnerConnection!);
            _respawnerConnection?.Dispose();
            DataSource?.Dispose();
        }
    }
}
