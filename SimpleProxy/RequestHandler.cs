using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleProxy
{
    class RequestHandler
    {
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] spaceSplit = new char[] { ' ' };
        private static readonly byte[] chunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);
        private static readonly byte[] chunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);


        public static IDictionary<string, IList<int>> hostMap = new Dictionary<string, IList<int>>();

        public static async void processHttp(TcpClient client)
        {
            var handle = client.Client.Handle;
            // System.Console.WriteLine("Socket: " + handle.ToString());
            
            var clientStream = client.GetStream();
            var clientStreamReader = new StreamReader(clientStream);

            var httpCmd = clientStreamReader.ReadLine();
            if (string.IsNullOrEmpty(httpCmd))
            {
                clientStreamReader.Close();
                clientStream.Close();
                client.Close();
                return;
            }

            var splitBuffer = httpCmd.Split(spaceSplit, 3);
            var method = splitBuffer[0].ToUpper();
            var remoteUri = splitBuffer[1];

            if (method == "CONNECT")
            {
                try
                {
                    await HandleConnectCmd(client, clientStream, clientStreamReader, remoteUri);
                }
                catch
                {

                }
                finally
                {
                    clientStreamReader.Dispose();
                    clientStream.Dispose();
                    client.Close();
                }

                return;
            }
            else
            {
                try
                {
                    await HandleCmd(client, clientStream, clientStreamReader, httpCmd);
                }
                catch
                {

                }
                finally
                {
                    clientStreamReader.Dispose();
                    clientStream.Dispose();
                    client.Close();
                }

                return;
            }
        }

        private static async Task<HttpWebRequest> CreateRequest(string remoteUri, string method, StreamReader clientStreamReader, HttpUtil.Headers requestHeaders)
        {
            var keepAlive = false;
            var proxyConnection = requestHeaders.getAsStringOrElse("proxy-connection", "");
            if (proxyConnection.ToLower() == "keep-alive")
            {
                keepAlive = true;
            }
            var contentLength = requestHeaders.getAsIntOrElse("content-length", 0);

            var webRequest = (HttpWebRequest)HttpWebRequest.Create(remoteUri);
            requestHeaders.ApplyTo(webRequest);

            // override headers
            webRequest.Method = method;
            webRequest.ProtocolVersion = new Version(1, 1);
            webRequest.Proxy = null;
            webRequest.KeepAlive = keepAlive;
            webRequest.AllowAutoRedirect = false;
            webRequest.AutomaticDecompression = DecompressionMethods.None;
            webRequest.Timeout = 10000;
            webRequest.ReadWriteTimeout = 10000;

            if (method == "POST" || method == "PUT")
            {
                var postBuffer = new char[contentLength];
                int total = 0;
                var requestBuffererdStream = new BufferedStream(webRequest.GetRequestStream());
                var requestWriter = new StreamWriter(requestBuffererdStream);

                while (true)
                {
                    if (total >= contentLength)
                    {
                        break;
                    }

                    // clientStream.Read does not read data and blocks forever
                    //var count = clientStream.Read(postBuffer, 0, contentLength);
                    var count = clientStreamReader.ReadBlock(postBuffer, 0, contentLength);
                    if (count <= 0)
                    {
                        break;
                    }

                    total += count;
                    requestWriter.Write(postBuffer, 0, count);
                }
                requestWriter.Flush();
                await requestBuffererdStream.FlushAsync();
            }

            return webRequest;

        }

        private static async Task SendResponseToClient(HttpStatusCode statusCode, string statusDescription,int contentLength, Stream responseStream,HttpUtil.Headers responseHeaders , Stream clientStream, StreamReader clientStreamReader)
        {
            StreamWriter responseWriter = null;
            var bufferedStream = new BufferedStream(clientStream);

            var transferEncoding = responseHeaders.getAsStringOrElse("Transfer-Encoding", "").ToLower();

            try
            {
                responseWriter = new StreamWriter(bufferedStream);
                var status = string.Format("HTTP/1.1 {0} {1}", (Int32)statusCode, statusDescription);
                responseWriter.WriteLine(status);

                foreach (var header in responseHeaders)
                {
                    responseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                }
                responseWriter.WriteLine();
                responseWriter.Flush();
                await bufferedStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in writing headers");
                Console.WriteLine(ex.Message);
            }

            Byte[] buffer;
            if (contentLength > 0)
            {
                try
                {
                    responseStream.CopyTo(bufferedStream, BUFFER_SIZE);
                    await bufferedStream.FlushAsync();
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine("Error while send response to client\r\n" + ex.Message);
                }
            }
            else if(transferEncoding == "chunked")
            {
                // chunked encoding
                buffer = new Byte[BUFFER_SIZE];
                while (true)
                {
                    var count = await responseStream.ReadAsync(buffer, 0, BUFFER_SIZE);
                    if (count > 0)
                    {
                        var chunkHead = Encoding.ASCII.GetBytes(count.ToString("x2"));

                        bufferedStream.Write(chunkHead, 0, chunkHead.Length);
                        bufferedStream.Write(chunkTrail, 0, chunkTrail.Length);
                        bufferedStream.Write(buffer, 0, count);
                        bufferedStream.Write(chunkTrail, 0, chunkTrail.Length);
                    }
                    else
                    {
                        break;
                    }
                }

                bufferedStream.Write(chunkEnd, 0, chunkEnd.Length);
                await bufferedStream.FlushAsync();
            }
        }

        private static async Task HandleCmd(TcpClient client, Stream clientStream, StreamReader clientStreamReader, string httpCmd)
        {
            var splitBuffer = httpCmd.Split(spaceSplit, 3);
            var method = splitBuffer[0].ToUpper();
            var remoteUri = splitBuffer[1];
            var version = new Version(1, 1);

            // read request header
            bool keepConnection = false;
            var requestHeaders = HttpUtil.ReadHeaders(clientStreamReader);
            var contentLength = requestHeaders.getAsIntOrElse("content-length", 0);

            HttpWebResponse response = null;
            
            Stream responseStream = null;

            try
            {
                var webRequest = await CreateRequest(remoteUri, method, clientStreamReader, requestHeaders);
                
                try
                {
                    response = (HttpWebResponse)(await webRequest.GetResponseAsync());
                }
                catch (WebException webEx)
                {
                    response = webEx.Response as HttpWebResponse;
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine("Error while receiving resopnse");
                    System.Console.WriteLine(ex.Message);
                }

                if (response == null)
                {
                    return;
                }

                var responseHeaders = HttpUtil.ReadHeaders(response);
                responseHeaders.Add(new Tuple<string, string>("X-Proxied-By", "simple-proxy"));

                var responseConnection = responseHeaders.getAsStringOrElse("connection", "");
                if (responseConnection.ToLower() != "close")
                {
                    keepConnection = true;
                }

                responseStream = response.GetResponseStream();
                await SendResponseToClient(response.StatusCode, response.StatusDescription, (int)response.ContentLength, responseStream, responseHeaders, clientStream, clientStreamReader);

                if (responseStream != null)
                {
                    responseStream.Close();
                    responseStream.Dispose();
                }
                if (response != null)
                {
                    response.Close();
                    response.Dispose();
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("Error while sending response");
                Console.WriteLine(ex.Message);
            }

            try
            {
                if (keepConnection)
                {
                    var nexthttpCmdTask = clientStreamReader.ReadLineAsync();
                    var task = await Task.WhenAny(nexthttpCmdTask, Task.Delay(2000));
                    if (task == nexthttpCmdTask)
                    {
                        var nexthttpCmd = nexthttpCmdTask.Result;
                        // System.Console.WriteLine("next: " + nexthttpCmd);
                        if (!string.IsNullOrEmpty(nexthttpCmd))
                        {
                            await HandleCmd(client, clientStream, clientStreamReader, nexthttpCmd);
                        }
                    }
                    else
                    {
                        System.Console.WriteLine("Close Connection by Timeout of 2000msec");
                    }

                }
                else
                {
                    System.Console.WriteLine("Close Connection");
                }
            }
            catch(Exception ex)
            {
                System.Console.WriteLine("Erro while keep connection");
                System.Console.WriteLine(ex.Message);
            }
            
        }

        private static async Task HandleConnectCmd(TcpClient client, Stream clientStream, StreamReader clientStreamReader, string uri)
        {
            var remoteUri = "https://" + uri;

            // extract host and port number
            var splitBuffer = uri.Split(':');
            var host = splitBuffer[0];
            var port = int.Parse(splitBuffer[1]);

            // read all headers
            var data = clientStreamReader.ReadLine();
            while (!string.IsNullOrEmpty(data))
            {
                // System.Console.WriteLine(data);
                data = clientStreamReader.ReadLine();
            }

            // System.Console.WriteLine("Tunnel: " + host + ":" + port.ToString());

            // connect to host synchronously
            var tunnelClient = new TcpClient(host, port);
            if (!tunnelClient.Connected)
            {
                return;
            }

            // send response headers to client
            var clientStreamWriter = new StreamWriter(clientStream);
            clientStreamWriter.WriteLine("HTTP/1.0 200 Connection established");
            clientStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            clientStreamWriter.WriteLine("Proxy-agent: simple-proxy");
            clientStreamWriter.WriteLine("Connection: close");
            clientStreamWriter.WriteLine();
            await clientStreamWriter.FlushAsync();

            var tunnelStream = tunnelClient.GetStream();

            // System.Console.WriteLine("tunnel start");
            try
            {
                var relayToServer = clientStream.CopyToAsync(tunnelStream, BUFFER_SIZE);
                var relayToClient = tunnelStream.CopyToAsync(clientStream, BUFFER_SIZE);

                var waitCancell = new CancellationTokenSource();
                var closeFromClientTask = relayToClient.ContinueWith((t) =>
                {
                    if (waitCancell.Token.IsCancellationRequested)
                    {
                        return;
                    }
                    waitCancell.Cancel();

                    // System.Console.WriteLine("tunnel close (by client close) " + host);
                    try
                    {
                        if (tunnelStream != null)
                        {
                            tunnelStream.Dispose();
                        }
                        if (tunnelClient != null)
                        {
                            tunnelClient.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine("Error while closing connection");
                        System.Console.WriteLine(ex.Message);
                    }
                }, waitCancell.Token);

                await relayToServer;

                var closeByTimeoutTask = Task.Delay(5 * 1000, waitCancell.Token)
                    .ContinueWith((delay) =>
                    {
                        if (waitCancell.Token.IsCancellationRequested)
                        {
                            return;
                        }
                        waitCancell.Cancel();

                        // System.Console.WriteLine("tunnel close (by timeout) " + host);

                        try
                        {
                            if (tunnelStream != null)
                            {
                                tunnelStream.Dispose();
                            }
                            if (tunnelClient != null)
                            {
                                tunnelClient.Close();
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Console.WriteLine("Error while closing connection");
                            System.Console.WriteLine(ex.Message);
                        }
                    });

                await Task.WhenAny(closeFromClientTask, closeByTimeoutTask);

            }
            catch(Exception ex)
            {
                System.Console.WriteLine(ex.Message);
            }

        }
    }
}
