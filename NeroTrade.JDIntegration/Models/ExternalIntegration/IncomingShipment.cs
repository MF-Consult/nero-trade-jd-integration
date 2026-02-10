using System.Text.Json.Serialization;

namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public class JdIncomingLine
{
    public long? id { get; set; }
    public int quantity { get; set; }
    public bool? isSubItem { get; set; }
    public string? externalIdentification { get; set; }
    public string? unit { get; set; }
    
    [JsonIgnore]
    public string? Sku { get; set; }

    public JdIncomingShipmentCatalogRef? catalog { get; set; }
    public JdIncomingShipmentContainerTypeRef? inventoryContainerType { get; set; }
}

// Create DTO (only fields we intend to send)
public class JdIncomingShipmentCreate
{
    public DateTime? date { get; set; }
    public string? notificationEmails { get; set; }
    public JdIncomingShipmentOrderRef? order { get; set; }
    public string? text { get; set; }
    public string? carrier { get; set; }
    public bool? disableApprovalEmail { get; set; }
    
    [JsonIgnore]
    public int? SourcePurchaseNumber { get; set; }

    public List<JdIncomingShipmentFileRef>? files { get; set; }
    public List<JdIncomingLine> lines { get; set; } = new();
}

public class JdIncomingShipment
{
    public long? id { get; set; }
    public DateTime? date { get; set; }
    public string? notificationEmails { get; set; }
    public string? text { get; set; }
    public string? carrier { get; set; }
    public int? status { get; set; }
    public DateTime? modifiedOn { get; set; }
    public List<JdIncomingLine>? lines { get; set; }
    public List<JdRegisteredItem>? registeredItems { get; set; }
}

public class JdRegisteredItem
{
    public long? id { get; set; }
    public long? parentId { get; set; }
    public int quantity { get; set; }
    public string? inventoryContainerTypeName { get; set; }
    public string? inventoryContainerIdentifier { get; set; }
    public JdRegisteredItemCatalog? catalog { get; set; }
}

public class JdRegisteredItemCatalog
{
    public long? id { get; set; }
    public string? sku { get; set; }
}

public class JdIncomingShipmentOrderRef { public long? id { get; set; } }
public class JdIncomingShipmentFileRef { public long? id { get; set; } }
public class JdIncomingShipmentCatalogRef { public long? id { get; set; } }
public class JdIncomingShipmentContainerTypeRef { public long? id { get; set; } }


