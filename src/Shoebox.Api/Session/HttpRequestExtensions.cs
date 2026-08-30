namespace Shoebox.Api.Session;

public static class HttpRequestExtensions
{
    /// <summary>The shoebox this request belongs to.</summary>
    public static string? GetShoeboxId(this HttpRequest request)
    {
        return request.Query[ShoeboxConstants.QueryParamName].FirstOrDefault();
    }
}