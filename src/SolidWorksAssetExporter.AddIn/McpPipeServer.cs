using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class McpPipeServer : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Control _dispatcher;
        private readonly Func<string, Dictionary<string, object>, Task<object>> _call;
        private readonly Action<string> _log;
        private readonly string _name;
        private readonly PipeSecurity _security = new PipeSecurity();
        private NamedPipeServerStream _pipe;
        private bool _disposed;

        internal McpPipeServer(Control dispatcher, Func<string, Dictionary<string, object>, Task<object>> call, Action<string> log)
        {
            _dispatcher = dispatcher;
            _call = call;
            _log = log;
            using (var process = Process.GetCurrentProcess()) _name = McpProtocol.PipeName(process.Id);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                _security.SetAccessRuleProtection(true, false);
                _security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Deny));
                _security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            _pipe = NewPipe();
            Task.Run((Func<Task>)Serve);
        }

        private NamedPipeServerStream NewPipe()
        {
            return new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, _security);
        }

        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    NamedPipeServerStream pipe;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        pipe = _pipe;
                    }
                    try
                    {
                        await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                        await Handle(pipe).ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    catch (DecoderFallbackException) { }
                    finally { pipe.Dispose(); }
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _pipe = NewPipe();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate) if (!_disposed) _log("MCP pipe stopped: " + ex);
            }
        }

        private async Task Handle(NamedPipeServerStream pipe)
        {
            using (var reader = McpProtocol.Reader(pipe))
            using (var writer = McpProtocol.Writer(pipe))
            {
                var read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(5000)).ConfigureAwait(false) != read) return;
                var line = await read.ConfigureAwait(false);
                if (line == null) return;
                var json = McpProtocol.Json();
                object response;
                try
                {
                    var request = McpProtocol.Object(json.DeserializeObject(line));
                    var name = McpProtocol.Value(request, "name") as string;
                    var args = McpProtocol.ToolArguments(name, McpProtocol.Value(request, "arguments"));
                    // SOLIDWORKS COM objects are only accessed on the add-in's UI thread.
                    var invoke = _dispatcher.BeginInvoke(new Func<Task<object>>(async () =>
                    {
                        try { return new { result = await _call(name, args) }; }
                        catch (Exception ex) { return new { error = ex.Message }; }
                    }));
                    response = await ((Task<object>)_dispatcher.EndInvoke(invoke)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new { error = ex.Message };
                }
                await writer.WriteLineAsync(json.Serialize(response)).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pipe.Dispose();
            }
        }
    }
}
