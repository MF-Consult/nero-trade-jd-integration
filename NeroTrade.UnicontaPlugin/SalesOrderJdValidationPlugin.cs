// Only compiled into the net48 target — the Uniconta desktop client is .NET Framework,
// and the net9.0 target exists solely so the test project can exercise the pure core.
#if NETFRAMEWORK
using System.ComponentModel;
using Uniconta.API.Plugin;
using Uniconta.ClientTools.DataModel;
using Uniconta.Common;

namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// Save-time validation of sales orders flagged for transfer to JD Logistik (xTransferToJD).
/// Register in the Uniconta client on the sales order page (control "DebtorOrders" — verify
/// with F12 on the page) with ClassName "SalesOrderJdValidationPlugin", plugin type Event.
/// See README.md for the full validation matrix and deployment steps.
/// </summary>
public sealed class SalesOrderJdValidationPlugin : PageEventsBase
{
    // Assumes one plugin instance per open page (Uniconta's host model); the flag only
    // guards against the change event re-raised by our own SetUserField call below.
    private bool _isClearingDeliveryType;

    public override string? CheckMandatoryFields(UnicontaBaseEntity record)
    {
        // Wrong-page registration must never block saves.
        if (!(record is DebtorOrderClient order))
            return null;

        try
        {
            var transferToJd = order.GetUserFieldBoolean(PluginFieldNames.TransferToJd);
            var trackingNote = order.GetUserField(PluginFieldNames.TrackingNote) as string;
            var transportType = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.TransportType), TransportTypeValues.InIndexOrder);
            var deliveryType = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.DeliveryType), DeliveryTypeValues.InIndexOrder);

            return SalesOrderJdValidator.Validate(
                transferToJd, order._DeliveryDate, trackingNote, transportType, deliveryType);
        }
        catch
        {
            // Fail open: a plugin malfunction (e.g. a user field missing from the company
            // schema) must never lock users out of saving. JD's own rejection remains the
            // existing fallback for orders that slip through.
            return null;
        }
    }

    /// <summary>
    /// Best-effort UX nicety: when the transport type changes to one that forbids a delivery
    /// type ("Ekstern Transport" / "Afhenter Selv"), clear xDeliveryType so the user is not
    /// stopped at save time. PropertyName semantics for user fields are not guaranteed across
    /// Uniconta versions — if this never fires, <see cref="CheckMandatoryFields"/> still
    /// enforces the rule at save time.
    /// </summary>
    public override void Record_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isClearingDeliveryType)
            return;
        if (!(sender is DebtorOrderClient order))
            return;
        if (!string.Equals(e?.PropertyName, PluginFieldNames.TransportType, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _isClearingDeliveryType = true;

            var transportType = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.TransportType), TransportTypeValues.InIndexOrder);
            var forbidsDeliveryType =
                string.Equals(transportType, TransportTypeValues.Ekstern, StringComparison.OrdinalIgnoreCase)
                || string.Equals(transportType, TransportTypeValues.AfhenterSelv, StringComparison.OrdinalIgnoreCase);
            if (!forbidsDeliveryType)
                return;

            var deliveryType = order.GetUserField(PluginFieldNames.DeliveryType) as string;
            if (string.IsNullOrWhiteSpace(deliveryType))
                return; // never write a no-op — second guard against change-event loops

            order.SetUserField(PluginFieldNames.DeliveryType, "");
        }
        catch
        {
            // Best-effort only — must never crash the desktop client.
        }
        finally
        {
            _isClearingDeliveryType = false;
        }
    }
}
#endif
