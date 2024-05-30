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

[TestFixture, DatabaseTestWithRollback]
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

    [TestFixture]
    public class Given_something : UpsertTests, IDatabaseTest
    {
        private UpsertResult? result;

        public NpgsqlDataSource? DataSource { get; set; }

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest = (
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
                    DocumentUuid = new DocumentUuid(Guid.Empty)
                }
            ).ActLike<IUpsertRequest>();
            result = await UpsertDocument().Upsert(upsertRequest, DataSource!);
        }

        [Test]
        public void It_has_something()
        {
            result!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }
    }
}
