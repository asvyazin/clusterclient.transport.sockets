using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vostok.Clusterclient.Core.Model;
using Vostok.Clusterclient.Transport.Sockets.Client;
using Vostok.Clusterclient.Transport.Sockets.Hacks;
using Vostok.Clusterclient.Transport.Sockets.Messages;
using Vostok.Clusterclient.Transport.Sockets.ResponseReading;
using Vostok.Clusterclient.Transport.Sockets.Sender;
using Vostok.Logging.Abstractions;
using Vostok.Logging.Console;

namespace Vostok.Clusterclient.Transport.Sockets.Tests
{
    public class InternalTransport_Tests
    {
        private IHttpRequestMessageFactory requestFactory;
        private IResponseReader responseReader;
        private InternalTransport sender;
        private ISocketTuner socketTuner;
        private ILog log;
        private IHttpClient client;
        private Request request;

        [SetUp]
        public void Setup()
        {
            requestFactory = Substitute.For<IHttpRequestMessageFactory>();
            responseReader = Substitute.For<IResponseReader>();
            socketTuner = Substitute.For<ISocketTuner>();
            client = Substitute.For<IHttpClient>();
            log = new ConsoleLog();
            request = Request.Post("http://localhost/");
            
            sender = new InternalTransport(requestFactory, responseReader, socketTuner, log);
            
            requestFactory.WhenForAnyArgs(x => x.Create(null, default, out _)).Do(x => x[2] = new SendContext());
        }

        [Test]
        public async Task Should_handle_errors()
        {
            requestFactory
                .Create(null, default, out _)
                .ThrowsForAnyArgs(new ArgumentException());
            var response = await sender.SendAsync(null, null, CancellationToken.None);
            response.Code.Should().Be(ResponseCode.UnknownFailure);
        }

        [Test]
        public async Task Should_return_Cancelled_on_OperationCancelledException_when_cancellation_requested()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                
                requestFactory
                    .Create(null, default, out _)
                    .ThrowsForAnyArgs(new OperationCanceledException());
                var response = await sender.SendAsync(null, null, cts.Token);
                response.Code.Should().Be(ResponseCode.Canceled);
            }
        }

        [Test]
        public async Task Should_return_UnknownFailure_on_OperationCancelledException_when_cancellation_token_is_not_signaled()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                
                requestFactory
                    .Create(null, default, out _)
                    .ThrowsForAnyArgs(new OperationCanceledException());
                var response = await sender.SendAsync(null, null, CancellationToken.None);
                response.Code.Should().Be(ResponseCode.UnknownFailure);
            }
        }

        [Test]
        public void Should_not_catch_StreamAlreadyUsedException()
        {
            requestFactory
                .Create(null, default, out _)
                .ThrowsForAnyArgs(new StreamAlreadyUsedException(""));
            
            new Action(() => sender.SendAsync(null, null, default).GetAwaiter().GetResult())
                .Should().Throw<StreamAlreadyUsedException>();
        }

        [TestCase(SocketError.HostNotFound)]
        [TestCase(SocketError.AddressNotAvailable)]
        [TestCase(SocketError.ConnectionRefused)]
        [TestCase(SocketError.TryAgain)]
        [TestCase(SocketError.NetworkUnreachable)]
        [TestCase(SocketError.NetworkDown)]
        [TestCase(SocketError.HostDown)]
        [TestCase(SocketError.HostUnreachable)]
        public async Task Should_return_ConnectionFailure_based_on_socket_error(SocketError socketError)
        {
            var exception = new HttpRequestException(string.Empty, new SocketException((int) socketError));
            
            client
                .SendAsync(null, default, default)
                .ThrowsForAnyArgs(exception);

            var result = await sender.SendAsync(client, request, default);
            
            result.Code.Should().Be(ResponseCode.ConnectFailure);
        }
        
        [TestCase(ResponseCode.Accepted)]
        [TestCase(ResponseCode.Conflict)]
        public async Task Should_return_Response_from_SendContext(ResponseCode code)
        {
            var sendContext = new SendContext();

            requestFactory.Create(null, default, out _)
                .ReturnsForAnyArgs(
                    x =>
                    {
                        x[2] = sendContext;
                        return null;
                    });
            
            client
                .WhenForAnyArgs(x => x.SendAsync(null, default, default))
                .Do(_ => sendContext.Response = new Response(code));

            var result = await sender.SendAsync(client, request, default);
            
            result.Code.Should().Be(code);
        }
        
        [Test]
        public async Task Should_tune_socket()
        {
            var sendContext = new SendContext();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            requestFactory.Create(null, default, out _)
                .ReturnsForAnyArgs(
                    x =>
                    {
                        x[2] = sendContext;
                        return null;
                    });
            
            client
                .WhenForAnyArgs(x => x.SendAsync(null, default, default))
                .Do(_ => sendContext.Socket = socket);

            await sender.SendAsync(client, request, default);
            
            socketTuner.Received(1).Tune(socket);
        }

        [Test]
        public async Task Should_call_client_with_correct_parameters()
        {
            using (var cts = new CancellationTokenSource())
            {
                var requestMessage = new HttpRequestMessage();
                var token = cts.Token;
                
                requestFactory.Create(null, default, out _)
                    .ReturnsForAnyArgs(requestMessage);

                await sender.SendAsync(client, request, token);

                client.Received(1).SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token);
            }
        }

        [Test]
        public async Task Should_call_response_reader_with_correct_parameters()
        {
            using (var cts = new CancellationTokenSource())
            {
                var responseMessage = new HttpResponseMessage();
                var token = cts.Token;

                client
                    .SendAsync(null, default, default)
                    .ReturnsForAnyArgs(responseMessage);

                await sender.SendAsync(client, request, token); 

                responseReader.Received(1).ReadResponseBodyAsync(responseMessage, token);
            }
        }
    }
}