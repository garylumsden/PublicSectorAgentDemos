namespace Demo2.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers.Pragma = "no-cache";
            headers["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; " +
                "img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
                "connect-src 'self' ws: wss:";
            // Easy Auth needs a trusted origin signal for cookie-authenticated browser POSTs.
            headers["Referrer-Policy"] = "same-origin";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });

        return next(context);
    }
}
