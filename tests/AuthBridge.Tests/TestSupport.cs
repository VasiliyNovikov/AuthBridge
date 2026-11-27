using System.Net;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

/// <summary>Builds what many tests set up the same way.</summary>
internal static class TestSupport
{
    /// <summary>A program that reads its standard input until it closes, like the remote command of a tunnel.</summary>
    public static string StdinReader => OperatingSystem.IsWindows() ? "sort.exe" : "cat";

    /// <summary>An HTTP server on a free loopback port.</summary>
    public static HttpServer Serve(Func<HttpRequest, HttpResponse> respond) =>
        HttpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), request => Task.FromResult(respond(request)));

    /// <summary>Settings that fail the test when they report a problem.</summary>
    public static HostSettings NewSettings(string path) => new(path, message => Assert.Fail(message));

    /// <summary>A helper without tunnels that fails the test if it opens a browser.</summary>
    public static Helper NewHelper(Action<string>? openBrowser = null) =>
        new(openBrowser ?? (_ => Assert.Fail("This test does not open a browser.")), () => [], () => null);

    /// <summary>A helper that listens on a free loopback port.</summary>
    public static Helper StartHelper(Action<string>? openBrowser = null)
    {
        var helper = NewHelper(openBrowser);
        helper.Start(0);
        return helper;
    }
}

/// <summary>A temporary directory that is deleted with its contents on disposal.</summary>
internal sealed class TempDirectory(string prefix) : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory(prefix);

    public string FullName => _directory.FullName;

    public DirectoryInfo CreateSubdirectory(string path) => _directory.CreateSubdirectory(path);

    public void Dispose() => _directory.Delete(recursive: true);
}

/// <summary>Sends the console's output or error to other writers until disposed. Tests that use it must not run in parallel.</summary>
internal sealed class ConsoleRedirect : IDisposable
{
    private readonly TextWriter? _output, _error;

    public ConsoleRedirect(TextWriter? output = null, TextWriter? error = null)
    {
        if (output is not null)
        {
            _output = Console.Out;
            Console.SetOut(output);
        }
        if (error is not null)
        {
            _error = Console.Error;
            Console.SetError(error);
        }
    }

    public void Dispose()
    {
        if (_output is not null)
            Console.SetOut(_output);
        if (_error is not null)
            Console.SetError(_error);
    }
}
