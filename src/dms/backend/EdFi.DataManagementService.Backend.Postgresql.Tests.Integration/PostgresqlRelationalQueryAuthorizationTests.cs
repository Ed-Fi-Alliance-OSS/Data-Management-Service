// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record PostgresqlRelationalQueryAuthorizationRecordedCommand(
    int SessionId,
    string CommandText
);

internal sealed class PostgresqlRelationalQueryAuthorizationWriteSessionRecorder
{
    private readonly object _sync = new();
    private readonly List<PostgresqlRelationalQueryAuthorizationRecordedCommand> _commands = [];
    private int _nextSessionId;

    public IReadOnlyList<PostgresqlRelationalQueryAuthorizationRecordedCommand> Commands
    {
        get
        {
            lock (_sync)
            {
                return [.. _commands];
            }
        }
    }

    public int CreateSessionId()
    {
        lock (_sync)
        {
            _nextSessionId++;
            return _nextSessionId;
        }
    }

    public void Record(int sessionId, RelationalCommand command)
    {
        lock (_sync)
        {
            _commands.Add(
                new PostgresqlRelationalQueryAuthorizationRecordedCommand(sessionId, command.CommandText)
            );
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _commands.Clear();
            _nextSessionId = 0;
        }
    }
}

internal sealed class PostgresqlRelationalQueryAuthorizationRecordingWriteSessionFactory(
    NpgsqlDataSourceProvider dataSourceProvider,
    IOptions<DatabaseOptions> databaseOptions,
    PostgresqlRelationalQueryAuthorizationWriteSessionRecorder recorder
) : IRelationalWriteSessionFactory
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));
    private readonly IOptions<DatabaseOptions> _databaseOptions =
        databaseOptions ?? throw new ArgumentNullException(nameof(databaseOptions));
    private readonly PostgresqlRelationalQueryAuthorizationWriteSessionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            var transaction = await connection.BeginTransactionAsync(
                _databaseOptions.Value.IsolationLevel,
                cancellationToken
            );
            return new PostgresqlRelationalQueryAuthorizationRecordingWriteSession(
                connection,
                transaction,
                _recorder.CreateSessionId(),
                _recorder
            );
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class PostgresqlRelationalQueryAuthorizationRecordingWriteSession(
    DbConnection connection,
    DbTransaction transaction,
    int sessionId,
    PostgresqlRelationalQueryAuthorizationWriteSessionRecorder recorder
) : IRelationalWriteSession
{
    public DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public DbTransaction Transaction { get; } =
        transaction ?? throw new ArgumentNullException(nameof(transaction));

    private readonly PostgresqlRelationalQueryAuthorizationWriteSessionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public DbCommand CreateCommand(RelationalCommand command)
    {
        _recorder.Record(sessionId, command);
        return SessionRelationalCommandFactory.CreateCommand(Connection, Transaction, command);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        Transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        Transaction.RollbackAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal sealed class PostgresqlRelationalQueryAuthorizationTestContext : IAsyncDisposable
{
    private const int MaximumPageSize = 500;
    private readonly Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    >? _providerFailureTransform;
    private readonly Dictionary<
        (string ProjectEndpointName, string ResourceName),
        ResourceHandle
    > _resourceCache = [];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private ServiceProvider _serviceProvider = null!;
    private PostgresqlRelationalQueryExecutionRecorder _recorder = null!;
    private PostgresqlRelationalQueryAuthorizationWriteSessionRecorder _writeSessionRecorder = null!;

    public MappingSet MappingSet => _fixture.MappingSet;

    public PostgresqlGeneratedDdlTestDatabase Database { get; private set; } = null!;

    public PostgresqlRelationalQueryAuthorizationTestContext(
        Func<
            RelationshipAuthorizationProviderFailure,
            RelationshipAuthorizationProviderFailure
        >? providerFailureTransform = null
    )
    {
        _providerFailureTransform = providerFailureTransform;
    }

    public async Task InitializeAsync(
        string fixtureRelativePath,
        bool strict,
        bool replaceReadTargetLookup = true
    )
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            fixtureRelativePath,
            strict
        );
        Database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider(replaceReadTargetLookup);
        _recorder = _serviceProvider.GetRequiredService<PostgresqlRelationalQueryExecutionRecorder>();
        _writeSessionRecorder =
            _serviceProvider.GetRequiredService<PostgresqlRelationalQueryAuthorizationWriteSessionRecorder>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }

    public void ResetRecorder()
    {
        _recorder.Reset();
        _writeSessionRecorder.Reset();
    }

    public void AssertDeleteWithIfMatchSharedGuardedSession()
    {
        var commands = _writeSessionRecorder.Commands;

        commands.Select(static command => command.SessionId).Distinct().Should().ContainSingle();

        // A specific-tag If-Match delete is two commands on one guarded session. The capture lock and the
        // relationship check share the first, in that order; the etag is then composed in process from the
        // ContentVersion that capture already returned — neither a second lock nor a state-hydration read —
        // and only once it matches do the deletes run as an ordered segment on the same transaction.
        commands.Should().HaveCount(2);

        var openingCommandText = commands[0].CommandText;
        IsPostgresqlDocumentLockCommand(openingCommandText).Should().BeTrue();
        IsPostgresqlRelationshipAuthorizationCommand(openingCommandText).Should().BeTrue();
        openingCommandText
            .IndexOf("FOR UPDATE", StringComparison.Ordinal)
            .Should()
            .BeLessThan(openingCommandText.IndexOf("AUTH1", StringComparison.Ordinal));
        IsPostgresqlDocumentDeleteCommand(openingCommandText).Should().BeFalse();

        IsPostgresqlDocumentDeleteCommand(commands[1].CommandText).Should().BeTrue();
        commands.Count(command => IsPostgresqlDocumentLockCommand(command.CommandText)).Should().Be(1);
    }

    /// <summary>
    /// Asserts the whole of a delete that needs no in-process decision between observing the target and
    /// modifying it: one command carrying capture and lock, the relationship check, and both deletes, in
    /// that order, committed once.
    /// </summary>
    public void AssertDeleteIsOneCommittedCommand()
    {
        var commands = _writeSessionRecorder.Commands;

        commands.Should().ContainSingle();
        commands.Select(static command => command.SessionId).Distinct().Should().ContainSingle();

        var commandText = commands[0].CommandText;
        var lockIndex = commandText.IndexOf("FOR UPDATE", StringComparison.Ordinal);
        var authorizationIndex = commandText.IndexOf("AUTH1", StringComparison.Ordinal);
        var documentDeleteIndex = commandText.IndexOf(
            "DELETE FROM dms.\"Document\"",
            StringComparison.Ordinal
        );

        lockIndex.Should().BePositive();
        authorizationIndex.Should().BeGreaterThan(lockIndex);
        documentDeleteIndex.Should().BeGreaterThan(authorizationIndex);
    }

    public PageKeysetSpec.Query AssertSingleQueryHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Query>();
        return (PageKeysetSpec.Query)_recorder.HydrationKeysets[0];
    }

    public PageKeysetSpec.Single AssertSingleDocumentHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Single>();
        return (PageKeysetSpec.Single)_recorder.HydrationKeysets[0];
    }

    public void AssertSingleDocumentMaterialized()
    {
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(1);
        _recorder.PageMaterializationCallCount.Should().Be(0);
    }

    public void AssertNoHydration()
    {
        _recorder.HydrationKeysets.Should().BeEmpty();
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
    }

    public void AssertHydratedWithoutMaterialization(int expectedHydrationCount)
    {
        _recorder.HydrationKeysets.Should().HaveCount(expectedHydrationCount);
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
    }

    public void BeforeNextHydration(Func<CancellationToken, Task> beforeHydrationAsync)
    {
        _recorder.BeforeNextHydrationAsync = beforeHydrationAsync;
    }

    public async Task SeedSchoolDescriptorDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
    }

    public async Task<UpsertResult> CreateSchoolAsync(
        QuerySchoolSeed seed,
        short? creatorOwnershipTokenId = null
    )
    {
        return await UpsertAsync(
            "ed-fi",
            "School",
            RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                seed.SchoolId,
                seed.NameOfInstitution
            ),
            seed.DocumentUuid,
            $"seed-school-{seed.SchoolId}",
            creatorOwnershipTokenId: creatorOwnershipTokenId
        );
    }

    public async Task<UpsertResult> CreateClassPeriodAsync(ClassPeriodSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "ClassPeriod",
            RelationalQueryAuthorizationRequestBodies.CreateClassPeriodRequestBody(seed),
            seed.DocumentUuid,
            $"seed-class-period-{seed.SchoolId}-{seed.ClassPeriodName}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationAndAsync(AuthorizationAndSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationAndResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationAndRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-and-{seed.AuthorizationAndId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationRootChildAsync(
        AuthorizationRootChildSeed seed,
        short? creatorOwnershipTokenId = null
    )
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationRootChildResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-root-child-{seed.AuthorizationRootChildId}",
            creatorOwnershipTokenId: creatorOwnershipTokenId
        );
    }

    public async Task<UpsertResult> UpsertAuthorizationRootChildAsync(
        AuthorizationRootChildSeed seed,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null,
        BackendProfileWriteContext? backendProfileWriteContext = null,
        JsonNode? requestBody = null
    )
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationRootChildResource",
            requestBody
                ?? RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            seed.DocumentUuid,
            $"post-auth-root-child-{seed.AuthorizationRootChildId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch,
            backendProfileWriteContext
        );
    }

    public async Task<UpdateResult> UpdateAuthorizationRootChildByIdAsync(
        AuthorizationRootChildSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null,
        BackendProfileWriteContext? backendProfileWriteContext = null,
        JsonNode? requestBody = null
    )
    {
        return await UpdateAsync(
            "authz",
            "AuthorizationRootChildResource",
            requestBody
                ?? RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            documentUuid,
            $"put-auth-root-child-{seed.AuthorizationRootChildId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch,
            backendProfileWriteContext
        );
    }

    public async Task<UpsertResult> CreateAuthorizationChildOnlyAsync(AuthorizationChildOnlySeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationChildOnlyResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationChildOnlyRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-child-only-{seed.AuthorizationChildOnlyId}"
        );
    }

    public async Task<UpsertResult> CreateAuthorizationNullableAsync(AuthorizationNullableSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationNullableResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNullableRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-nullable-{seed.AuthorizationNullableId}"
        );
    }

    /// <summary>
    /// Seeds an <c>AuthorizationNamespaceResource</c> row through the production write path with no
    /// authorization strategies configured, so any stored namespace value can be established without
    /// first passing namespace authorization.
    /// </summary>
    public async Task<UpsertResult> CreateAuthorizationNamespaceAsync(
        AuthorizationNamespaceSeed seed,
        short? creatorOwnershipTokenId = null
    )
    {
        return await UpsertAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.NamespaceResourceName,
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNamespaceRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-namespace-{seed.AuthorizationNamespaceId}",
            creatorOwnershipTokenId: creatorOwnershipTokenId
        );
    }

    public async Task<UpsertResult> CreateAuthorizationStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed
    )
    {
        var resourceKeyId = GetCompiledResourceKeyId("authz", "AuthorizationStudentAcademicRecordResource");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);
        var studentAcademicRecordDocumentId = await GetStudentAcademicRecordDocumentIdAsync(
            seed.EducationOrganizationId,
            seed.SchoolYear,
            seed.StudentUniqueId,
            seed.TermDescriptor
        );
        var termDescriptorId = await GetDescriptorDocumentIdAsync("TermDescriptor", seed.TermDescriptor);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "authz"."AuthorizationStudentAcademicRecordResource" (
                "DocumentId",
                "StudentAcademicRecord_DocumentId",
                "StudentAcademicRecord_EducationOrganizationId",
                "StudentAcademicRecord_SchoolYear",
                "StudentAcademicRecord_StudentUniqueId",
                "StudentAcademicRecord_TermDescriptor_DescriptorId",
                "AuthorizationStudentAcademicRecordId",
                "Name"
            )
            VALUES (
                @documentId,
                @studentAcademicRecordDocumentId,
                @educationOrganizationId,
                @schoolYear,
                @studentUniqueId,
                @termDescriptorId,
                @authorizationStudentAcademicRecordId,
                @name
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("studentAcademicRecordDocumentId", studentAcademicRecordDocumentId),
            new NpgsqlParameter("educationOrganizationId", seed.EducationOrganizationId),
            new NpgsqlParameter("schoolYear", seed.SchoolYear),
            new NpgsqlParameter("studentUniqueId", seed.StudentUniqueId),
            new NpgsqlParameter("termDescriptorId", termDescriptorId),
            new NpgsqlParameter(
                "authorizationStudentAcademicRecordId",
                seed.AuthorizationStudentAcademicRecordId
            ),
            new NpgsqlParameter("name", seed.Name)
        );

        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                "Authz",
                "AuthorizationStudentAcademicRecordResource",
                (
                    "$.authorizationStudentAcademicRecordId",
                    seed.AuthorizationStudentAcademicRecordId.ToString(CultureInfo.InvariantCulture)
                )
            ),
            documentId,
            resourceKeyId
        );

        return new UpsertResult.InsertSuccess(seed.DocumentUuid, "\"test-etag\"");
    }

    public async Task<UpsertResult> UpsertAuthorizationStudentAcademicRecordAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationStudentAcademicRecordResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationStudentAcademicRecordRequestBody(
                seed
            ),
            seed.DocumentUuid,
            $"post-auth-student-academic-record-{seed.AuthorizationStudentAcademicRecordId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch
        );
    }

    public async Task<UpdateResult> UpdateAuthorizationStudentAcademicRecordByIdAsync(
        AuthorizationStudentAcademicRecordSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await UpdateAsync(
            "authz",
            "AuthorizationStudentAcademicRecordResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationStudentAcademicRecordRequestBody(
                seed
            ),
            documentUuid,
            $"put-auth-student-academic-record-{seed.AuthorizationStudentAcademicRecordId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch
        );
    }

    public async Task<UpsertResult> CreateAuthorizationStudentSchoolAsync(AuthorizationStudentSchoolSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationStudentSchoolResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationStudentSchoolRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-student-school-{seed.AuthorizationStudentSchoolId}"
        );
    }

    public async Task<UpsertResult> UpsertAuthorizationStudentSchoolAsync(
        AuthorizationStudentSchoolSeed seed,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationStudentSchoolResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationStudentSchoolRequestBody(seed),
            seed.DocumentUuid,
            $"post-auth-student-school-{seed.AuthorizationStudentSchoolId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch
        );
    }

    public async Task<UpdateResult> UpdateAuthorizationStudentSchoolByIdAsync(
        AuthorizationStudentSchoolSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await UpdateAsync(
            "authz",
            "AuthorizationStudentSchoolResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationStudentSchoolRequestBody(seed),
            documentUuid,
            $"put-auth-student-school-{seed.AuthorizationStudentSchoolId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch
        );
    }

    public async Task<UpsertResult> CreateContactAsync(ContactSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "Contact",
            RelationalQueryAuthorizationRequestBodies.CreateContactRequestBody(seed),
            seed.DocumentUuid,
            $"seed-contact-{seed.ContactUniqueId}"
        );
    }

    public async Task<UpsertResult> CreateStaffAsync(StaffSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "Staff",
            RelationalQueryAuthorizationRequestBodies.CreateStaffRequestBody(seed),
            seed.DocumentUuid,
            $"seed-staff-{seed.StaffUniqueId}"
        );
    }

    public async Task<UpsertResult> CreateStudentContactAssociationAsync(StudentContactAssociationSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "StudentContactAssociation",
            RelationalQueryAuthorizationRequestBodies.CreateStudentContactAssociationRequestBody(seed),
            seed.DocumentUuid,
            $"seed-student-contact-association-{seed.StudentUniqueId}-{seed.ContactUniqueId}"
        );
    }

    public async Task<UpsertResult> CreateStaffEducationOrganizationAssignmentAssociationAsync(
        StaffEducationOrganizationAssignmentAssociationSeed seed
    )
    {
        return await UpsertAsync(
            "ed-fi",
            "StaffEducationOrganizationAssignmentAssociation",
            RelationalQueryAuthorizationRequestBodies.CreateStaffEducationOrganizationAssignmentAssociationRequestBody(
                seed
            ),
            seed.DocumentUuid,
            $"seed-staff-assignment-{seed.StaffUniqueId}-{seed.EducationOrganizationId}"
        );
    }

    public async Task<UpsertResult> CreateStaffEducationOrganizationEmploymentAssociationAsync(
        StaffEducationOrganizationEmploymentAssociationSeed seed
    )
    {
        return await UpsertAsync(
            "ed-fi",
            "StaffEducationOrganizationEmploymentAssociation",
            RelationalQueryAuthorizationRequestBodies.CreateStaffEducationOrganizationEmploymentAssociationRequestBody(
                seed
            ),
            seed.DocumentUuid,
            $"seed-staff-employment-{seed.StaffUniqueId}-{seed.EducationOrganizationId}"
        );
    }

    public async Task<UpsertResult> CreateStudentEducationOrganizationResponsibilityAssociationAsync(
        StudentEducationOrganizationResponsibilityAssociationSeed seed
    )
    {
        return await UpsertAsync(
            "ed-fi",
            "StudentEducationOrganizationResponsibilityAssociation",
            RelationalQueryAuthorizationRequestBodies.CreateStudentEducationOrganizationResponsibilityAssociationRequestBody(
                seed
            ),
            seed.DocumentUuid,
            $"seed-student-responsibility-{seed.StudentUniqueId}-{seed.EducationOrganizationId}"
        );
    }

    public async Task SeedTermDescriptorAsync(Guid documentUuid, string termDescriptor)
    {
        await SeedDescriptorAsync(
            documentUuid,
            "TermDescriptor",
            "Ed-Fi:TermDescriptor",
            termDescriptor,
            "uri://ed-fi.org/TermDescriptor",
            termDescriptor[(termDescriptor.LastIndexOf('#') + 1)..],
            termDescriptor[(termDescriptor.LastIndexOf('#') + 1)..]
        );
    }

    /// <summary>
    /// The four additional descriptors the bulk volume generator's root tables require as NOT NULL references.
    /// Public wrappers rather than a widened <see cref="SeedDescriptorAsync"/>, matching how every other
    /// descriptor is exposed here.
    /// </summary>
    public async Task SeedGradingPeriodDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedNamedDescriptorAsync(documentUuid, "GradingPeriodDescriptor", descriptor);
    }

    public async Task SeedGradeTypeDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedNamedDescriptorAsync(documentUuid, "GradeTypeDescriptor", descriptor);
    }

    public async Task SeedCourseAttemptResultDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedNamedDescriptorAsync(documentUuid, "CourseAttemptResultDescriptor", descriptor);
    }

    public async Task SeedAttendanceEventCategoryDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedNamedDescriptorAsync(documentUuid, "AttendanceEventCategoryDescriptor", descriptor);
    }

    /// <summary>
    /// Splits the descriptor URI once on its fragment separator: namespace before it, code value after. Callers
    /// pass the <c>uri://ed-fi.org/&lt;Name&gt;#&lt;Code&gt;</c> constants from
    /// <c>RelationshipAuthorizationVolumeIdentifiers</c>, so the separator is a precondition rather than optional —
    /// hence one lookup shared by both slices instead of two that could disagree about a URI without one.
    /// </summary>
    private async Task SeedNamedDescriptorAsync(Guid documentUuid, string resourceName, string descriptorUri)
    {
        var fragmentIndex = descriptorUri.LastIndexOf('#');
        var codeValue = descriptorUri[(fragmentIndex + 1)..];

        await SeedDescriptorAsync(
            documentUuid,
            resourceName,
            $"Ed-Fi:{resourceName}",
            descriptorUri,
            descriptorUri[..fragmentIndex],
            codeValue,
            codeValue
        );
    }

    public async Task SeedStaffClassificationDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedDescriptorAsync(
            documentUuid,
            "StaffClassificationDescriptor",
            "Ed-Fi:StaffClassificationDescriptor",
            descriptor,
            "uri://ed-fi.org/StaffClassificationDescriptor",
            descriptor[(descriptor.LastIndexOf('#') + 1)..],
            descriptor[(descriptor.LastIndexOf('#') + 1)..]
        );
    }

    public async Task SeedEmploymentStatusDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedDescriptorAsync(
            documentUuid,
            "EmploymentStatusDescriptor",
            "Ed-Fi:EmploymentStatusDescriptor",
            descriptor,
            "uri://ed-fi.org/EmploymentStatusDescriptor",
            descriptor[(descriptor.LastIndexOf('#') + 1)..],
            descriptor[(descriptor.LastIndexOf('#') + 1)..]
        );
    }

    public async Task SeedResponsibilityDescriptorAsync(Guid documentUuid, string descriptor)
    {
        await SeedDescriptorAsync(
            documentUuid,
            "ResponsibilityDescriptor",
            "Ed-Fi:ResponsibilityDescriptor",
            descriptor,
            "uri://ed-fi.org/ResponsibilityDescriptor",
            descriptor[(descriptor.LastIndexOf('#') + 1)..],
            descriptor[(descriptor.LastIndexOf('#') + 1)..]
        );
    }

    public async Task SeedSchoolYearTypeAsync(SchoolYearTypeSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", seed.CurrentSchoolYear),
            new NpgsqlParameter("schoolYear", seed.SchoolYear),
            new NpgsqlParameter("schoolYearDescription", seed.SchoolYearDescription)
        );

        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                "Ed-Fi",
                "SchoolYearType",
                ("$.schoolYear", seed.SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            documentId,
            resourceKeyId
        );
    }

    public async Task SeedStudentAsync(StudentSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" (
                "DocumentId",
                "BirthDate",
                "FirstName",
                "LastSurname",
                "StudentUniqueId"
            )
            VALUES (
                @documentId,
                @birthDate,
                @firstName,
                @lastSurname,
                @studentUniqueId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", seed.FirstName),
            new NpgsqlParameter("lastSurname", seed.LastSurname),
            new NpgsqlParameter("studentUniqueId", seed.StudentUniqueId)
        );

        await InsertReferentialIdentityAsync(
            CreateReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", seed.StudentUniqueId)),
            documentId,
            resourceKeyId
        );
    }

    public async Task SeedStudentSchoolAssociationAsync(StudentSchoolAssociationSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StudentSchoolAssociation");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);
        var schoolDocumentId = await GetSchoolDocumentIdAsync(seed.SchoolId);
        var studentDocumentId = await GetStudentDocumentIdAsync(seed.StudentUniqueId);
        var entryGradeLevelDescriptorId = await GetDescriptorDocumentIdAsync(
            "GradeLevelDescriptor",
            seed.EntryGradeLevelDescriptor
        );

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentSchoolAssociation" (
                "DocumentId",
                "SchoolId_Unified",
                "School_DocumentId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "EntryGradeLevelDescriptor_DescriptorId",
                "EntryDate"
            )
            VALUES (
                @documentId,
                @schoolId,
                @schoolDocumentId,
                @studentDocumentId,
                @studentUniqueId,
                @entryGradeLevelDescriptorId,
                @entryDate
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", seed.SchoolId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", seed.StudentUniqueId),
            new NpgsqlParameter("entryGradeLevelDescriptorId", entryGradeLevelDescriptorId),
            new NpgsqlParameter("entryDate", seed.EntryDate)
        );
    }

    public async Task SeedStudentAcademicRecordAsync(StudentAcademicRecordSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StudentAcademicRecord");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);
        var schoolDocumentId = await GetSchoolDocumentIdAsync(seed.EducationOrganizationId);
        var schoolYearDocumentId = await GetSchoolYearDocumentIdAsync(seed.SchoolYear);
        var studentDocumentId = await GetStudentDocumentIdAsync(seed.StudentUniqueId);
        var termDescriptorId = await GetDescriptorDocumentIdAsync("TermDescriptor", seed.TermDescriptor);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentAcademicRecord" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "TermDescriptor_DescriptorId"
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @educationOrganizationId,
                @schoolYearDocumentId,
                @schoolYear,
                @studentDocumentId,
                @studentUniqueId,
                @termDescriptorId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("educationOrganizationId", seed.EducationOrganizationId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", seed.SchoolYear),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", seed.StudentUniqueId),
            new NpgsqlParameter("termDescriptorId", termDescriptorId)
        );

        await InsertReferentialIdentityAsync(
            CreateStudentAcademicRecordReferentialId(seed),
            documentId,
            resourceKeyId
        );
    }

    /// <summary>
    /// Course and CourseTranscript are seeded for the transitive person pathway. Unlike the older helpers here,
    /// neither writes <c>dms.ReferentialIdentity</c> by hand: the table's own FOR EACH ROW
    /// <c>TR_&lt;Table&gt;_ReferentialIdentity</c> trigger computes it from the natural key, which is the value
    /// the product would store.
    /// </summary>
    public async Task SeedCourseAsync(CourseSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Course");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);
        var schoolDocumentId = await GetSchoolDocumentIdAsync(seed.EducationOrganizationId);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Course" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "CourseCode",
                "CourseTitle",
                "NumberOfParts"
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @educationOrganizationId,
                @courseCode,
                @courseTitle,
                1
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("educationOrganizationId", (long)seed.EducationOrganizationId),
            new NpgsqlParameter("courseCode", seed.CourseCode),
            new NpgsqlParameter("courseTitle", seed.CourseTitle)
        );
    }

    public async Task SeedCourseTranscriptAsync(CourseTranscriptSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "CourseTranscript");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);
        var courseDocumentId = await GetCourseDocumentIdAsync(
            seed.CourseCode,
            seed.CourseEducationOrganizationId
        );
        var studentAcademicRecordDocumentId = await GetStudentAcademicRecordDocumentIdAsync(
            seed.StudentAcademicRecordEducationOrganizationId,
            seed.SchoolYear,
            seed.StudentUniqueId,
            seed.TermDescriptor
        );
        var termDescriptorId = await GetDescriptorDocumentIdAsync("TermDescriptor", seed.TermDescriptor);
        var courseAttemptResultDescriptorId = await GetDescriptorDocumentIdAsync(
            "CourseAttemptResultDescriptor",
            seed.CourseAttemptResultDescriptor
        );

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."CourseTranscript" (
                "DocumentId",
                "CourseCourse_DocumentId",
                "CourseCourse_CourseCode",
                "CourseCourse_EducationOrganizationId",
                "StudentAcademicRecord_DocumentId",
                "StudentAcademicRecord_EducationOrganizationId",
                "StudentAcademicRecord_SchoolYear",
                "StudentAcademicRecord_StudentUniqueId",
                "StudentAcademicRecord_TermDescriptor_DescriptorId",
                "CourseAttemptResultDescriptor_DescriptorId"
            )
            VALUES (
                @documentId,
                @courseDocumentId,
                @courseCode,
                @courseEducationOrganizationId,
                @studentAcademicRecordDocumentId,
                @studentAcademicRecordEducationOrganizationId,
                @schoolYear,
                @studentUniqueId,
                @termDescriptorId,
                @courseAttemptResultDescriptorId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("courseDocumentId", courseDocumentId),
            new NpgsqlParameter("courseCode", seed.CourseCode),
            new NpgsqlParameter("courseEducationOrganizationId", (long)seed.CourseEducationOrganizationId),
            new NpgsqlParameter("studentAcademicRecordDocumentId", studentAcademicRecordDocumentId),
            new NpgsqlParameter(
                "studentAcademicRecordEducationOrganizationId",
                (long)seed.StudentAcademicRecordEducationOrganizationId
            ),
            new NpgsqlParameter("schoolYear", seed.SchoolYear),
            new NpgsqlParameter("studentUniqueId", seed.StudentUniqueId),
            new NpgsqlParameter("termDescriptorId", termDescriptorId),
            new NpgsqlParameter("courseAttemptResultDescriptorId", courseAttemptResultDescriptorId)
        );
    }

    public async Task<UpsertResult> UpsertAuthorizationNullableAsync(
        AuthorizationNullableSeed seed,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames
    )
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationNullableResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNullableRequestBody(seed),
            seed.DocumentUuid,
            $"post-auth-nullable-{seed.AuthorizationNullableId}",
            claimEducationOrganizationIds,
            strategyNames
        );
    }

    public async Task<UpdateResult> UpdateAuthorizationNullableByIdAsync(
        AuthorizationNullableSeed seed,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames
    )
    {
        return await UpdateAsync(
            "authz",
            "AuthorizationNullableResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNullableRequestBody(seed),
            documentUuid,
            $"put-auth-nullable-{seed.AuthorizationNullableId}",
            claimEducationOrganizationIds,
            strategyNames
        );
    }

    public async Task InsertAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "auth"."EducationOrganizationIdToEducationOrganizationId" (
                "SourceEducationOrganizationId",
                "TargetEducationOrganizationId"
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task DeleteAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            DELETE FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
            WHERE "SourceEducationOrganizationId" = @sourceEducationOrganizationId
              AND "TargetEducationOrganizationId" = @targetEducationOrganizationId;
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task<long> CountAuthEdgesAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)::bigint
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
            WHERE "SourceEducationOrganizationId" = @sourceEducationOrganizationId
              AND "TargetEducationOrganizationId" = @targetEducationOrganizationId;
            """,
            new NpgsqlParameter("sourceEducationOrganizationId", sourceEducationOrganizationId),
            new NpgsqlParameter("targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    /// <summary>
    /// Asserts the people auth view for <paramref name="viewKind"/> yields exactly
    /// <paramref name="expectedPairCount"/> rows for the (claim EducationOrganizationId, person) pair.
    /// Duplicate-pair scenarios (DMS-1329) assert a count above one before exercising consumers so their
    /// single-result assertions cannot pass vacuously against a fixture that accidentally produced only
    /// one authorization pair. Each view reaches duplicate cardinality by its own route: Student and
    /// Contact through multiple claim-reachable enrollments, Staff across its two <c>UNION ALL</c> arms,
    /// and StudentThroughResponsibility through multiple responsibilities at one EducationOrganization.
    /// The view name and columns are read from <see cref="AuthObjectDefinitions"/> so a rename moves this
    /// probe with the definition.
    /// </summary>
    public async Task AssertPeopleAuthViewPairCountAsync(
        AuthPeopleViewKind viewKind,
        string personUniqueId,
        long claimEducationOrganizationId,
        long expectedPairCount
    )
    {
        var definition = AuthObjectDefinitions.GetPeopleAuthViewDefinition(viewKind);
        var (personTable, personUniqueIdColumn) = PeopleAuthViewPersonIdentity(viewKind);

        var pairCount = await Database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)::bigint
            FROM "{definition.View.Schema.Value}"."{definition.View.Name}" v
            INNER JOIN "edfi"."{personTable}" p
              ON p."DocumentId" = v."{definition.PersonDocumentIdOutputColumn.Value}"
            WHERE v."{definition.ClaimEducationOrganizationIdColumn.Value}" = @claimEducationOrganizationId
              AND p."{personUniqueIdColumn}" = @personUniqueId;
            """,
            new NpgsqlParameter("claimEducationOrganizationId", claimEducationOrganizationId),
            new NpgsqlParameter("personUniqueId", personUniqueId)
        );

        pairCount
            .Should()
            .Be(
                expectedPairCount,
                $"the {definition.View.Name} view should yield exactly {expectedPairCount} row(s) for "
                    + $"'{personUniqueId}' under claim {claimEducationOrganizationId}"
            );
    }

    /// <summary>
    /// The <c>edfi</c> person table and unique-id column that each people auth view's person DocumentId
    /// output resolves to. Both student views land on <c>edfi.Student</c>.
    /// </summary>
    private static (string PersonTable, string PersonUniqueIdColumn) PeopleAuthViewPersonIdentity(
        AuthPeopleViewKind viewKind
    ) =>
        viewKind switch
        {
            AuthPeopleViewKind.Student or AuthPeopleViewKind.StudentThroughResponsibility => (
                "Student",
                "StudentUniqueId"
            ),
            AuthPeopleViewKind.Contact => ("Contact", "ContactUniqueId"),
            AuthPeopleViewKind.Staff => ("Staff", "StaffUniqueId"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(viewKind),
                viewKind,
                "Unsupported people auth view kind."
            ),
        };

    /// <summary>
    /// Creates (or replaces) the "auth"."{strategyName}" custom authorization view, authorizing only the
    /// School documents whose SchoolId is in <paramref name="authorizedSchoolIds"/>. The view always
    /// selects the basis resource's own DocumentId, per the custom-view authorization contract — the same
    /// view authorizes both the basis resource (School) and any subject resource transitively related to
    /// it (e.g. ClassPeriod) through the resolved join path.
    /// </summary>
    public async Task CreateSchoolCustomAuthViewAsync(
        string strategyName,
        IReadOnlyList<int> authorizedSchoolIds
    )
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var schoolIdList = string.Join(", ", authorizedSchoolIds);

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT "DocumentId"
            FROM "{physicalSchema}"."School"
            WHERE "SchoolId" IN ({schoolIdList});
            """
        );
    }

    /// <summary>
    /// Creates a custom authorization view over descriptor storage, authorizing the supplied code values.
    /// Every descriptor resource shares one root table, so a descriptor basis filters that table rather than
    /// a per-resource one.
    /// </summary>
    public async Task CreateDescriptorCustomAuthViewAsync(
        string strategyName,
        IReadOnlyList<string> authorizedCodeValues
    )
    {
        var codeValueList = string.Join(
            ", ",
            authorizedCodeValues.Select(codeValue => $"'{codeValue.Replace("'", "''")}'")
        );

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT "DocumentId"
            FROM "dms"."Descriptor"
            WHERE "CodeValue" IN ({codeValueList});
            """
        );
    }

    /// <summary>
    /// Creates the custom authorization view with an <em>unquoted</em> name, which PostgreSQL folds to
    /// lower case: the object lands as <c>auth.{lowercased}</c> while the configured strategy name stays
    /// PascalCase. This is the mistake hand-written DDL actually makes, as opposed to simulating the
    /// folded result by passing an already-lowercased name to the quoted helper above.
    /// </summary>
    public async Task CreateSchoolCustomAuthViewWithUnquotedNameAsync(
        string strategyName,
        IReadOnlyList<int> authorizedSchoolIds
    )
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var schoolIdList = string.Join(", ", authorizedSchoolIds);

        await DropCustomAuthViewAsync(FoldUnquotedIdentifier(strategyName));
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW auth.{strategyName} AS
            SELECT "DocumentId"
            FROM "{physicalSchema}"."School"
            WHERE "SchoolId" IN ({schoolIdList});
            """
        );
    }

    /// <summary>The name PostgreSQL stores for an unquoted identifier.</summary>
    public static string FoldUnquotedIdentifier(string identifier) => identifier.ToLowerInvariant();

    /// <summary>
    /// Creates (or replaces) an "auth"."{strategyName}" view that omits the required DocumentId column,
    /// simulating a misconfigured custom authorization view.
    /// </summary>
    public async Task CreateCustomAuthViewWithoutDocumentIdAsync(string strategyName)
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT "SchoolId"
            FROM "{physicalSchema}"."School";
            """
        );
    }

    public async Task CreateCustomAuthViewWithTextDocumentIdAsync(string strategyName)
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT "DocumentId"::text AS "DocumentId"
            FROM "{physicalSchema}"."School";
            """
        );
    }

    /// <summary>
    /// Creates (or replaces) an "auth"."{strategyName}" view whose DocumentId column is typed as integer
    /// instead of bigint. PostgreSQL provides a valid <c>bigint = integer</c> operator, so the query-time
    /// join never surfaces an error the way a text-typed column would; the invalid DocumentId contract
    /// must therefore be detected by a catalog check. Emitting no rows (WHERE 1 = 0) additionally proves
    /// the check does not depend on the join producing rows.
    /// </summary>
    public async Task CreateEmptyCustomAuthViewWithIntegerDocumentIdAsync(string strategyName)
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT CAST("DocumentId" AS integer) AS "DocumentId"
            FROM "{physicalSchema}"."School"
            WHERE 1 = 0;
            """
        );
    }

    /// <summary>
    /// Creates (or replaces) "auth"."{strategyName}" as a <em>materialized</em> view with a bigint
    /// DocumentId. Materialized views satisfy the custom authorization object contract, but their
    /// columns are not exposed through information_schema, so a type guard reading that view would
    /// reject a conforming DocumentId. The catalog guard must therefore read pg_catalog.pg_attribute.
    /// </summary>
    public async Task CreateSchoolCustomAuthMaterializedViewAsync(
        string strategyName,
        IReadOnlyList<int> authorizedSchoolIds
    )
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var schoolIdList = string.Join(", ", authorizedSchoolIds);

        await DropCustomAuthMaterializedViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE MATERIALIZED VIEW "auth"."{strategyName}" AS
            SELECT "DocumentId"
            FROM "{physicalSchema}"."School"
            WHERE "SchoolId" IN ({schoolIdList});
            """
        );
    }

    /// <summary>
    /// Creates (or replaces) the "auth"."{strategyName}" custom authorization view over
    /// <c>AuthorizationNamespaceResource</c>, authorizing only the rows whose AuthorizationNamespaceId is
    /// in <paramref name="authorizedIds"/>. That resource also carries a Namespace securable column, so
    /// one view composes with NamespaceBased on the same page query.
    /// </summary>
    public async Task CreateAuthorizationNamespaceCustomAuthViewAsync(
        string strategyName,
        IReadOnlyList<int> authorizedIds
    )
    {
        var namespaceResource = new QualifiedResourceName(
            "Authz",
            RelationshipAuthorizationCrudTestSupport.NamespaceResourceName
        );
        var physicalSchema = MappingSet.ReadPlansByResource[namespaceResource].Model.PhysicalSchema.Value;
        var rootTable = RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
        var idList = string.Join(", ", authorizedIds);

        await DropCustomAuthViewAsync(strategyName);
        await Database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW "auth"."{strategyName}" AS
            SELECT "DocumentId"
            FROM "{physicalSchema}"."{rootTable}"
            WHERE "AuthorizationNamespaceId" IN ({idList});
            """
        );
    }

    public async Task DropCustomAuthViewAsync(string strategyName)
    {
        await Database.ExecuteNonQueryAsync($"""DROP VIEW IF EXISTS "auth"."{strategyName}";""");
    }

    public async Task DropCustomAuthMaterializedViewAsync(string strategyName)
    {
        await Database.ExecuteNonQueryAsync($"""DROP MATERIALIZED VIEW IF EXISTS "auth"."{strategyName}";""");
    }

    public async Task<QueryResult> QueryAsync(
        string projectEndpointName,
        string resourceName,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        int? limit = null,
        int? offset = null,
        bool totalCount = true,
        ChangeVersionRange? changeVersionRange = null,
        IReadOnlyList<string>? namespacePrefixes = null,
        IReadOnlyList<short>? ownershipTokenIds = null,
        CollectionPaging? paging = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext(
                claimEducationOrganizationIds,
                namespacePrefixes ?? [],
                creatorOwnershipTokenId: null,
                ownershipTokenIds ?? []
            ),
            MappingSet: MappingSet,
            QueryElements: [],
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            Paging: paging
                ?? new CollectionPaging.Traditional(
                    new PaginationParameters(
                        Limit: limit,
                        Offset: offset,
                        TotalCount: totalCount,
                        MaximumPageSize: MaximumPageSize
                    )
                ),
            TraceId: new TraceId($"{resourceName}-authorization-query"),
            PageOrderingMode: PageOrderingMode.DocumentId,
            ChangeVersionRange: changeVersionRange
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    public async Task<PartitionResult> QueryPartitionsAsync(
        string projectEndpointName,
        string resourceName,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        int requestedPartitionCount,
        long minimumPartitionSize,
        IReadOnlyList<string>? namespacePrefixes = null,
        IReadOnlyList<short>? ownershipTokenIds = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalPartitionRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext(
                claimEducationOrganizationIds,
                namespacePrefixes ?? [],
                creatorOwnershipTokenId: null,
                ownershipTokenIds ?? []
            ),
            MappingSet: MappingSet,
            QueryElements: [],
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            RequestedPartitionCount: requestedPartitionCount,
            MinimumPartitionSize: minimumPartitionSize,
            TraceId: new TraceId($"{resourceName}-authorization-partitions"),
            PageOrderingMode: PageOrderingMode.DocumentId
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryPartitions(request);
    }

    public async Task<GetResult> GetByIdAsync(
        string projectEndpointName,
        string resourceName,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? traceId = null,
        Func<MappingSet, MappingSet>? mappingSetTransform = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);
        var mappingSet = mappingSetTransform is null ? MappingSet : mappingSetTransform(MappingSet);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new IntegrationRelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: resourceHandle.ResourceInfo,
            MappingSet: mappingSet,
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            TraceId: new TraceId(traceId ?? $"{resourceName}-authorization-get-by-id")
        )
        {
            AuthorizationContext = new RelationalAuthorizationContext(claimEducationOrganizationIds),
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    public async Task<DeleteResult> DeleteByIdAsync(
        string projectEndpointName,
        string resourceName,
        DocumentUuid documentUuid,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null,
        string? traceId = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new DeleteRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: resourceHandle.ResourceInfo,
            TraceId: new TraceId(traceId ?? $"{resourceName}-authorization-delete-by-id"),
            Headers: CreateHeaders(ifMatch),
            MappingSet: MappingSet
        )
        {
            AuthorizationContext = new RelationalAuthorizationContext(claimEducationOrganizationIds),
            AuthorizationStrategyEvaluators =
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .DeleteDocumentById(request);
    }

    public async Task<long> CountDocumentRowsAsync(DocumentUuid documentUuid)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)::bigint
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
    }

    public async Task<long> CountResourceRootRowsAsync(
        string physicalSchema,
        string resourceName,
        DocumentUuid documentUuid
    )
    {
        return await Database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)::bigint
            FROM "{physicalSchema}"."{resourceName}" root
            INNER JOIN "dms"."Document" document
                ON document."DocumentId" = root."DocumentId"
            WHERE document."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
    }

    public async Task<long> CountResourceRootRowsAsync(string projectEndpointName, string resourceName)
    {
        var writePlan = GetWritePlan(projectEndpointName, resourceName);
        return await CountRowsInTableAsync(writePlan.Model.Root.Table);
    }

    public async Task<long> CountResourceCollectionRowsAsync(string projectEndpointName, string resourceName)
    {
        var writePlan = GetWritePlan(projectEndpointName, resourceName);
        long rowCount = 0;

        foreach (
            var tablePlan in writePlan.TablePlansInDependencyOrder.Where(static tablePlan =>
                tablePlan.TableModel.IdentityMetadata.TableKind
                    is DbTableKind.Collection
                        or DbTableKind.ExtensionCollection
            )
        )
        {
            rowCount += await CountRowsInTableAsync(tablePlan.TableModel.Table);
        }

        return rowCount;
    }

    public async Task<long> CountReferentialIdentityRowsForAuthorizationRootChildAsync(
        AuthorizationRootChildSeed seed
    )
    {
        var referentialId = CreateAuthorizationRootChildDocumentInfo(seed).ReferentialId;

        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)::bigint
            FROM "dms"."ReferentialIdentity"
            WHERE "ReferentialId" = @referentialId;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value)
        );
    }

    public async Task<AuthorizationWriteSideEffectState> ReadAuthorizationRootChildSideEffectStateAsync(
        DocumentUuid documentUuid
    )
    {
        var resourceKeyId = GetCompiledResourceKeyId("authz", "AuthorizationRootChildResource");
        var document = await ReadDocumentStateAsync(documentUuid, resourceKeyId);

        return new AuthorizationWriteSideEffectState(
            Document: document,
            ResourceTables: await ReadResourceTableStatesAsync(
                "authz",
                "AuthorizationRootChildResource",
                document.DocumentId
            ),
            ReferentialIdentities: await ReadReferentialIdentityRowsForDocumentAsync(
                document.DocumentId,
                resourceKeyId
            )
        );
    }

    public async Task<AuthorizationWriteSideEffectState> ReadAuthorizationNullableSideEffectStateAsync(
        DocumentUuid documentUuid
    )
    {
        var resourceKeyId = GetCompiledResourceKeyId("authz", "AuthorizationNullableResource");
        var document = await ReadDocumentStateAsync(documentUuid, resourceKeyId);

        return new AuthorizationWriteSideEffectState(
            Document: document,
            ResourceTables: await ReadResourceTableStatesAsync(
                "authz",
                "AuthorizationNullableResource",
                document.DocumentId
            ),
            ReferentialIdentities: await ReadReferentialIdentityRowsForDocumentAsync(
                document.DocumentId,
                resourceKeyId
            )
        );
    }

    public async Task<AuthorizationWriteSideEffectState> ReadAuthorizationStudentAcademicRecordSideEffectStateAsync(
        DocumentUuid documentUuid
    )
    {
        var resourceKeyId = GetCompiledResourceKeyId("authz", "AuthorizationStudentAcademicRecordResource");
        var document = await ReadDocumentStateAsync(documentUuid, resourceKeyId);

        return new AuthorizationWriteSideEffectState(
            Document: document,
            ResourceTables: await ReadResourceTableStatesAsync(
                "authz",
                "AuthorizationStudentAcademicRecordResource",
                document.DocumentId
            ),
            ReferentialIdentities: await ReadReferentialIdentityRowsForDocumentAsync(
                document.DocumentId,
                resourceKeyId
            )
        );
    }

    public async Task<AuthorizationWriteSideEffectState> ReadAuthorizationStudentSchoolSideEffectStateAsync(
        DocumentUuid documentUuid
    )
    {
        var resourceKeyId = GetCompiledResourceKeyId("authz", "AuthorizationStudentSchoolResource");
        var document = await ReadDocumentStateAsync(documentUuid, resourceKeyId);

        return new AuthorizationWriteSideEffectState(
            Document: document,
            ResourceTables: await ReadResourceTableStatesAsync(
                "authz",
                "AuthorizationStudentSchoolResource",
                document.DocumentId
            ),
            ReferentialIdentities: await ReadReferentialIdentityRowsForDocumentAsync(
                document.DocumentId,
                resourceKeyId
            )
        );
    }

    public void AssertPostCreateRelationshipAuthorizationBeforeDocumentInsert()
    {
        var command = GetPostCreateRelationshipAuthorizationCommand();

        command!
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO dms.\"Document\"", StringComparison.Ordinal));
    }

    public void AssertPostCreateStandaloneRelationshipAuthorizationWithoutDocumentInsert()
    {
        var commands = _writeSessionRecorder
            .Commands.Select(static recorded => recorded.CommandText)
            .ToArray();

        commands
            .Where(static commandText => commandText.Contains("AUTH1", StringComparison.Ordinal))
            .Should()
            .NotBeEmpty("deferred reference writes should force proposed authorization before returning 409");
        commands
            .Where(static commandText =>
                commandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            )
            .Should()
            .BeEmpty("deferred missing references should stop before inserting the document");
    }

    public void AssertPostCreateDirectClaimMatchAuthorizationBeforeDocumentInsert()
    {
        var command = GetPostCreateRelationshipAuthorizationCommand();

        // The claim parameter carries the composite allocator's statement-ordinal suffix now that the
        // proposed check is a co-batched statement rather than a prefix on the insert, so the direct-claim
        // comparison and the hierarchy-edge fallback are matched without pinning the issued name.
        command.Should().Contain("= ANY(@ClaimEducationOrganizationIds");
        command.Should().Contain(") OR EXISTS");
        command.Should().Contain("\"auth\".\"EducationOrganizationIdToEducationOrganizationId\"");
        command
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO dms.\"Document\"", StringComparison.Ordinal));
    }

    public void AssertPostCreatePeopleAuthorizationBeforeDocumentInsert()
    {
        var command = GetPostCreateRelationshipAuthorizationCommand();

        command.Should().Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"");
        command.Should().Contain("\"edfi\".\"StudentAcademicRecord\"");
        command
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO dms.\"Document\"", StringComparison.Ordinal));
    }

    public void AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization()
    {
        var peopleAuthorizationCommands = _writeSessionRecorder
            .Commands.Select((command, index) => (command, index))
            .Where(static item =>
                item.command.CommandText.Contains("AUTH1", StringComparison.Ordinal)
                && item.command.CommandText.Contains(
                    "\"auth\".\"EducationOrganizationIdToStudentDocumentId\"",
                    StringComparison.Ordinal
                )
                && item.command.CommandText.Contains(
                    "\"edfi\".\"StudentAcademicRecord\"",
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        peopleAuthorizationCommands.Should().HaveCount(2);
        peopleAuthorizationCommands
            .Select(static item => item.command.SessionId)
            .Distinct()
            .Should()
            .ContainSingle();
        // The stored check is co-batched behind the capture in the first-phase command, so it consumes the
        // provider carrier's captured document id rather than binding one; the proposed check is the one that
        // binds values extracted from the finalized root row.
        peopleAuthorizationCommands[0]
            .command.CommandText.Should()
            .Contain("current_setting('dms.composite_target_documentid', true)");
        peopleAuthorizationCommands[0].command.CommandText.Should().NotContain("@relationshipAuthorization_");
        peopleAuthorizationCommands[1].command.CommandText.Should().Contain("@relationshipAuthorization_");
        peopleAuthorizationCommands[0].index.Should().BeLessThan(peopleAuthorizationCommands[1].index);
    }

    public async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKeyId = MappingSet.ResourceKeyIdByResource[schoolResource];
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var rows = await Database.QueryRowsAsync(
            $"""
            SELECT
                doc."DocumentId",
                doc."DocumentUuid",
                school."SchoolId",
                school."NameOfInstitution",
                school."ContentVersion"
            FROM "dms"."Document" doc
            INNER JOIN "{physicalSchema}"."School" school
                ON school."DocumentId" = doc."DocumentId"
            WHERE doc."ResourceKeyId" = @resourceKeyId
            ORDER BY doc."DocumentId";
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return
        [
            .. rows.Select(row => new PersistedQuerySchool(
                DocumentId: GetRequiredInt64(row, "DocumentId"),
                DocumentUuid: GetRequiredGuid(row, "DocumentUuid"),
                SchoolId: GetRequiredInt32(row, "SchoolId"),
                NameOfInstitution: GetRequiredString(row, "NameOfInstitution"),
                ContentVersion: GetRequiredInt64(row, "ContentVersion")
            )),
        ];
    }

    public async Task MutateAuthorizationRootChildSchoolAsync(
        DocumentUuid documentUuid,
        int newSchoolId,
        CancellationToken cancellationToken = default
    )
    {
        _ = cancellationToken;
        var documentId = await GetDocumentIdByUuidAsync(documentUuid);
        var schoolDocumentId = await GetSchoolDocumentIdAsync(newSchoolId);

        await Database.ExecuteNonQueryAsync(
            """
            UPDATE "authz"."AuthorizationRootChildResource"
            SET
                "School_DocumentId" = @schoolDocumentId,
                "School_SchoolId" = @newSchoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("newSchoolId", newSchoolId),
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<UpsertResult> UpsertAsync(
        string projectEndpointName,
        string resourceName,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string traceId,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? strategyNames = null,
        string? ifMatch = null,
        BackendProfileWriteContext? backendProfileWriteContext = null,
        short? creatorOwnershipTokenId = null
    )
    {
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new UpsertRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                resourceHandle.ResourceInfo,
                resourceHandle.ResourceSchema,
                MappingSet
            ),
            MappingSet: MappingSet,
            EdfiDoc: requestBody,
            Headers: CreateHeaders(ifMatch),
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            BackendProfileWriteContext: backendProfileWriteContext
        )
        {
            // The creator token is stamped onto dms.Document by every create regardless of the configured
            // strategies, which is how a fixture seeds the ownership a later GET-many filters on.
            AuthorizationContext = new RelationalAuthorizationContext(
                claimEducationOrganizationIds ?? [],
                [],
                creatorOwnershipTokenId,
                []
            ),
            AuthorizationStrategyEvaluators =
            [
                .. (strategyNames ?? []).Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpdateResult> UpdateAsync(
        string projectEndpointName,
        string resourceName,
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string traceId,
        IReadOnlyList<long>? claimEducationOrganizationIds = null,
        IReadOnlyList<string>? strategyNames = null,
        string? ifMatch = null,
        BackendProfileWriteContext? backendProfileWriteContext = null
    )
    {
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new UpdateRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                resourceHandle.ResourceInfo,
                resourceHandle.ResourceSchema,
                MappingSet
            ),
            MappingSet: MappingSet,
            EdfiDoc: requestBody,
            Headers: CreateHeaders(ifMatch),
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            BackendProfileWriteContext: backendProfileWriteContext
        )
        {
            AuthorizationContext = new RelationalAuthorizationContext(claimEducationOrganizationIds ?? []),
            AuthorizationStrategyEvaluators =
            [
                .. (strategyNames ?? []).Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
        };

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private async Task<AuthorizationDocumentState> ReadDocumentStateAsync(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        var rows = await Database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt",
                "CreatedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid
              AND "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new AuthorizationDocumentState(
                GetRequiredInt64(rows[0], "DocumentId"),
                GetRequiredGuid(rows[0], "DocumentUuid"),
                GetRequiredInt16(rows[0], "ResourceKeyId"),
                GetRequiredInt64(rows[0], "ContentVersion"),
                GetRequiredDateTime(rows[0], "ContentLastModifiedAt"),
                GetRequiredDateTime(rows[0], "CreatedAt")
            )
            : throw new InvalidOperationException(
                $"Expected one AuthorizationRootChildResource document row for '{documentUuid.Value}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<AuthorizationResourceTableState>> ReadResourceTableStatesAsync(
        string projectEndpointName,
        string resourceName,
        long documentId
    )
    {
        var writePlan = GetWritePlan(projectEndpointName, resourceName);
        List<AuthorizationResourceTableState> states = [];

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            var table = tablePlan.TableModel.Table;
            var columns = tablePlan.TableModel.Columns.Select(static column => column.ColumnName).ToArray();
            var locatorColumns = tablePlan.TableModel.IdentityMetadata.RootScopeLocatorColumns;

            if (locatorColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Table '{table.Schema.Value}.{table.Name}' has no root-scope locator columns."
                );
            }

            var selectColumns = string.Join(", ", columns.Select(static column => $"\"{column.Value}\""));
            var orderColumns =
                tablePlan.TableModel.Key.Columns.Count != 0
                    ? tablePlan.TableModel.Key.Columns.Select(static column => column.ColumnName).ToArray()
                    : columns;
            var orderBy = string.Join(", ", orderColumns.Select(static column => $"\"{column.Value}\""));
            var where = string.Join(
                " AND ",
                locatorColumns.Select(static column => $"\"{column.Value}\" = @documentId")
            );
            var rows = await Database.QueryRowsAsync(
                $"""
                SELECT {selectColumns}
                FROM "{table.Schema.Value}"."{table.Name}"
                WHERE {where}
                ORDER BY {orderBy};
                """,
                new NpgsqlParameter("documentId", documentId)
            );

            states.Add(
                new AuthorizationResourceTableState(
                    $"{table.Schema.Value}.{table.Name}",
                    NormalizeRows(rows, columns)
                )
            );
        }

        return states;
    }

    private async Task<IReadOnlyList<ReferentialIdentityRow>> ReadReferentialIdentityRowsForDocumentAsync(
        long documentId,
        short resourceKeyId
    )
    {
        var rows = await Database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            WHERE "DocumentId" = @documentId
              AND "ResourceKeyId" = @resourceKeyId
            ORDER BY "ResourceKeyId", "ReferentialId";
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return
        [
            .. rows.Select(row => new ReferentialIdentityRow(
                GetRequiredGuid(row, "ReferentialId"),
                GetRequiredInt64(row, "DocumentId"),
                GetRequiredInt16(row, "ResourceKeyId")
            )),
        ];
    }

    private DocumentInfo CreateAuthorizationRootChildDocumentInfo(AuthorizationRootChildSeed seed)
    {
        var resourceHandle = GetResourceHandle("authz", "AuthorizationRootChildResource");

        return RelationalDocumentInfoTestHelper.CreateDocumentInfo(
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            resourceHandle.ResourceInfo,
            resourceHandle.ResourceSchema,
            MappingSet
        );
    }

    private ResourceWritePlan GetWritePlan(string projectEndpointName, string resourceName)
    {
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);
        var resource = new QualifiedResourceName(
            resourceHandle.ResourceInfo.ProjectName.Value,
            resourceHandle.ResourceInfo.ResourceName.Value
        );

        return MappingSet.WritePlansByResource[resource];
    }

    private short GetCompiledResourceKeyId(string projectEndpointName, string resourceName)
    {
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);
        var resource = new QualifiedResourceName(
            resourceHandle.ResourceInfo.ProjectName.Value,
            resourceHandle.ResourceInfo.ResourceName.Value
        );

        return MappingSet.ResourceKeyIdByResource[resource];
    }

    private async Task<long> CountRowsInTableAsync(DbTableName table)
    {
        return await Database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)::bigint
            FROM "{table.Schema.Value}"."{table.Name}";
            """
        );
    }

    private string GetPostCreateRelationshipAuthorizationCommand()
    {
        var commands = _writeSessionRecorder.Commands;
        var command = commands
            .Select(static recorded => recorded.CommandText)
            .FirstOrDefault(commandText =>
                commandText.Contains("AUTH1", StringComparison.Ordinal)
                && commandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );

        command.Should().NotBeNull("POST create should compose authorization and dms.Document insert");
        return command!;
    }

    private ResourceHandle GetResourceHandle(string projectEndpointName, string resourceName)
    {
        var key = (projectEndpointName, resourceName);

        if (_resourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var effectiveProjectSchema = _fixture.EffectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );
        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project endpoint '{projectEndpointName}'."
            );

        var resourceSchema = new ResourceSchema(resourceSchemaNode);
        var resourceInfo = new ResourceInfo(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );

        var resourceHandle = new ResourceHandle(projectSchema, resourceSchema, resourceInfo);
        _resourceCache[key] = resourceHandle;
        return resourceHandle;
    }

    private ServiceProvider CreateServiceProvider(bool replaceReadTargetLookup)
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddSingleton<PostgresqlRelationalQueryExecutionRecorder>();
        services.AddSingleton<PostgresqlRelationalQueryAuthorizationWriteSessionRecorder>();
        services.AddPostgresqlBackendIntegrationTestServices();
        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalWriteSessionFactory,
                PostgresqlRelationalQueryAuthorizationRecordingWriteSessionFactory
            >()
        );
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, RecordingPostgresqlDocumentHydrator>());
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalReadMaterializer, RecordingRelationalReadMaterializer>()
        );

        if (_providerFailureTransform is not null)
        {
            services.Replace(
                ServiceDescriptor.Scoped<IRelationshipAuthorizationProviderFailureExtractor>(
                    _ => new TransformingPostgresqlRelationshipAuthorizationProviderFailureExtractor(
                        _providerFailureTransform
                    )
                )
            );
        }

        if (replaceReadTargetLookup)
        {
            services.Replace(
                ServiceDescriptor.Scoped<
                    IRelationalReadTargetLookupService,
                    ThrowingRelationalReadTargetLookupService
                >()
            );
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalQueryAuthorization",
                    ConnectionString: Database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        var documentId = await InsertDescriptorAsync(
            documentUuid,
            resourceKeyId,
            discriminator,
            uri,
            @namespace,
            codeValue,
            shortDescription
        );

        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await Database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> GetDocumentIdByUuidAsync(DocumentUuid documentUuid)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );
    }

    private async Task<long> GetSchoolDocumentIdAsync(int schoolId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."School"
            WHERE "SchoolId" = @schoolId;
            """,
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task<long> GetSchoolYearDocumentIdAsync(int schoolYear)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."SchoolYearType"
            WHERE "SchoolYear" = @schoolYear;
            """,
            new NpgsqlParameter("schoolYear", schoolYear)
        );
    }

    private async Task<long> GetStudentDocumentIdAsync(string studentUniqueId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."Student"
            WHERE "StudentUniqueId" = @studentUniqueId;
            """,
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task<long> GetStudentAcademicRecordDocumentIdAsync(
        int educationOrganizationId,
        int schoolYear,
        string studentUniqueId,
        string termDescriptor
    )
    {
        var termDescriptorId = await GetDescriptorDocumentIdAsync("TermDescriptor", termDescriptor);

        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."StudentAcademicRecord"
            WHERE "EducationOrganization_EducationOrganizationId" = @educationOrganizationId
              AND "SchoolYear_SchoolYear" = @schoolYear
              AND "Student_StudentUniqueId" = @studentUniqueId
              AND "TermDescriptor_DescriptorId" = @termDescriptorId;
            """,
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("termDescriptorId", termDescriptorId)
        );
    }

    private async Task<long> GetCourseDocumentIdAsync(string courseCode, int educationOrganizationId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT "DocumentId"
            FROM "edfi"."Course"
            WHERE "CourseCode" = @courseCode
              AND "EducationOrganization_EducationOrganizationId" = @educationOrganizationId;
            """,
            new NpgsqlParameter("courseCode", courseCode),
            new NpgsqlParameter("educationOrganizationId", (long)educationOrganizationId)
        );
    }

    private async Task<long> GetDescriptorDocumentIdAsync(string resourceName, string uri)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);

        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT descriptor."DocumentId"
            FROM "dms"."Descriptor" descriptor
            INNER JOIN "dms"."Document" document
                ON document."DocumentId" = descriptor."DocumentId"
            WHERE document."ResourceKeyId" = @resourceKeyId
              AND descriptor."Uri" = @uri;
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("uri", uri)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("DocumentId", "ResourceKeyId") DO UPDATE
            SET "ReferentialId" = EXCLUDED."ReferentialId";
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), true),
            new DocumentIdentity([
                new DocumentIdentityElement(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    descriptorUri.ToLowerInvariant()
                ),
            ])
        );
    }

    private static ReferentialId CreateStudentAcademicRecordReferentialId(StudentAcademicRecordSeed seed) =>
        CreateReferentialId(
            "Ed-Fi",
            "StudentAcademicRecord",
            (
                "$.educationOrganizationReference.educationOrganizationId",
                seed.EducationOrganizationId.ToString(CultureInfo.InvariantCulture)
            ),
            ("$.schoolYearTypeReference.schoolYear", seed.SchoolYear.ToString(CultureInfo.InvariantCulture)),
            ("$.studentReference.studentUniqueId", seed.StudentUniqueId),
            ("$.termDescriptor", seed.TermDescriptor.ToLowerInvariant())
        );

    private static ReferentialId CreateReferentialId(
        string projectName,
        string resourceName,
        params (string JsonPath, string Value)[] identityElements
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), false),
            new DocumentIdentity([
                .. identityElements.Select(static identityElement => new DocumentIdentityElement(
                    new JsonPath(identityElement.JsonPath),
                    identityElement.Value
                )),
            ])
        );
    }

    private sealed record ResourceHandle(
        ProjectSchema ProjectSchema,
        ResourceSchema ResourceSchema,
        ResourceInfo ResourceInfo
    );

    private sealed class TransformingPostgresqlRelationshipAuthorizationProviderFailureExtractor(
        Func<RelationshipAuthorizationProviderFailure, RelationshipAuthorizationProviderFailure> transform
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var providerFailure = exception is PostgresException postgresException
                ? new RelationshipAuthorizationProviderFailure(
                    postgresException.SqlState,
                    postgresException.MessageText
                )
                : new RelationshipAuthorizationProviderFailure(null, exception.Message);

            return transform(providerFailure);
        }
    }

    private static long GetRequiredInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static short GetRequiredInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static int GetRequiredInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static DateTime GetRequiredDateTime(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) is DateTime value
            ? value
            : throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateTime value."
            );
    }

    private static Guid GetRequiredGuid(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) as string
            ?? throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a string value."
            );
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException($"Expected row to contain non-null column '{columnName}'.");
        }

        return value;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> NormalizeRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<DbColumnName> columns
    ) =>
        [
            .. rows.Select(row =>
                (IReadOnlyDictionary<string, string?>)
                    columns.ToDictionary(
                        static column => column.Value,
                        column =>
                            row.TryGetValue(column.Value, out var value)
                                ? NormalizeRowValue(value)
                                : throw new InvalidOperationException(
                                    $"Expected persisted row to contain column '{column.Value}'."
                                ),
                        StringComparer.Ordinal
                    )
            ),
        ];

    private static string? NormalizeRowValue(object? value) =>
        value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private static Dictionary<string, string> CreateHeaders(string? ifMatch) =>
        ifMatch is null ? [] : new Dictionary<string, string> { ["If-Match"] = ifMatch };

    private static bool IsPostgresqlDocumentLockCommand(string commandText) =>
        commandText.Contains("FOR UPDATE", StringComparison.Ordinal)
        && commandText.Contains("dms.\"Document\"", StringComparison.Ordinal);

    private static bool IsPostgresqlRelationshipAuthorizationCommand(string commandText) =>
        commandText.Contains("\"AuthorizationResult\"", StringComparison.Ordinal)
        && commandText.Contains("AUTH1", StringComparison.Ordinal);

    private static bool IsPostgresqlDocumentDeleteCommand(string commandText) =>
        commandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal);
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_Direct_EdOrg_Claim_Match
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly QuerySchoolSeed _directClaimSchoolSeed = new(
        new DocumentUuid(Guid.Parse("99999999-1000-0000-0000-000000000001")),
        (int)ClaimEducationOrganizationId,
        "Claim School"
    );
    private static readonly AuthorizationRootChildSeed _directClaimRootChildSeed = new(
        new DocumentUuid(Guid.Parse("99999999-2000-0000-0000-000000000001")),
        901,
        "query-direct-claim",
        (int)ClaimEducationOrganizationId,
        []
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false
        );
        await _context.SeedSchoolDescriptorDataAsync();

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(_directClaimSchoolSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_directClaimRootChildSeed)
        );
        await _context.DeleteAuthEdgeAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_returns_get_many_results_by_direct_claim_match_without_a_hierarchy_edge()
    {
        (await _context.CountAuthEdgesAsync(ClaimEducationOrganizationId, ClaimEducationOrganizationId))
            .Should()
            .Be(0);

        var result = await _context.QueryAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName,
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_directClaimRootChildSeed.DocumentUuid.Value.ToString());

        var keyset = _context.AssertSingleQueryHydration();
        const string DirectClaimMatchSql =
            "r.\"School_SchoolId\" = ANY(@ClaimEducationOrganizationIds) OR r.\"School_SchoolId\" IN (SELECT";
        keyset.Plan.PageDocumentIdSql.Should().Contain(DirectClaimMatchSql);
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql!.Should().Contain(DirectClaimMatchSql);
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_The_Authoritative_Ds52_School_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const long ClaimEducationOrganizationId = 900;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly IReadOnlyList<string> _invertedStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly IReadOnlyList<string> _normalAndInvertedStrategies =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
    ];
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000001")), 100, "Alpha High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000002")), 200, "Beta High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000003")), 300, "Gamma High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000004")), 400, "Delta High"),
        new(new DocumentUuid(Guid.Parse("11111111-0000-0000-0000-000000000005")), 500, "Epsilon High"),
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await _context.CreateSchoolAsync(schoolSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
        await _context.InsertAuthEdgeAsync(300, ClaimEducationOrganizationId);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 400);
        await _context.InsertAuthEdgeAsync(400, ClaimEducationOrganizationId);

        _persistedSchoolsInDocumentOrder = await _context.ReadPersistedSchoolsInDocumentOrderAsync();
        _persistedSchoolsInDocumentOrder
            .Select(static school => school.SchoolId)
            .Should()
            .Equal(100, 200, 300, 400, 500);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_filters_normal_relationship_authorization_for_the_derived_school_resource()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(3);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[0].DocumentUuid.Value.ToString(),
                _schoolSeeds[1].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );

        var keyset = _context.AssertSingleQueryHydration();
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("= ANY(@ClaimEducationOrganizationIds)")
            .And.Contain("\"TargetEducationOrganizationId\"")
            .And.Contain("\"SchoolId\"");
        keyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .Equal(ClaimEducationOrganizationId);
    }

    [Test]
    public async Task It_filters_inverted_relationship_authorization_bottom_to_top()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _invertedStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[2].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );

        _context
            .AssertSingleQueryHydration()
            .Plan.PageDocumentIdSql.Should()
            .Contain("\"SourceEducationOrganizationId\"")
            .And.Contain("\"TargetEducationOrganizationId\"");
    }

    [Test]
    public async Task It_ors_normal_and_inverted_relationship_authorization_without_duplicates()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalAndInvertedStrategies
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(4);
        success.EdfiDocs.Should().HaveCount(4);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _schoolSeeds[0].DocumentUuid.Value.ToString(),
                _schoolSeeds[1].DocumentUuid.Value.ToString(),
                _schoolSeeds[2].DocumentUuid.Value.ToString(),
                _schoolSeeds[3].DocumentUuid.Value.ToString()
            );
    }

    [Test]
    public async Task It_pages_and_counts_after_relationship_authorization_filtering()
    {
        var authorizedDocumentIds = _persistedSchoolsInDocumentOrder
            .Where(static school => school.SchoolId is 100 or 200 or 300 or 400)
            .Skip(1)
            .Take(2)
            .Select(static school => school.DocumentUuid.ToString())
            .ToArray();

        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalAndInvertedStrategies,
            limit: 2,
            offset: 1,
            totalCount: true
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(4);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(authorizedDocumentIds);
    }

    [Test]
    public async Task It_returns_an_empty_page_and_zero_total_count_when_claim_edorgs_are_empty()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [],
            _normalAndInvertedStrategies,
            totalCount: true
        );

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0) { SelectionSkipped = true });
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_composes_the_change_version_window_with_relationship_authorization_filtering()
    {
        // The stamping triggers assign strictly increasing ContentVersion values in insert order, and
        // minChangeVersion is inclusive. A window from Beta through Gamma holds authorized Beta
        // (SchoolId 200) plus unauthorized Gamma (SchoolId 300). Authorization excludes in-window
        // Gamma, so the lower-bound row proves the change-version predicate composes with auth.
        var betaSchool = _persistedSchoolsInDocumentOrder[1];
        var gammaSchool = _persistedSchoolsInDocumentOrder[2];

        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            _normalStrategy,
            totalCount: true,
            changeVersionRange: new ChangeVersionRange(betaSchool.ContentVersion, gammaSchool.ContentVersion)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(betaSchool.DocumentUuid.ToString());

        var keyset = _context.AssertSingleQueryHydration();
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"ContentVersion\" >= @minChangeVersion")
            .And.Contain("r.\"ContentVersion\" <= @maxChangeVersion")
            .And.Contain("= ANY(@ClaimEducationOrganizationIds)");
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_A_Synthetic_EdOrg_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/synthetic/authorization-query";
    private const long ClaimEducationOrganizationId = 900;
    private static readonly IReadOnlyList<string> _normalStrategy =
    [
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
    ];
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000002")), 200, "South School"),
        new(new DocumentUuid(Guid.Parse("22222222-0000-0000-0000-000000000003")), 300, "West School"),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000001")), 100, "P1"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000002")), 200, "P2"),
        new(new DocumentUuid(Guid.Parse("33333333-0000-0000-0000-000000000003")), 300, "P3"),
    ];
    private static readonly AuthorizationAndSeed[] _authorizationAndSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000001")),
            1,
            "requires-both",
            100,
            200
        ),
        new(
            new DocumentUuid(Guid.Parse("44444444-0000-0000-0000-000000000002")),
            2,
            "missing-secondary-auth",
            100,
            300
        ),
    ];
    private static readonly AuthorizationRootChildSeed[] _authorizationRootChildSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000001")),
            1,
            "authorized-by-root",
            100,
            [new ClassPeriodReferenceSeed("P3", 300)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000002")),
            2,
            "child-would-match-but-root-does-not",
            300,
            [new ClassPeriodReferenceSeed("P1", 100)]
        ),
        new(
            new DocumentUuid(Guid.Parse("55555555-0000-0000-0000-000000000003")),
            3,
            "authorized-with-empty-child-collection",
            100,
            []
        ),
    ];
    private static readonly AuthorizationChildOnlySeed _authorizationChildOnlySeed = new(
        new DocumentUuid(Guid.Parse("66666666-0000-0000-0000-000000000001")),
        1,
        "child-only",
        [new ClassPeriodReferenceSeed("P1", 100)]
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: false);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await _context.CreateSchoolAsync(schoolSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            var createResult = await _context.CreateClassPeriodAsync(classPeriodSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var authorizationAndSeed in _authorizationAndSeeds)
        {
            var createResult = await _context.CreateAuthorizationAndAsync(authorizationAndSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        foreach (var authorizationRootChildSeed in _authorizationRootChildSeeds)
        {
            var createResult = await _context.CreateAuthorizationRootChildAsync(authorizationRootChildSeed);
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(createResult);
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationChildOnlyAsync(_authorizationChildOnlySeed)
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_ands_multiple_root_base_edorg_subjects_within_one_strategy()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationAndResource",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_authorizationAndSeeds[0].DocumentUuid.Value.ToString());

        _context.AssertSingleQueryHydration().Plan.PageDocumentIdSql.Should().Contain(" AND ");
    }

    [Test]
    public async Task It_authorizes_root_plus_child_resources_from_the_root_subject_only_including_empty_children()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationRootChildResource",
            [ClaimEducationOrganizationId],
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _authorizationRootChildSeeds[0].DocumentUuid.Value.ToString(),
                _authorizationRootChildSeeds[2].DocumentUuid.Value.ToString()
            );
    }

    [Test]
    public async Task It_returns_security_configuration_failure_for_child_only_resources()
    {
        var result = await _context.QueryAsync(
            "authz",
            "AuthorizationChildOnlyResource",
            [ClaimEducationOrganizationId],
            _normalStrategy,
            totalCount: false
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;

        failure.Errors.Should().ContainSingle();
        failure.Errors[0].Should().Contain("$.classPeriods[*].classPeriodReference.schoolId");
        failure.Errors[0].Should().Contain("SchoolId");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Duplicate people-auth-pair scenarios (DMS-1329)
//
// Every route by which the change admits duplicates is covered, and
// all four people views participate:
//   1. Multiple closure paths — the views no longer SELECT DISTINCT, so
//      a student enrolled at two schools reachable from the same claim
//      EdOrg yields the (claim, student) pair once per closure path.
//   2. Cross-arm — the staff view combines its assignment and
//      employment arms with UNION ALL rather than UNION, so a staff
//      member both assigned and employed at one claim-reachable EdOrg
//      yields the (claim, staff) pair once per arm.
//   3. Multiple association rows on ONE closure edge — two
//      responsibilities at a single EdOrg (BeginDate is an identity
//      field) duplicate the pair in the through-responsibility view
//      without a second closure path or a second arm.
//   4. Duplicates carried across joins — the contact view reaches the
//      person through two joins, so scenario 1's enrollments duplicate
//      the (claim, contact) pair from one association row.
// Each test first proves that duplicate cardinality at the view level
// (non-vacuity), then proves the IN/EXISTS consumers still return each
// authorized document exactly once with unchanged authorization
// outcomes.
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_A_Duplicate_People_Auth_Pair
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private const string StaffClassificationDescriptor =
        "uri://ed-fi.org/StaffClassificationDescriptor#Teacher";
    private const string EmploymentStatusDescriptor =
        "uri://ed-fi.org/EmploymentStatusDescriptor#Substitute/temporary";
    private const string ResponsibilityDescriptor = "uri://ed-fi.org/ResponsibilityDescriptor#Accountability";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000002")), 200, "East School"),
        new(new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000011")),
        2026,
        true,
        "2026"
    );

    private static readonly StudentSeed _dualEnrolledStudentSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000021")),
        "20001",
        "Dana",
        "Dual"
    );

    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000022")),
        "20002",
        "Uri",
        "Unreachable"
    );

    // The dual-enrolled student's two associations are both reachable from the claim EdOrg, so the
    // student auth view yields the (claim, student) pair once per closure path.
    private static readonly StudentSchoolAssociationSeed[] _studentSchoolAssociationSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000031")),
            "20001",
            100,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000032")),
            "20001",
            200,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000033")),
            "20002",
            300,
            2026,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
    ];

    private static readonly StudentAcademicRecordSeed _dualEnrolledStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000041")),
        100,
        2026,
        "20001",
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000042")),
        300,
        2026,
        "20002",
        TermDescriptor
    );

    private static readonly AuthorizationStudentAcademicRecordSeed _dualEnrolledAuthorizationSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000051")),
        9101,
        "duplicate-pair-authorized",
        100,
        2026,
        "20001",
        TermDescriptor
    );

    private static readonly AuthorizationStudentAcademicRecordSeed _unauthorizedAuthorizationSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000052")),
        9102,
        "duplicate-pair-unauthorized",
        300,
        2026,
        "20002",
        TermDescriptor
    );

    // Assigned AND employed at School 100, so the staff view's two UNION ALL arms each contribute the
    // (claim, staff) pair — the cross-arm duplicate that per-arm enrollment duplicates cannot produce.
    private static readonly StaffSeed _dualPathwayStaffSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000071")),
        "20071",
        "Dana",
        "Dualpathway"
    );

    private static readonly StaffSeed _unauthorizedStaffSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000072")),
        "20072",
        "Uri",
        "Unreachablestaff"
    );

    private static readonly StaffEducationOrganizationAssignmentAssociationSeed[] _staffAssignmentSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000081")),
            "20071",
            100,
            StaffClassificationDescriptor,
            new DateOnly(2025, 8, 1)
        ),
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000082")),
            "20072",
            300,
            StaffClassificationDescriptor,
            new DateOnly(2025, 8, 1)
        ),
    ];

    private static readonly StaffEducationOrganizationEmploymentAssociationSeed _staffEmploymentSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000091")),
        "20071",
        100,
        EmploymentStatusDescriptor,
        new DateOnly(2025, 8, 1)
    );

    // Contacts reach the claim only through their student. The contact view is the deepest people view
    // (closure -> StudentSchoolAssociation -> StudentContactAssociation), so the dual-enrolled student's
    // two claim-reachable enrollments duplicate the (claim, contact) pair from a single association row.
    private static readonly ContactSeed _duplicateReachableContactSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000101")),
        "20101",
        "Dana",
        "Dualcontact"
    );

    private static readonly ContactSeed _unauthorizedContactSeed = new(
        new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000102")),
        "20102",
        "Uri",
        "Unreachablecontact"
    );

    private static readonly StudentContactAssociationSeed[] _studentContactAssociationSeeds =
    [
        new(new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000111")), "20001", "20101", true),
        new(new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000112")), "20002", "20102", true),
    ];

    // BeginDate is part of this association's identity, so two responsibilities at the SAME
    // EducationOrganization are two distinct documents. That makes the through-responsibility view the
    // one people view reaching duplicate cardinality from a single closure edge — no second enrollment
    // and no second view arm are involved.
    private static readonly StudentEducationOrganizationResponsibilityAssociationSeed[] _studentResponsibilitySeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000121")),
            "20001",
            100,
            ResponsibilityDescriptor,
            new DateOnly(2025, 8, 1)
        ),
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000122")),
            "20001",
            100,
            ResponsibilityDescriptor,
            new DateOnly(2026, 1, 12)
        ),
        new(
            new DocumentUuid(Guid.Parse("12121212-0000-0000-0000-000000000123")),
            "20002",
            300,
            ResponsibilityDescriptor,
            new DateOnly(2025, 8, 1)
        ),
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        // replaceReadTargetLookup: false — this fixture exercises GET-by-id as well as GET-many, so
        // the real read-target lookup must stay wired in.
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();
        await _context.SeedTermDescriptorAsync(
            Guid.Parse("12121212-0000-0000-0000-000000000061"),
            TermDescriptor
        );

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_dualEnrolledStudentSeed);
        await _context.SeedStudentAsync(_unauthorizedStudentSeed);

        foreach (var associationSeed in _studentSchoolAssociationSeeds)
        {
            await _context.SeedStudentSchoolAssociationAsync(associationSeed);
        }

        await _context.SeedStudentAcademicRecordAsync(_dualEnrolledStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_unauthorizedStudentAcademicRecordSeed);
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(_dualEnrolledAuthorizationSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationStudentAcademicRecordAsync(_unauthorizedAuthorizationSeed)
        );

        await _context.SeedStaffClassificationDescriptorAsync(
            Guid.Parse("12121212-0000-0000-0000-000000000062"),
            StaffClassificationDescriptor
        );
        await _context.SeedEmploymentStatusDescriptorAsync(
            Guid.Parse("12121212-0000-0000-0000-000000000063"),
            EmploymentStatusDescriptor
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateStaffAsync(_dualPathwayStaffSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateStaffAsync(_unauthorizedStaffSeed)
        );

        foreach (var assignmentSeed in _staffAssignmentSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateStaffEducationOrganizationAssignmentAssociationAsync(assignmentSeed)
            );
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateStaffEducationOrganizationEmploymentAssociationAsync(_staffEmploymentSeed)
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateContactAsync(_duplicateReachableContactSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateContactAsync(_unauthorizedContactSeed)
        );

        foreach (var studentContactAssociationSeed in _studentContactAssociationSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateStudentContactAssociationAsync(studentContactAssociationSeed)
            );
        }

        await _context.SeedResponsibilityDescriptorAsync(
            Guid.Parse("12121212-0000-0000-0000-000000000064"),
            ResponsibilityDescriptor
        );

        foreach (var responsibilitySeed in _studentResponsibilitySeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateStudentEducationOrganizationResponsibilityAssociationAsync(
                    responsibilitySeed
                )
            );
        }

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_returns_each_authorized_document_exactly_once_under_duplicate_auth_pairs()
    {
        // Non-vacuity precondition: the (claim, student) pair genuinely occurs twice in the view,
        // and the control student never appears under the claim.
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Student,
            _dualEnrolledStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Student,
            _unauthorizedStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 0
        );

        var result = await _context.QueryAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.StudentAcademicRecordResourceName,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_dualEnrolledAuthorizationSeed.DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_authorizes_single_record_reads_under_duplicate_auth_pairs()
    {
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Student,
            _dualEnrolledStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );

        var authorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.StudentAcademicRecordResourceName,
            _dualEnrolledAuthorizationSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames
        );
        var unauthorizedResult = await _context.GetByIdAsync(
            "authz",
            RelationshipAuthorizationCrudTestSupport.StudentAcademicRecordResourceName,
            _unauthorizedAuthorizationSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames
        );

        var success = authorizedResult.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(_dualEnrolledAuthorizationSeed.DocumentUuid);
        unauthorizedResult.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>();
    }

    [Test]
    public async Task It_returns_each_authorized_staff_exactly_once_under_cross_arm_duplicate_auth_pairs()
    {
        // The staff view is the only multi-arm people auth view, and DMS-1329 combines its arms with
        // UNION ALL instead of UNION. A staff member both assigned and employed at the same
        // claim-reachable EdOrg is therefore the one duplicate class the set-operator alone produces —
        // the per-arm enrollment duplicates the student scenarios cover cannot reach it.
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Staff,
            _dualPathwayStaffSeed.StaffUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Staff,
            _unauthorizedStaffSeed.StaffUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 0
        );

        var result = await _context.QueryAsync(
            "ed-fi",
            "Staff",
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_dualPathwayStaffSeed.DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_authorizes_single_record_staff_reads_under_cross_arm_duplicate_auth_pairs()
    {
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Staff,
            _dualPathwayStaffSeed.StaffUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );

        var authorizedResult = await _context.GetByIdAsync(
            "ed-fi",
            "Staff",
            _dualPathwayStaffSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );
        var unauthorizedResult = await _context.GetByIdAsync(
            "ed-fi",
            "Staff",
            _unauthorizedStaffSeed.DocumentUuid,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        var success = authorizedResult.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(_dualPathwayStaffSeed.DocumentUuid);
        unauthorizedResult.Should().BeOfType<GetResult.GetFailureRelationshipNotAuthorized>();
    }

    [Test]
    public async Task It_returns_each_authorized_contact_exactly_once_under_duplicate_auth_pairs()
    {
        // The contact view carries the duplicate through two joins (closure -> StudentSchoolAssociation
        // -> StudentContactAssociation): one association row, but the dual-enrolled student reaches the
        // claim twice. GET-many is the shape ODS needed its dedup for — per auth.md, "to ensure that
        // multiple entries in the auth views don't result in duplicate rows during GET-many".
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Contact,
            _duplicateReachableContactSeed.ContactUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Contact,
            _unauthorizedContactSeed.ContactUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 0
        );

        var result = await _context.QueryAsync(
            "ed-fi",
            "Contact",
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.PeopleOnlyStrategyNames
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_duplicateReachableContactSeed.DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_returns_each_authorized_student_exactly_once_under_duplicate_responsibility_pairs()
    {
        // Two responsibilities at School 100 differing only in BeginDate (an identity field) duplicate
        // the (claim, student) pair from a SINGLE closure edge — no second enrollment and no second view
        // arm, which is a duplicate route neither the student nor the staff scenario reaches.
        // RelationshipsWithStudentsOnlyThroughResponsibility is also a distinct consumer strategy.
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.StudentThroughResponsibility,
            _dualEnrolledStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.StudentThroughResponsibility,
            _unauthorizedStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 0
        );

        var result = await _context.QueryAsync(
            "ed-fi",
            "Student",
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyThroughResponsibilityStrategyNames
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_dualEnrolledStudentSeed.DocumentUuid.Value.ToString());
    }
}

