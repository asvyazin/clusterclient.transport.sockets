using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Vostok.Commons.Collections;

namespace Vostok.Clusterclient.Transport.Sockets
{
    internal class SocketsHandlerProvider
    {
        private const int GlobalCacheCapacity = 25;
        private const int LocalCacheCapacity = 3;

        // (iloktionov): Global cache is limited in size to prevent leaks resulting from random connection timeouts:
        private static readonly RecyclingBoundedCache<GlobalCacheKey, SocketsHttpHandler> globalCache
            = new RecyclingBoundedCache<GlobalCacheKey, SocketsHttpHandler>(GlobalCacheCapacity, GlobalCacheKeyComparer.Instance);

        // (iloktionov): Local cache isolates well-behaving clients from others who cause global cache purging:
        private readonly RecyclingBoundedCache<TimeSpan, SocketsHttpHandler> localCache;
        private readonly Func<TimeSpan, SocketsHttpHandler> localCacheFactory;

        public SocketsHandlerProvider(SocketsTransportSettings settings)
        {
            localCache = new RecyclingBoundedCache<TimeSpan, SocketsHttpHandler>(LocalCacheCapacity);
            localCacheFactory = timeout => globalCache.Obtain(CreateKey(settings, timeout), CreateHandler);
        }

        public SocketsHttpHandler Obtain(TimeSpan? connectionTimeout)
            => localCache.Obtain(connectionTimeout ?? Timeout.InfiniteTimeSpan, localCacheFactory);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static GlobalCacheKey CreateKey(SocketsTransportSettings settings, TimeSpan? connectionTimeout)
            => new GlobalCacheKey(
                settings.Proxy,
                settings.AllowAutoRedirect,
                connectionTimeout ?? Timeout.InfiniteTimeSpan,
                settings.ConnectionIdleTimeout,
                settings.ConnectionLifetime,
                settings.MaxConnectionsPerEndpoint,
                settings.CustomTuning);

        private static SocketsHttpHandler CreateHandler(GlobalCacheKey key)
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = key.Proxy,
                UseProxy = key.Proxy != null,
                ConnectTimeout = key.ConnectionTimeout,
                AllowAutoRedirect = key.AllowAutoRedirect,
                PooledConnectionIdleTimeout = key.ConnectionIdleTimeout,
                PooledConnectionLifetime = key.ConnectionLifetime,
                MaxConnectionsPerServer = key.MaxConnectionsPerEndpoint,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = 64 * 1024,
                MaxAutomaticRedirections = 3,
                UseCookies = false,
                SslOptions =
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, __, ___, ____) => true,
                }
            };

            key.CustomTuning?.Invoke(handler);

            return handler;
        }

        private readonly struct GlobalCacheKey
        {
            public readonly IWebProxy Proxy;
            public readonly bool AllowAutoRedirect;
            public readonly TimeSpan ConnectionTimeout;
            public readonly TimeSpan ConnectionIdleTimeout;
            public readonly TimeSpan ConnectionLifetime;
            public readonly int MaxConnectionsPerEndpoint;
            public readonly Action<SocketsHttpHandler> CustomTuning;

            public GlobalCacheKey(
                IWebProxy proxy,
                bool allowAutoRedirect,
                TimeSpan connectionTimeout,
                TimeSpan connectionIdleTimeout,
                TimeSpan connectionLifetime,
                int maxConnectionsPerEndpoint,
                Action<SocketsHttpHandler> customTuning)
            {
                Proxy = proxy;
                AllowAutoRedirect = allowAutoRedirect;
                ConnectionTimeout = connectionTimeout;
                ConnectionIdleTimeout = connectionIdleTimeout;
                ConnectionLifetime = connectionLifetime;
                MaxConnectionsPerEndpoint = maxConnectionsPerEndpoint;
                CustomTuning = customTuning;
            }
        }

        private class GlobalCacheKeyComparer : IEqualityComparer<GlobalCacheKey>
        {
            public static readonly GlobalCacheKeyComparer Instance = new GlobalCacheKeyComparer();

            public bool Equals(GlobalCacheKey x, GlobalCacheKey y)
            {
                return
                    ReferenceEquals(x.Proxy, y.Proxy) &&
                    ReferenceEquals(x.CustomTuning, y.CustomTuning) &&
                    x.AllowAutoRedirect == y.AllowAutoRedirect &&
                    x.ConnectionTimeout == y.ConnectionTimeout &&
                    x.ConnectionIdleTimeout == y.ConnectionIdleTimeout &&
                    x.ConnectionLifetime == y.ConnectionLifetime &&
                    x.MaxConnectionsPerEndpoint == y.MaxConnectionsPerEndpoint;
            }

            public int GetHashCode(GlobalCacheKey key)
                => HashCode.Combine(
                    key.Proxy,
                    key.AllowAutoRedirect,
                    key.ConnectionTimeout,
                    key.ConnectionIdleTimeout,
                    key.ConnectionLifetime,
                    key.MaxConnectionsPerEndpoint,
                    key.CustomTuning);
        }
    }
}
