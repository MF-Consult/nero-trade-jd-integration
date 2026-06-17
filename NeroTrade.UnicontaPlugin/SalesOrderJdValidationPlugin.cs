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
    private bool _isUpdatingFields;

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
            var exchangePallets = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.ExchangePallets), ExchangePalletsValues.InIndexOrder);

            return SalesOrderJdValidator.Validate(
                transferToJd, order._DeliveryDate, trackingNote, transportType, deliveryType, exchangePallets);
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
    /// Best-effort UX wiring. On a transport-type change (see <see cref="DeliveryTypeRules"/>):
    /// <list type="bullet">
    /// <item>"JD Logistik Transport" → default xDeliveryType to "Palle Fragt" (GLS stays selectable)
    /// and show the field.</item>
    /// <item>"Ekstern Transport" / "Afhenter Selv" → clear xDeliveryType and hide the field.</item>
    /// </list>
    /// On either a transport-type or delivery-type change, xByttepaller is shown only for pallet
    /// orders (see <see cref="ExchangePalletsRules"/>) and hidden otherwise. PropertyName semantics
    /// for user fields are not guaranteed across Uniconta versions — if this never fires,
    /// <see cref="CheckMandatoryFields"/> still enforces the rules at save time.
    /// </summary>
    public override void Record_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingFields)
            return;
        if (!(sender is DebtorOrderClient order))
            return;

        var propertyName = e?.PropertyName;
        var transportChanged = string.Equals(propertyName, PluginFieldNames.TransportType, StringComparison.OrdinalIgnoreCase);
        var deliveryChanged = string.Equals(propertyName, PluginFieldNames.DeliveryType, StringComparison.OrdinalIgnoreCase);
        if (!transportChanged && !deliveryChanged)
            return;

        try
        {
            _isUpdatingFields = true;

            if (transportChanged)
            {
                var transportType = UserFieldValueNormalizer.Normalize(
                    order.GetUserField(PluginFieldNames.TransportType), TransportTypeValues.InIndexOrder);
                var currentDeliveryType = UserFieldValueNormalizer.Normalize(
                    order.GetUserField(PluginFieldNames.DeliveryType), DeliveryTypeValues.InIndexOrder);

                var newDeliveryType =
                    DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange(transportType, currentDeliveryType);
                if (newDeliveryType != null) // null = leave untouched, never write a no-op (change-event loop guard)
                {
                    order.SetUserField(PluginFieldNames.DeliveryType, newDeliveryType);
                    // SetUserField writes the value but does not raise PropertyChanged for the bound
                    // control, so the field would keep showing its old (blank) value until a refresh.
                    // Notify the binding explicitly so "Palle Fragt" (or the cleared value) shows now.
                    order.NotifyPropertyChanged(PluginFieldNames.DeliveryType);
                }
            }

            // xByttepaller relevance depends on BOTH transport and (possibly just-updated) delivery
            // type, so re-apply both fields' visibility after any relevant change.
            ApplyFieldVisibility(order);
        }
        catch
        {
            // Best-effort only — must never crash the desktop client.
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }

    /// <summary>
    /// Sets the initial xDeliveryType / xByttepaller visibility when the page opens, so an order
    /// whose transport/delivery type is already filled in shows the right fields before the user
    /// touches anything. Best-effort: <see cref="PageEventsBase.master"/> may not be the order on
    /// every page variant, and the visibility calls are guarded.
    /// </summary>
    public override void OnPageLayoutLoaded() => ApplyFieldVisibility(master);

    /// <summary>
    /// Shows/hides xDeliveryType (only for "JD Logistik Transport") and xByttepaller (only for
    /// pallet orders) for the given order. No-ops for anything that is not a sales order.
    /// </summary>
    private void ApplyFieldVisibility(UnicontaBaseEntity? record)
    {
        try
        {
            if (!(record is DebtorOrderClient order))
                return;

            var transportType = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.TransportType), TransportTypeValues.InIndexOrder);
            var deliveryType = UserFieldValueNormalizer.Normalize(
                order.GetUserField(PluginFieldNames.DeliveryType), DeliveryTypeValues.InIndexOrder);

            SetControlVisible(PluginControlNames.DeliveryType, DeliveryTypeRules.ShouldShowDeliveryType(transportType));
            SetControlVisible(PluginControlNames.ExchangePallets, ExchangePalletsRules.IsRelevant(transportType, deliveryType));
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>
    /// Shows or hides a named form control. Uses reflection to set the WPF <c>Visibility</c> property
    /// so the plugin takes no PresentationFramework build dependency (which would break the net48
    /// compile on the Linux PR runner). Silently no-ops when the control name does not resolve —
    /// verify the names in <see cref="PluginControlNames"/> with F12 if a field is not hidden.
    /// </summary>
    private void SetControlVisible(string controlName, bool visible)
    {
        try
        {
            var control = GetFormControl(controlName);
            var visibilityProperty = control?.GetType().GetProperty("Visibility");
            if (visibilityProperty == null)
                return;

            // System.Windows.Visibility: Visible = 0, Collapsed = 2.
            var value = Enum.Parse(visibilityProperty.PropertyType, visible ? "Visible" : "Collapsed");
            visibilityProperty.SetValue(control, value);
        }
        catch
        {
            // Best-effort UI only — never crash the desktop client over a layout tweak.
        }
    }
}
#endif
