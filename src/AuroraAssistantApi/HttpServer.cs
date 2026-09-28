using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AuroraAssistantApi
{
    internal class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";

        public string Q(string key, string fallback = null) => Query.TryGetValue(key, out var v) ? v : fallback;
    }

    internal class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public string Body = "";
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on a raw TcpListener. HttpListener depends on http.sys, which
    /// is unreliable under Wine, so we parse requests ourselves. One request per connection.
    /// </summary>
    internal class HttpServer
    {
        private readonly TcpListener listener;
        private readonly Func<HttpRequest, HttpResponse> handler;
        private readonly Action<string> logError;

        public HttpServer(IPAddress address, int port, Func<HttpRequest, HttpResponse> handler, Action<string> logError)
        {
            listener = new TcpListener(address, port);
            this.handler = handler;
            this.logError = logError;
        }

        public void Start()
        {
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "Aurora Assistant API HTTP" }.Start();
        }

        private void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Serve(client));
                }
                catch (Exception e)
                {
                    logError("Accept failed: " + e.Message);
                    Thread.Sleep(500);
                }
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    var stream = client.GetStream();
                    var request = Read(stream);
                    HttpResponse response;
                    try
                    {
                        response = handler(request);
                    }
                    catch (Exception e)
                    {
                        response = new HttpResponse
                        {
                            Status = 500,
                            Body = Newtonsoft.Json.JsonConvert.SerializeObject(new { error = e.Message, detail = e.ToString() })
                        };
                    }
                    Write(stream, response);
                }
                catch (Exception e)
                {
                    logError("Request failed: " + e.Message);
                }
            }
        }

        private static HttpRequest Read(NetworkStream stream)
        {
            // Read header bytes up to the blank line, then the body by Content-Length.
            var header = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    throw new IOException("Connection closed during headers");
                header.WriteByte((byte)b);
                matched = (b == "\r\n\r\n"[matched]) ? matched + 1 : (b == '\r' ? 1 : 0);
            }

            var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };

            var target = parts.Length > 1 ? parts[1] : "/";
            int q = target.IndexOf('?');
            request.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0)
                        continue;
                    int eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString(eq >= 0 ? pair.Substring(0, eq) : pair);
                    var value = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : "";
                    request.Query[key] = value;
                }
            }

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                    request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (request.Headers.TryGetValue("Content-Length", out var len) && int.TryParse(len, out var n) && n > 0)
            {
                var body = new byte[n];
                int read = 0;
                while (read < n)
                {
                    int r = stream.Read(body, read, n - read);
                    if (r <= 0)
                        break;
                    read += r;
                }
                request.Body = Encoding.UTF8.GetString(body, 0, read);
            }
            return request;
        }

        private static void Write(NetworkStream stream, HttpResponse response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body ?? "");
            var head = "HTTP/1.1 " + response.Status + " " + Reason(response.Status) + "\r\n" +
                       "Content-Type: " + response.ContentType + "\r\n" +
                       "Content-Length: " + body.Length + "\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                default: return "Error";
            }
        }
    }
}
