// Copyright (C) 2025, The Duplicati Team
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

using Duplicati.Library.Interface;
using Duplicati.Library.Main;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.RestAPI;
using Duplicati.Server;
using Duplicati.Server.Database;
using Duplicati.WebserverCore.Abstractions;
using Duplicati.WebserverCore.Dto.V2;
using Microsoft.AspNetCore.Mvc;

namespace Duplicati.WebserverCore.Endpoints.V2.Backup;

public class DestinationVerify : IEndpointV2
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/destination/test", ([FromServices] Connection connection, [FromBody] Dto.V2.DestinationTestRequestDto input, CancellationToken cancelToken)
            => ExecuteTest(input, cancelToken))
            .RequireAuthorization();
    }

    private record TupleDisposeWrapper(IBackend Backend, IEnumerable<IGenericModule> Modules) : IDisposable
    {
        public void Dispose()
        {
            Backend.Dispose();
            foreach (var n in Modules)
                if (n is IDisposable disposable)
                    disposable.Dispose();
        }
    }

    private static Dictionary<string, string?> ParseUrlOptions(Library.Utility.Uri uri)
    {
        var qp = uri.QueryParameters;

        var opts = Runner.GetCommonOptions();
        foreach (var k in qp.Keys.Cast<string>())
            opts[k] = qp[k];

        return opts;
    }

    private static IEnumerable<IGenericModule> ConfigureModules(IDictionary<string, string?> opts)
    {
        // TODO: This works because the generic modules are implemented
        // with pre .NetCore logic, using static methods
        // The modules are created to allow multipe dispose,
        // which violates the .Net patterns
        var modules = Library.DynamicLoader.GenericLoader.Modules.OfType<IConnectionModule>().ToArray();
        foreach (var n in modules)
            n.Configure(opts);

        return modules;
    }

    private static async Task<TupleDisposeWrapper> GetBackend(string url, CancellationToken cancelToken)
    {
        var uri = new Library.Utility.Uri(url);
        var opts = ParseUrlOptions(uri);

        var tmp = new[] { uri };
        await SecretProviderHelper.ApplySecretProviderAsync([], tmp, opts, Library.Utility.TempFolder.SystemTempPath, FIXMEGlobal.SecretProvider, cancelToken);
        url = tmp[0].ToString();

        var modules = ConfigureModules(opts);
        var backend = Library.DynamicLoader.BackendLoader.GetBackend(url, new Dictionary<string, string>());
        return new TupleDisposeWrapper(backend, modules);
    }


    private static async Task<DestinationTestResponseDto> ExecuteTest(DestinationTestRequestDto input, CancellationToken cancelToken)
    {
        TupleDisposeWrapper? wrapper = null;

        try
        {
            wrapper = await GetBackend(input.DestinationUrl, cancelToken);

            using (var b = wrapper.Backend)
            {
                try { await b.TestAsync(cancelToken).ConfigureAwait(false); }
                catch (FolderMissingException)
                {
                    if (!input.AutoCreate)
                        throw;

                    await b.CreateFolderAsync(cancelToken).ConfigureAwait(false);
                    await b.TestAsync(cancelToken).ConfigureAwait(false);
                }

                var anyFiles = false;
                var anyBackups = false;
                var anyEncryptedFiles = false;
                await foreach (var f in b.ListAsync(cancelToken).ConfigureAwait(false))
                {
                    if (f.IsFolder)
                        continue;

                    anyFiles = true;
                    var parsed = VolumeBase.ParseFilename(f.Name);
                    if (parsed != null)
                    {
                        anyBackups = true;
                        anyEncryptedFiles = !string.IsNullOrWhiteSpace(parsed.EncryptionModule);
                        break;
                    }
                }

                return DestinationTestResponseDto.Create(
                    anyFiles,
                    anyBackups,
                    anyEncryptedFiles
                );

            }
        }
        catch (FolderMissingException)
        {
            if (input.AutoCreate)
                return DestinationTestResponseDto.Failure(
                    "Failed to create folder",
                    "error-creating-folder",
                    folderExists: false
                );

            return DestinationTestResponseDto.Failure(
                "Folder does not exist",
                "missing-folder",
                folderExists: false
            );
        }
        catch (Library.Utility.SslCertificateValidator.InvalidCertificateException icex)
        {
            if (string.IsNullOrWhiteSpace(icex.Certificate))
                return DestinationTestResponseDto.Failure(
                    icex.Message,
                    "CertError"
                );

            return DestinationTestResponseDto.Failure(
                "Host certificate error",
                "incorrect-cert",
                hostCertificate: icex.Certificate
            );
        }
        catch (Library.Utility.HostKeyException hex)
        {
            if (string.IsNullOrWhiteSpace(hex.ReportedHostKey))
                return DestinationTestResponseDto.Failure(
                    hex.Message,
                    "KeyError"
                );

            return DestinationTestResponseDto.Failure(
                "Host key error",
                "incorrect-host-key",
                reportedHostKey: hex.ReportedHostKey,
                acceptedHostKey: hex.AcceptedHostKey
            );
        }
        catch (UserInformationException uex)
        {
            return DestinationTestResponseDto.Failure(
                uex.Message,
                uex.HelpID
            );
        }
        catch (Exception ex)
        {
            return DestinationTestResponseDto.Failure(
                ex.Message,
                "error"
            );
        }
        finally
        {
            wrapper?.Dispose();
        }
    }
}