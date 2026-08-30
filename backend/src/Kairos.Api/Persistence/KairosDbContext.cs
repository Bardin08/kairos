using System.Data.Common;

using Kairos.Api.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kairos.Api.Persistence;

public sealed class KairosDbContext(DbContextOptions<KairosDbContext> options) : DbContext(options)
{
    public DbSet<Settings> Settings => Set<Settings>();

    public DbSet<Pattern> Patterns => Set<Pattern>();

    public DbSet<PatternRule> PatternRules => Set<PatternRule>();

    public DbSet<PatternOverride> PatternOverrides => Set<PatternOverride>();

    public DbSet<Link> Links => Set<Link>();

    public DbSet<LinkDuration> LinkDurations => Set<LinkDuration>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<BookingParticipant> BookingParticipants => Set<BookingParticipant>();

    public DbSet<BookingProposal> BookingProposals => Set<BookingProposal>();

    public DbSet<PersonalInvite> PersonalInvites => Set<PersonalInvite>();

    public DbSet<IntegrationCredential> IntegrationCredentials => Set<IntegrationCredential>();

    public DbSet<IntegrationSetting> IntegrationSettings => Set<IntegrationSetting>();

    public DbSet<CalendarEntry> CalendarEntries => Set<CalendarEntry>();

    public DbSet<ConferenceLink> ConferenceLinks => Set<ConferenceLink>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<FollowUp> FollowUps => Set<FollowUp>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(PragmaInterceptor.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KairosDbContext).Assembly);

    /// <summary>
    /// Re-applies the pragmas every time EF opens a connection. Private and nested because it is
    /// wiring for this context rather than a type anything else should reach for — code that
    /// owns its own connection calls <see cref="SqlitePragmas.Apply"/> directly.
    /// </summary>
    private sealed class PragmaInterceptor : DbConnectionInterceptor
    {
        public static readonly PragmaInterceptor Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            SqlitePragmas.Apply((SqliteConnection)connection);

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            SqlitePragmas.Apply((SqliteConnection)connection);
            return Task.CompletedTask;
        }
    }
}
