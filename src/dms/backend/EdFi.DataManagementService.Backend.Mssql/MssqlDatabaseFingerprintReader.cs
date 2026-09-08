// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public class MssqlDatabaseFingerprintReader : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query =
        DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(SqlDialect.Mssql);

    private readonly IMssqlConnectionAcquisition _acquisition;
    private readonly ILogger<MssqlDatabaseFingerprintReader> _logger;

    public MssqlDatabaseFingerprintReader(
        IMssqlConnectionAcquisition acquisition,
        ILogger<MssqlDatabaseFingerprintReader> logger
    )
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Taking the lease and opening the connection are one guarded step, because the connection
        // string is parsed twice before the open - once realizing the derivative's effective string,
        // once in the SqlConnection constructor - and a provider-invalid string fails at one of those
        // rather than at the open. The leased connection owns both and releases them in order, so this
        // is also the only disposal the read needs.
        //
        // This seam has no CancellationToken by design: nothing on the fingerprint path supplies one,
        // so no cancellation here is attributable to a caller.
        await using MssqlLeasedConnection leased = await ConnectionAcquisition.GuardAsync(
            () => MssqlLeasedConnection.OpenAsync(_acquisition, target, CancellationToken.None),
            target.Kind,
            MssqlConnectionAcquisitionFailure.IsExpected,
            _logger,
            CancellationToken.None
        );

        return await DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            leased.Connection,
            _query,
            _logger,
            static exception => exception is SqlException { Number: 207 }
        );
    }
}
