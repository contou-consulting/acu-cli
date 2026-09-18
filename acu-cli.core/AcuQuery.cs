namespace AcuCli.Core;

/// <summary>
/// Query options supported by Acumatica contract-based endpoints, verified against a real
/// site's swagger.json ($select, $filter, $expand, $custom, $top, $skip).
/// </summary>
public sealed record AcuQuery
{
    /// <summary>Raw <c>$filter</c> expression, e.g. <c>InventoryID eq 'AALEGO500'</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>Comma-separated list of fields for <c>$select</c>, e.g. <c>InventoryID,Description</c>.</summary>
    public string? Select { get; init; }

    /// <summary>Comma-separated list of related views for <c>$expand</c>, e.g. <c>SalesOrderDetails</c>.</summary>
    public string? Expand { get; init; }

    /// <summary>
    /// Comma-separated list of custom (not-in-contract) fields for <c>$custom</c>, as documented by the site.
    /// </summary>
    public string? Custom { get; init; }

    /// <summary>Maximum records to return (<c>$top</c>). Null or 0 means no limit is sent.</summary>
    public int? Top { get; init; }

    /// <summary>Records to skip (<c>$skip</c>).</summary>
    public int? Skip { get; init; }
}
