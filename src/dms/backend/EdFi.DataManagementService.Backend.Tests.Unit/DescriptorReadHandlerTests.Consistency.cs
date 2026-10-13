// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public partial class Given_DescriptorReadHandler
{
    private static readonly DateTimeOffset _stableDescriptorTime = new(
        2026,
        10,
        13,
        12,
        34,
        56,
        TimeSpan.Zero
    );
    private static readonly Guid _consistentDescriptorUuid = Guid.Parse(
        "aaaaaaaa-1111-2222-3333-111111111111"
    );

    private static IEnumerable<TestCaseData> MixedDescriptorCases()
    {
        (long? Leading, long Joined, long? Trailing)[] versions =
        [
            (10, 11, 11),
            (11, 10, 11),
            (10, 10, 11),
            (null, 11, 11),
            (11, 11, null),
        ];
        foreach (var version in versions)
        {
            foreach (var dialect in new[] { SqlDialect.Pgsql, SqlDialect.Mssql })
            {
                foreach (
                    string path in new[]
                    {
                        "get",
                        "page",
                        "get-candidate",
                        "page-candidate",
                        "selected-fallback",
                    }
                )
                {
                    yield return new TestCaseData(
                        version.Leading,
                        version.Joined,
                        version.Trailing,
                        dialect,
                        path
                    );
                }
            }
        }
    }

    [TestCaseSource(nameof(MixedDescriptorCases))]
    public async Task It_discards_mixed_descriptor_acquisitions(
        long? leading,
        long joined,
        long? trailing,
        SqlDialect dialect,
        string path
    )
    {
        var mixed = DescriptorAttempt(leading, joined, trailing, oldBody: true);
        var stable = DescriptorAttempt(11, 11, 11);
        List<InMemoryRelationalCommandExecution> attempts =
            path == "selected-fallback" ? [stable, mixed, stable] : [mixed, stable];
        if (path.EndsWith("candidate", StringComparison.Ordinal))
        {
            attempts.Add(stable);
        }
        var executor = new DescriptorReadCommandExecutor(attempts, dialect, stableVersions: false);
        JsonObject body = await ReadConsistentDescriptorAsync(executor, dialect, path);
        body["shortDescription"]!.GetValue<string>().Should().Be("Committed body");
        body["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-10-13T12:34:56Z");
        body["_etag"]!.GetValue<string>().Should().Be(ExpectedComposedDescriptorEtag(11));
        executor.Commands.Should().HaveCount(attempts.Count);
        foreach (var command in executor.Commands)
        {
            AssertDescriptorVersionFences(command);
        }
        if (path.Contains("page", StringComparison.Ordinal) || path == "selected-fallback")
        {
            // The selection SQL occurs once, before both version boundaries and the owner query.
            string sql = executor.Commands[0].CommandText;
            sql.Split("OFFSET", StringSplitOptions.None).Should().HaveCount(2);
            sql.IndexOf("INSERT INTO", StringComparison.Ordinal)
                .Should()
                .BeLessThan(sql.IndexOf("-- Descriptor acquisition: leading", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task It_bounds_descriptor_acquisition_retries(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect,
        [Values(false, true)] bool page
    )
    {
        var mixed = DescriptorAttempt(10, 11, 11, oldBody: true);
        var executor = new DescriptorReadCommandExecutor(
            [mixed, mixed, mixed],
            dialect,
            stableVersions: false
        );
        var sut = CreateHandler(executor);
        if (page)
        {
            (await sut.HandleQueryAsync(CreateQueryRequest(dialect)))
                .Should()
                .BeOfType<QueryResult.UnknownFailure>()
                .Which.FailureMessage.Should()
                .Contain("stable representation");
        }
        else
        {
            (await sut.HandleGetByIdAsync(CreateRequest(dialect, new(_consistentDescriptorUuid))))
                .Should()
                .BeOfType<GetResult.UnknownFailure>()
                .Which.FailureMessage.Should()
                .Contain("stable representation");
        }
        executor.Commands.Should().HaveCount(3);
    }

    [Test]
    public async Task It_rechecks_document_deletion_before_reporting_a_missing_descriptor_owner(
        [Values(SqlDialect.Pgsql, SqlDialect.Mssql)] SqlDialect dialect,
        [Values(false, true)] bool deleted,
        [Values(false, true)] bool page
    )
    {
        var missingOwner = new InMemoryRelationalCommandExecution([
            VersionRows(10),
            InMemoryRelationalResultSet.Create(
                CreateDescriptorRow(_consistentDescriptorUuid, contentVersion: 10, descriptorMissing: true)
            ),
            VersionRows(deleted ? null : 10),
        ]);
        var empty = new InMemoryRelationalCommandExecution([
            VersionRows(null),
            InMemoryRelationalResultSet.Create(),
            VersionRows(null),
        ]);
        var executor = new DescriptorReadCommandExecutor(
            deleted ? [missingOwner, empty] : [missingOwner],
            dialect,
            stableVersions: false
        );
        var sut = CreateHandler(executor);
        if (page)
        {
            var result = await sut.HandleQueryAsync(CreateQueryRequest(dialect));
            if (deleted)
            {
                result.Should().BeOfType<QueryResult.QuerySuccess>().Which.EdfiDocs.Should().BeEmpty();
            }
            else
            {
                result
                    .Should()
                    .BeOfType<QueryResult.UnknownFailure>()
                    .Which.FailureMessage.Should()
                    .Contain("corruption");
            }
        }
        else
        {
            var result = await sut.HandleGetByIdAsync(CreateRequest(dialect, new(_consistentDescriptorUuid)));
            if (deleted)
            {
                result.Should().BeOfType<GetResult.GetFailureNotExists>();
            }
            else
            {
                result
                    .Should()
                    .BeOfType<GetResult.UnknownFailure>()
                    .Which.FailureMessage.Should()
                    .Contain("corruption");
            }
        }
        executor.Commands.Should().HaveCount(deleted ? 2 : 1);
    }

    private static InMemoryRelationalResultSet VersionRows(long? version) =>
        version is null
            ? InMemoryRelationalResultSet.Create()
            : InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(("DocumentId", 101L), ("ContentVersion", version.Value))
            );

    private static InMemoryRelationalCommandExecution DescriptorAttempt(
        long? leading,
        long joined,
        long? trailing,
        bool oldBody = false
    ) =>
        new([
            VersionRows(leading),
            InMemoryRelationalResultSet.Create(
                CreateDescriptorRow(
                    _consistentDescriptorUuid,
                    contentVersion: joined,
                    contentLastModifiedAt: oldBody
                        ? _stableDescriptorTime.AddDays(-1)
                        : _stableDescriptorTime,
                    shortDescription: oldBody ? "Old body" : "Committed body"
                )
            ),
            VersionRows(trailing),
        ]);

    private static async Task<JsonObject> ReadConsistentDescriptorAsync(
        DescriptorReadCommandExecutor executor,
        SqlDialect dialect,
        string path
    )
    {
        var coordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        A.CallTo(() =>
                coordinator.GetByIdAsync(
                    A<DocumentCacheReadAccelerationGetByIdRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (DocumentCacheReadAccelerationGetByIdRequest request, CancellationToken ct) =>
                {
                    if (path == "get")
                    {
                        return await request.RelationalFallback(ct);
                    }
                    var candidate = (await request.SelectAuthorizedCandidate(ct))
                        .Should()
                        .BeOfType<DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate>()
                        .Subject;
                    candidate.AuthorizedCandidate.ContentVersion.Should().Be(11);
                    candidate.AuthorizedCandidate.ContentLastModifiedAt.Should().Be(_stableDescriptorTime);
                    return await candidate.RelationalFallback(ct);
                }
            );
        A.CallTo(() =>
                coordinator.QueryAsync(A<DocumentCacheReadAccelerationQueryRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                async (DocumentCacheReadAccelerationQueryRequest request, CancellationToken ct) =>
                {
                    if (path == "page")
                    {
                        return await request.RelationalFallback(ct);
                    }
                    var selected = (await request.SelectAuthorizedCandidatePage(ct))
                        .Should()
                        .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                        .Subject;
                    var candidate = selected
                        .AuthorizedCandidatePage.Candidates.Should()
                        .ContainSingle()
                        .Subject;
                    candidate.ContentVersion.Should().Be(11);
                    candidate.ContentLastModifiedAt.Should().Be(_stableDescriptorTime);
                    return await selected.RelationalFallback(ct);
                }
            );
        var sut = CreateHandler(executor, coordinator);
        if (path.StartsWith("get", StringComparison.Ordinal))
        {
            return (await sut.HandleGetByIdAsync(CreateRequest(dialect, new(_consistentDescriptorUuid))))
                .Should()
                .BeOfType<GetResult.GetSuccess>()
                .Subject.EdfiDoc.AsObject();
        }
        return (await sut.HandleQueryAsync(CreateQueryRequest(dialect)))
            .Should()
            .BeOfType<QueryResult.QuerySuccess>()
            .Subject.EdfiDocs[0]!.AsObject();
    }

    private static void AssertDescriptorVersionFences(RelationalCommand command)
    {
        string sql = command.CommandText;
        int leading = sql.IndexOf("-- Descriptor acquisition: leading", StringComparison.Ordinal);
        int owner = sql.IndexOf("-- Descriptor acquisition: owner", StringComparison.Ordinal);
        int trailing = sql.IndexOf("-- Descriptor acquisition: trailing", StringComparison.Ordinal);
        leading.Should().BeGreaterThanOrEqualTo(0);
        owner.Should().BeGreaterThan(leading);
        trailing.Should().BeGreaterThan(owner);
        foreach (string fence in new[] { sql[leading..owner], sql[trailing..] })
        {
            fence.Should().Contain("ContentVersion").And.Contain("DocumentId");
            fence
                .Should()
                .NotContain("ContentLastModifiedAt")
                .And.NotContain("descriptor.\"")
                .And.NotContain("descriptor.[");
            fence.Should().NotContain("DescriptorId").And.NotContain("OFFSET");
        }
    }
}
