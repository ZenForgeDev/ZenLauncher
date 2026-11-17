using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ZenStationShell.Services;

/// <summary>
/// Lite innloggingsvindu (WebView2) som bare lever mens du logger inn hos Epic eller GOG. Koden fanges automatisk,
/// så du slipper å kopiere og lime inn. Selve launcheren har fortsatt ingen webview.
///  - GOG: koden står i adressen GOG sender deg til etter innlogging (…on_login_success?code=KODE).
///  - Epic: siden etter innlogging viser JSON med «authorizationCode», som leses fra sideteksten.
/// Returnerer null hvis vinduet lukkes uten kode.
/// </summary>
internal static class LoginBrowser
{
    public const string EpicUrl = "https://legendary.gl/epiclogin";

    public enum Kind { Epic, Gog }

    public static Task<string?> GetCodeAsync(string title, string url, Kind kind)
    {
        var tcs = new TaskCompletionSource<string?>();
        var win = new Window { Title = title };
        var web = new WebView2();
        win.Content = web;
        try { win.AppWindow.Resize(new SizeInt32(1000, 820)); } catch { }
        bool done = false;

        void Finish(string? code)
        {
            if (done) return;
            done = true;
            tcs.TrySetResult(code);
            try { win.Close(); } catch { }
        }

        win.Closed += (_, _) =>
        {
            if (done) return;
            done = true;
            tcs.TrySetResult(null);
        };
        win.Activate();
        _ = Run();
        return tcs.Task;

        async Task Run()
        {
            try
            {
                await web.EnsureCoreWebView2Async();
                var core = web.CoreWebView2;
                if (kind == Kind.Gog)
                {
                    core.NavigationStarting += (_, e) =>
                    {
                        var uri = e.Uri ?? "";
                        var m = Regex.Match(uri, @"[?&]code=([A-Za-z0-9_\-]+)");
                        if (m.Success && uri.Contains("on_login_success", StringComparison.OrdinalIgnoreCase))
                        {
                            e.Cancel = true;
                            Finish(m.Groups[1].Value);
                        }
                    };
                }
                else
                {
                    core.NavigationCompleted += async (_, _) => await TryReadEpicCodeAsync();
                }
                core.Navigate(url);
            }
            catch
            {
                Finish(null);
            }
        }

        async Task TryReadEpicCodeAsync()
        {
            try
            {
                var text = await web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                var m = Regex.Match(text ?? "", @"authorizationCode\W+([A-Za-z0-9]+)");
                if (m.Success) Finish(m.Groups[1].Value);
            }
            catch { }
        }
    }
}
