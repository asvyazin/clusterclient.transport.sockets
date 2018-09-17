using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Vostok.ClusterClient.Core.Model;
using Vostok.ClusterClient.Core.Transport;
using Vostok.ClusterClient.Transport.Webrequest.Pool;
using Vostok.Commons.Helpers.Extensions;
using Vostok.Logging.Abstractions;

namespace Vostok.ClusterClient.Transport.Sockets
{
    public class SocketsTransport : ITransport, IDisposable
    {
        private const int BufferSize = 16*1024;
        private const int PreferredReadSize = 16*1024;
        private const int LOHObjectSizeThreshold = 85*1000;
        
        private readonly SocketsTransportSettings settings;
        private readonly ILog log;
        private readonly IPool<byte[]> pool;
        private readonly HttpClient client;
        private readonly HttpRequestMessageFactory requestFactory;
        
        public SocketsTransport(SocketsTransportSettings settings, ILog log)
        {
            settings = settings.Clone();
            
            this.settings = settings;
            this.log = log;
            this.pool = new Pool<byte[]>(() => new byte[BufferSize]);
            var handler = new SocketsHttpHandler
            {
                Proxy = settings.Proxy,
                ConnectTimeout = settings.ConnectionTimeout ?? TimeSpan.FromMinutes(2),
                UseProxy = settings.Proxy != null,
                AllowAutoRedirect = settings.AllowAutoRedirect,
                PooledConnectionIdleTimeout = settings.ConnectionIdleTimeout, //TODO
                PooledConnectionLifetime = settings.TcpKeepAliveTime
            };
            
            client = new HttpClient(handler, true);

            requestFactory = new HttpRequestMessageFactory(pool, log);
        }

        /// <inheritdoc />
        public async Task<Response> SendAsync(Request request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout.TotalMilliseconds < 1)
            {
                LogRequestTimeout(request, timeout);
                return new Response(ResponseCode.RequestTimeout);
            }
            var sw = Stopwatch.StartNew();
            
