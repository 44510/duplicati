// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HttpServer.HttpModules;

using Duplicati.Server.WebServer.RESTMethods;
using Duplicati.Library.RestAPI;

namespace Duplicati.Server.WebServer
{
    public class RESTHandler : HttpModule
    {
        public const string API_URI_PATH = "/api/v1";
        public static readonly int API_URI_SEGMENTS = API_URI_PATH.Split(new char[] {'/'}).Length;

        private static readonly Dictionary<string, IRESTMethod> _modules = new Dictionary<string, IRESTMethod>(StringComparer.OrdinalIgnoreCase);

        public static IDictionary<string, IRESTMethod> Modules { get { return _modules; } }

        /// <summary>
        /// Loads all REST modules in the Duplicati.Server.WebServer.RESTMethods namespace
        /// </summary>
        static RESTHandler()
        {
            var lst = 
                from n in typeof(IRESTMethod).Assembly.GetTypes()
                where
                    n.Namespace == typeof(IRESTMethod).Namespace
                    &&
                    typeof(IRESTMethod).IsAssignableFrom(n)
                    &&
                    !n.IsAbstract
                    &&
                    !n.IsInterface
                select n;

            foreach(var t in lst)
            {
                var m = (IRESTMethod)Activator.CreateInstance(t);
                _modules.Add(t.Name.ToLowerInvariant(), m);
            }
        }

        public static void HandleControlCGI(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, Type module)
        {
            var method = request.Method;
            if (!string.IsNullOrWhiteSpace(request.Headers["X-HTTP-Method-Override"]))
                method = request.Headers["X-HTTP-Method-Override"];
            
            DoProcess(request, response, session, method, module.Name.ToLowerInvariant(), (String.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) ? request.Form : request.QueryString)["id"].Value);
        }

        private static readonly ConcurrentDictionary<string, System.Globalization.CultureInfo> _cultureCache = new ConcurrentDictionary<string, System.Globalization.CultureInfo>(StringComparer.OrdinalIgnoreCase);

        private static System.Globalization.CultureInfo ParseRequestCulture(RequestInfo info)
        {
            // Inject the override
            return ParseRequestCulture(string.Format("{0},{1}", info.Request.Headers["X-UI-Language"], info.Request.Headers["Accept-Language"]));
        }

        public static System.Globalization.CultureInfo ParseDefaultRequestCulture(RequestInfo info)
        {
            if (info == null)
                return null;
            return ParseRequestCulture(info.Request.Headers["Accept-Language"]);
        }

