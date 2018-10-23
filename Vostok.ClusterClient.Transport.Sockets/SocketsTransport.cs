using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Vostok.Clusterclient.Core.Model;
using Vostok.Clusterclient.Core.Transport;
using Vostok.Clusterclient.Transport.Sockets.Pool;
using Vostok.Clusterclient.Transport.Sockets.Sender;
using Vostok.Commons.Time;
using Vostok.Logging.Abstractions;

namespace Vostok.Clusterclient.Transport.Sockets
{
    /// <summary>
    ///     <para>ClusterClient HTTP transport for .NET Core 2.1 and later.</para>
    ///     <para>Internally uses <see cref="SocketsHttpHandler" />.</para>
    /// </summary>
    [PublicAPI]
    public class SocketsTransport : ITransport, IDisposable
    {
        private readonly SocketsTransportSettings settings;
        private readonly ILog log;
        private readonly ISocketsTransportRequestSender sender;

        /// <summary>
        ///     Creates ClusterClient transport for .NET Core 2.1 and later based on <see cref="SocketsHttpHandler" />
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="log"></param>
        public SocketsTransport(SocketsTransportSettings settings, ILog log)
            : this(settings.Clone(), log, null)
        {
        }

        internal SocketsTransport(SocketsTransportSettings settings, ILog log, ISocketsTransportRequestSender sender)
        {
            this.settings = settings;
            this.log = log;

            this.sender = sender ?? CreateSender(settings, log);
        }

        /// <inheritdoc />
        public TransportCapabilities Capabilities { get; } = TransportCapabilities.RequestStreaming | TransportCapabilities.ResponseStreaming;

        /// <inheritdoc />
        public async Task<Response> SendAsync(Request request, TimeSpan? connectionTimeout, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Responses.Canceled;

            if (timeout.TotalMilliseconds < 1)
            {
                LogRequestTimeout(request, timeout);
                return Responses.Timeout;
            }

            using (var timeoutCancellation = new CancellationTokenSource())
            using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
                var senderTask = sender.SendAsync(request, connectionTimeout ?? Timeout.InfiniteTimeSpan, requestCancellation.Token);
                var completedTask = await Task.WhenAny(timeoutTask, senderTask).ConfigureAwait(false);
                if (completedTask is Task<Response> taskWithResponse)
                {
                    timeoutCancellation.Cancel();
                    return taskWithResponse.GetAwaiter().GetResult();
                }

                // completedTask is timeout Task
                requestCancellation.Cancel();
                LogRequestTimeout(request, timeout);

                // wait for cancellation & dispose resources associated with Response object
                // ReSharper disable once MethodSupportsCancellation
                var senderTaskContinuation = senderTask.ContinueWith(
                    t =>
                    {
                        if (t.IsCompleted)
                            t.GetAwaiter().GetResult().Dispose();
                    });

                using (var abortCancellation = new CancellationTokenSource())
                {
                    var abortWaitingDelay = Task.Delay(settings.RequestAbortTimeout, abortCancellation.Token);

                    await Task.WhenAny(senderTaskContinuation, abortWaitingDelay).ConfigureAwait(false);

                    abortCancellation.Cancel();
                }

                if (!senderTask.IsCompleted)
                    LogFailedToWaitForRequestAbort();

                return Responses.Timeout;
            }
        }

        /// <inheritdoc />
        public void Dispose()
            => sender.Dispose();

        private static SocketsTransportRequestSender CreateSender(SocketsTransportSettings settings, ILog log)
        {
            var pool = new Pool<byte[]>(() => new byte[SocketsTransportConstants.PooledBufferSize]);

            var requestFactory = new HttpRequestMessageFactory(pool, log);
            var responseReader = new ResponseReader(settings, pool, log);

            var keepAliveValues = KeepAliveTuner.GetKeepAliveValues(settings);

            return new SocketsTransportRequestSender(settings, requestFactory, responseReader, keepAliveValues, log);
        }

        private void LogRequestTimeout(Request request, TimeSpan timeout)
        {
            log.Warn(
                "Request timed out. Target = {Target}. Timeout = {Timeout}.",
                request.Url.Authority,
                timeout.ToPrettyString());
        }

        private void LogFailedToWaitForRequestAbort()
        {
            log.Warn(
                "Timed out request was aborted but did not complete in {RequestAbortTimeout}.",
                settings.RequestAbortTimeout.ToPrettyString());
        }
    }
}