            for (var i = 0; i < settings.ConnectionAttempts; i++)
            {
                var attemptTimeout = timeout - sw.Elapsed;
                if (attemptTimeout < TimeSpan.Zero)
                    return new Response(ResponseCode.RequestTimeout);

                try
                {
                    using (var timeoutCts = new CancellationTokenSource())
                    using (var attemptCts = new CancellationTokenSource())
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptCts.Token))
                    {
                        var sendTask = SendOnceAsync(request, timeout, linkedCts);
                        var timeoutTask = Task.Delay(timeout, linkedCts.Token);
                        if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
                        {
                            attemptCts.Cancel();
                            continue;
                        }

                        timeoutCts.Cancel();
                        var response = sendTask.GetAwaiter().GetResult();
                        if (response != null)
                            return response;
                    }
                }
                catch (StreamAlreadyUsedException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    LogUnknownException(e);
                    return Responses.UnknownFailure;
                }
            }

            return new Response(ResponseCode.ConnectFailure);
        }

        private async Task<Response> SendOnceAsync(Request request, TimeSpan timeout, CancellationTokenSource cancellationTokenSource)
        {
            using (var state = new RequestState(timeout, cancellationTokenSource))
            {
                // should create new HttpRequestMessage per attempt
                state.Request = request;
                state.RequestMessage = requestFactory.Create(request, timeout, cancellationTokenSource.Token);

                try
                {
                    state.ResponseMessage = await client.SendAsync(state.RequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationTokenSource.Token).ConfigureAwait(false);

                }
                catch (HttpRequestException e) when (e.InnerException is SocketException se && IsConnectionFailure(se.SocketErrorCode) ||
                                                     e.InnerException is TaskCanceledException)
                {
                    // connection timeout
                    return null;
                }
                catch (OperationCanceledException)
                {
                    return Responses.Canceled;
                }
                catch (ResponseException e)
                {
                    return e.Response;
                }

                state.ResponseCode = (ResponseCode) (int) state.ResponseMessage.StatusCode;

                state.Headers = HeadersConverter.Create(state.ResponseMessage);

                var contentLength = state.ResponseMessage.Content.Headers.ContentLength;

                try
                {

                    if (NeedToStreamResponseBody(contentLength))
                    {
                        return await GetResponseWithStreamAsync(state).ConfigureAwait(false);
                    }

                    if (contentLength != null)
                    {
                        if (contentLength > settings.MaxResponseBodySize)
                            return new Response(ResponseCode.InsufficientStorage, headers: state.Headers);

                        return await GetResponseWithKnownContentLength(state, (int) contentLength, cancellationTokenSource.Token).ConfigureAwait(false);
                    }

                    return await GetResponseWithUnknownContentLength(state, cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    return Responses.Canceled;
                }
                catch (ResponseException e)
                {
                    return e.Response;
                }
            }
        }

        private async Task<Response> GetResponseWithUnknownContentLength(RequestState state, CancellationToken cancellationToken)
        {
            try
            {
                using (var stream = await state.ResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memoryStream = new MemoryStream())
                using (pool.AcquireHandle(out var buffer))
                {
                    while (true)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        memoryStream.Write(buffer, 0, bytesRead);

                        if (memoryStream.Length > settings.MaxResponseBodySize)
                            return new Response(ResponseCode.InsufficientStorage, headers: state.Headers);
                    }

                    var content = new Content(memoryStream.GetBuffer(), 0, (int) memoryStream.Length);
                    return new Response(state.ResponseCode, content, state.Headers);
                }
            }
            catch (Exception e)
            {
                LogReceiveBodyFailure(state.Request, e);
                return new Response(ResponseCode.ReceiveFailure, headers: state.Headers);
            }
        }

        private async Task<Response> GetResponseWithKnownContentLength(RequestState state, int contentLength, CancellationToken cancellationToken)
        {
            try
            {
                using (var stream = await state.ResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var array = settings.BufferFactory(contentLength);
                    
                    var totalBytesRead = 0;

                    // TODO: check .net core socket behavior
                    if (contentLength < LOHObjectSizeThreshold)
                    {
                        while (totalBytesRead < contentLength)
                        {
                            var bytesToRead = Math.Min(contentLength - totalBytesRead, PreferredReadSize);
                            var bytesRead = await stream.ReadAsync(array, totalBytesRead, bytesToRead, cancellationToken).ConfigureAwait(false);
                            if (bytesRead == 0)
                                break;

                            totalBytesRead += bytesRead;
                        }
                    }
                    else
                    {
                        using (pool.AcquireHandle(out var buffer))
                        {
                            while (totalBytesRead < contentLength)
                            {
                                var bytesToRead = Math.Min(contentLength - totalBytesRead, buffer.Length);
                                var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken).ConfigureAwait(false);
                                if (bytesRead == 0)
                                    break;

                                Buffer.BlockCopy(buffer, 0, array, totalBytesRead, bytesRead);

                                totalBytesRead += bytesRead;
                            }
                        }
                    }

                    if (totalBytesRead < contentLength)
                        throw new EndOfStreamException($"Response stream ended prematurely. Read only {totalBytesRead} byte(s), but Content-Length specified {contentLength}.");

                    return new Response(state.ResponseCode, new Content(array, 0, contentLength), state.Headers);
                }
            }
            catch (Exception e)
            {
                LogReceiveBodyFailure(state.Request, e);
                return new Response(ResponseCode.ReceiveFailure, headers: state.Headers);
            }
        }

        private bool NeedToStreamResponseBody(long? length)
        {
            try
            {
                return Settings.UseResponseStreaming(length);
            }
            catch (Exception error)
            {
                log.Error(error);
                return false;
            }
        }
        
        private async Task<Response> GetResponseWithStreamAsync(RequestState state)
        {
            var stream = await state.ResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var wrappedStream = new ResponseStream(stream, state);
            state.PreventNextDispose();
            return new Response(state.ResponseCode, null, state.Headers, wrappedStream);
        }

        private void LogRequestTimeout(Request request, TimeSpan timeout)
        {
            log.Error($"Request timed out. Target = {request.Url.Authority}. Timeout = {timeout.ToPrettyString()}.");
        }

        private void LogUnknownException(Exception error)
        {
            log.Error(error, "Unknown error in sending request.");
        }

        private void LogReceiveBodyFailure(Request request, Exception error)
        {
            log.Error(error, "Error in receiving request body from " + request.Url.Authority);
        }

        private static bool IsConnectionFailure(SocketError socketError)
        {
            switch (socketError)
            {
                case SocketError.HostNotFound:
                case SocketError.AddressNotAvailable:
                    return true;
                default:
                    Console.WriteLine(socketError);
                    return false;
            }
        }

        public TransportCapabilities Capabilities { get; } = TransportCapabilities.RequestStreaming | TransportCapabilities.ResponseStreaming;
        internal SocketsTransportSettings Settings => settings;

        /// <inheritdoc />
        public void Dispose()
        {
            client?.Dispose();
        }
    }
}