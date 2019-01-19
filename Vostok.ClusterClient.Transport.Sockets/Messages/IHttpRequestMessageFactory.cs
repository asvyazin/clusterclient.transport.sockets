using System.Net.Http;
using System.Threading;
using Vostok.Clusterclient.Core.Model;

namespace Vostok.Clusterclient.Transport.Sockets.Messages
{
    internal interface IHttpRequestMessageFactory
    {
        HttpRequestMessage Create(
            Request request,
            CancellationToken cancellationToken,
            out SendContext sendContext);
    }
}