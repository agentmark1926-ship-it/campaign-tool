using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactList> Lists => Set<ContactList>();
    public DbSet<ListContact> ListContacts => Set<ListContact>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<EmailEvent> EmailEvents => Set<EmailEvent>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are stored as their names so raw SQL (e.g. the claim query) reads like the spec.
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Contact>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.EmailNormalized).HasMaxLength(320);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.StatusReason).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(50);
            e.HasIndex(x => x.EmailNormalized).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Import).WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ContactList>(e =>
        {
            e.ToTable("Lists");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<ListContact>(e =>
        {
            e.HasKey(x => new { x.ListId, x.ContactId });
            e.HasOne(x => x.List).WithMany(x => x.ListContacts).HasForeignKey(x => x.ListId);
            e.HasOne(x => x.Contact).WithMany(x => x.ListContacts).HasForeignKey(x => x.ContactId);
        });

        b.Entity<Suppression>(e =>
        {
            e.HasKey(x => x.EmailNormalized);
            e.Property(x => x.EmailNormalized).HasMaxLength(320);
            e.Property(x => x.Source).HasMaxLength(100);
        });

        b.Entity<Template>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Campaign>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.Preheader).HasMaxLength(500);
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.FromEmail).HasMaxLength(320);
            e.Property(x => x.ReplyTo).HasMaxLength(320);
            e.Property(x => x.ExcludeListIds).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.ScheduledAtUtc });
            e.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CampaignRecipient>(e =>
        {
            e.Property(x => x.EmailSnapshot).HasMaxLength(320);
            e.Property(x => x.AcsMessageId).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.CampaignId, x.ContactId }).IsUnique();
            e.HasIndex(x => new { x.CampaignId, x.Status, x.NextAttemptAtUtc });
            e.HasIndex(x => x.AcsMessageId);
            e.HasOne(x => x.Campaign).WithMany().HasForeignKey(x => x.CampaignId);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId);
        });

        b.Entity<EmailEvent>(e =>
        {
            e.Property(x => x.EventId).HasMaxLength(100);
            e.Property(x => x.AcsMessageId).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => x.CampaignRecipientId);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasOne(x => x.CampaignRecipient).WithMany().HasForeignKey(x => x.CampaignRecipientId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Import>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.BlobPath).HasMaxLength(500);
            e.Property(x => x.Delimiter).HasMaxLength(2);
            e.HasIndex(x => x.Status);
        });

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }
}
