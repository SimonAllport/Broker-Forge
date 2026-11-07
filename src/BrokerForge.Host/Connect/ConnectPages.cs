using System.Text.Encodings.Web;

namespace BrokerForge.Host.Connect;

/// <summary>Minimal HTML for the connect page. Every value is HTML-encoded; no credential is ever written into a page.</summary>
internal static class ConnectPages
{
    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    public static IResult Page(int status, string title, string body) => new NoFraming(Results.Content(
        $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{E(title)}}</title>
        <style>body{font:16px/1.5 system-ui,sans-serif;max-width:36rem;margin:2rem auto;padding:0 1rem}label{display:block;margin:.5rem 0}input{width:100%}button{margin-top:.75rem}</style>
        </head><body><h1>{{E(title)}}</h1>{{body}}</body></html>
        """, "text/html; charset=utf-8", statusCode: status));

    /// <summary>The pages are forms (keys, the role step, connect): no other site may frame them.</summary>
    private sealed class NoFraming(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            http.Response.Headers.XFrameOptions = "DENY";
            return inner.ExecuteAsync(http);
        }
    }

    public static IResult Message(int status, string title, string message) => Page(status, title, $"<p>{E(message)}</p>");

    public static string Form(string action, string antiforgery, string fields, string button) =>
        $"""<form method="post" action="{E(action)}"><input type="hidden" name="__RequestVerificationToken" value="{E(antiforgery)}">{fields}<button>{E(button)}</button></form>""";

    public static string Paragraph(string text) => $"<p>{E(text)}</p>";

    /// <summary>One instance secret: its name as a hidden field and the value as a password field that is never filled in.</summary>
    public static string SecretFields(string name) =>
        $"""<input type="hidden" name="name" value="{E(name)}"><label>{E(name)} <input name="value" type="password" autocomplete="off"></label>""";

    public static string SsoFields(bool needsUsername) =>
        (needsUsername ? """<label>Username <input name="username" autocomplete="username"></label>""" : "")
        + """<label>Password or key <input name="secret" type="password" autocomplete="current-password"></label>""";
}
