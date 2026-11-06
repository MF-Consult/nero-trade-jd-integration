namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// How much of JD's request-order list the JD→Uniconta status sync reads per run (section <c>RequestOrderSync</c>).
/// The list is paged newest-first and holds every order ever created (~1.470, 8 pages at 200), so reading all of it
/// every few minutes cost ~8 JD calls and ~7 s per run. Regular runs now stop paging once a whole page is older than
/// <see cref="RecentWindowDays"/>; one run per <see cref="FullSweepHours"/> still reads everything, so an old order that
/// changes status late is picked up within a day at worst.
/// </summary>
public sealed class RequestOrderSyncOptions
{
    public const string SectionName = "RequestOrderSync";

    /// <summary>Only read orders created within this many days on a regular run. 0 disables the shortcut (always full).</summary>
    public int RecentWindowDays { get; set; } = 30;

    /// <summary>Run a full read at least this often.</summary>
    public int FullSweepHours { get; set; } = 24;
}
