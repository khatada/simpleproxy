using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleProxy
{
    class HttpUtil
    {
        private static readonly string[] colonSpaceSplit = new string[] { ": " };
        private static readonly char[] semiSplit = new char[] { ';' };
        private static readonly char[] rangeSplit = new char[] { ',', '=' };
        private static readonly char[] hyphenSplit = new char[] { '-' };
        private static readonly Regex cookieSplitRegEx = new Regex(@",(?! )");

        public class Headers : List<Tuple<string, string>>
        {
            public int getAsIntOrElse(string name, int elseValue){
                var header = this.FirstOrDefault((h) => h.Item1.ToLower() == name);
                if (header == null)
                {
                    return elseValue;
                }

                try
                {
                    return int.Parse(header.Item2);
                }
                catch
                {
                    return elseValue;
                }
            }

            public string getAsStringOrElse(string name, string elseValue)
            {
                var header = this.FirstOrDefault((h) => h.Item1.ToLower() == name);
                if (header == null)
                {
                    return elseValue;
                }

                return header.Item2;
            }

            public DateTime getAsDateTimeOrElse(string name, DateTime elseValue)
            {
                var header = this.FirstOrDefault((h) => h.Item1.ToLower() == name);
                if (header == null)
                {
                    return elseValue;
                }

                try
                {
                    return DateTime.Parse(header.Item2.Trim().Split(semiSplit)[0]);
                }
                catch
                {
                    return elseValue;
                }
            }

            public string Print()
            {
                var sb = new StringBuilder();
                foreach (var header in this)
                {
                    sb.Append(string.Format("{0}: {1}\r\n", header.Item1, header.Item2));
                }
                return sb.ToString();
            }

            public void ApplyTo(HttpWebRequest webRequest)
            {
                foreach (var header in this)
                {
                    switch (header.Item1.ToLower())
                    {
                        case "host":
                            webRequest.Host = header.Item2;
                            break;
                        case "user-agent":
                            webRequest.UserAgent = header.Item2;
                            break;
                        case "accept":
                            webRequest.Accept = header.Item2;
                            break;
                        case "referer":
                            webRequest.Referer = header.Item2;
                            break;
                        case "cookie":
                            webRequest.Headers["Cookie"] = header.Item2;
                            break;
                        case "proxy-connection":
                            break;
                        case "connection":
                            // webRequest.Connection = header.Item2;
                            break;
                        case "keep-alive":
                            // webRequest.KeepAlive = bool.Parse(header.Item2);
                            break;
                        case "content-length":
                            webRequest.ContentLength = int.Parse(header.Item2);
                            break;
                        case "content-type":
                            webRequest.ContentType = header.Item2;
                            break;
                        case "if-modified-since":
                            var sb = header.Item2.Trim().Split(semiSplit);
                            DateTime d;
                            if (DateTime.TryParse(sb[0], out d))
                            {
                                webRequest.IfModifiedSince = d;
                            }  
                            break;
                        case "upgrade-insecure-requests":
                            break;
                        case "range":
                            var rangeTok = header.Item2.Trim().Split(rangeSplit);
                            for(var i=0; i<rangeTok.Length; i++)
                            {
                                if(i == 0)
                                {
                                    continue;
                                }
                                var tok = rangeTok[i].Trim().Split(hyphenSplit, StringSplitOptions.None);
                                if(tok.Length == 2)
                                {
                                    int start, end;
                                    if (string.IsNullOrEmpty(tok[0]))
                                    {
                                        if(int.TryParse(tok[1], out end))
                                        {
                                            webRequest.AddRange(-end);
                                        }
                                    }
                                    else if (string.IsNullOrEmpty(tok[1]))
                                    {
                                        if (int.TryParse(tok[0], out start))
                                        {
                                            webRequest.AddRange(-start);
                                        }
                                    }
                                    else
                                    {
                                        if (int.TryParse(tok[0], out start) && int.TryParse(tok[1], out end))
                                        {
                                            webRequest.AddRange(start, end);
                                        }
                                    }
                                }
                            }
                            
                            break;
                        default:
                            try
                            {
                                webRequest.Headers.Add(header.Item1, header.Item2);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(String.Format("Could not add header {0} : {1}.  Exception message:{2}", header.Item1, header.Item2, ex.Message));
                            }
                            break;
                    }
                }
            }
        }

        public static Headers ReadHeaders(StreamReader sr)
        {
            var headers = new Headers();
            while (true)
            {
                var httpCmd = sr.ReadLine();
                if (string.IsNullOrWhiteSpace(httpCmd))
                {
                    break;
                }
                var header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                if (header.Length == 2)
                {
                    headers.Add(new Tuple<string, string>(header[0], header[1]));
                }
            }

            return headers;
        }

        public static Headers ReadHeaders(HttpWebResponse response)
        {
            string value = null;
            string header = null;
            var headers = new Headers();

            foreach (string s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else
                {
                    headers.Add(new Tuple<String, String>(s, response.Headers[s]));
                }
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                var cookies = cookieSplitRegEx.Split(value);
                foreach (string cookie in cookies)
                {
                    headers.Add(new Tuple<String, String>("Set-Cookie", cookie));
                }
            }
            
            return headers;
        }
    }
}
