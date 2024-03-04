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
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Duplicati.Server.WebServer.RESTMethods
{
    public class RemoteOperation : IRESTMethodGET, IRESTMethodPOST
    {
        private void LocateDbUri(string uri, RequestInfo info)
        {
            var path = Library.Main.DatabaseLocator.GetDatabasePath(uri, null, false, false);
            info.OutputOK(new
            {
                Exists = !string.IsNullOrWhiteSpace(path),
                Path = path
            });
        }

        private void CreateFolder(string uri, RequestInfo info)
        {
            using (var b = Duplicati.Library.DynamicLoader.BackendLoader.GetBackend(uri, new Dictionary<string, string>()))
                b.CreateFolder();

            info.OutputOK();
        }

        private void UploadFile(string uri, RequestInfo info)
        {
            var data = info.Request.QueryString["data"].Value;
            var remotename = info.Request.QueryString["filename"].Value;

            using (var ms = new System.IO.MemoryStream())
            using (var b = Library.DynamicLoader.BackendLoader.GetBackend(uri, new Dictionary<string, string>()))
            {
                using (var tf = new Library.Utility.TempFile())
                {
                    System.IO.File.WriteAllText(tf, data);
                    b.PutAsync(remotename, tf, CancellationToken.None).Wait();
                }
            }

            info.OutputOK();
        }

        private void ListFolder(string uri, RequestInfo info)
        {
            using (var b = Duplicati.Library.DynamicLoader.BackendLoader.GetBackend(uri, new Dictionary<string, string>()))
                info.OutputOK(b.List());
        }

        private void TestConnection(string url, RequestInfo info)
        {

            var modules = (from n in Library.DynamicLoader.GenericLoader.Modules
                           where n is Library.Interface.IConnectionModule
                           select n).ToArray();

            try
            {
                var uri = new Library.Utility.Uri(url);
                var qp = uri.QueryParameters;

                var opts = new Dictionary<string, string>();
                foreach (var k in qp.Keys.Cast<string>())
                    opts[k] = qp[k];

                foreach (var n in modules)
                    n.Configure(opts);

                using (var b = Duplicati.Library.DynamicLoader.BackendLoader.GetBackend(url, new Dictionary<string, string>()))
                    b.Test();

                info.OutputOK();
            }
            catch (Duplicati.Library.Interface.FolderMissingException)
            {
                info.ReportServerError("missing-folder");
            }
            catch (Duplicati.Library.Utility.SslCertificateValidator.InvalidCertificateException icex)
            {
                if (string.IsNullOrWhiteSpace(icex.Certificate))
                    info.ReportServerError(icex.Message);
                else
                    info.ReportServerError("incorrect-cert:" + icex.Certificate);
            }
            catch (Duplicati.Library.Utility.HostKeyException hex)
            {
                if (string.IsNullOrWhiteSpace(hex.ReportedHostKey))
                    info.ReportServerError(hex.Message);
                else
                {
                    info.ReportServerError(string.Format(
                        @"incorrect-host-key:""{0}"", accepted-host-key:""{1}""",
                        hex.ReportedHostKey,
                        hex.AcceptedHostKey
                    ));
                }
            }
            finally
            {
                foreach (var n in modules)
                    if (n is IDisposable disposable)
                        disposable.Dispose();
            }
        }

        public void GET(string key, RequestInfo info)
        {
            var parts = (key ?? "").Split(new char[] { '/' }, 2);

            if (parts.Length <= 1)
            {
                info.ReportClientError("No url or operation supplied", System.Net.HttpStatusCode.BadRequest);
                return;
            }

            var url = Library.Utility.Uri.UrlDecode(parts.First());
            var operation = parts.Last().ToLowerInvariant();

            switch (operation)
            {
                case "dbpath":
                    LocateDbUri(url, info);
                    return;
                case "list":
                    ListFolder(url, info);
                    return;
                case "create":
                    CreateFolder(url, info);
                    return;
                case "test":
                    TestConnection(url, info);
                    return;
                default:
                    info.ReportClientError("No such method", System.Net.HttpStatusCode.BadRequest);
                    return;
            }
        }

        public void POST(string key, RequestInfo info)
        {
            string url = System.Threading.Tasks.Task.Run(async () =>
            {
                using (var sr = new System.IO.StreamReader(info.Request.Body, System.Text.Encoding.UTF8, true))

                   return await sr.ReadToEndAsync();
            }).GetAwaiter().GetResult();

            switch (key)
            {
                case "dbpath":
                    LocateDbUri(url, info);
                    return;
                case "list":
                    ListFolder(url, info);
                    return;
                case "create":
                    CreateFolder(url, info);
                    return;
                case "put":
                    UploadFile(url, info);
                    return;
                case "test":
                    TestConnection(url, info);
                    return;
                default:
                    info.ReportClientError("No such method", System.Net.HttpStatusCode.BadRequest);
                    return;
            }
        }
    }
}

