using CloudOrders.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace CloudOrders.Infrastructure.Persistence;

public sealed class CloudOrdersDbContext(DbContextOptions<CloudOrdersDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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

        var outboxMessage = modelBuilder.Entity<OutboxMessage>();

        outboxMessage.ToTable(
            "outbox_messages",
            table => table.HasCheckConstraint(
                "CK_outbox_messages_attempt_count",
                "attempt_count >= 0"));
        outboxMessage.HasKey(item => item.Id);
        outboxMessage.HasIndex(item => item.MessageId).IsUnique();
        outboxMessage.HasIndex(item => new { item.PublishedAtUtc, item.CreatedAtUtc });
        outboxMessage.HasIndex(item => item.OrderId);
        outboxMessage.Property(item => item.OrderId)
            .HasColumnName("order_id")
            .IsRequired();
        outboxMessage.Property(item => item.MessageId)
            .HasColumnName("message_id")
            .HasMaxLength(128)
            .IsRequired();
        outboxMessage.Property(item => item.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(128)
            .IsRequired();
        outboxMessage.Property(item => item.Subject)
            .HasColumnName("subject")
            .HasMaxLength(256)
            .IsRequired();
        outboxMessage.Property(item => item.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(128)
            .IsRequired();
        outboxMessage.Property(item => item.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("text")
            .IsRequired();
        outboxMessage.Property(item => item.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();
        outboxMessage.Property(item => item.PublishedAtUtc)
            .HasColumnName("published_at_utc");
        outboxMessage.Property(item => item.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .IsRequired();
        outboxMessage.Property(item => item.LastAttemptAtUtc)
            .HasColumnName("last_attempt_at_utc");
        outboxMessage.Property(item => item.LastErrorType)
            .HasColumnName("last_error_type")
            .HasMaxLength(256);
        outboxMessage.Property(item => item.TraceParent)
            .HasColumnName("trace_parent")
            .HasMaxLength(128);
        outboxMessage.Property(item => item.TraceState)
            .HasColumnName("trace_state")
            .HasColumnType("text");
    }
}
