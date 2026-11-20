// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Tests.E2E.ManagedDataStores;

/// <summary>A fresh catalog and an owned production CMS process; never controls Docker or the IDE.</summary>
internal sealed class ManagedDataStoreExternalDeployment : IAsyncDisposable
{
    private readonly bool _mssql;
    private readonly string _name = "dms1439_http_" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dms1439-http-" + Guid.NewGuid().ToString("N")
    );
    private readonly string _maintenance;
    private readonly string _connectionString;
    private readonly string _application;
    private readonly string _certificatePassword;
    private readonly string _encryptionKey;
    private readonly List<string> _secrets = [];
    private readonly ConcurrentQueue<string> _output = new();
    private TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private bool _created;
    private IReadOnlyDictionary<string, string> _provisioning = new Dictionary<string, string>();
    public Uri BaseAddress { get; }
    public string ClientSecret { get; }
    public string Output => string.Join('\n', _output);

    public void ConfigureProvisioning(IReadOnlyDictionary<string, string> configuration)
    {
        _provisioning = configuration;
    }

    public string? DecryptConnection(byte[] encrypted) =>
        new ConnectionStringEncryptionService(
            Options.Create(
                new DatabaseOptions { DatabaseConnection = _connectionString, EncryptionKey = _encryptionKey }
            )
        ).Decrypt(encrypted);

    private ManagedDataStoreExternalDeployment(bool mssql, string admin, string application)
    {
        _mssql = mssql;
        _application = Path.GetFullPath(application);
        if (mssql)
        {
            var builder = new SqlConnectionStringBuilder(admin)
            {
                InitialCatalog = "master",
                Pooling = false,
            };
            _maintenance = builder.ConnectionString;
            builder.InitialCatalog = _name;
            _connectionString = builder.ConnectionString;
            RememberSecret(builder.Password);
        }
        else
        {
            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
            _maintenance = builder.ConnectionString;
            builder.Database = _name;
            _connectionString = builder.ConnectionString;
            RememberSecret(builder.Password ?? "");
        }
        RememberSecret(admin);
        RememberSecret(_connectionString);
        _encryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _certificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        ClientSecret = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        RememberSecret(_encryptionKey);
        RememberSecret(_certificatePassword);
        RememberSecret(ClientSecret);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/managed-e2e/");
    }

    public static async Task<ManagedDataStoreExternalDeployment> CreateAsync(bool mssql)
    {
        string setting = mssql ? "ConnectionStrings__MssqlDmsAdmin" : "ConnectionStrings__PostgresqlDmsAdmin";
        string? admin = Environment.GetEnvironmentVariable(setting);
        string? application = Environment.GetEnvironmentVariable("DMS_MANAGED_E2E_CMS_DLL");
        if (
            string.IsNullOrWhiteSpace(admin)
            || string.IsNullOrWhiteSpace(application)
            || !File.Exists(application)
        )
        {
            Assert.Ignore(
                "The explicit managed HTTP lane requires its provider admin setting and DMS_MANAGED_E2E_CMS_DLL."
            );
        }
        var deployment = new ManagedDataStoreExternalDeployment(mssql, admin!, application!);
        try
        {
            Directory.CreateDirectory(deployment._directory);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=DMS1439 external HTTP tests",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1)
            );
            await File.WriteAllBytesAsync(
                Path.Combine(deployment._directory, "signing.pfx"),
                certificate.Export(X509ContentType.Pfx, deployment._certificatePassword)
            );
            await using var connection = deployment.Connection(deployment._maintenance);
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE \"{deployment._name}\";");
            deployment._created = true;
            return deployment;
        }
        catch
        {
            await deployment.DisposeAsync();
            throw;
        }
    }

    public void RememberSecret(string secret)
    {
        if (secret.Length > 0)
        {
            _secrets.Add(secret);
        }
    }

    public bool ContainsSecret(string text) =>
        _secrets.Exists(secret => text.Contains(secret, StringComparison.Ordinal));

    public string RedactedOutput()
    {
        string output = Output;
        foreach (string secret in _secrets.OrderByDescending(value => value.Length))
        {
            output = output.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return output;
    }

    public void Start(bool? management)
    {
        if (_process is not null && !_process.HasExited)
        {
            throw new InvalidOperationException("The owned CMS process is still running.");
        }
        _process?.Dispose();
        _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(_application);
        Dictionary<string, string> configuration = new()
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_CONTENTROOT"] = Path.GetDirectoryName(_application)!,
            ["ASPNETCORE_URLS"] = BaseAddress.GetLeftPart(UriPartial.Authority),
            ["AppSettings__Datastore"] = _mssql ? "mssql" : "postgresql",
            ["AppSettings__IdentityProvider"] = "self-contained",
            ["AppSettings__DeployDatabaseOnStartup"] = "true",
            ["AppSettings__MultiTenancy"] = "false",
            ["AppSettings__PathBase"] = "/managed-e2e",
            ["DatabaseSettings__DatabaseConnection"] = _connectionString,
            ["DatabaseSettings__EncryptionKey"] = _encryptionKey,
            ["IdentitySettings__AllowRegistration"] = "true",
            ["IdentitySettings__Authority"] = BaseAddress.ToString().TrimEnd('/'),
            ["IdentitySettings__ClientSecret"] = ClientSecret,
            ["IdentitySettings__EncryptionKey"] = _encryptionKey,
            ["IdentitySettings__RequireHttpsMetadata"] = "false",
            ["IdentitySettings__UseCertificates"] = "true",
            ["IdentitySettings__UseDevelopmentCertificates"] = "false",
            ["IdentitySettings__CertificatePath"] = Path.Combine(_directory, "signing.pfx"),
            ["IdentitySettings__CertificatePassword"] = _certificatePassword,
            ["DmsDataStoreProvisioning__Enabled"] = "false",
            ["DataStoreManagement__RuntimeConnectionString"] = "",
            ["Serilog__WriteTo__0__Args__path"] = Path.Combine(_directory, "cms.log"),
        };
        foreach (var pair in configuration)
        {
            start.Environment[pair.Key] = pair.Value;
        }
        foreach (var pair in _provisioning)
        {
            start.Environment[pair.Key] = pair.Value;
        }
        // In the omitted case the shipped Production configuration supplies the effective default.
        start.Environment.Remove("AppSettings__EnableDataStoreManagement");
        start.Environment.Remove("AppSettings:EnableDataStoreManagement");
        if (management is { } enabled)
        {
            start.Environment["AppSettings__EnableDataStoreManagement"] = enabled ? "true" : "false";
        }
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, args) =>
        {
            CaptureOutput(args.Data);
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            CaptureOutput(args.Data);
        };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void CaptureOutput(string? line)
    {
        if (line is null)
        {
            return;
        }
        _output.Enqueue(line);
        try
        {
            using var entry = JsonDocument.Parse(line);
            if (
                entry.RootElement.TryGetProperty("MessageTemplate", out var template)
                && template.GetString() == "Now listening on: {address}"
                && entry.RootElement.TryGetProperty("Properties", out var properties)
                && properties.TryGetProperty("address", out var address)
                && address.GetString() == BaseAddress.GetLeftPart(UriPartial.Authority)
            )
            {
                _listening.TrySetResult();
            }
        }
        catch (JsonException)
        {
            // Startup exceptions on stderr are plain text, not the configured JSON log format.
        }
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return _process.ExitCode;
    }

    public async Task WaitForHttpAsync(HttpClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // A released ephemeral port can be taken by another process. Never send credentials until
        // our own child confirms that it bound this address successfully.
        await Task.WhenAny(_listening.Task, _process!.WaitForExitAsync(deadline.Token))
            .WaitAsync(deadline.Token);
        if (!_listening.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Owned CMS process exited before binding its HTTP listener.");
        }
        while (!deadline.IsCancellationRequested)
        {
            if (_process!.HasExited)
            {
                throw new InvalidOperationException("Owned CMS process exited before HTTP readiness.");
            }
            try
            {
                using var response = await client.GetAsync("health", deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Connection refusal is expected while the owned server is still starting.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
        throw new TimeoutException("Owned CMS process did not become HTTP ready.");
    }

    private DbConnection Connection(string value) =>
        _mssql ? new SqlConnection(value) : new NpgsqlConnection(value);

    public async Task<DbConnection> OpenAsync()
    {
        var connection = Connection(_connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_created)
        {
            await using var connection = Connection(_maintenance);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                _mssql
                    ? $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];"
                    : $"DROP DATABASE \"{_name}\" WITH (FORCE);"
            );
            _created = false;
        }
        string directory = Path.GetFullPath(_directory);
        if (
            !directory.StartsWith(
                Path.Combine(Path.GetFullPath(Path.GetTempPath()), "dms1439-http-"),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException("Unexpected test cleanup directory.");
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public async ValueTask StopAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                // Only this Process instance, never a PID/name search or the IDE's process tree.
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            _process.Dispose();
            _process = null;
        }
    }
}
