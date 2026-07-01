// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlRelationalQueryAuthorizationRecordedCommand(int SessionId, string CommandText);

internal sealed class MssqlRelationalQueryAuthorizationWriteSessionRecorder
{
    private readonly object _sync = new();
    private readonly List<MssqlRelationalQueryAuthorizationRecordedCommand> _commands = [];
    private int _nextSessionId;

    public IReadOnlyList<MssqlRelationalQueryAuthorizationRecordedCommand> Commands
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
                new MssqlRelationalQueryAuthorizationRecordedCommand(sessionId, command.CommandText)
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

internal sealed class MssqlRelationalQueryAuthorizationRecordingWriteSessionFactory(
    IDataStoreSelection dataStoreSelection,
    IOptions<DatabaseOptions> databaseOptions,
    MssqlRelationalQueryAuthorizationWriteSessionRecorder recorder
) : IRelationalWriteSessionFactory
{
    private readonly IDataStoreSelection _dataStoreSelection =
        dataStoreSelection ?? throw new ArgumentNullException(nameof(dataStoreSelection));
    private readonly IOptions<DatabaseOptions> _databaseOptions =
        databaseOptions ?? throw new ArgumentNullException(nameof(databaseOptions));
    private readonly MssqlRelationalQueryAuthorizationWriteSessionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var selectedInstance = _dataStoreSelection.GetSelectedDataStore();
        var connectionString = selectedInstance.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Selected DMS instance '{selectedInstance.Id}' does not have a valid connection string."
            );
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            var transaction = await connection.BeginTransactionAsync(
                _databaseOptions.Value.IsolationLevel,
                cancellationToken
            );
            return new MssqlRelationalQueryAuthorizationRecordingWriteSession(
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

internal sealed class MssqlRelationalQueryAuthorizationRecordingWriteSession(
    DbConnection connection,
    DbTransaction transaction,
    int sessionId,
    MssqlRelationalQueryAuthorizationWriteSessionRecorder recorder
) : IRelationalWriteSession
{
    public DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public DbTransaction Transaction { get; } =
        transaction ?? throw new ArgumentNullException(nameof(transaction));

    private readonly MssqlRelationalQueryAuthorizationWriteSessionRecorder _recorder =
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

internal sealed class MssqlRelationalQueryAuthorizationTestContext : IAsyncDisposable
{
    private const int MaximumPageSize = 500;
    private readonly Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    >? _providerFailureTransform;
    private static readonly BaseResourceInfo SchoolResource = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("School"),
        false
    );
    private static readonly BaseResourceInfo ClassPeriodResource = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("ClassPeriod"),
        false
    );
    private readonly Dictionary<
        (string ProjectEndpointName, string ResourceName),
        ResourceHandle
    > _resourceCache = [];
    private readonly Dictionary<int, long> _schoolDocumentIdsBySchoolId = [];

    private MssqlGeneratedDdlFixture _fixture = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlRelationalQueryExecutionRecorder _recorder = null!;
    private MssqlRelationalQueryAuthorizationWriteSessionRecorder _writeSessionRecorder = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;

    public MappingSet MappingSet => _fixture.MappingSet;

    public MssqlGeneratedDdlTestDatabase Database { get; private set; } = null!;

    public MssqlRelationalQueryAuthorizationTestContext(
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
        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(fixtureRelativePath, strict);
        IMssqlGeneratedDdlBaselineDatabase baseline = await MssqlBackendBaselineCache.CreateOrGetAsync(
            MssqlBackendBaselineCache.BuildFixtureSignature(fixtureRelativePath, strict),
            _fixture.GeneratedDdl
        );
        _databaseLease = await baseline.AcquireRestoredDatabaseAsync();
        Database = _databaseLease.Database;
        _serviceProvider = CreateServiceProvider(replaceReadTargetLookup);
        _recorder = _serviceProvider.GetRequiredService<MssqlRelationalQueryExecutionRecorder>();
        _writeSessionRecorder =
            _serviceProvider.GetRequiredService<MssqlRelationalQueryAuthorizationWriteSessionRecorder>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
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

        var firstLockIndex = FindRequiredCommandIndex(commands, IsMssqlDocumentLockCommand);
        var authorizationIndex = FindRequiredCommandIndex(commands, IsMssqlRelationshipAuthorizationCommand);
        var secondLockIndex = FindRequiredCommandIndex(
            commands,
            IsMssqlDocumentLockCommand,
            authorizationIndex + 1
        );
        var deleteIndex = FindRequiredCommandIndex(commands, IsMssqlDocumentDeleteCommand);

        firstLockIndex.Should().BeLessThan(authorizationIndex);
        authorizationIndex.Should().BeLessThan(secondLockIndex);
        secondLockIndex.Should().BeLessThan(deleteIndex);
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

    public async Task<UpsertResult> CreateSchoolAsync(QuerySchoolSeed seed)
    {
        return await UpsertAsync(
            "ed-fi",
            "School",
            RelationalQueryAuthorizationRequestBodies.CreateSchoolRequestBody(
                seed.SchoolId,
                seed.NameOfInstitution
            ),
            seed.DocumentUuid,
            $"seed-school-{seed.SchoolId}"
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

    public async Task<UpsertResult> CreateAuthorizationRootChildAsync(AuthorizationRootChildSeed seed)
    {
        return await UpsertAsync(
            "authz",
            "AuthorizationRootChildResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationRootChildRequestBody(seed),
            seed.DocumentUuid,
            $"seed-auth-root-child-{seed.AuthorizationRootChildId}"
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

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "authz",
            "AuthorizationStudentAcademicRecordResource",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [authz].[AuthorizationStudentAcademicRecordResource] (
                        [DocumentId],
                        [StudentAcademicRecord_DocumentId],
                        [StudentAcademicRecord_EducationOrganizationId],
                        [StudentAcademicRecord_SchoolYear],
                        [StudentAcademicRecord_StudentUniqueId],
                        [StudentAcademicRecord_TermDescriptor_DescriptorId],
                        [AuthorizationStudentAcademicRecordId],
                        [Name]
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
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@studentAcademicRecordDocumentId", studentAcademicRecordDocumentId),
                    new SqlParameter("@educationOrganizationId", seed.EducationOrganizationId),
                    new SqlParameter("@schoolYear", seed.SchoolYear),
                    new SqlParameter("@studentUniqueId", seed.StudentUniqueId),
                    new SqlParameter("@termDescriptorId", termDescriptorId),
                    new SqlParameter(
                        "@authorizationStudentAcademicRecordId",
                        seed.AuthorizationStudentAcademicRecordId
                    ),
                    new SqlParameter("@name", seed.Name)
                )
        );

        await UpsertReferentialIdentityAsync(
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

        return new UpsertResult.InsertSuccess(seed.DocumentUuid);
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

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "SchoolYearType",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[SchoolYearType] (
                        [DocumentId],
                        [CurrentSchoolYear],
                        [SchoolYear],
                        [SchoolYearDescription]
                    )
                    VALUES (
                        @documentId,
                        @currentSchoolYear,
                        @schoolYear,
                        @schoolYearDescription
                    );
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@currentSchoolYear", seed.CurrentSchoolYear),
                    new SqlParameter("@schoolYear", seed.SchoolYear),
                    new SqlParameter("@schoolYearDescription", seed.SchoolYearDescription)
                )
        );

        await UpsertReferentialIdentityAsync(
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

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "Student",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[Student] (
                        [DocumentId],
                        [BirthDate],
                        [FirstName],
                        [LastSurname],
                        [StudentUniqueId]
                    )
                    VALUES (
                        @documentId,
                        @birthDate,
                        @firstName,
                        @lastSurname,
                        @studentUniqueId
                    );
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@birthDate", new DateOnly(2010, 5, 14)),
                    new SqlParameter("@firstName", seed.FirstName),
                    new SqlParameter("@lastSurname", seed.LastSurname),
                    new SqlParameter("@studentUniqueId", seed.StudentUniqueId)
                )
        );

        await UpsertReferentialIdentityAsync(
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

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "StudentSchoolAssociation",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[StudentSchoolAssociation] (
                        [DocumentId],
                        [SchoolId_Unified],
                        [School_DocumentId],
                        [Student_DocumentId],
                        [Student_StudentUniqueId],
                        [EntryGradeLevelDescriptor_DescriptorId],
                        [EntryDate]
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
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@schoolId", seed.SchoolId),
                    new SqlParameter("@schoolDocumentId", schoolDocumentId),
                    new SqlParameter("@studentDocumentId", studentDocumentId),
                    new SqlParameter("@studentUniqueId", seed.StudentUniqueId),
                    new SqlParameter("@entryGradeLevelDescriptorId", entryGradeLevelDescriptorId),
                    new SqlParameter("@entryDate", seed.EntryDate)
                )
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

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "StudentAcademicRecord",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[StudentAcademicRecord] (
                        [DocumentId],
                        [EducationOrganization_DocumentId],
                        [EducationOrganization_EducationOrganizationId],
                        [SchoolYear_DocumentId],
                        [SchoolYear_SchoolYear],
                        [Student_DocumentId],
                        [Student_StudentUniqueId],
                        [TermDescriptor_DescriptorId]
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
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@schoolDocumentId", schoolDocumentId),
                    new SqlParameter("@educationOrganizationId", seed.EducationOrganizationId),
                    new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
                    new SqlParameter("@schoolYear", seed.SchoolYear),
                    new SqlParameter("@studentDocumentId", studentDocumentId),
                    new SqlParameter("@studentUniqueId", seed.StudentUniqueId),
                    new SqlParameter("@termDescriptorId", termDescriptorId)
                )
        );

        await UpsertReferentialIdentityAsync(
            CreateStudentAcademicRecordReferentialId(seed),
            documentId,
            resourceKeyId
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
        IReadOnlyList<string> strategyNames,
        string? ifMatch = null
    )
    {
        return await UpdateAsync(
            "authz",
            "AuthorizationNullableResource",
            RelationalQueryAuthorizationRequestBodies.CreateAuthorizationNullableRequestBody(seed),
            documentUuid,
            $"put-auth-nullable-{seed.AuthorizationNullableId}",
            claimEducationOrganizationIds,
            strategyNames,
            ifMatch
        );
    }

    public async Task SeedSchoolReferenceResourceAsync(QuerySchoolSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId], [ContentVersion])
                    SELECT @documentId, @nameOfInstitution, @schoolId, doc.[ContentVersion]
                    FROM [dms].[Document] doc
                    WHERE doc.[DocumentId] = @documentId;
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@nameOfInstitution", seed.NameOfInstitution),
                    new SqlParameter("@schoolId", seed.SchoolId)
                )
        );

