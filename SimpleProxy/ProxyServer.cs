using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleProxy
{
    public class ProxyServer
    {
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] semiSplit = new char[] { ';' };
        private static readonly char[] equalSplit = new char[] { '=' };
        private static readonly String[] colonSpaceSplit = new string[] { ": " };
        private static readonly char[] spaceSplit = new char[] { ' ' };
        private static readonly char[] commaSplit = new char[] { ',' };
        private static readonly Regex cookieSplitRegEx = new Regex(@",(?! )");
        private static readonly byte[] ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);
        private static readonly byte[] ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);


        private TcpListener tcpListener;
        private Task listenerTask;
        private CancellationTokenSource listenerTaskCancel;

        public IPAddress ListeningIPInterface
        {
            get
            {
                IPAddress addr = IPAddress.Loopback;
                return addr;
            }
        }

        public int ListeningPort
        {
            get
            {
                var port = 8081;
                return port;
            }
        }

        public ProxyServer()
        {
            tcpListener = new TcpListener(ListeningIPInterface, ListeningPort);
            // ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        }

        public Boolean DumpHeaders { get; set; }
        public Boolean DumpPostData { get; set; }
        public Boolean DumpResponseData { get; set; }

        class ListenerThreadStartParam
        {
            public TcpListener listner { get; set; }
            public ProxyServer self { get; set; }
        }

        public bool Start()
        {
            try
            {
                tcpListener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }

            listenerTaskCancel = new CancellationTokenSource();
            listenerTask = Task.Factory.StartNew(() => {
                Listen(this);
            }, listenerTaskCancel.Token);

            return true;
        }

        public void Stop()
        {
            tcpListener.Stop();

            listenerTaskCancel.Cancel();
            listenerTask.Wait();
            listenerTask.Dispose();
            listenerTask = null;
            listenerTaskCancel.Dispose();
            listenerTaskCancel = null;
        }

        class ProxyClient
        {
            public TcpClient tcpClient { get; set; }
            public ProxyServer self { get; set; }
        }

        private static void Listen(Object obj)
        {
            var self = (ProxyServer)obj;
            TcpListener listener = self.tcpListener;
            var taskFactory = new TaskFactory();

            System.Console.WriteLine("Listen Start");
            try
            {
                while (true)
                {
                    if (self.listenerTaskCancel.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    var client = listener.AcceptTcpClient();

                    var param = new ProxyClient()
                    {
                        tcpClient = client,
                        self = self
                    };
                    taskFactory.StartNew(ProcessClient, param, self.listenerTaskCancel.Token);
                }
            }
            catch (SocketException) {
            }
        }

        private static void ProcessClient(Object obj)
        {
            ProxyClient client = (ProxyClient)obj;
            try
            {
                System.Console.WriteLine("connection");
                RequestHandler.processHttp(client.tcpClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unhandled Exception");
                Console.WriteLine(ex.Message);
            }
            finally
            {
                client.tcpClient.Close();
            }
        }

        #region deprecated
        private void ProcessHttp(TcpClient client)
        {
            Stream clientStream = client.GetStream();
            Stream outStream = clientStream;
            StreamReader clientStreamReader = new StreamReader(clientStream);

            try
            {
                //read the first line HTTP command
                String httpCmd = clientStreamReader.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                {
                    clientStreamReader.Close();
                    clientStream.Close();
                    return;
                }
                //break up the line into three components
                String[] splitBuffer = httpCmd.Split(spaceSplit, 3);

                String method = splitBuffer[0];
                String remoteUri = splitBuffer[1];
                Version version = new Version(1, 1);

                System.Console.WriteLine("Method: " + method);

                HttpWebRequest webReq;
                HttpWebResponse response = null;
                if (splitBuffer[0].ToUpper() == "CONNECT")
                {
                    //Browser wants to create a secure tunnel
                    //instead = we are going to perform a man in the middle "attack"
                    //the user's browser should warn them of the certification errors however.
                    //Please note: THIS IS ONLY FOR TESTING PURPOSES - you are responsible for the use of this code
                    remoteUri = "https://" + splitBuffer[1];
                    var data = clientStreamReader.ReadLine();
                    while (!string.IsNullOrEmpty(data))
                    {
                        System.Console.WriteLine(data);
                        data = clientStreamReader.ReadLine();
                    }

                    var splitBuffer2 = splitBuffer[1].Split(':');
                    var host = splitBuffer2[0];
                    var port = int.Parse(splitBuffer2[1]);
                    System.Console.WriteLine("Tunnel: " + host + ":" + port.ToString());
                    var tunnelClient = new TcpClient(host, port);
                    System.Console.WriteLine("Connected: " + tunnelClient.Connected.ToString());
                    var tunnelStream = tunnelClient.GetStream();

                    StreamWriter connectStreamWriter = new StreamWriter(clientStream);
                    connectStreamWriter.WriteLine("HTTP/1.0 200 Connection established");
                    connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
                    connectStreamWriter.WriteLine("Proxy-agent: matt-dot-net");
                    connectStreamWriter.WriteLine();
                    connectStreamWriter.Flush();

                    SslStream sslStream = null;
                    try
                    {
                        //sslStream = new SslStream(tunnelStream);
                        //sslStream.AuthenticateAsClient(host);
                        // tunnelStream = sslStream;
                        System.Console.WriteLine("tunnel start");
                        var relayToServer = Task.Factory.StartNew(() => {
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

                        
                        var relayToClient = Task.Factory.StartNew(()=> {
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
                        relayToClient.ContinueWith((t) =>
                        {
                            if (waitCancell.Token.IsCancellationRequested)
                            {
                                return;
                            }
                            waitCancell.Cancel();

                            System.Console.WriteLine("tunnel close (by client close) " + host);

                            if(clientStream != null)
                            {
                                try
                                {
                                    clientStream.Dispose();
                                    client.Close();
                                    tunnelStream.Dispose();
                                    tunnelClient.Close();
                                }
                                catch(Exception ex)
                                {
                                    System.Console.WriteLine("Error while closing connection");
                                    System.Console.WriteLine(ex.Message);
                                }
                            }
                        }, waitCancell.Token);

                        Task.WaitAll(relayToServer);

                        Task.Delay(5*1000, waitCancell.Token).ContinueWith((delay)=>{
                            if (waitCancell.Token.IsCancellationRequested)
                            {
                                return;
                            }
                            waitCancell.Cancel();

                            System.Console.WriteLine("tunnel close (by timeout) " + host);

                            if (clientStream != null)
                            {
                                try
                                {
                                    clientStream.Dispose();
                                    client.Close();
                                    tunnelStream.Dispose();
                                    tunnelClient.Close();
                                }
                                catch (Exception ex)
                                {
                                    System.Console.WriteLine("Error while closing connection");
                                    System.Console.WriteLine(ex.Message);
                                }
                            }
                        });

                    }
                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();

                        throw;
                    }
                    return;
                }

                //construct the web request that we are going to issue on behalf of the client.
                // System.Console.WriteLine("Create Connection to " + remoteUri);
                webReq = (HttpWebRequest)HttpWebRequest.Create(remoteUri);
                webReq.Method = method;
                webReq.ProtocolVersion = version;

                //read the request headers from the client and copy them to our request
                int contentLen = ReadRequestHeaders(clientStreamReader, webReq);

                webReq.Proxy = null;
                webReq.KeepAlive = false;
                webReq.AllowAutoRedirect = true;
                webReq.AutomaticDecompression = DecompressionMethods.None;


                //if (this.DumpHeaders)
                //{
                //    Console.WriteLine(String.Format("{0} {1} HTTP/{2}", webReq.Method, webReq.RequestUri.AbsoluteUri, webReq.ProtocolVersion));
                //    DumpHeaderCollectionToConsole(webReq.Headers);
                //}

                //using the completed request, check our cache
                if (method.ToUpper() == "GET")
                {
                    // cacheEntry = ProxyCache.GetData(webReq);
                }
                else if (method.ToUpper() == "POST")
                {
                    char[] postBuffer = new char[contentLen];
                    int bytesRead;
                    int totalBytesRead = 0;
                    StreamWriter sw = new StreamWriter(webReq.GetRequestStream());
                    while (totalBytesRead < contentLen && (bytesRead = clientStreamReader.ReadBlock(postBuffer, 0, contentLen)) > 0)
                    {
                        totalBytesRead += bytesRead;
                        sw.Write(postBuffer, 0, bytesRead);
                        if (this.DumpPostData)
                            Console.Write(postBuffer, 0, bytesRead);
                    }
                    if (this.DumpPostData)
                    {
                        Console.WriteLine();
                        Console.WriteLine();
                    }

                    sw.Close();
                }
                else
                {
                    System.Console.WriteLine("Error while reuqest to " + remoteUri);
                    System.Console.WriteLine("Unrecognized method " + method.ToUpper());
                }

                // if (cacheEntry == null)
                {
                    //Console.WriteLine(String.Format("ThreadID: {2} Requesting {0} on behalf of client {1}", webReq.RequestUri, client.Client.RemoteEndPoint.ToString(), Thread.CurrentThread.ManagedThreadId));
                    webReq.Timeout = 15000;

                    try
                    {
                        response = (HttpWebResponse)webReq.GetResponse();
                    }
                    catch (WebException webEx)
                    {
                        System.Console.WriteLine(webEx.Message);
                        response = webEx.Response as HttpWebResponse;
                    }

                    if (response != null)
                    {
                        List<Tuple<String, String>> responseHeaders = ProcessResponse(response);
                        StreamWriter myResponseWriter = new StreamWriter(outStream);
                        Stream responseStream = response.GetResponseStream();
                        try
                        {
                            Task writeTask = null;

                            //send the response status and response headers
                            try
                            {
                                WriteResponseStatus(response.StatusCode, response.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders);
                            }
                            catch(Exception ex)
                            {
                                Console.WriteLine("Error in writing headers");
                                Console.WriteLine(ex.Message);
                            }

                            var transferEncodingHeader = responseHeaders.FirstOrDefault((header) => header.Item1 == "Transfer-Encoding");
                            if(transferEncodingHeader != null)
                            {
                                System.Console.WriteLine(transferEncodingHeader.Item1 + ": " + transferEncodingHeader.Item2);
                            }

                            var connectionHeader = responseHeaders.FirstOrDefault((header) => header.Item1 == "Connection");
                            if (connectionHeader != null)
                            {
                                System.Console.WriteLine(connectionHeader.Item1 + ": " + connectionHeader.Item2);
                            }
                            else
                            {
                                System.Console.WriteLine("Connection: (null)");
                            }

                            Byte[] buffer;
                            if (response.ContentLength > 0)
                            {
                                //buffer = new Byte[response.ContentLength];

                                //int bytesRead;
                                //while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                                //{
                                //    outStream.Write(buffer, 0, bytesRead);
                                //}
                                try
                                {
                                    writeTask = responseStream.CopyToAsync(outStream, BUFFER_SIZE);
                                }
                                catch(Exception ex)
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
                                    if(count > 0)
                                    {
                                        var chunkHead = Encoding.ASCII.GetBytes(count.ToString("x2"));

                                        outStream.WriteAsync(chunkHead, 0, chunkHead.Length);
                                        outStream.WriteAsync(ChunkTrail, 0, ChunkTrail.Length);
                                        outStream.WriteAsync(buffer, 0, count);
                                        outStream.WriteAsync(ChunkTrail, 0, ChunkTrail.Length);
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }

                                writeTask = outStream.WriteAsync(ChunkEnd, 0, ChunkEnd.Length);
                            }

                            if (writeTask != null && !writeTask.IsCompleted)
                            {
                                writeTask.Wait();
                            }

                            try
                            {
                                responseStream.Close();
                                outStream.Flush();
                            }
                            catch(Exception ex)
                            {
                                Console.WriteLine("Error in closing connection");
                                Console.WriteLine(ex.Message);
                            }
                            //if (canCache)
                            //    ProxyCache.AddData(entry);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error while sending response");
                            Console.WriteLine(ex.Message);
                        }
                        finally
                        {
                            responseStream.Close();
                            response.Close();
                            myResponseWriter.Close();
                        }
                    }
                    else
                    {
                        System.Console.WriteLine("Resposne is null");
                    }
                }
                //else
                //{
                //    //serve from cache
                //    StreamWriter myResponseWriter = new StreamWriter(outStream);
                //    try
                //    {
                //        WriteResponseStatus(cacheEntry.StatusCode, cacheEntry.StatusDescription, myResponseWriter);
                //        WriteResponseHeaders(myResponseWriter, cacheEntry.Headers);
                //        if (cacheEntry.ResponseBytes != null)
                //        {
                //            outStream.Write(cacheEntry.ResponseBytes, 0, cacheEntry.ResponseBytes.Length);
                //            if (ProxyServer.Server.DumpResponseData)
                //                Console.Write(UTF8Encoding.UTF8.GetString(cacheEntry.ResponseBytes));
                //        }
                //        myResponseWriter.Close();
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine(ex.Message);
                //    }
                //    finally
                //    {
                //        myResponseWriter.Close();
                //    }
                //}
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                if (this.DumpHeaders || this.DumpPostData || this.DumpResponseData)
                {
                    //release the lock
                    //Monitor.Exit(_outputLockObj);
                }

                clientStreamReader.Close();
                clientStream.Close();
                //if (sslStream != null)
                //    sslStream.Close();
                outStream.Close();
            }

        }
        #endregion

        private static List<Tuple<String, String>> ProcessResponse(HttpWebResponse response)
        {
            String value = null;
            String header = null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();
            foreach (String s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }
            returnHeaders.Add(new Tuple<String, String>("X-Proxied-By", "matt-dot-net proxy"));
            return returnHeaders;
        }

        private void WriteResponseStatus(HttpStatusCode code, String description, StreamWriter myResponseWriter)
        {
            String s = String.Format("HTTP/1.1 {0} {1}", (Int32)code, description);
            
            //if (this.DumpHeaders)
            //    Console.WriteLine(s);
            myResponseWriter.WriteLine(s);
        }

        private void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<String, String>> headers)
        {
            if (headers != null)
            {
                foreach (Tuple<String, String> header in headers)
                {
                    myResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                }
                    
            }

            //if (this.DumpHeaders)
            //    DumpHeaderCollectionToConsole(headers);

            myResponseWriter.WriteLine();
            myResponseWriter.Flush();


        }

        private static void DumpHeaderCollectionToConsole(WebHeaderCollection headers)
        {
            foreach (String s in headers.AllKeys)
                Console.WriteLine(String.Format("{0}: {1}", s, headers[s]));
            Console.WriteLine();
        }

        private static void DumpHeaderCollectionToConsole(List<Tuple<String, String>> headers)
        {
            foreach (Tuple<String, String> header in headers)
                Console.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
            Console.WriteLine();
        }

        private static int ReadRequestHeaders(StreamReader sr, HttpWebRequest webReq)
        {
            String httpCmd;
            int contentLen = 0;
            do
            {
                httpCmd = sr.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                    return contentLen;
                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                switch (header[0].ToLower())
                {
                    case "host":
                        webReq.Host = header[1];
                        break;
                    case "user-agent":
                        webReq.UserAgent = header[1];
                        break;
                    case "accept":
                        webReq.Accept = header[1];
                        break;
                    case "referer":
                        webReq.Referer = header[1];
                        break;
                    case "cookie":
                        webReq.Headers["Cookie"] = header[1];
                        break;
                    case "proxy-connection":
                    case "connection----":
                    case "keep-alive":
                        //ignore these
                        break;
                    case "content-length":
                        int.TryParse(header[1], out contentLen);
                        break;
                    case "content-type":
                        webReq.ContentType = header[1];
                        break;
                    case "if-modified-since":
                        String[] sb = header[1].Trim().Split(semiSplit);
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webReq.IfModifiedSince = d;
                        break;
                    case "upgrade-insecure-requests":
                        break;
                    default:
                        try
                        {
                            webReq.Headers.Add(header[0], header[1]);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(String.Format("Could not add header {0} : {1}.  Exception message:{2}", header[0], header[1], ex.Message));
                        }
                        break;
                }
            } while (!String.IsNullOrWhiteSpace(httpCmd));
            return contentLen;
        }
    }
}
