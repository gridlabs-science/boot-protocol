namespace boot_portal.Utils;

public static class NodeSetupPolicy
{
    public static bool IsAllowedSetupPath(PathString path)
    {
        return path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/setup.css", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase);
    }

    public static bool WantsHtml(HttpRequest request)
    {
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
               request.Headers.Accept.Any(value =>
                   value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }
}
