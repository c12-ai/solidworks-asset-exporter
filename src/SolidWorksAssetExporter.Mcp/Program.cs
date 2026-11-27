using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SolidWorksAssetExporter.Mcp
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int pid = 0;
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--pid" ||
                !int.TryParse(args[1], out pid) || pid <= 0))
            {
                Console.Error.WriteLine("Usage: SolidWorksAssetExporter.Mcp.exe [--pid SOLIDWORKS_PROCESS_ID]");
                return 2;
            }

            using (var input = McpProtocol.Reader(Console.OpenStandardInput()))
            using (var output = McpProtocol.Writer(Console.OpenStandardOutput()))
            {
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    var response = McpProtocol.Respond(line, (name, arguments) => CallSolidWorks(pid, name, arguments));
                    if (response != null) output.WriteLine(response);
                }
            }
            return 0;
        }

        private static object CallSolidWorks(int requestedPid, string name, Dictionary<string, object> arguments)
        {
            var json = McpProtocol.Json();
            using (var pipe = Connect(requestedPid))
            using (var reader = McpProtocol.Reader(pipe))
            using (var writer = McpProtocol.Writer(pipe))
            {
                writer.WriteLine(json.Serialize(new { name = name, arguments = arguments }));
                var response = reader.ReadLine();
                if (response == null) throw new IOException("SOLIDWORKS 插件在返回结果前断开连接，请检查当前操作的结果后重试。");
                var envelope = McpProtocol.Object(json.DeserializeObject(response));
                var error = McpProtocol.Value(envelope, "error") as string;
                if (error != null) throw new InvalidOperationException(error);
                return McpProtocol.Value(envelope, "result");
            }
        }

        private static NamedPipeClientStream Connect(int requestedPid)
        {
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var processes = Process.GetProcessesByName("SLDWORKS");
            try
            {
                var candidates = processes.Where(p => p.SessionId == session && (requestedPid == 0 || p.Id == requestedPid)).ToArray();
                if (candidates.Length > 1)
                    throw new InvalidOperationException("当前会话有多个 SOLIDWORKS 进程。请用 --pid 选择一个：" +
                        string.Join(", ", candidates.Select(p => p.Id)));
                if (candidates.Length == 0)
                    throw new InvalidOperationException("当前 Windows 会话中找不到指定的 SOLIDWORKS 进程。");
                var pid = candidates[0].Id;
                var pipe = new NamedPipeClientStream(".", McpProtocol.PipeName(pid), PipeDirection.InOut,
                    PipeOptions.None, TokenImpersonationLevel.Identification);
                try
                {
                    try { pipe.Connect(2000); }
                    catch (TimeoutException) { throw new InvalidOperationException("无法连接 SOLIDWORKS MCP。请确认此进程已加载支持 MCP 的 Asset Exporter 插件。"); }
                    uint serverPid;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out serverPid) || serverPid != (uint)pid)
                        throw new InvalidOperationException("本地 MCP 管道身份与指定的 SOLIDWORKS 进程不一致。");
                    return pipe;
                }
                catch { pipe.Dispose(); throw; }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    }
}