/// <summary>
/// Binds the real query pipeline to the anchored authorization predicate on properly seeded rows, for one
/// direct-column person pathway (StudentAcademicRecord) and one transitive pathway (CourseTranscript).
/// </summary>
/// <remarks>
/// The volume fixtures generate rows with direct set-based SQL and measure SQL against them, which is what the
/// plan-shape and equivalence evidence needs but stops short of the product's own read path. This fixture closes
/// that gap at small scale: it seeds through the same helpers the rest of this file uses and then asserts that
/// <c>QueryAsync</c> returns the authorized documents, in order, with the anchored predicate visibly in the SQL
/// the pipeline actually issued.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_Anchored_Person_Pathways
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string TermDescriptor = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string EntryGradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private const string CourseAttemptResultDescriptor = "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass";
    private const string CourseCode = "ANCHOR-101";
    private const int SchoolYear = 2026;
    private const string DirectPathwayResourceName = "StudentAcademicRecord";
    private const string TransitivePathwayResourceName = "CourseTranscript";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000001")), 100, "North School"),
        new(new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000002")), 200, "East School"),
        new(new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000003")), 300, "West School"),
    ];

    private static readonly SchoolYearTypeSeed _schoolYearSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000011")),
        SchoolYear,
        true,
        "2026"
    );

    /// <summary>Enrolled at two claim-reachable schools, so the student auth view yields its pair twice.</summary>
    private static readonly StudentSeed _dualEnrolledStudentSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000021")),
        "31001",
        "Dana",
        "Dual"
    );

    private static readonly StudentSeed _singleEnrolledStudentSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000022")),
        "31002",
        "Alex",
        "Single"
    );

    private static readonly StudentSeed _unauthorizedStudentSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000023")),
        "31003",
        "Uri",
        "Unreachable"
    );

    private static readonly StudentSchoolAssociationSeed[] _studentSchoolAssociationSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000031")),
            "31001",
            100,
            SchoolYear,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000032")),
            "31001",
            200,
            SchoolYear,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000033")),
            "31002",
            100,
            SchoolYear,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
        new(
            new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000034")),
            "31003",
            300,
            SchoolYear,
            EntryGradeLevelDescriptor,
            new DateOnly(2026, 8, 15)
        ),
    ];

    // Seeded in this order, so DocumentId — and therefore the page ordering — follows it.
    private static readonly StudentAcademicRecordSeed _dualEnrolledStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000041")),
        100,
        SchoolYear,
        "31001",
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _singleEnrolledStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000042")),
        100,
        SchoolYear,
        "31002",
        TermDescriptor
    );

    private static readonly StudentAcademicRecordSeed _unauthorizedStudentAcademicRecordSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000043")),
        300,
        SchoolYear,
        "31003",
        TermDescriptor
    );

    private static readonly CourseSeed _courseSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000051")),
        CourseCode,
        100,
        "Anchored Pathways"
    );

    private static readonly CourseTranscriptSeed _dualEnrolledCourseTranscriptSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000061")),
        CourseCode,
        100,
        100,
        SchoolYear,
        "31001",
        TermDescriptor,
        CourseAttemptResultDescriptor
    );

    private static readonly CourseTranscriptSeed _singleEnrolledCourseTranscriptSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000062")),
        CourseCode,
        100,
        100,
        SchoolYear,
        "31002",
        TermDescriptor,
        CourseAttemptResultDescriptor
    );

    private static readonly CourseTranscriptSeed _unauthorizedCourseTranscriptSeed = new(
        new DocumentUuid(Guid.Parse("14141414-0000-0000-0000-000000000063")),
        CourseCode,
        100,
        300,
        SchoolYear,
        "31003",
        TermDescriptor,
        CourseAttemptResultDescriptor
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false
        );
        await _context.SeedSchoolDescriptorDataAsync();
        await _context.SeedTermDescriptorAsync(
            Guid.Parse("14141414-0000-0000-0000-000000000071"),
            TermDescriptor
        );
        await _context.SeedCourseAttemptResultDescriptorAsync(
            Guid.Parse("14141414-0000-0000-0000-000000000072"),
            CourseAttemptResultDescriptor
        );

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        await _context.SeedSchoolYearTypeAsync(_schoolYearSeed);
        await _context.SeedStudentAsync(_dualEnrolledStudentSeed);
        await _context.SeedStudentAsync(_singleEnrolledStudentSeed);
        await _context.SeedStudentAsync(_unauthorizedStudentSeed);

        foreach (var associationSeed in _studentSchoolAssociationSeeds)
        {
            await _context.SeedStudentSchoolAssociationAsync(associationSeed);
        }

        await _context.SeedStudentAcademicRecordAsync(_dualEnrolledStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_singleEnrolledStudentAcademicRecordSeed);
        await _context.SeedStudentAcademicRecordAsync(_unauthorizedStudentAcademicRecordSeed);

        await _context.SeedCourseAsync(_courseSeed);
        await _context.SeedCourseTranscriptAsync(_dualEnrolledCourseTranscriptSeed);
        await _context.SeedCourseTranscriptAsync(_singleEnrolledCourseTranscriptSeed);
        await _context.SeedCourseTranscriptAsync(_unauthorizedCourseTranscriptSeed);

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_returns_the_authorized_direct_pathway_documents_in_order()
    {
        var result = await QueryAsync(DirectPathwayResourceName);

        AssertPage(
            result,
            expectedTotalCount: 2,
            _dualEnrolledStudentAcademicRecordSeed.DocumentUuid,
            _singleEnrolledStudentAcademicRecordSeed.DocumentUuid
        );
    }

    [Test]
    public async Task It_returns_the_authorized_transitive_pathway_documents_in_order()
    {
        var result = await QueryAsync(TransitivePathwayResourceName);

        AssertPage(
            result,
            expectedTotalCount: 2,
            _dualEnrolledCourseTranscriptSeed.DocumentUuid,
            _singleEnrolledCourseTranscriptSeed.DocumentUuid
        );
    }

    /// <summary>
    /// The boundary offset: the last authorized document, then a page past the end. TotalCount stays at the
    /// authorized count either way, which is what proves the empty page is a paging boundary rather than a
    /// collapsed authorization result.
    /// </summary>
    [TestCase(DirectPathwayResourceName)]
    [TestCase(TransitivePathwayResourceName)]
    public async Task It_pages_the_authorized_documents_to_the_boundary(string resourceName)
    {
        var lastAuthorizedDocumentUuid =
            resourceName == DirectPathwayResourceName
                ? _singleEnrolledStudentAcademicRecordSeed.DocumentUuid
                : _singleEnrolledCourseTranscriptSeed.DocumentUuid;

        var lastPage = await QueryAsync(resourceName, limit: 1, offset: 1);
        AssertPage(lastPage, expectedTotalCount: 2, lastAuthorizedDocumentUuid);

        _context.ResetRecorder();

        var pastTheEnd = await QueryAsync(resourceName, limit: 1, offset: 2);
        AssertPage(pastTheEnd, expectedTotalCount: 2);
    }

    /// <summary>
    /// The anchored predicate, in the SQL the pipeline actually issued: the root relation appears exactly once
    /// in both the page and the totalCount statement, and the semi-join opens on a column of the root row — the
    /// person column itself for the direct pathway.
    /// </summary>
    [Test]
    public async Task It_issues_the_anchored_direct_predicate_with_one_root_relation_reference()
    {
        await QueryAsync(DirectPathwayResourceName);

        var keyset = _context.AssertSingleQueryHydration();

        AssertSingleRootRelationReference(keyset, DirectPathwayResourceName);
        AssertBothStatementsContain(keyset, "r.\"Student_DocumentId\" IN (SELECT");
    }

    /// <summary>
    /// The transitive twin: the semi-join opens on the root row's reference FK and the subquery starts at the
    /// first hop's target table, so the root relation is never reopened.
    /// </summary>
    [Test]
    public async Task It_issues_the_anchored_transitive_predicate_with_one_root_relation_reference()
    {
        await QueryAsync(TransitivePathwayResourceName);

        var keyset = _context.AssertSingleQueryHydration();

        AssertSingleRootRelationReference(keyset, TransitivePathwayResourceName);
        AssertBothStatementsContain(
            keyset,
            "r.\"StudentAcademicRecord_DocumentId\" IN (SELECT t0.\"DocumentId\" "
                + "FROM \"edfi\".\"StudentAcademicRecord\" t0"
        );
    }

    /// <summary>
    /// The short-circuit the anchoring rewrite must not disturb: an empty claim set never reaches page SQL.
    /// </summary>
    [TestCase(DirectPathwayResourceName)]
    [TestCase(TransitivePathwayResourceName)]
    public async Task It_returns_an_empty_page_without_hydrating_when_claim_edorgs_are_empty(
        string resourceName
    )
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            resourceName,
            [],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames
        );

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0) { SelectionSkipped = true });
        _context.AssertNoHydration();
    }

    /// <summary>
    /// The duplicate-pair guarantee, extended to the transitive shape. The dual-enrolled student's pair occurs
    /// twice in the auth view, and the semi-join must still yield its document once — a join would not.
    /// </summary>
    [TestCase(DirectPathwayResourceName)]
    [TestCase(TransitivePathwayResourceName)]
    public async Task It_returns_each_authorized_document_once_under_duplicate_auth_pairs(string resourceName)
    {
        await _context.AssertPeopleAuthViewPairCountAsync(
            AuthPeopleViewKind.Student,
            _dualEnrolledStudentSeed.StudentUniqueId,
            ClaimEducationOrganizationId,
            expectedPairCount: 2
        );

        var result = await QueryAsync(resourceName);

        var duplicatePairDocumentUuid =
            resourceName == DirectPathwayResourceName
                ? _dualEnrolledStudentAcademicRecordSeed.DocumentUuid
                : _dualEnrolledCourseTranscriptSeed.DocumentUuid;
        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .ContainSingle(id => id == duplicatePairDocumentUuid.Value.ToString());
    }

    private async Task<QueryResult> QueryAsync(string resourceName, int? limit = null, int? offset = null) =>
        await _context.QueryAsync(
            "ed-fi",
            resourceName,
            [ClaimEducationOrganizationId],
            RelationshipAuthorizationCrudTestSupport.StudentsOnlyStrategyNames,
            limit,
            offset
        );

    private static void AssertSingleRootRelationReference(PageKeysetSpec.Query keyset, string rootTableName)
    {
        // Derived from the dialect rather than hand-quoted, matching the other root-relation counts in
        // this branch: QualifyTable carries the closing delimiter, which is what stops a shorter table
        // name from matching inside a longer one.
        var quotedRootRelation = SqlDialectFactory
            .Create(SqlDialect.Pgsql)
            .QualifyTable(new DbTableName(new DbSchemaName("edfi"), rootTableName));

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        CountOccurrences(keyset.Plan.PageDocumentIdSql, quotedRootRelation).Should().Be(1);
        CountOccurrences(keyset.Plan.TotalCountSql!, quotedRootRelation).Should().Be(1);
    }

    private static void AssertBothStatementsContain(PageKeysetSpec.Query keyset, string expected)
    {
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.PageDocumentIdSql.Should().Contain(expected);
        keyset.Plan.TotalCountSql!.Should().Contain(expected);
    }

    private static int CountOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;

    private static void AssertPage(
        QueryResult result,
        int expectedTotalCount,
        params DocumentUuid[] expectedDocumentUuids
    )
    {
        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(expectedTotalCount);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(expectedDocumentUuids.Select(static documentUuid => documentUuid.Value.ToString()));
    }
}
