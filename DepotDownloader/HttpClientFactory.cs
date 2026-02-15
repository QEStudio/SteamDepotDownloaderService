// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    // This is based on the dotnet issue #44686 and its workaround at https://github.com/dotnet/runtime/issues/44686#issuecomment-733797994
    // We don't know if the IPv6 stack is functional.
    class HttpClientFactory
    {
        public static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = ConnectAsync,
            };

            var timeoutSecondsRaw = (Environment.GetEnvironmentVariable("STEAMDDS_HTTP_TIMEOUT_SECONDS") ?? "20").Trim();
            if (!int.TryParse(timeoutSecondsRaw, out var timeoutSeconds) || timeoutSeconds <= 0)
            {
                timeoutSeconds = 20;
            }

            handler.ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, timeoutSeconds));

            var proxy = ContentDownloader.Config?.HttpProxy;
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                var normalized = proxy.Trim();
                if (string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
                {
                    handler.UseProxy = false;
                }
                else
                {
                    handler.Proxy = new WebProxy(normalized);
                }
            }

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            var assemblyVersion = typeof(HttpClientFactory).Assembly.GetName().Version.ToString(fieldCount: 3);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DepotDownloader", assemblyVersion));

            return client;
        }

        static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            // By default, we create dual-mode sockets:
            // Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                var dnsServerRaw = ContentDownloader.Config?.DnsServer;
                if (!string.IsNullOrWhiteSpace(dnsServerRaw))
                {
                    var dnsServer = dnsServerRaw.Trim();
                    if (TryParseDnsServer(dnsServer, out var dnsEndpoint))
                    {
                        try
                        {
                            var ip = await ResolveIPv4Async(context.DnsEndPoint.Host, dnsEndpoint, cancellationToken).ConfigureAwait(false);
                            await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                        }
                    }
                }

                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static bool TryParseDnsServer(string input, out IPEndPoint endpoint)
        {
            endpoint = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var trimmed = input.Trim();
            var port = 53;
            var host = trimmed;

            var lastColon = trimmed.LastIndexOf(':');
            if (lastColon > 0 && lastColon < trimmed.Length - 1 && trimmed.IndexOf(':') == lastColon)
            {
                host = trimmed[..lastColon];
                if (!int.TryParse(trimmed[(lastColon + 1)..], out port) || port <= 0 || port > 65535)
                {
                    port = 53;
                }
            }

            if (!IPAddress.TryParse(host, out var ip))
            {
                return false;
            }

            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            endpoint = new IPEndPoint(ip, port);
            return true;
        }

        private static async Task<IPAddress> ResolveIPv4Async(string host, IPEndPoint dnsServer, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    return parsed;
                }
            }

            var query = BuildDnsQueryA(host, out var expectedId);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(dnsServer);
            await udp.SendAsync(query, cancellationToken).ConfigureAwait(false);

            var receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
            var timeoutTask = Task.Delay(2000, cancellationToken);
            var completed = await Task.WhenAny(receiveTask, timeoutTask).ConfigureAwait(false);
            if (completed != receiveTask)
            {
                throw new TimeoutException("DNS query timeout");
            }

            var res = await receiveTask.ConfigureAwait(false);
            var ip = ParseDnsResponseA(res.Buffer, expectedId);
            if (ip == null)
            {
                throw new Exception("DNS response has no A record");
            }

            return ip;
        }

        private static byte[] BuildDnsQueryA(string host, out ushort id)
        {
            var hostLabels = host.TrimEnd('.').Split('.');
            id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);

            using var ms = new MemoryStream();
            WriteU16(ms, id);
            WriteU16(ms, 0x0100);
            WriteU16(ms, 1);
            WriteU16(ms, 0);
            WriteU16(ms, 0);
            WriteU16(ms, 0);

            foreach (var label in hostLabels)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63)
                {
                    throw new ArgumentException("Invalid DNS name");
                }
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0);
            WriteU16(ms, 1);
            WriteU16(ms, 1);
            return ms.ToArray();
        }

        private static IPAddress ParseDnsResponseA(byte[] buffer, ushort expectedId)
        {
            if (buffer.Length < 12) return null;
            var id = ReadU16(buffer, 0);
            if (id != expectedId) return null;

            var flags = ReadU16(buffer, 2);
            if ((flags & 0x8000) == 0) return null;
            if ((flags & 0x000F) != 0) return null;

            var qdcount = ReadU16(buffer, 4);
            var ancount = ReadU16(buffer, 6);
            var offset = 12;

            for (var i = 0; i < qdcount; i++)
            {
                if (!SkipName(buffer, ref offset)) return null;
                offset += 4;
                if (offset > buffer.Length) return null;
            }

            for (var i = 0; i < ancount; i++)
            {
                if (!SkipName(buffer, ref offset)) return null;
                if (offset + 10 > buffer.Length) return null;
                var type = ReadU16(buffer, offset);
                var cls = ReadU16(buffer, offset + 2);
                var rdlen = ReadU16(buffer, offset + 8);
                offset += 10;
                if (offset + rdlen > buffer.Length) return null;
                if (type == 1 && cls == 1 && rdlen == 4)
                {
                    return new IPAddress(new byte[] { buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] });
                }
                offset += rdlen;
            }

            return null;
        }

        private static bool SkipName(byte[] buffer, ref int offset)
        {
            while (true)
            {
                if (offset >= buffer.Length) return false;
                var len = buffer[offset++];
                if (len == 0) return true;
                if ((len & 0xC0) == 0xC0)
                {
                    if (offset >= buffer.Length) return false;
                    offset++;
                    return true;
                }
                offset += len;
                if (offset > buffer.Length) return false;
            }
        }

        private static ushort ReadU16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static void WriteU16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }
}
