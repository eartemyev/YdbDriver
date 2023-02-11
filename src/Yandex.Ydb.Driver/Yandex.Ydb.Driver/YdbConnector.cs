﻿using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;

namespace Yandex.Ydb.Driver;

internal sealed class YdbConnector : IYdbConnector, IAsyncDisposable
{
    private GrpcChannel? _channel;
    private CallInvoker _invoker;
    private readonly CallOptions _defaultOptions;

    internal YdbConnector(YdbDataSource dataSource)
    {
        DataSource = dataSource;
        _defaultOptions = new CallOptions()
        {
            Headers = { }
        };
    }

    internal YdbDataSource DataSource { get; }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
        }
    }

    internal async Task Open(TimeSpan timeout, CancellationToken token)
    {
        Debug.Assert(_channel == null);
        var settings = DataSource.Settings;

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 1000
        };

        var url = $"{(settings.UseSsl ? "https" : "http")}://{settings.Host}:{settings.Port}";
        LogMessages.OpenningGrpcChannel(DataSource.LoggingConfiguration.ConnectionLogger, url);

        if (settings.UseSsl)
        {
            var path = settings.RootCertificate;
            if (path == null)
                Helpers.ThrowHelper.InvalidDataException("Root certificate is null in setting for connection");

            if (!File.Exists(Path.Combine(path, "cert.pem")))
                Helpers.ThrowHelper.FileNotFound($"Cert.pem file does not exist in path `{path}`", "cert.pem");

            if (!File.Exists(Path.Combine(path, "key.pem")))
                Helpers.ThrowHelper.FileNotFound($"Key.pem file does not exist in path `{path}`", "key.pem");

            var cert = X509Certificate2.CreateFromPemFile(Path.Combine(path, "cert.pem"),
                Path.Combine(path, "key.pem"));
            handler.SslOptions.ClientCertificates ??= new X509CertificateCollection();
            handler.SslOptions.ClientCertificates.Add(cert);

            if (settings.TrustSsl)
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy()
                    { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { cert } };
        }

        _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions()
        {
            HttpHandler = handler
        });
        await _channel.ConnectAsync(token);

        _invoker = _channel.CreateCallInvoker();
    }

    public async ValueTask<TResponse> UnaryCallAsync<TRequest, TResponse>(Method<TRequest, TResponse> method,
        TRequest request, CallOptions? options = null) where TRequest : class where TResponse : class
    {
        return await _invoker.AsyncUnaryCall(method, null, options ?? GetDefaultOptions(), request);
    }

    private CallOptions GetDefaultOptions()
    {
        return _defaultOptions;
    }

    public TResponse UnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
        TRequest request, CallOptions? options = null) where TRequest : class where TResponse : class
    {
        return _invoker.BlockingUnaryCall(method, null, options ?? GetDefaultOptions(), request);
    }
}