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
using Duplicati.Library.RestAPI;
using System;

namespace Duplicati.Server.WebServer.RESTMethods
{
    public class Updates : IRESTMethodPOST
    {
        public void POST(string key, RequestInfo info)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "check":
                    FIXMEGlobal.UpdatePoller.CheckNow();
                    info.OutputOK();
                    return;

                case "install":
                    FIXMEGlobal.UpdatePoller.InstallUpdate();
                    info.OutputOK();
                    return;

                case "activate":
                    if (FIXMEGlobal.WorkThread.CurrentTask != null || FIXMEGlobal.WorkThread.CurrentTasks.Count != 0)
                    {
                        info.ReportServerError("Cannot activate update while task is running or scheduled");
                    }
                    else
                    {
                        FIXMEGlobal.UpdatePoller.ActivateUpdate();
                        info.OutputOK();
                    }
                    return;
                
                default:
                    info.ReportClientError("No such action", System.Net.HttpStatusCode.NotFound);
                    return;
            }
        }
    }
}
