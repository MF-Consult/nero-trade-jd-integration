namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

public class JdInventory
{
    
    public long? id { get; set; }
    public DateTime? createdOn { get; set; }
    public long? createdById { get; set; }
    public DateTime? modifiedOn { get; set; }
    public long? modifiedById { get; set; }
    public bool? food { get; set; }
    public string? email { get; set; }
    public string? name { get; set; }
    public bool? shopifyEnabled { get; set; }
    public bool? wooCommerceEnabled { get; set; }
    public int? billingInterval { get; set; }
    public List<JdInventoryOrder>? orders { get; set; }
}

public class JdInventoryOrder
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