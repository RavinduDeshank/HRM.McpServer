using Microsoft.EntityFrameworkCore;

namespace HR.McpServer;

public class Employee
{
    public string EmployeeId { get; set; } = string.Empty; // e.g., "EMP001"
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // e.g., "Probation", "Permanent"
    public int CasualLeaveBalance { get; set; }
    public int SickLeaveBalance { get; set; }
    public int AnnualLeaveBalance { get; set; }
}

public class EmployeeDbContext : DbContext
{
    public DbSet<Employee> Employees { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string connectionString = "Server=localhost\\SQLEXPRESS;Database=HR_Leave_DB;Trusted_Connection=True;TrustServerCertificate=True;";
        optionsBuilder.UseSqlServer(connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>().HasKey(e => e.EmployeeId);
    }
}