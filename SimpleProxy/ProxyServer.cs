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
            ServicePointManager.DefaultConnectionLimit = ServicePointManager.DefaultPersistentConnectionLimit;
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

    }
}
