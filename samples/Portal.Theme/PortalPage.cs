namespace SaaS.Theme.Portal;

// Fixed platform-owned markup: no tenant template, inline script, untrusted HTML or secrets.
public static class PortalPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>SaasOne Portal</title></head>
        <body><main><h1>SaasOne Portal</h1><p>Independent platform product.</p>
        <p>This read-only sample uses the Portal theme and the existing platform readiness boundary.</p>
        <p>No customer business modules or public setup are exposed.</p></main></body>
        </html>
        """;
}
