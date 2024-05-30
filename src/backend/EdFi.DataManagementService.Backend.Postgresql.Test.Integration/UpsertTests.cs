// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpsertTests
{
    public static UpsertDocument UpsertDocument()
    {
        return new UpsertDocument(new SqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    public static T AsValueType<T, U>(U value)
        where T : class
    {
        return (new { Value = value }).ActLike<T>();
    }

    [TestFixture, DatabaseTestWithRollback]
    public class Given_something : UpsertTests, IDatabaseTest
    {
        private UpsertResult? result1;
        private UpsertResult? result2;
        private UpsertResult? result3;

        public NpgsqlDataSource? DataSource { get; set; }

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest1 = (
                new
                {
                    ResourceInfo = (
                        new
                        {
                            ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                            AllowIdentityUpdates = false,
                            ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                            ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
                            IsDescriptor = false
                        }
                    ).ActLike<IResourceInfo>(),
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
                            ReferentialId = new ReferentialId(Guid.Empty),
                            DocumentReferences = new List<IDocumentReference>(),
                            DescriptorReferences = new List<IDocumentReference>(),
                            SuperclassIdentity = null as ISuperclassIdentity
                        }
                    ).ActLike<IDocumentInfo>(),
                    EdfiDoc = JsonNode.Parse("{}"),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(Guid.NewGuid())
                }
            ).ActLike<IUpsertRequest>();

            IUpsertRequest upsertRequest2 = (
                new
                {
                    ResourceInfo = (
                        new
                        {
                            ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                            AllowIdentityUpdates = false,
                            ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                            ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
                            IsDescriptor = false
                        }
                    ).ActLike<IResourceInfo>(),
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
                            ReferentialId = new ReferentialId(Guid.Empty),
                            DocumentReferences = new List<IDocumentReference>(),
                            DescriptorReferences = new List<IDocumentReference>(),
                            SuperclassIdentity = null as ISuperclassIdentity
                        }
                    ).ActLike<IDocumentInfo>(),
                    EdfiDoc = JsonNode.Parse("{}"),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(Guid.NewGuid())
                }
            ).ActLike<IUpsertRequest>();

            IUpsertRequest upsertRequest3 = (
                new
                {
                    ResourceInfo = (
                        new
                        {
                            ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                            AllowIdentityUpdates = false,
                            ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                            ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
                            IsDescriptor = false
                        }
                    ).ActLike<IResourceInfo>(),
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
                            ReferentialId = new ReferentialId(Guid.Empty),
                            DocumentReferences = new List<IDocumentReference>(),
                            DescriptorReferences = new List<IDocumentReference>(),
                            SuperclassIdentity = null as ISuperclassIdentity
                        }
                    ).ActLike<IDocumentInfo>(),
                    EdfiDoc = JsonNode.Parse("{}"),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(Guid.NewGuid())
                }
            ).ActLike<IUpsertRequest>();

            result1 = await UpsertDocument().Upsert(upsertRequest1, DataSource!);
            result2 = await UpsertDocument().Upsert(upsertRequest2, DataSource!);
            result3 = await UpsertDocument().Upsert(upsertRequest3, DataSource!);
        }

        [Test]
        public void It_has_something()
        {
            result1!.Should().BeOfType<UpsertResult.InsertSuccess>();
            result2!.Should().BeOfType<UpsertResult.InsertSuccess>();
            result3!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }
    }
}
