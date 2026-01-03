namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

using System.Text.Json;
using System.Text.Json.Serialization;

public class JdRequestOrder
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public DateTime? date { get; set; }
    public int? type { get; set; }
    public int? status { get; set; }
    public int? stage { get; set; }
    public bool? disableApprovalEmail { get; set; }
    public string? text { get; set; }
    public string? trackingNote { get; set; }
    public string? deliveryNoteText { get; set; }
    public string? shopOrderId { get; set; }
    public string? trackAndTraceUrl { get; set; }
    public JdRequestOrderAddress? inOutAddress { get; set; }
    public JdRequestOrderContact? inOutContact { get; set; }
    public JdRequestOrderOrderInfo? order { get; set; }
    public JdRequestOrderShipmondoInfo? shipmondo { get; set; }
    public List<JdRequestOrderProductItemDetail>? productItems { get; set; }
    public List<JdRequestOrderFileDetail>? files { get; set; }
}

public class JdRequestOrderAddress
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public string? name { get; set; }
    public string? att { get; set; }
    public string? street { get; set; }
    public string? zip { get; set; }
    public string? city { get; set; }
    public string? country { get; set; }
    public string? countryCode { get; set; }
}

public class JdRequestOrderContact
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public string? name { get; set; }
    public string? title { get; set; }
    public string? department { get; set; }
    public string? company { get; set; }
    public string? vat { get; set; }
    public string? email { get; set; }
    public string? telephoneDirect { get; set; }
    public string? telephoneMobile { get; set; }
}

public class JdRequestOrderOrderInfo
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public string? name { get; set; }
    public bool? shopOrder { get; set; }
    public bool? b2BOrder { get; set; }
}

public class JdRequestOrderShipmondoInfo
{
    public string? carrierCode { get; set; }
    public string? productCode { get; set; }
    public List<string>? productServices { get; set; }
    public string? pickupPointId { get; set; }
    public string? carrierInstructions { get; set; }
    public long? draftShipmentId { get; set; }
}

public class JdRequestOrderProductItemDetail
{
    public int quantity { get; set; }
    public JdRequestOrderCatalogDetail? catalog { get; set; }
    public List<DateTime>? bestBefores { get; set; }
}

public class JdRequestOrderCatalogDetail
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public string? sku { get; set; }
    public string? name { get; set; }
    public string? description { get; set; }
    public bool? metaEco { get; set; }
    public List<JdRequestOrderBarcode>? barcodes { get; set; }
}

public class JdRequestOrderBarcode
{
    public long? id { get; set; }
    public string? barcode { get; set; }
}

public class JdRequestOrderFileDetail
{
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public string? name { get; set; }
    public string? displayName { get; set; }
    public bool? verified { get; set; }
    public string? description { get; set; }
    public string? url { get; set; }
    public string? mimeType { get; set; }
    public bool? packageLabel { get; set; }
}

public class JdRequestOrderCreate
{
    public DateTime? date { get; set; }
    public string? text { get; set; }
    public string? trackingNote { get; set; }
    public string? deliveryNoteText { get; set; }
    public string? shopOrderId { get; set; }
    public bool? disableApprovalEmail { get; set; }
    public JdAddress? address { get; set; }
    public JdRequestOrderContactPerson? contactPerson { get; set; }
    public JdRequestOrderOrderRef? order { get; set; }
    public JdRequestOrderShipmondo? shipmondo { get; set; }
    public List<JdRequestOrderProductItem> productItems { get; set; } = new();
    public List<JdRequestOrderFileRef>? files { get; set; }
}

public class JdRequestOrderContactPerson
{
    public string? name { get; set; }
    public string? title { get; set; }
    public string? department { get; set; }
    public string? company { get; set; }
    public string? vat { get; set; }
    public string? email { get; set; }
    public string? telephoneDirect { get; set; }
    public string? telephoneMobile { get; set; }
}

public class JdRequestOrderOrderRef { public long? id { get; set; } }

public class JdRequestOrderShipmondo
{
    public string? carrierCode { get; set; }
    public string? productCode { get; set; }
    public List<string>? productServices { get; set; }
    public string? pickupPointId { get; set; }
    public string? carrierInstructions { get; set; }
    public long? draftShipmentId { get; set; }
}

public class JdRequestOrderProductItem
{
    public int quantity { get; set; }
    public JdRequestOrderProductCatalog catalog { get; set; } = new();
    public DateTime? bestBefore { get; set; }
}

public class JdRequestOrderProductCatalog
{
    public string? sku { get; set; }
}

public class JdRequestOrderFileRef
{
    public long id { get; set; }
    public bool? packageLabel { get; set; }
}

// File upload models
public class JdFileCreate
{
    public string? displayName { get; set; }
    public string? description { get; set; }
}

public class JdFileResponse
{
    // JD API returns "presignedUrl" (lowercase 's'), not "preSignedUrl"
    public string? presignedUrl { get; set; }
    public DateTime? expiresAt { get; set; }
    
    // The 'file' property contains the actual file metadata including the ID
    public JdFileInfo? file { get; set; }
    
    // For convenience, expose the file ID at root level
    public long id => file?.id ?? 0;
}

public class JdFileInfo
{
    public long id { get; set; }
    public string? name { get; set; }
    public string? displayName { get; set; }
    public bool? verified { get; set; }
    public string? description { get; set; }
    public string? url { get; set; }
    public string? mimeType { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
}

public class JdFileVerify
{
    public long id { get; set; }
}