        await UpsertReferentialIdentityAsync(
            CreateSchoolReferentialId(seed.SchoolId),
            documentId,
            resourceKeyId
        );

        _schoolDocumentIdsBySchoolId[seed.SchoolId] = documentId;
    }

    public async Task SeedClassPeriodReferenceResourceAsync(ClassPeriodSeed seed)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "ClassPeriod");
        var documentId = await InsertDocumentAsync(seed.DocumentUuid.Value, resourceKeyId);

        if (!_schoolDocumentIdsBySchoolId.TryGetValue(seed.SchoolId, out var schoolDocumentId))
        {
            throw new InvalidOperationException(
                $"School '{seed.SchoolId}' must be seeded before ClassPeriod '{seed.ClassPeriodName}'."
            );
        }

        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "ClassPeriod",
            async () =>
                await Database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO [edfi].[ClassPeriod] (
                        [DocumentId],
                        [ClassPeriodName],
                        [School_DocumentId],
                        [School_SchoolId]
                    )
                    VALUES (
                        @documentId,
                        @classPeriodName,
                        @schoolDocumentId,
                        @schoolId
                    );
                    """,
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@classPeriodName", seed.ClassPeriodName),
                    new SqlParameter("@schoolDocumentId", schoolDocumentId),
                    new SqlParameter("@schoolId", seed.SchoolId)
                )
        );

        await UpsertReferentialIdentityAsync(CreateClassPeriodReferentialId(seed), documentId, resourceKeyId);
    }

    public async Task InsertAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId] (
                [SourceEducationOrganizationId],
                [TargetEducationOrganizationId]
            )
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task DeleteAuthEdgeAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            DELETE FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
            WHERE [SourceEducationOrganizationId] = @sourceEducationOrganizationId
              AND [TargetEducationOrganizationId] = @targetEducationOrganizationId;
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task<long> CountAuthEdgesAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
            WHERE [SourceEducationOrganizationId] = @sourceEducationOrganizationId
              AND [TargetEducationOrganizationId] = @targetEducationOrganizationId;
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }

    public async Task<QueryResult> QueryAsync(
        string projectEndpointName,
        string resourceName,
        IReadOnlyList<long> claimEducationOrganizationIds,
        IReadOnlyList<string> strategyNames,
        int? limit = null,
        int? offset = null,
        bool totalCount = true,
        IReadOnlyList<QueryElement>? queryElements = null,
        Func<MappingSet, MappingSet>? mappingSetTransform = null,
        ChangeVersionRange? changeVersionRange = null
    )
    {
        ResetRecorder();
        var resourceHandle = GetResourceHandle(projectEndpointName, resourceName);
        var mappingSet = mappingSetTransform is null ? MappingSet : mappingSetTransform(MappingSet);

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: resourceHandle.ResourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext(claimEducationOrganizationIds),
            MappingSet: mappingSet,
            QueryElements: queryElements is null ? [] : [.. queryElements],
            AuthorizationStrategyEvaluators:
            [
                .. strategyNames.Select(static strategyName => new AuthorizationStrategyEvaluator(
                    strategyName,
                    [],
                    FilterOperator.And
                )),
            ],
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId($"{resourceName}-authorization-query"),
            ChangeVersionRange: changeVersionRange
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
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
            SELECT COUNT_BIG(*)
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
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
            SELECT COUNT_BIG(*)
            FROM [{physicalSchema}].[{resourceName}] root
            INNER JOIN [dms].[Document] document
                ON document.[DocumentId] = root.[DocumentId]
            WHERE document.[DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
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
            SELECT COUNT_BIG(*)
            FROM [dms].[ReferentialIdentity]
            WHERE [ReferentialId] = @referentialId;
            """,
            new SqlParameter("@referentialId", referentialId.Value)
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
        var command = GetRequiredPostCreateRelationshipAuthorizationCommand();

        command
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO [dms].[Document]", StringComparison.Ordinal));
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
                commandText.Contains("INSERT INTO [dms].[Document]", StringComparison.Ordinal)
            )
            .Should()
            .BeEmpty("deferred missing references should stop before inserting the document");
    }

    public void AssertPostCreateDirectClaimMatchAuthorizationBeforeDocumentInsert()
    {
        var command = GetRequiredPostCreateRelationshipAuthorizationCommand();

        command.Should().Contain("IN (@ClaimEducationOrganizationIds_0) OR EXISTS");
        command.Should().Contain("[auth].[EducationOrganizationIdToEducationOrganizationId]");
        command
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO [dms].[Document]", StringComparison.Ordinal));
    }

    public void AssertPostCreatePeopleAuthorizationBeforeDocumentInsert()
    {
        var command = GetRequiredPostCreateRelationshipAuthorizationCommand();

        command.Should().Contain("[auth].[EducationOrganizationIdToStudentDocumentId]");
        command.Should().Contain("[edfi].[StudentAcademicRecord]");
        command
            .IndexOf("AUTH1", StringComparison.Ordinal)
            .Should()
            .BeLessThan(command.IndexOf("INSERT INTO [dms].[Document]", StringComparison.Ordinal));
    }

    public void AssertPeopleUpdateRunsStoredThenProposedRelationshipAuthorization()
    {
        var peopleAuthorizationCommands = _writeSessionRecorder
            .Commands.Select((command, index) => (command, index))
            .Where(static item =>
                item.command.CommandText.Contains("AUTH1", StringComparison.Ordinal)
                && item.command.CommandText.Contains(
                    "[auth].[EducationOrganizationIdToStudentDocumentId]",
                    StringComparison.Ordinal
                )
                && item.command.CommandText.Contains(
                    "[edfi].[StudentAcademicRecord]",
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
        peopleAuthorizationCommands[0].command.CommandText.Should().Contain("@DocumentId");
        peopleAuthorizationCommands[1].command.CommandText.Should().Contain("@relationshipAuthorization_");
        peopleAuthorizationCommands[0].index.Should().BeLessThan(peopleAuthorizationCommands[1].index);
    }

    public void AssertPostCreateRelationshipAuthorizationUsesScalarClaimParameters(int expectedCount)
    {
        var command = GetRequiredPostCreateRelationshipAuthorizationCommand();

        command.Should().Contain("@ClaimEducationOrganizationIds_0");
        command.Should().Contain($"@ClaimEducationOrganizationIds_{expectedCount - 1}");
        command.Should().NotContain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
    }

    public void AssertPostCreateRelationshipAuthorizationUsesStructuredClaimParameter()
    {
        var command = GetRequiredPostCreateRelationshipAuthorizationCommand();

        command.Should().Contain("SELECT [Id] FROM @ClaimEducationOrganizationIds");
        command.Should().NotContain("@ClaimEducationOrganizationIds_0");
    }

    public void AssertUpdateRelationshipAuthorizationUsesScalarClaimParameters(int expectedCount)
    {
        var commands = GetRequiredUpdateRelationshipAuthorizationCommands();

        commands
            .Should()
            .OnlyContain(command =>
                command.Contains("@ClaimEducationOrganizationIds_0", StringComparison.Ordinal)
                && command.Contains(
                    $"@ClaimEducationOrganizationIds_{expectedCount - 1}",
                    StringComparison.Ordinal
                )
                && !command.Contains(
                    "SELECT [Id] FROM @ClaimEducationOrganizationIds",
                    StringComparison.Ordinal
                )
            );
    }

    public void AssertUpdateRelationshipAuthorizationUsesStructuredClaimParameter()
    {
        var commands = GetRequiredUpdateRelationshipAuthorizationCommands();

        commands
            .Should()
            .OnlyContain(command =>
                command.Contains("SELECT [Id] FROM @ClaimEducationOrganizationIds", StringComparison.Ordinal)
                && !command.Contains("@ClaimEducationOrganizationIds_0", StringComparison.Ordinal)
            );
    }

    public async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKeyId = MappingSet.ResourceKeyIdByResource[schoolResource];
        var physicalSchema = MappingSet.ReadPlansByResource[schoolResource].Model.PhysicalSchema.Value;
        var rows = await Database.QueryRowsAsync(
            $"""
            SELECT
                doc.[DocumentId],
                doc.[DocumentUuid],
                school.[SchoolId],
                school.[NameOfInstitution],
                school.[ContentVersion]
            FROM [dms].[Document] doc
            INNER JOIN [{physicalSchema}].[School] school
                ON school.[DocumentId] = doc.[DocumentId]
            WHERE doc.[ResourceKeyId] = @resourceKeyId
            ORDER BY doc.[DocumentId];
            """,
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            UPDATE [authz].[AuthorizationRootChildResource]
            SET
                [School_DocumentId] = @schoolDocumentId,
                [School_SchoolId] = @newSchoolId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@newSchoolId", newSchoolId),
            new SqlParameter("@documentId", documentId)
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
        BackendProfileWriteContext? backendProfileWriteContext = null
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
                [DocumentId],
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [IdentityVersion],
                [ContentLastModifiedAt],
                [IdentityLastModifiedAt],
                [CreatedAt]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid
              AND [ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        return rows.Count == 1
            ? new AuthorizationDocumentState(
                GetRequiredInt64(rows[0], "DocumentId"),
                GetRequiredGuid(rows[0], "DocumentUuid"),
                GetRequiredInt16(rows[0], "ResourceKeyId"),
                GetRequiredInt64(rows[0], "ContentVersion"),
                GetRequiredInt64(rows[0], "IdentityVersion"),
                GetRequiredDateTime(rows[0], "ContentLastModifiedAt"),
                GetRequiredDateTime(rows[0], "IdentityLastModifiedAt"),
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

            var selectColumns = string.Join(", ", columns.Select(static column => $"[{column.Value}]"));
            var orderColumns =
                tablePlan.TableModel.Key.Columns.Count != 0
                    ? tablePlan.TableModel.Key.Columns.Select(static column => column.ColumnName).ToArray()
                    : columns;
            var orderBy = string.Join(", ", orderColumns.Select(static column => $"[{column.Value}]"));
            var where = string.Join(
                " AND ",
                locatorColumns.Select(static column => $"[{column.Value}] = @documentId")
            );
            var rows = await Database.QueryRowsAsync(
                $"""
                SELECT {selectColumns}
                FROM [{table.Schema.Value}].[{table.Name}]
                WHERE {where}
                ORDER BY {orderBy};
                """,
                new SqlParameter("@documentId", documentId)
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
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
              AND [ResourceKeyId] = @resourceKeyId
            ORDER BY [ResourceKeyId], [ReferentialId];
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            SELECT COUNT_BIG(*)
            FROM [{table.Schema.Value}].[{table.Name}];
            """
        );
    }

    private IReadOnlyList<string> GetRequiredUpdateRelationshipAuthorizationCommands()
    {
        var commands = _writeSessionRecorder
            .Commands.Select(static recorded => recorded.CommandText)
            .Where(static commandText =>
                commandText.Contains("AUTH1", StringComparison.Ordinal)
                && !commandText.Contains("INSERT INTO [dms].[Document]", StringComparison.Ordinal)
            )
            .ToArray();

        commands.Should().NotBeEmpty("update authorization should emit AUTH1 command text");
        return commands;
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
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddSingleton<MssqlRelationalQueryExecutionRecorder>();
        services.AddSingleton<MssqlRelationalQueryAuthorizationWriteSessionRecorder>();
        services.AddMssqlReferenceResolver();
        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalWriteSessionFactory,
                MssqlRelationalQueryAuthorizationRecordingWriteSessionFactory
            >()
        );
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, RecordingMssqlDocumentHydrator>());
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalReadMaterializer, RecordingRelationalReadMaterializer>()
        );

        if (_providerFailureTransform is not null)
        {
            services.Replace(
                ServiceDescriptor.Scoped<IRelationshipAuthorizationProviderFailureExtractor>(
                    _ => new TransformingMssqlRelationshipAuthorizationProviderFailureExtractor(
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
                    Name: "MssqlRelationalQueryAuthorization",
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

        await UpsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await Database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> GetDocumentIdByUuidAsync(DocumentUuid documentUuid)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );
    }

    private async Task<long> GetSchoolDocumentIdAsync(int schoolId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [edfi].[School]
            WHERE [SchoolId] = @schoolId;
            """,
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task<long> GetSchoolYearDocumentIdAsync(int schoolYear)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [edfi].[SchoolYearType]
            WHERE [SchoolYear] = @schoolYear;
            """,
            new SqlParameter("@schoolYear", schoolYear)
        );
    }

    private async Task<long> GetStudentDocumentIdAsync(string studentUniqueId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId]
            FROM [edfi].[Student]
            WHERE [StudentUniqueId] = @studentUniqueId;
            """,
            new SqlParameter("@studentUniqueId", studentUniqueId)
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
            SELECT [DocumentId]
            FROM [edfi].[StudentAcademicRecord]
            WHERE [EducationOrganization_EducationOrganizationId] = @educationOrganizationId
              AND [SchoolYear_SchoolYear] = @schoolYear
              AND [Student_StudentUniqueId] = @studentUniqueId
              AND [TermDescriptor_DescriptorId] = @termDescriptorId;
            """,
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@termDescriptorId", termDescriptorId)
        );
    }

    private async Task<long> GetDescriptorDocumentIdAsync(string resourceName, string uri)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);

        return await Database.ExecuteScalarAsync<long>(
            """
            SELECT descriptor.[DocumentId]
            FROM [dms].[Descriptor] descriptor
            INNER JOIN [dms].[Document] document
                ON document.[DocumentId] = descriptor.[DocumentId]
            WHERE document.[ResourceKeyId] = @resourceKeyId
              AND descriptor.[Uri] = @uri;
            """,
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@uri", uri)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await Database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
        );

        return documentId;
    }

    private async Task UpsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await Database.ExecuteNonQueryAsync(
            """
            IF EXISTS (
                SELECT 1
                FROM [dms].[ReferentialIdentity]
                WHERE [DocumentId] = @documentId
                  AND [ResourceKeyId] = @resourceKeyId
            )
            BEGIN
                UPDATE [dms].[ReferentialIdentity]
                SET [ReferentialId] = @referentialId
                WHERE [DocumentId] = @documentId
                  AND [ResourceKeyId] = @resourceKeyId;
            END
            ELSE IF NOT EXISTS (
                SELECT 1
                FROM [dms].[ReferentialIdentity]
                WHERE [ReferentialId] = @referentialId
            )
            BEGIN
                INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
                VALUES (@referentialId, @documentId, @resourceKeyId);
            END;
            """,
            new SqlParameter("@referentialId", referentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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

    private static ReferentialId CreateSchoolReferentialId(int schoolId)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return ReferentialIdCalculator.ReferentialIdFrom(SchoolResource, schoolIdentity);
    }

    private static ReferentialId CreateClassPeriodReferentialId(ClassPeriodSeed seed)
    {
        var classPeriodIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.classPeriodName"), seed.ClassPeriodName),
            new DocumentIdentityElement(
                new JsonPath("$.schoolReference.schoolId"),
                seed.SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return ReferentialIdCalculator.ReferentialIdFrom(ClassPeriodResource, classPeriodIdentity);
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

    private async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action
    )
    {
        await Database.ExecuteNonQueryAsync($"""DISABLE TRIGGER ALL ON [{schema}].[{table}];""");

        try
        {
            await action();
        }
        finally
        {
            await Database.ExecuteNonQueryAsync($"""ENABLE TRIGGER ALL ON [{schema}].[{table}];""");
        }
    }

    private sealed record ResourceHandle(
        ProjectSchema ProjectSchema,
        ResourceSchema ResourceSchema,
        ResourceInfo ResourceInfo
    );

    private sealed class TransformingMssqlRelationshipAuthorizationProviderFailureExtractor(
        Func<RelationshipAuthorizationProviderFailure, RelationshipAuthorizationProviderFailure> transform
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return transform(new RelationshipAuthorizationProviderFailure(null, exception.Message));
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

    private static int FindRequiredCommandIndex(
        IReadOnlyList<MssqlRelationalQueryAuthorizationRecordedCommand> commands,
        Func<string, bool> predicate,
        int startIndex = 0
    )
    {
        for (var commandIndex = startIndex; commandIndex < commands.Count; commandIndex++)
        {
            if (predicate(commands[commandIndex].CommandText))
            {
                return commandIndex;
            }
        }

        throw new InvalidOperationException("Expected relational write command was not recorded.");
    }

    private static bool IsMssqlDocumentLockCommand(string commandText) =>
        commandText.Contains("UPDLOCK", StringComparison.Ordinal)
        && commandText.Contains("[dms].[Document]", StringComparison.Ordinal);

    private static bool IsMssqlRelationshipAuthorizationCommand(string commandText) =>
        commandText.Contains("[AuthorizationResult]", StringComparison.Ordinal)
        && commandText.Contains("AUTH1", StringComparison.Ordinal);

    private static bool IsMssqlDocumentDeleteCommand(string commandText) =>
        commandText.Contains("DELETE FROM [dms].[Document]", StringComparison.Ordinal);

    private string GetRequiredPostCreateRelationshipAuthorizationCommand()
    {
        var command = _writeSessionRecorder
            .Commands.Select(static recorded => recorded.CommandText)
            .FirstOrDefault(commandText =>
                commandText.Contains("AUTH1", StringComparison.Ordinal)
                && commandText.Contains("INSERT INTO [dms].[Document]", StringComparison.Ordinal)
            );

        command.Should().NotBeNull("POST create should compose authorization and dms.Document insert");
        return command!;
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Query_Authorization_With_Direct_EdOrg_Claim_Match
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

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
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
            "r.[School_SchoolId] IN (@ClaimEducationOrganizationIds_0) OR r.[School_SchoolId] IN (SELECT";
        keyset.Plan.PageDocumentIdSql.Should().Contain(DirectClaimMatchSql);
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql!.Should().Contain(DirectClaimMatchSql);
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Query_Authorization_With_The_Authoritative_Ds52_School_Fixture
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

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            await _context.SeedSchoolReferenceResourceAsync(schoolSeed);
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
            .Contain("@ClaimEducationOrganizationIds_0")
            .And.Contain("[TargetEducationOrganizationId]")
            .And.Contain("[SchoolId]");
        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(ClaimEducationOrganizationId);
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
            .Contain("[SourceEducationOrganizationId]")
            .And.Contain("[TargetEducationOrganizationId]");
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

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_composes_the_change_version_window_with_relationship_authorization_filtering()
    {
        // The stamping triggers assign strictly increasing ContentVersion values in insert order, so a
        // window from Beta to Gamma holds Beta — SchoolId 200, authorized under the normal strategy —
        // and unauthorized Gamma, SchoolId 300. The window excludes authorized Alpha and Delta, and
        // authorization excludes in-window Gamma, so only the intersection survives.
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
            .Contain("r.[ContentVersion] >= @minChangeVersion")
            .And.Contain("r.[ContentVersion] <= @maxChangeVersion")
            .And.Contain("@ClaimEducationOrganizationIds_0");
    }

    [Test]
    public async Task It_uses_expanded_scalar_parameters_for_1999_unique_claim_edorg_ids()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateUniqueClaimEducationOrganizationIds(1999),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertScalarFilterParameters(keyset, 1999, 900L, 2997L);
    }

    [Test]
    public async Task It_uses_a_structured_tvp_for_2000_unique_claim_edorg_ids()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateUniqueClaimEducationOrganizationIds(2000),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertStructuredFilterParameter(keyset, 2000, 900L, 2998L);
    }

    [Test]
    public async Task It_deduplicates_duplicate_heavy_claim_edorg_ids_before_using_the_scalar_threshold()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            CreateDuplicateHeavyClaimEducationOrganizationIds(),
            _normalStrategy
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(3);

        var keyset = _context.AssertSingleQueryHydration();
        AssertScalarFilterParameters(keyset, 1999, 900L, 2997L);
        keyset
            .ParameterValues.ContainsKey(
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
            .Should()
            .BeFalse();
    }

    private static IReadOnlyList<long> CreateUniqueClaimEducationOrganizationIds(int uniqueCount)
    {
        if (uniqueCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(uniqueCount));
        }

        return
        [
            ClaimEducationOrganizationId,
            .. Enumerable.Range(0, uniqueCount - 1).Select(static index => 1000L + index),
        ];
    }

    private static IReadOnlyList<long> CreateDuplicateHeavyClaimEducationOrganizationIds()
    {
        var uniqueClaimIds = CreateUniqueClaimEducationOrganizationIds(1999);

        return [.. uniqueClaimIds, .. uniqueClaimIds.Take(50), .. uniqueClaimIds.Take(50)];
    }

    private static void AssertScalarFilterParameters(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().HaveCount(expectedCount);
        pageFilterParameters[0].ParameterName.Should().Be("ClaimEducationOrganizationIds_0");
        pageFilterParameters[^1]
            .ParameterName.Should()
            .Be($"ClaimEducationOrganizationIds_{expectedCount - 1}");
        pageFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().HaveCount(expectedCount);
        totalCountFilterParameters
            .Select(static parameter => parameter.Binding.Kind)
            .Should()
            .OnlyContain(static kind => kind == QuerySqlParameterBindingKind.Scalar);

        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(expectedFirstValue);
        keyset
            .ParameterValues[$"ClaimEducationOrganizationIds_{expectedCount - 1}"]
            .Should()
            .Be(expectedLastValue);
    }

    private static void AssertStructuredFilterParameter(
        PageKeysetSpec.Query keyset,
        int expectedCount,
        long expectedFirstValue,
        long expectedLastValue
    )
    {
        var pageFilterParameters = GetPageFilterParameters(keyset);
        pageFilterParameters.Should().ContainSingle();
        pageFilterParameters[0]
            .ParameterName.Should()
            .Be(RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds);
        pageFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);
        pageFilterParameters[0].Binding.StructuredTypeName.Should().Be("dms.BigIntTable");
        pageFilterParameters[0].Binding.StructuredColumnName.Should().Be("Id");

        var totalCountFilterParameters = GetTotalCountFilterParameters(keyset);
        totalCountFilterParameters.Should().ContainSingle();
        totalCountFilterParameters[0].Binding.Kind.Should().Be(QuerySqlParameterBindingKind.MssqlStructured);

        keyset.Plan.PageDocumentIdSql.Should().Contain("SELECT [Id] FROM @ClaimEducationOrganizationIds");

        keyset
            .ParameterValues[RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds]
            .Should()
            .BeAssignableTo<IReadOnlyList<long>>()
            .Which.Should()
            .HaveCount(expectedCount)
            .And.StartWith(expectedFirstValue)
            .And.EndWith(expectedLastValue);
    }

    private static IReadOnlyList<QuerySqlParameter> GetPageFilterParameters(PageKeysetSpec.Query keyset)
    {
        return
        [
            .. keyset.Plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }

    private static IReadOnlyList<QuerySqlParameter> GetTotalCountFilterParameters(PageKeysetSpec.Query keyset)
    {
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();

        return
        [
            .. keyset.Plan.TotalCountParametersInOrder!.Value.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }
}

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Query_Authorization_With_A_Synthetic_EdOrg_Fixture
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

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: false);
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            await _context.SeedSchoolReferenceResourceAsync(schoolSeed);
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            await _context.SeedClassPeriodReferenceResourceAsync(classPeriodSeed);
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
