namespace NeroTrade.JDIntegration.Services.Logging;

public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }

    public static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + Sanitize(ex.Message);
}
