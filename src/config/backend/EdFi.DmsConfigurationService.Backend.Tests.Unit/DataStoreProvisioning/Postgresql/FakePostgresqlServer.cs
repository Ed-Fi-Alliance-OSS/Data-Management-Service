// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// A loopback server speaking just enough of the PostgreSQL protocol to observe what the real Npgsql driver
/// does on one connection: it answers an SSL request with TLS under the given certificate (or refuses TLS without
/// one), reads the startup message, asks for a cleartext password and records it or reports authentication
/// complete, then ends the session with a <see cref="FinalSqlState"/> error. A client that rejects the server
/// certificate never reaches the startup message.
/// </summary>
internal sealed class FakePostgresqlServer : IAsyncDisposable
{
    /// <summary>The SQLSTATE of the error that ends every session that got past authentication.</summary>
    public const string FinalSqlState = "57P03";

    private const int SslRequestCode = 80877103;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _session;

    /// <param name="serverCertificate">The certificate to answer TLS with, or null to refuse TLS.</param>
    /// <param name="requestPassword">Whether to ask for a cleartext password.</param>
    /// <param name="intermediates">
    /// Null to let the TLS layer choose what to send; otherwise exactly the certificates sent after the server
    /// certificate (none when empty), without downloading any.
    /// </param>
    public FakePostgresqlServer(
        X509Certificate2? serverCertificate,
        bool requestPassword,
        X509Certificate2Collection? intermediates = null
    )
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _session = Task.Run(() => ServeAsync(serverCertificate, requestPassword, intermediates));
    }

    public int Port { get; }

    /// <summary>Whether the client sent its startup message, so it accepted the server certificate when TLS was negotiated.</summary>
    public bool StartupReceived { get; private set; }

    /// <summary>The password the client sent when one was requested, or null when none arrived.</summary>
    public string? ReceivedPassword { get; private set; }

    /// <summary>Waits until the session has ended, so the recorded facts are final.</summary>
    public Task Completion => _session.WaitAsync(TimeSpan.FromSeconds(30));

    private async Task ServeAsync(
        X509Certificate2? serverCertificate,
        bool requestPassword,
        X509Certificate2Collection? intermediates
    )
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            Stream stream = client.GetStream();
            byte[] message = await ReadUntypedAsync(stream);
            if (BinaryPrimitives.ReadInt32BigEndian(message) == SslRequestCode)
            {
                if (serverCertificate is null)
                {
                    await WriteAsync(stream, "N"u8.ToArray());
                }
                else
                {
                    await WriteAsync(stream, "S"u8.ToArray());
                    SslStream tls = new(stream, leaveInnerStreamOpen: false);
                    if (intermediates is null)
                    {
                        await tls.AuthenticateAsServerAsync(serverCertificate);
                    }
                    else
                    {
                        await tls.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificateContext = SslStreamCertificateContext.Create(
                                    serverCertificate,
                                    intermediates,
                                    offline: true
                                ),
                            }
                        );
                    }

                    stream = tls;
                }

                await ReadUntypedAsync(stream);
            }

            StartupReceived = true;
            if (requestPassword)
            {
                await WriteTypedAsync(stream, 'R', Int32(3));
                byte[] password = await ReadTypedAsync(stream);
                ReceivedPassword = Encoding.UTF8.GetString(password, 0, password.Length - 1);
            }
            else
            {
                await WriteTypedAsync(stream, 'R', Int32(0));
            }

            await WriteTypedAsync(
                stream,
                'E',
                Encoding.UTF8.GetBytes($"SFATAL\0VFATAL\0C{FinalSqlState}\0Mfake server\0\0")
            );
            await stream.DisposeAsync();
        }
        catch (Exception)
        {
            // The client rejected the certificate, gave up, or never connected; what was recorded stands.
        }
    }

    private static byte[] Int32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static async Task<byte[]> ReadUntypedAsync(Stream stream)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length);
        byte[] body = new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4];
        await stream.ReadExactlyAsync(body);
        return body;
    }

    private static async Task<byte[]> ReadTypedAsync(Stream stream)
    {
        byte[] type = new byte[1];
        await stream.ReadExactlyAsync(type);
        return await ReadUntypedAsync(stream);
    }

    private static async Task WriteTypedAsync(Stream stream, char type, byte[] body)
    {
        byte[] message = new byte[5 + body.Length];
        message[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), body.Length + 4);
        body.CopyTo(message, 5);
        await WriteAsync(stream, message);
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _session.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

/// <summary>Throwaway certificate authorities and the server certificates they issue, for TLS tests.</summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateRoot(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
        );
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>An intermediate authority issued by <paramref name="issuer"/>, with its key, inside the issuer's validity.</summary>
    public static X509Certificate2 CreateIntermediate(X509Certificate2 issuer, string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
        );
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false)
        );
        using X509Certificate2 issued = request.Create(
            issuer,
            new DateTimeOffset(issuer.NotBefore).AddHours(1),
            new DateTimeOffset(issuer.NotAfter).AddDays(-1),
            Serial()
        );
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// A server certificate for <paramref name="name"/> (a host name or an IP address) issued by <paramref name="issuer"/>,
    /// with its key; with <paramref name="issuerDownloadUri"/>, it names where its issuer can be downloaded
    /// (Authority Information Access, <c>caIssuers</c>), as public certificates do.
    /// </summary>
    public static X509Certificate2 CreateServer(
        X509Certificate2 issuer,
        string name,
        string? issuerDownloadUri = null
    )
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder alternativeNames = new();
        if (IPAddress.TryParse(name, out IPAddress? address))
        {
            alternativeNames.AddIpAddress(address);
        }
        else
        {
            alternativeNames.AddDnsName(name);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)
        );
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false)
        );
        if (issuerDownloadUri is not null)
        {
            request.CertificateExtensions.Add(
                new X509AuthorityInformationAccessExtension(null, [issuerDownloadUri])
            );
        }

        // Derived from the issuer's own window, never from the clock again, so the leaf always lies inside it.
        using X509Certificate2 issued = request.Create(
            issuer,
            new DateTimeOffset(issuer.NotBefore).AddHours(1),
            new DateTimeOffset(issuer.NotAfter).AddDays(-1),
            Serial()
        );
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);

        // Windows TLS cannot use an ephemeral key; a PKCS#12 round trip gives it one it can.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private static byte[] Serial()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7f;
        return serial;
    }
}

/// <summary>
/// A loopback HTTP server that answers every request with one certificate, as the <c>caIssuers</c> location of a
/// public certificate does, and counts the requests, so a test can show that a client fetched it.
/// </summary>
internal sealed class CertificateDownloadServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private int _requests;

    public CertificateDownloadServer(X509Certificate2 certificate)
    {
        _certificate = certificate.RawData;
        _listener.Start();
        Uri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/issuer.cer";
        _serving = Task.Run(ServeAsync);
    }

    public string Uri { get; }

    /// <summary>The number of requests answered so far.</summary>
    public int Requests => Volatile.Read(ref _requests);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    StringBuilder request = new();
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Interlocked.Increment(ref _requests);
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nContent-Length: {_certificate.Length}\r\nConnection: close\r\n\r\n"
                        ),
                        _stop.Token
                    );
                    await stream.WriteAsync(_certificate, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                catch (Exception)
                {
                    // The client gave up; the count stands.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving.WaitAsync(TimeSpan.FromSeconds(30));
        _stop.Dispose();
    }
}
