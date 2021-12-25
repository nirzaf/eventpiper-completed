using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace eventpiper
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Missing argument: process to start or PID");
                return;
            }
            using var cts = new CancellationTokenSource();

            int.TryParse(args[0], out var pid);
            await using var diagSession = pid == 0 ? StartNewProcess(args) : AttachToProcess(pid);

            var providers = new[] {
                new EventPipeProvider("Microsoft-Diagnostics-DiagnosticSource", EventLevel.Verbose, 0x3L,
                    new Dictionary<string, string>() {
                        ["FilterAndPayloadSpecs"] = "HttpHandlerDiagnosticListener/System.Net.Http.Request:-Request.RequestUri;Request.Method;LoggingRequestId\n" +
                                                    "HttpHandlerDiagnosticListener/System.Net.Http.Response:-Response.StatusCode;LoggingRequestId"
                })
            };

            using var session = diagSession.DiagClient.StartEventPipeSession(providers, false);

            Console.CancelKeyPress += (o, ev) => { ev.Cancel = true; session.Stop(); };

            if (pid == 0)
            {
                UnofficialDiagnosticsClientApi.ResumeRuntime(diagSession.DiagClient);
            }

            using var eventSource = new EventPipeEventSource(session.EventStream);

            eventSource.Dynamic.All += OnEvent;

            eventSource.Process();
        }

        static DiagnosticsSession AttachToProcess(int pid)
        {
            return new DiagnosticsSession(null, new DiagnosticsClient(pid));
        }

        static DiagnosticsSession StartNewProcess(string[] args)
        {
            var diagPortName = $"eventpiper-{Process.GetCurrentProcess().Id}-{DateTime.Now:yyyyMMdd_HHmmss}.socket";
            var server = UnofficialDiagnosticsClientApi.CreateReversedServer(diagPortName);

            UnofficialDiagnosticsClientApi.Start(server);

            var startInfo = new ProcessStartInfo(args[0], string.Join(' ', args, 1, args.Length - 1)) {
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.Environment.Add("DOTNET_DiagnosticPorts", diagPortName);

            using var proc = Process.Start(startInfo);
            var client = UnofficialDiagnosticsClientApi.WaitForProcessToConnect(server, proc.Id);

            return new DiagnosticsSession(server, client);
        }

        public static void OnEvent(TraceEvent ev)
        {
            if (ev.EventName == "Event")
            {
                var diagSourceEventName = ev.PayloadStringByName("EventName");
                var args = (IDictionary<string, object>[])ev.PayloadByName("Arguments");

                var requestId = (string)args.Single(d => (string)d["Key"] == "LoggingRequestId")["Value"];
                if (diagSourceEventName == "System.Net.Http.Request")
                {
                    var uri = (string)args.Single(d => (string)d["Key"] == "RequestUri")["Value"];
                    var method = (string)args.Single(d => (string)d["Key"] == "Method")["Value"];
                    Console.WriteLine($"{ev.TimeStampRelativeMSec:0.000}ms Request#{requestId} - {method} {uri}");
                }
                else
                {
                    // diagSourceEventName == "System.Net.Http.Response"
                    var statusCode = (string)args.Single(d => (string)d["Key"] == "StatusCode")["Value"];
                    Console.WriteLine($"{ev.TimeStampRelativeMSec:0.000}ms Response#{requestId} - {statusCode}");
                }
            }
        }
    }

    class DiagnosticsSession : IAsyncDisposable
    {
        private readonly object diagnosticsServer;
        private readonly DiagnosticsClient diagnosticsClient;

        public DiagnosticsSession(object server, DiagnosticsClient client) {
            diagnosticsClient = client;
            diagnosticsServer = server;
        }

        public DiagnosticsClient DiagClient => diagnosticsClient;

        public ValueTask DisposeAsync()
        {
            if (diagnosticsServer is null) {
                return ValueTask.CompletedTask;
            }
            return UnofficialDiagnosticsClientApi.DisposeAsync(diagnosticsServer);
        }
    }
}
