// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobEnqueuerTests
{
    [TestFixture]
    public class Given_a_valid_command_in_a_caller_transaction
    {
        private ServiceProvider _provider = null!;
        private RecordingJobRepository _repository = null!;
        private readonly StandInTransaction _transaction = new();
        private JobEnqueueResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _repository = _provider.GetRequiredService<RecordingJobRepository>();
            _repository.Enqueued.Clear();
            _result = await _provider
                .GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(
                    new JobEnqueueCommand(
                        JobServices.RefreshJobType,
                        2,
                        """{ "dataStoreId" : 7,  "dataStoreCode": "north-1" }"""
                    ),
                    _transaction,
                    CancellationToken.None
                );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_repository_result() =>
            _result.Should().Be(new JobEnqueueResult.Success("job-1"));

        [Test]
        public void It_enqueues_the_serializer_output_not_the_caller_text() =>
            _repository
                .Enqueued.Should()
                .ContainSingle()
                .Which.Command.Should()
                .Be(
                    new JobEnqueueCommand(
                        JobServices.RefreshJobType,
                        2,
                        """{"dataStoreCode":"north-1","dataStoreId":7}"""
                    )
                );

        [Test]
        public void It_passes_the_caller_transaction_through() =>
            _repository.Enqueued.Single().Transaction.Should().BeSameAs(_transaction);
    }

    [TestFixture(
        "unknown type",
        "DataStore.Unknown",
        1,
        """{"dataStoreCode":"a","dataStoreId":1}""",
        "UnsupportedType",
        ""
    )]
    [TestFixture(
        "type in a different case",
        "datastore.refresheducationorganizations",
        1,
        """{"dataStoreCode":"a","dataStoreId":1}""",
        "UnsupportedType",
        ""
    )]
    [TestFixture(
        "unsupported version",
        JobServices.RefreshJobType,
        3,
        """{"dataStoreCode":"a","dataStoreId":1}""",
        "UnsupportedVersion",
        ""
    )]
    [TestFixture(
        "unexpected member",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":1,"connectionString":"Server=db;Password=hunter2"}""",
        "PayloadInvalid",
        "InvalidJson"
    )]
    [TestFixture(
        "secret in an identifier",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"Server=db;Password=hunter2","dataStoreId":1}""",
        "PayloadInvalid",
        "InvalidIdentifier"
    )]
    [TestFixture(
        "number as a string",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":"1"}""",
        "PayloadInvalid",
        "InvalidJson"
    )]
    [TestFixture(
        "array root",
        JobServices.RefreshJobType,
        1,
        """[{"dataStoreCode":"a","dataStoreId":1}]""",
        "PayloadInvalid",
        "NotAnObject"
    )]
    [TestFixture(
        "validator failure",
        JobServices.RefreshJobType,
        1,
        """{"dataStoreCode":"a","dataStoreId":0}""",
        "PayloadInvalid",
        RefreshValidator.DataStoreIdOutOfRange
    )]
    public class Given_a_command_the_checks_reject(
        string label,
        string jobType,
        int payloadVersion,
        string payloadJson,
        string expected,
        string reasonCode
    )
    {
        private ServiceProvider _provider = null!;
        private RecordingJobRepository _repository = null!;
        private JobEnqueueResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _repository = _provider.GetRequiredService<RecordingJobRepository>();
            _result = await _provider
                .GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(
                    new JobEnqueueCommand(jobType, (short)payloadVersion, payloadJson),
                    null,
                    CancellationToken.None
                );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_reports_the_rejection() =>
            _result
                .Should()
                .Be(
                    expected switch
                    {
                        "UnsupportedType" => new JobEnqueueResult.FailureUnsupportedType(),
                        "UnsupportedVersion" => new JobEnqueueResult.FailureUnsupportedVersion(),
                        _ => new JobEnqueueResult.FailurePayloadInvalid(reasonCode),
                    },
                    label
                );

        [Test]
        public void It_enqueues_nothing() => _repository.Enqueued.Should().BeEmpty(label);
    }

    [TestFixture]
    public class Given_a_repository_failure
    {
        private ServiceProvider _provider = null!;
        private readonly JobEnqueueResult.FailureUnknown _failure = new(
            new JobFailureDiagnostic("Npgsql.PostgresException", "23505", "EnqueueJob")
        );
        private JobEnqueueResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _provider.GetRequiredService<RecordingJobRepository>().Result = _failure;
            _result = await _provider
                .GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(
                    new JobEnqueueCommand(
                        JobServices.RefreshJobType,
                        1,
                        """{"dataStoreCode":"a","dataStoreId":1}"""
                    ),
                    null,
                    CancellationToken.None
                );
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_returns_the_repository_failure() => _result.Should().Be(_failure);
    }
}
