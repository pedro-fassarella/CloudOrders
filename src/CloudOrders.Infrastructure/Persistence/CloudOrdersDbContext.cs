using CloudOrders.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace CloudOrders.Infrastructure.Persistence;

public sealed class CloudOrdersDbContext(DbContextOptions<CloudOrdersDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var order = modelBuilder.Entity<Order>();

        order.ToTable("orders");
        order.HasKey(item => item.Id);
        order.Property(item => item.CustomerId)
            .HasMaxLength(100)
            .IsRequired();
        order.Property(item => item.CreatedAtUtc)
            .IsRequired();
        order.Property(item => item.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        var processedMessage = modelBuilder.Entity<ProcessedMessage>();

        processedMessage.ToTable(
            "processed_messages",
            table => table.HasCheckConstraint(
                "CK_processed_messages_state",
                "state IN ('Processing', 'Completed')"));
        processedMessage.HasKey(item => new { item.ConsumerName, item.MessageId });
        processedMessage.Property(item => item.ConsumerName)
            .HasColumnName("consumer_name")
            .HasMaxLength(64)
            .IsRequired();
        processedMessage.Property(item => item.MessageId)
            .HasColumnName("message_id")
            .HasMaxLength(128)
            .IsRequired();
        processedMessage.Property(item => item.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        processedMessage.Property(item => item.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .IsRequired();
        processedMessage.Property(item => item.CompletedAtUtc)
            .HasColumnName("completed_at_utc");
    }
}
