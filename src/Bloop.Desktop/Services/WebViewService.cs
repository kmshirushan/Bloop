using System.IO;
using System.Text.Json;
using Bloop.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bloop.Desktop.Services;

/// <summary>
/// Manages the WebView2CompositionControl lifecycle, transparent background configuration,
/// virtual host folder mapping to the built Three.js renderer, and typed JSON messaging.
/// </summary>
public sealed class WebViewService : IDisposable
{
    private const string VirtualHostName = "bloop.app";
    private WebView2? _webView;
    private bool _isInitialized;
    private bool _isDisposed;

    public event EventHandler<RawIpcMessage>? MessageReceived;
    public event EventHandler? RendererReady;

    public async Task InitializeAsync(WebView2 webView)
    {
        _webView = webView;

        // Ensure transparent background before and after CoreWebView2 initialization
        _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userDataFolder = !string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(appData, "Bloop", "WebView2")
            : Path.Combine(AppContext.BaseDirectory, "WebView2Data");

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: new CoreWebView2EnvironmentOptions()
        );

        await _webView.EnsureCoreWebView2Async(env);
        _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        var settings = _webView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var wwwroot = ResolveRendererDistPath();
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            wwwroot,
            CoreWebView2HostResourceAccessKind.Allow
        );

        _isInitialized = true;
        _webView.CoreWebView2.Navigate($"https://{VirtualHostName}/index.html");
    }

    public void SendMessage<TPayload>(string type, TPayload payload)
    {
        if (!_isInitialized || _isDisposed || _webView?.CoreWebView2 is null)
        {
            return;
        }

        var message = new IpcMessage<TPayload>(
            Type: type,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload: payload
        );

        var json = JsonSerializer.Serialize(message);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<RawIpcMessage>(e.WebMessageAsJson);
            if (raw is null || string.IsNullOrEmpty(raw.Type))
            {
                return;
            }

            if (raw.Type == "renderer:ready")
            {
                RendererReady?.Invoke(this, EventArgs.Empty);
            }

            MessageReceived?.Invoke(this, raw);
        }
        catch
        {
            // Ignore malformed JSON frames
        }
    }

    private static string ResolveRendererDistPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var outputWwwroot = Path.Combine(baseDir, "wwwroot");
        if (Directory.Exists(outputWwwroot) && File.Exists(Path.Combine(outputWwwroot, "index.html")))
        {
            return outputWwwroot;
        }

        // Fallback to repo renderer/dist during development
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "renderer", "dist");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate renderer/dist folder containing index.html.");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_webView?.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        _webView?.Dispose();
    }
}
