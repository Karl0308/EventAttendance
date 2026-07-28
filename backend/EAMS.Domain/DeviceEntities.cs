namespace EAMS.Domain;

// Technical Plan §4.10 — RFID readers / kiosks.
public class Device : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string Name { get; set; } = "";
    public string DeviceType { get; set; } = ""; // Mobile/Kiosk/Handheld
    public string? ReaderModel { get; set; }
    public string? ApiKey { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
}
