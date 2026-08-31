using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The merge, which is the only thing about this store that is not obvious, and the isolation of
/// the write from the caller that asked for it. What the merge has to hold is that a writer
/// touching one key changes one key: the operator's Save and a provider materialising a default of
/// its own both go through here, and either one replacing the row would silently drop what the
/// other decided.
/// </summary>
public class IntegrationSettingStoreTests
{
    /// <summary>2026-08-19T09:01:12Z. Any instant; nothing here reads a real clock.</summary>
    private static readonly Instant WrittenAt = Instant.FromUnixTimeSeconds(1_787_130_072);

    /// <summary>
    /// Stands in for whatever the caller has staged and not committed. Derived from the seeded
    /// margin rather than typed, so it cannot silently become equal to it and pass by accident.
    /// </summary>
    private const int StagedMarginMinutes = SchedulingDefaults.MarginMinutes + 1;

    /// <summary>The keys a provider might describe. Their meaning is not this store's business.</summary>
    private const string WriteCalendarKey = "writeCalendar";

    private const string BusyCalendarsKey = "busyCalendars";

    private const string WriteCalendar = "operator@example.com";

    private const string OtherWriteCalendar = "team@example.com";

    private const string BusyCalendar = "holidays@example.com";

    [Fact]
    public async Task ReadAsync_ForAProviderNobodyConfigured_AnswersEmpty()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();

        var values = await NewStore(context, container).ReadAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        values.ShouldBeEmpty();
    }

    /// <summary>
    /// The isolation, and the whole reason the write has a context of its own. This store is
    /// written to from a READ path — a provider materialising a default mid-freebusy — and that
    /// read runs inside <see cref="Kairos.Api.Features.Bookings.BookingMover.ReleaseAndCheckAsync"/>,
    /// where a booking sits Cancelled on disk with a restore still to come. On the caller's context
    /// this write's <c>SaveChangesAsync</c> is the caller's, and it commits whatever they had staged
    /// and not yet decided to keep.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhileTheCallerHasWorkStaged_LeavesThatWorkUncommitted()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();

        var staged = await context.Settings.SingleAsync(TestContext.Current.CancellationToken);
        staged.MarginMinutes = StagedMarginMinutes;

        await NewStore(context, container).WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        await using var reader = database.CreateContext();
        var committed = await reader.Settings.SingleAsync(TestContext.Current.CancellationToken);
        committed.MarginMinutes.ShouldBe(SchedulingDefaults.MarginMinutes);

        // Asserted too, or the test above passes just as well against a WriteAsync that does
        // nothing at all.
        var values = await ReadBackAsync(database, container);
        values[WriteCalendarKey].GetString().ShouldBe(WriteCalendar);
    }

    /// <summary>
    /// The other half of the same isolation, and the half that bites without a race to lose. An
    /// insert that fails — two first loads against the unique index, or SQLITE_BUSY behind another
    /// writer — used to stay Added on the caller's change tracker, where every later
    /// <c>SaveChangesAsync</c> retries it. The provider materialising the default logs the failure
    /// and carries on by design, so the throw lands on whatever that request saved next: for a
    /// reschedule, the save that puts the booking back confirmed.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ForAProviderWithNoRowYet_LeavesNothingOnTheCallersChangeTracker()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();

        await NewStore(context, container).WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        context.ChangeTracker.Entries<IntegrationSetting>().ShouldBeEmpty();
    }

    /// <summary>
    /// The merge. A second writer that knows one key must leave the key it has never heard of
    /// exactly as the first writer left it — a replace here is how a default materialised behind
    /// the operator deletes the calendar they chose.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ForAKeyAnotherWriterDoesNotKnow_KeepsWhatTheFirstWriteLeft()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();
        var store = NewStore(context, container);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((BusyCalendarsKey, Value(BusyCalendar))),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database, container);
        values[WriteCalendarKey].GetString().ShouldBe(WriteCalendar);
        values[BusyCalendarsKey].GetString().ShouldBe(BusyCalendar);
    }

    [Fact]
    public async Task WriteAsync_ForAKeyThatIsAlreadySet_ReplacesThatKeysValue()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();
        var store = NewStore(context, container);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(OtherWriteCalendar))),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database, container);
        values[WriteCalendarKey].GetString().ShouldBe(OtherWriteCalendar);
    }

    /// <summary>
    /// Removal, which a merge cannot express by leaving a key out. Without this an operator
    /// clearing an optional field stores a null, and "unset" has two spellings — an absent key and
    /// a present null — that every reader of these settings then has to know about.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ForAKeySetToNull_DeletesItRatherThanStoringANull()
    {
        using var database = new TestDatabase();
        await using var container = NewContainer(database);
        await using var context = database.CreateContext();
        var store = NewStore(context, container);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar)), (BusyCalendarsKey, Value(BusyCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((BusyCalendarsKey, Nothing())),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database, container);
        values.ShouldNotContainKey(BusyCalendarsKey);
        values[WriteCalendarKey].GetString().ShouldBe(WriteCalendar);
    }

    private static IntegrationSettingStore NewStore(
        KairosDbContext context,
        IServiceProvider container) =>
        new(
            context,
            container.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(WrittenAt));

    /// <summary>
    /// The scope the store's write opens, over the database the caller's context is already on.
    /// </summary>
    /// <remarks>
    /// The options are built by hand and registered as a singleton rather than through
    /// <c>AddDbContext</c>, for the reason <see cref="Features.KairosApp"/> writes down: AddDbContext
    /// keys EF's model on the container that registered it, and
    /// <see cref="Kairos.Api.Availability.AvailabilityQuery"/>'s compiled queries are static and bind
    /// to the first model they meet, so a second model in this process fails a later suite. Built
    /// this way the model is <see cref="TestDatabase"/>'s, which is the point: the store's context
    /// and the caller's have to be the same context on the same model, or the isolation asserted
    /// below would be an artefact of the fixture.
    /// </remarks>
    private static ServiceProvider NewContainer(TestDatabase database)
    {
        var services = new ServiceCollection();

        services.AddSingleton(new DbContextOptionsBuilder<KairosDbContext>()
            .UseSqlite(database.ConnectionString)
            .Options);

        services.AddScoped<KairosDbContext>();

        return services.BuildServiceProvider();
    }

    private static JsonElement Value(string value) => JsonSerializer.SerializeToElement(value);

    /// <summary>What the panel sends for a field the operator cleared.</summary>
    private static JsonElement Nothing() => JsonSerializer.SerializeToElement((string?)null);

    private static Dictionary<string, JsonElement> Map(
        params (string Key, JsonElement Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    /// <summary>
    /// A second context and the store's own read, so what is asserted is the JSON that reached the
    /// file rather than a dictionary still in memory.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, JsonElement>> ReadBackAsync(
        TestDatabase database,
        IServiceProvider container)
    {
        await using var context = database.CreateContext();
        return await NewStore(context, container).ReadAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);
    }
}
