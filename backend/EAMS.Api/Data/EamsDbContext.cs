using EAMS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Api.Data;

public class EamsDbContext : DbContext
{
    public EamsDbContext(DbContextOptions<EamsDbContext> options) : base(options) { }

    public DbSet<School> Schools => Set<School>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<RfidCard> RfidCards => Set<RfidCard>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Student>().Ignore(s => s.FullName);

        b.Entity<Student>().HasIndex(s => new { s.SchoolId, s.StudentNumber }).IsUnique();
        b.Entity<RfidCard>().HasIndex(c => c.CardUid).IsUnique();

        // One attendance per student per event (TimeInOut updates the same row).
        b.Entity<AttendanceRecord>().HasIndex(a => new { a.EventId, a.StudentId }).IsUnique();

        b.Entity<RfidCard>()
            .HasOne(c => c.Student).WithMany(s => s.Cards)
            .HasForeignKey(c => c.StudentId);

        b.Entity<AttendanceRecord>()
            .HasOne(a => a.Event).WithMany(e => e.AttendanceRecords)
            .HasForeignKey(a => a.EventId);

        b.Entity<AttendanceRecord>()
            .HasOne(a => a.Student).WithMany()
            .HasForeignKey(a => a.StudentId);
    }
}
