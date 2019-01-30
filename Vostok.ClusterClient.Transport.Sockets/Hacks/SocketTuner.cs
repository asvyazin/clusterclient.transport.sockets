using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Vostok.Commons.Helpers.Network;
using Vostok.Logging.Abstractions;

namespace Vostok.Clusterclient.Transport.Sockets.Hacks
{
    internal class SocketTuner : ISocketTuner
    {
        private readonly SocketsTransportSettings settings;
        private readonly ILog log;
        private readonly byte[] keepAliveValues;

        public SocketTuner(SocketsTransportSettings settings, ILog log)
        {
            this.settings = settings;
            this.log = log;
            keepAliveValues = GetKeepAliveValues(settings);
        }

        public void Tune(Socket socket)
        {
            if (socket == null)
                return;

            try
            {
                TuneArp(socket);
            }
            catch (Exception e)
            {
                log.Warn(e);
            }

            try
            {
                TuneKeepAlive(socket);
            }
            catch (Exception e)
            {
                log.Warn(e);
            }
        }

        private static byte[] GetKeepAliveValues(SocketsTransportSettings settings)
        {
            if (!settings.TcpKeepAliveEnabled || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            var tcpKeepAliveTime = (int)settings.TcpKeepAliveTime.TotalMilliseconds;
            var tcpKeepAliveInterval = (int)settings.TcpKeepAliveInterval.TotalMilliseconds;

            return new byte[]
            {
                1,
                0,
                0,
                0,
                (byte)(tcpKeepAliveTime & byte.MaxValue),
                (byte)((tcpKeepAliveTime >> 8) & byte.MaxValue),
                (byte)((tcpKeepAliveTime >> 16) & byte.MaxValue),
                (byte)((tcpKeepAliveTime >> 24) & byte.MaxValue),
                (byte)(tcpKeepAliveInterval & byte.MaxValue),
                (byte)((tcpKeepAliveInterval >> 8) & byte.MaxValue),
                (byte)((tcpKeepAliveInterval >> 16) & byte.MaxValue),
                (byte)((tcpKeepAliveInterval >> 24) & byte.MaxValue)
            };
        }

        private void TuneArp(Socket socket)
        {
            if (settings.ArpCacheWarmupEnabled && socket.RemoteEndPoint is IPEndPoint ipEndPoint && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ArpCacheMaintainer.ReportAddress(ipEndPoint.Address);
        }

        private void TuneKeepAlive(Socket socket)
        {
            if (settings.TcpKeepAliveEnabled && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
        }
    }
}