namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public sealed class JdPagination
{
    public int currentPage { get; set; }
    public int pageCount { get; set; }
    public int pageSize { get; set; }
    public int rowCount { get; set; }
    public int firstRowOnPage { get; set; }
    public int lastRowOnPage { get; set; }
}

public sealed class JdPagedResponse<T>
{
    public List<T> items { get; set; } = new();
    public JdPagination? pagination { get; set; }
}


