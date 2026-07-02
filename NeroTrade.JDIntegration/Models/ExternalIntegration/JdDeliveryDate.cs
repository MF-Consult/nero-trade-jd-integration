namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// Normalizes the delivery date sent to JD on a request order.
/// JD rejects delivery dates in the past, but only the calendar day matters. The previous logic
/// (<c>if (date &lt; DateTime.UtcNow) date = DateTime.UtcNow</c>) compared a delivery date
/// (Kind=Unspecified, e.g. today 00:00) against the current UTC instant, so a same-day "deliver
/// today" order was overwritten with "now" — which JD then pushed past its same-day cutoff to the
/// next business day (the rush-order bug Maiwand reported). This keeps the intended day.
/// </summary>
public static class JdDeliveryDate
{
    /// <summary>
    /// Returns the delivery date to send to JD: the original day, unless it is strictly before
    /// <paramref name="nowUtc"/>'s date (then bumped up to today). A date with no time-of-day is
    /// sent at noon, so a same-day delivery is never read as a past instant and the calendar day
    /// cannot roll across timezones; an explicit time (timed delivery) is preserved.
    /// </summary>
    public static DateTime? Normalize(DateTime? date, DateTime nowUtc)
    {
        if (!date.HasValue)
            return null;

        var today = nowUtc.Date;
        var day = date.Value.Date < today ? today : date.Value.Date;
        var timeOfDay = date.Value.TimeOfDay;

        return timeOfDay == TimeSpan.Zero ? day.AddHours(12) : day.Add(timeOfDay);
    }
}
