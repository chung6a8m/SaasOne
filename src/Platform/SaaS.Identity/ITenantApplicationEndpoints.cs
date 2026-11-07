using Microsoft.AspNetCore.Http;
namespace SaaS.Identity;

/// <summary>Host-composed, explicit customer routes. Never grants access to native CMS controllers.</summary>
public interface ITenantApplicationEndpoints
{
    bool Matches(string path, string method);
    Task HandleAsync(HttpContext context);
}
