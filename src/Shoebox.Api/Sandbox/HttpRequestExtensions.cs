namespace Shoebox.Api.Sandbox;

public static class HttpRequestExtensions
{
    public static string? GetSandboxId(this HttpRequest request)
    {
        return request.Query[SandboxConstants.QueryParamName].FirstOrDefault();
    }
}