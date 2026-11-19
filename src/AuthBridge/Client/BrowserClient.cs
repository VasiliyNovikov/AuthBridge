using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Handles browser requests and hands off a loopback redirect or cancellation to a detached worker.</summary>
internal sealed class BrowserClient(Action<string[]> startWorker, TextWriter messages)
{
    public const string RelayCommand = "__relay";
    public const string CancelCommand = "__cancel";

    public async Task<int> OpenAsync(OpenRequest request, Bridge.Connection? bridge)
    {
        if (bridge is null)
        {
            messages.WriteLine("authbridge: not connected to the Windows helper");
            if (TryCancel(request, "the Windows helper is not connected"))
                return 0;
            messages.WriteLine($"Open this URL manually:\n  {request.Url}");
            return 1;
        }

        var (status, session, error) = await bridge.Client.OpenAsync(request);
        if (status != 200)
        {
            messages.WriteLine($"authbridge: {error}");
            // Windows can reserve the port the application picked; a retry gets another one.
            return status == 409 && TryCancel(request, $"{error}; retry the sign-in") ? 0 : 1;
        }

        var opened = $"authbridge: opened in the browser on {bridge.Info.Machine ?? "Windows"}";
        if (session is not null)
        {
            var port = request.RelayPort ?? throw new IOException("the helper returned a relay session without a requested port");
            startWorker([RelayCommand, bridge.Client.Endpoint, session, port.ToString()]);
            opened += $"; relaying http://localhost:{port}";
        }
        messages.WriteLine(opened);
        return 0;
    }

    private bool TryCancel(OpenRequest request, string reason)
    {
        if (request.RelayPort is not { } port || OAuth.CancelRequest(request.Url, port, reason) is null)
            return false;
        startWorker([CancelCommand, port.ToString(), request.Url, reason]);
        messages.WriteLine("authbridge: sign-in cancelled; retry it later or use another sign-in method");
        return true;
    }
}
