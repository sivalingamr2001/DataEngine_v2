namespace DataEngine.Core.Configuration;

/// <summary>
/// Audit logging configuration.
/// </summary>
public sealed class AuditOptions
{
    public string ReadAuditTableName { get; set; } = "de_audit_read_log";

    public string WriteAuditTableName { get; set; } = "de_audit_write_log";

    public bool PersistWriteAudits { get; set; } = true;

    public bool PersistReadAudits { get; set; } = true;

    public int ChannelCapacity { get; set; } = 5_000;

    public int BatchSize { get; set; } = 50;

    /// <summary>Standard audit column names applied on insert/update when present.</summary>
    public AuditColumnOptions AuditColumns { get; set; } = new();
}

public sealed class AuditColumnOptions
{
    public string CreatedAt { get; set; } = "created_at";

    public string CreatedBy { get; set; } = "created_by";

    public string UpdatedAt { get; set; } = "updated_at";

    public string UpdatedBy { get; set; } = "updated_by";
}
