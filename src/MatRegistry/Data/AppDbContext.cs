using MatRegistry.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatRegistry.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId, UpdateDate and UpdateUserId
/// (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/>), and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// The layers of the images are not in the database: they lie in the blob store (<c>Services/Registry/BlobStore</c>); the manifests are (they are small and their bytes count).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? current = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Registry> Registries => Set<Registry>();

    public DbSet<RegistryAccessEntry> RegistryAccess => Set<RegistryAccessEntry>();

    public DbSet<Repository> Repositories => Set<Repository>();

    public DbSet<Manifest> Manifests => Set<Manifest>();

    public DbSet<ManifestReference> ManifestReferences => Set<ManifestReference>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<Blob> Blobs => Set<Blob>();

    public DbSet<RepositoryBlob> RepositoryBlobs => Set<RepositoryBlob>();

    public DbSet<BlobUpload> BlobUploads => Set<BlobUpload>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    public DbSet<CopyJob> CopyJobs => Set<CopyJob>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.Property(x => x.Scheme).HasMaxLength(10);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
            e.HasIndex(x => x.IdentitySessionId);
        });

        b.Entity<Registry>(e =>
        {
            e.ToTable("Registry");
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.Username).HasMaxLength(255);
            e.Property(x => x.SecretProtected).HasMaxLength(4000);
            e.Property(x => x.LastCheckResult).HasMaxLength(1000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<RegistryAccessEntry>(e =>
        {
            e.ToTable("RegistryAccess");
            e.HasIndex(x => new { x.RegistryId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Registry).WithMany(x => x.Access).HasForeignKey(x => x.RegistryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Repository>(e =>
        {
            e.ToTable("Repository");
            e.HasIndex(x => new { x.RegistryId, x.Name }).IsUnique();
            e.HasOne(x => x.Registry).WithMany().HasForeignKey(x => x.RegistryId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Description).HasMaxLength(2000);
        });

        b.Entity<Manifest>(e =>
        {
            e.ToTable("Manifest");
            e.HasIndex(x => new { x.RepositoryId, x.Digest }).IsUnique();
            e.HasIndex(x => x.Digest);
            e.HasIndex(x => new { x.RepositoryId, x.SubjectDigest });
            e.HasOne(x => x.Repository).WithMany(x => x.Manifests).HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Digest).HasMaxLength(100);
            e.Property(x => x.MediaType).HasMaxLength(255);
            e.Property(x => x.ArtifactType).HasMaxLength(255);
            e.Property(x => x.SubjectDigest).HasMaxLength(100);
            e.Property(x => x.Os).HasMaxLength(40);
            e.Property(x => x.Architecture).HasMaxLength(40);
            e.Property(x => x.Variant).HasMaxLength(40);
        });

        b.Entity<ManifestReference>(e =>
        {
            e.ToTable("ManifestReference");
            e.HasIndex(x => x.ManifestId);
            e.HasIndex(x => x.Digest);
            e.HasOne(x => x.Manifest).WithMany(x => x.References).HasForeignKey(x => x.ManifestId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Digest).HasMaxLength(100);
            e.Property(x => x.MediaType).HasMaxLength(255);
            e.Property(x => x.Platform).HasMaxLength(80);
        });

        b.Entity<Tag>(e =>
        {
            e.ToTable("Tag");
            e.HasIndex(x => new { x.RepositoryId, x.Name }).IsUnique();
            e.HasIndex(x => x.ManifestId);
            e.HasOne(x => x.Repository).WithMany(x => x.Tags).HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Manifest).WithMany().HasForeignKey(x => x.ManifestId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128);
        });

        b.Entity<Blob>(e =>
        {
            e.ToTable("Blob");
            e.HasIndex(x => x.Digest).IsUnique();
            e.Property(x => x.Digest).HasMaxLength(100);
        });

        b.Entity<RepositoryBlob>(e =>
        {
            e.ToTable("RepositoryBlob");
            e.HasIndex(x => new { x.RepositoryId, x.BlobId }).IsUnique();
            e.HasIndex(x => x.BlobId);
            e.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Blob).WithMany().HasForeignKey(x => x.BlobId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BlobUpload>(e =>
        {
            e.ToTable("BlobUpload");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AccessToken>(e =>
        {
            e.ToTable("AccessToken");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.SecretHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.SecretPrefix).HasMaxLength(16);
        });

        b.Entity<CopyJob>(e =>
        {
            e.ToTable("CopyJob");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.State);
            e.HasOne(x => x.SourceRegistry).WithMany().HasForeignKey(x => x.SourceRegistryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TargetRegistry).WithMany().HasForeignKey(x => x.TargetRegistryId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SourceRepository).HasMaxLength(255);
            e.Property(x => x.SourceReference).HasMaxLength(128);
            e.Property(x => x.TargetRepository).HasMaxLength(255);
            e.Property(x => x.TargetTag).HasMaxLength(128);
            e.Property(x => x.Digest).HasMaxLength(100);
            e.Property(x => x.Message).HasMaxLength(1000);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activity");
            e.HasIndex(x => x.OccurredDate);
            e.Property(x => x.Consumer).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Target).HasMaxLength(255);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.Property(x => x.PublicUrl).HasMaxLength(300);
            e.Property(x => x.LastGcResult).HasMaxLength(500);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var user = current?.Id;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= user;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=matregistry;Username=matregistry;Password=matregistry").Options);
}
