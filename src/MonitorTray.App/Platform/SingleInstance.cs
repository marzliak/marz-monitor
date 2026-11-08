using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace MonitorTray.App.Platform;

/// <summary>Instância única por usuário: Mutex + pipe nomeado "show".</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cts = new();
    public bool IsFirst { get; }
    public event Action<string>? Message;

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "x";
        bool created;
        try { mutex = new Mutex(true, @"Local\MonitorTray." + sid, out created); }
        catch (UnauthorizedAccessException)
        {
            // Já existe, criado pela instância elevada: não somos a primeira.
            mutex = new Mutex(false); created = false;
        }
        IsFirst = created;
        pipeName = "MonitorTray." + sid;
    }

    public void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cts.Token);
                    using var r = new StreamReader(server);
                    var msg = (await r.ReadLineAsync(cts.Token)) ?? "show";
                    Message?.Invoke(msg.Length > 32 ? "show" : msg);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { await Task.Delay(200); }
            }
        });
    }

    public bool Send(string msg)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            c.Connect(1500);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(msg);
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        cts.Cancel();
        if (IsFirst) try { mutex.ReleaseMutex(); } catch (Exception) { }
        mutex.Dispose();
    }
}
