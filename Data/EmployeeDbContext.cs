using Microsoft.EntityFrameworkCore;

namespace HR.McpServer;

// Employee record with leave balances.
public class Employee
{
    public string EmployeeId { get; set; } = string.Empty; // e.g., "EMP001"
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // e.g., "Probation", "Permanent"
    public int CasualLeaveBalance { get; set; }
    public int SickLeaveBalance { get; set; }
    public int AnnualLeaveBalance { get; set; }
}

// Document class.
public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = string.Empty; // "Policy" | "Contract"
    public string? EmployeeId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

// One chunk of a Document's text plus its embedding vector, used for RAG similarity search.
public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Type { get; set; } = string.Empty; // copied from Document, for scope filtering
    public string? EmployeeId { get; set; } // copied from Document, for scope filtering
    public string FileName { get; set; } = string.Empty; // copied from Document, for display
    public string Text { get; set; } = string.Empty;
    public string Vector { get; set; } = string.Empty; // comma-separated floats
}

// DB context.
public class EmployeeDbContext : DbContext
{
    public DbSet<Employee> Employees { get; set; } = null!;
    public DbSet<Document> Documents { get; set; } = null!;
    public DbSet<DocumentChunk> DocumentChunks { get; set; } = null!;

    // Local SQL database.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string connectionString = "Server=localhost\\SQLEXPRESS;Database=HR_Leave_DB;Trusted_Connection=True;TrustServerCertificate=True;";
        optionsBuilder.UseSqlServer(connectionString);
    }

    // Primary keys (no migrations).
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>().HasKey(e => e.EmployeeId);
        modelBuilder.Entity<Document>().HasKey(d => d.Id);
        modelBuilder.Entity<DocumentChunk>().HasKey(c => c.Id);
    }
}