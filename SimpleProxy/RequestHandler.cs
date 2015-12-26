using System;
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


        public static void processHttp(TcpClient client)
        {
            bool keep = false;
            var clientStream = client.GetStream();
            var clientStreamReader = new StreamReader(clientStream);

            System.Console.WriteLine("aaa");

            var httpCmd = clientStreamReader.ReadLine();
            if (string.IsNullOrEmpty(httpCmd))
            {
                clientStreamReader.Close();
                clientStream.Close();
                return;
            }

            var splitBuffer = httpCmd.Split(spaceSplit, 3);
            var method = splitBuffer[0].ToUpper();
            var remoteUri = splitBuffer[1];
            var version = new Version(1, 1);

            if (method == "CONNECT")
            {
                try
                {
                    HandleConnectCmd(client, clientStream, clientStreamReader, remoteUri);
                }
                finally
                {
                    clientStreamReader.Dispose();
                    clientStream.Dispose();
                    client.Close();
                }

                return;
            }

            // read request header
            var requestHeaders = HttpUtil.ReadHeaders(clientStreamReader);
            var contentLength = requestHeaders.getAsIntOrElse("content-length",0);

            var webRequest = (HttpWebRequest)HttpWebRequest.Create(remoteUri);
            webRequest.Method = method;
            webRequest.ProtocolVersion = version;
            requestHeaders.ApplyTo(webRequest);

            // override headers
            webRequest.Proxy = null;
            webRequest.KeepAlive = false;
            webRequest.AllowAutoRedirect = true;
            webRequest.AutomaticDecompression = DecompressionMethods.None;
            webRequest.Timeout = 15000;

            System.Console.WriteLine(requestHeaders.Print());

            if (method == "GET")
            {

            }
            else if (method == "POST")
            {
                char[] postBuffer = new char[contentLength];
                int total= 0;
                var requestWriter = new StreamWriter(webRequest.GetRequestStream());

                while(true)
                {
                    if (total >= contentLength)
                    {
                        break;
                    }
                    var count = clientStreamReader.ReadBlock(postBuffer, 0, contentLength);
                    if(count <= 0)
                    {
                        break;
                    }

                    total += count;
                    requestWriter.Write(postBuffer, 0, count);
                }

                requestWriter.Close();
            }
            else
            {
                System.Console.WriteLine("Error while reuqest to " + remoteUri);
                System.Console.WriteLine("Unrecognized method " + method.ToUpper());
            }

            HttpWebResponse response = null;
            try
            {
                response = (HttpWebResponse)webRequest.GetResponse();
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
                clientStreamReader.Dispose();
                clientStream.Dispose();
                client.Close();
                return;
            }

            var responseHeaders = HttpUtil.ReadHeaders(response);
            responseHeaders.Add(new Tuple<string, string>("X-Proxied-By", "simple-proxy"));

            System.Console.WriteLine(responseHeaders.Print());

            var responseStream = response.GetResponseStream();
            try
            {
                Task writeTask = null;
                // When StreamWriter is closed, clientStream is also closed.
                StreamWriter responseWriter = null;
                //send the response status and response headers
                try
                {
                    responseWriter = new StreamWriter(clientStream);
                    var status = string.Format("HTTP/1.1 {0} {1}", (Int32)response.StatusCode, response.StatusDescription);
                    responseWriter.WriteLine(status);
                    System.Console.WriteLine(status);

                    foreach (var header in responseHeaders)
                    {
                        responseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                    }
                    responseWriter.WriteLine();
                    responseWriter.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error in writing headers");
                    Console.WriteLine(ex.Message);
                }

                Task serverWriteTask = null;
                
                if (responseHeaders.getAsStringOrElse("WWW-Authenticate", "").Contains("NTLM"))
                {
                    keep = true;
                    //serverWriteTask = clientStream.CopyToAsync(response.GetResponseStream())
                    //        .ContinueWith((t) =>
                    //        {
                    //            System.Console.WriteLine("servver send ends");
                    //        });
                }

                //var connectionHeader = responseHeaders.FirstOrDefault((header) => header.Item1 == "Connection");
                //if (connectionHeader != null)
                //{
                //    System.Console.WriteLine(connectionHeader.Item1 + ": " + connectionHeader.Item2);
                //}
                //else
                //{
                //    System.Console.WriteLine("Connection: (null)");
                //}

                Byte[] buffer;
                if (response.ContentLength > 0)
                {
                    try
                    {
                        writeTask = responseStream.CopyToAsync(clientStream, BUFFER_SIZE);
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine("Error while send response to client\r\n" + ex.Message);
                    }
                }
                else
                {
                    // chunked encoding
                    buffer = new Byte[BUFFER_SIZE];
                    while (true)
                    {
                        var readTask = responseStream.ReadAsync(buffer, 0, BUFFER_SIZE);
                        readTask.Wait();
                        var count = readTask.Result;
                        if (count > 0)
                        {
                            var chunkHead = Encoding.ASCII.GetBytes(count.ToString("x2"));

                            clientStream.WriteAsync(chunkHead, 0, chunkHead.Length);
                            clientStream.WriteAsync(chunkTrail, 0, chunkTrail.Length);
                            clientStream.WriteAsync(buffer, 0, count);
                            clientStream.WriteAsync(chunkTrail, 0, chunkTrail.Length);
                        }
                        else
                        {
                            break;
                        }
                    }

                    writeTask = clientStream.WriteAsync(chunkEnd, 0, chunkEnd.Length);
                }

                if (writeTask != null && !writeTask.IsCompleted)
                {
                    writeTask.Wait();
                }

                if(serverWriteTask != null && !serverWriteTask.IsCompleted)
                {
                    serverWriteTask.Wait();
                }

                try
                {
                    responseStream.Close();
                    response.Close();
                    responseWriter.Close();
                    clientStream.Flush();
                    clientStream.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error in closing connection");
                    Console.WriteLine(ex.Message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error while sending response");
                Console.WriteLine(ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        private static void HandleConnectCmd(TcpClient client, Stream clientStream, StreamReader clientStreamReader, string uri)
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
                System.Console.WriteLine(data);
                data = clientStreamReader.ReadLine();
            }

            System.Console.WriteLine("Tunnel: " + host + ":" + port.ToString());

            // connect to host synchronously
            var tunnelClient = new TcpClient(host, port);
            if (!tunnelClient.Connected)
            {
                return;
            }

            // send response headers to client
            using (var clientStreamWriter = new StreamWriter(clientStream))
            {
                clientStreamWriter.WriteLine("HTTP/1.0 200 Connection established");
                clientStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
                clientStreamWriter.WriteLine("Proxy-agent: simple-proxy");
                clientStreamWriter.WriteLine();
                clientStreamWriter.Flush();
            }

            var tunnelStream = tunnelClient.GetStream();

            System.Console.WriteLine("tunnel start");
            var relayToServer = Task.Factory.StartNew(() =>
            {
                try
                {
#if MANUAL_TUNNEL
                                var loopCancel = new CancellationTokenSource();
                                Task writeTask = null;
                                while (true)
                                {
                                    if (loopCancel.Token.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    var buffer = new byte[BUFFER_SIZE];
                                    var readTask = clientStream.ReadAsync(buffer, 0, BUFFER_SIZE);
                                    try
                                    {
                                        readTask.Wait();
                                    }
                                    catch
                                    {
                                        break;
                                    }
                                    

                                    var count = readTask.Result;
                                    if (count > 0)
                                    {
                                        System.Console.WriteLine("tunnel to " + host + " " + count.ToString() + "bytes");
                                        writeTask = tunnelStream.WriteAsync(buffer, 0, count);
                                    }
                                    else
                                    {
                                        loopCancel.Cancel();
                                        System.Console.WriteLine("tunnel to " + host + " ends");
                                        break;
                                    }

                                }

                                if (writeTask != null && !writeTask.IsCompleted)
                                {
                                    writeTask.Wait();
                                }
#else
                    clientStream.CopyToAsync(tunnelStream, BUFFER_SIZE).Wait();
#endif
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine("Error while tunneling to " + host);
                    System.Console.WriteLine(ex.Message);
                }
            });


            var relayToClient = Task.Factory.StartNew(() =>
            {
                try
                {
#if MANUAL_TUNNEL
                                var loopCancel = new CancellationTokenSource();
                                Task writeTask = null;
                                while (true)
                                {
                                    if (loopCancel.Token.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    var buffer = new byte[BUFFER_SIZE];
                                    var readTask = tunnelStream.ReadAsync(buffer, 0, BUFFER_SIZE);
                                    try
                                    {
                                        readTask.Wait();
                                    }
                                    catch
                                    {
                                        break;
                                    }

                                    var count = readTask.Result;
                                    if (count > 0)
                                    {
                                        System.Console.WriteLine("tunnel from " + host + " " + count.ToString() + "bytes");
                                        writeTask = clientStream.WriteAsync(buffer, 0, count);
                                    }
                                    else
                                    {
                                        loopCancel.Cancel();
                                        System.Console.WriteLine("tunnel from " + host + " ends");
                                        break;
                                    }

                                }

                                if(writeTask != null && !writeTask.IsCompleted)
                                {
                                    writeTask.Wait();
                                }
#else
                    tunnelStream.CopyToAsync(clientStream, BUFFER_SIZE).Wait();
#endif
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine("Error while tunneling from " + host);
                    System.Console.WriteLine(ex.Message);
                }
            });

            var waitCancell = new CancellationTokenSource();
            var closeFromClientTask = relayToClient.ContinueWith((t) =>
            {
                if (waitCancell.Token.IsCancellationRequested)
                {
                    return;
                }
                waitCancell.Cancel();

                System.Console.WriteLine("tunnel close (by client close) " + host);
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

            Task.WaitAll(relayToServer);

            var closeByTimeoutTask = Task.Delay(5 * 1000, waitCancell.Token)
                .ContinueWith((delay) => 
                {
                    if (waitCancell.Token.IsCancellationRequested)
                    {
                        return;
                    }
                    waitCancell.Cancel();

                    System.Console.WriteLine("tunnel close (by timeout) " + host);

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

            Task.WaitAny(closeFromClientTask, closeByTimeoutTask);

        }
    }
}
