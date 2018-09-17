using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Vostok.ClusterClient.Core.Model;
using Vostok.ClusterClient.Transport.Sockets.Utilities;
using Vostok.Logging.Abstractions;

namespace Vostok.ClusterClient.Transport.Sockets
{
    internal static class HeadersConverter
    {
        public static void Fill(Request request, HttpRequestMessage message, TimeSpan timeout, ILog log)
        {
            if (request.Headers != null)
            {
                var canAssignDirectly = HttpHeadersUnlocker.TryUnlockRestrictedHeaders(message.Headers, log);
                if (canAssignDirectly)
                {
                    AssignHeadersDirectly(request.Headers, message.Headers);
                }
                else
                {
                    //AssignHeadersThroughProperties(request.Headers, webRequest);
                }
            }

            SetRequestTimeoutHeader(message.Headers, timeout);

            TrySetHostExplicitly(request.Headers, message.Headers);
            TrySetClientIdentityHeader(message.Headers);
        }

        public static Headers Create(HttpResponseMessage responseMessage)
        {
            var headers = Headers.Empty;
            
            foreach (var responseHeader in responseMessage.Headers)
                headers = headers.Set(responseHeader.Key, string.Join(',', responseHeader.Value));
            
            if (responseMessage.Content != null)
                foreach (var contentHeader in responseMessage.Content.Headers)
                    headers = headers.Set(contentHeader.Key, string.Join(',', contentHeader.Value));

            return headers;
        }

        private static void AssignHeadersDirectly( Headers source,HttpHeaders target)
        {
            foreach (var header in source)
            {
                if (NeedToSkipHeader(header.Name))
                    continue;

                target.Add(header.Name, header.Value);
            }
        }

        private static void SetRequestTimeoutHeader(HttpHeaders headers, TimeSpan timeout)
        {
            headers.Add(HeaderNames.RequestTimeout, timeout.Ticks.ToString());
        }

        private static void TrySetClientIdentityHeader(HttpHeaders headers)
        {
            
            if (!headers.Contains(HeaderNames.ClientApplication))
                headers.Add(HeaderNames.ClientApplication, ClientIdentityCore.Get());
        }

        private static void TrySetHostExplicitly(Headers source, HttpRequestHeaders target)
        {
            var host = source?[HeaderNames.Host];
            if (host != null)
                target.Host = host;
        }
        
        private static bool NeedToSkipHeader(string name)
        {
            return
                name.Equals(HeaderNames.ContentLength) ||
                name.Equals(HeaderNames.Connection) ||
                name.Equals(HeaderNames.Host) ||
                name.Equals(HeaderNames.TransferEncoding);
        }
    }
}