        private static System.Globalization.CultureInfo ParseRequestCulture(string acceptheader)
        {
            acceptheader = acceptheader ?? string.Empty;

            // Lock-free read
            System.Globalization.CultureInfo ci;
            if (_cultureCache.TryGetValue(acceptheader, out ci))
                return ci;

            // Lock-free assignment, we might compute the value twice
            return _cultureCache[acceptheader] =
                // Parse headers like "Accept-Language: da, en-gb;q=0.8, en;q=0.7"
                acceptheader
                .Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x =>
                {
                    var opts = x.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries);
                    var lang = opts.FirstOrDefault();
                    var weight =
                    opts.Where(y => y.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                        .Select(y =>
                        {
                            float f;
                            float.TryParse(y.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);
                            return f;
                        }).FirstOrDefault();

                    // Set the default weight=1
                    if (weight <= 0.001 && weight >= 0)
                        weight = 1;

                    return new KeyValuePair<string, float>(lang, weight);
                })
                // Handle priority
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)
                .Distinct()
                // Filter invalid/unsupported items
                .Where(x => !string.IsNullOrWhiteSpace(x) && Library.Localization.LocalizationService.ParseCulture(x) != null)
                .Select(x => Library.Localization.LocalizationService.ParseCulture(x))
                // And get the first that works
                .FirstOrDefault();

        }

        public static void DoProcess(RequestInfo info, string method, string module, string key)
        {
            var ci = ParseRequestCulture(info);

            using (Library.Localization.LocalizationService.TemporaryContext(ci))
            {
                try
                {
                    if (ci != null)
                        info.Response.AddHeader("Content-Language", ci.Name);

                    IRESTMethod mod;
                    _modules.TryGetValue(module, out mod);

                    if (mod == null)
                    {
                        info.Response.Status = System.Net.HttpStatusCode.NotFound;
                        info.Response.Reason = "No such module";
                    }
                    else if (method == HttpServer.Method.Get && mod is IRESTMethodGET get)
                    {
                        if (info.Request.Form != HttpServer.HttpForm.EmptyForm)
                        {
                            if (info.Request.QueryString == HttpServer.HttpInput.Empty)
                            {
                                var r = info.Request.GetType().GetField("_queryString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                r.SetValue(info.Request, new HttpServer.HttpInput("formdata"));
                            }

                            foreach (HttpServer.HttpInputItem v in info.Request.Form)
                                if (!info.Request.QueryString.Contains(v.Name))
                                    info.Request.QueryString.Add(v.Name, v.Value);
                        }

                        get.GET(key, info);
                    }
                    else if (method == HttpServer.Method.Put && mod is IRESTMethodPUT put)
                        put.PUT(key, info);
                    else if (method == HttpServer.Method.Post && mod is IRESTMethodPOST post)
                    {
                        if (info.Request.Form == HttpServer.HttpForm.EmptyForm || info.Request.Form == HttpServer.HttpInput.Empty)
                        {
                            var r = info.Request.GetType().GetMethod("AssignForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new Type[] {typeof(HttpServer.HttpForm)}, null);
                            r.Invoke(info.Request, new object[] {new HttpServer.HttpForm(info.Request.QueryString)});
                        }
                        else
                        {
                            foreach (HttpServer.HttpInputItem v in info.Request.QueryString)
                                if (!info.Request.Form.Contains(v.Name))
                                    info.Request.Form.Add(v.Name, v.Value);
                        }

                        post.POST(key, info);
                    }
                    else if (method == HttpServer.Method.Delete && mod is IRESTMethodDELETE delete)
                        delete.DELETE(key, info);
                    else if (method == "PATCH" && mod is IRESTMethodPATCH patch)
                        patch.PATCH(key, info);
                    else
                    {
                        info.Response.Status = System.Net.HttpStatusCode.MethodNotAllowed;
                        info.Response.Reason = "Method is not allowed";
                    }
                }
                catch (Exception ex)
                {
                    FIXMEGlobal.DataConnection.LogError("", string.Format("Request for {0} gave error", info.Request.Uri), ex);
                    Console.WriteLine(ex);

                    try
                    {
                        if (!info.Response.HeadersSent)
                        {
                            info.Response.Status = System.Net.HttpStatusCode.InternalServerError;
                            info.Response.Reason = "Error";
                            info.Response.ContentType = "text/plain";

                            var wex = ex;
                            while (wex is System.Reflection.TargetInvocationException && wex.InnerException != wex)
                                wex = wex.InnerException;

                            info.BodyWriter.WriteJsonObject(new
                            {
                                Message = wex.Message,
                                Type = wex.GetType().Name,
#if DEBUG
                                Stacktrace = wex.ToString()
#endif
                            });
                            info.BodyWriter.Flush();
                        }
                    }
                    catch (Exception flex)
                    {
                        FIXMEGlobal.DataConnection.LogError("", "Reporting error gave error", flex);
                    }
                }
            }
        }

        public static void DoProcess(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session, string method, string module, string key)
        {
            using(var reqinfo = new RequestInfo(request, response, session))
                DoProcess(reqinfo, method, module, key);
        }
            
        public override bool Process(HttpServer.IHttpRequest request, HttpServer.IHttpResponse response, HttpServer.Sessions.IHttpSession session)
        {
            if (!request.Uri.AbsolutePath.StartsWith(API_URI_PATH, StringComparison.OrdinalIgnoreCase))
                return false;

            var module = request.Uri.Segments.Skip(API_URI_SEGMENTS).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(module))
                module = "help";

            module = module.Trim('/');

            var key = string.Join("", request.Uri.Segments.Skip(API_URI_SEGMENTS + 1)).Trim('/');

            var method = request.Method;
            if (!string.IsNullOrWhiteSpace(request.Headers["X-HTTP-Method-Override"]))
                method = request.Headers["X-HTTP-Method-Override"];

            DoProcess(request, response, session, method, module, key);

            return true;
        }
    }
}

