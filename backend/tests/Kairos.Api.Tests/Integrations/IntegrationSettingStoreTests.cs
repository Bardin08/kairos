using System.Text.Json;

using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The merge, which is the only thing about this store that is not obvious. What it has to hold is
/// that a writer touching one key changes one key: the operator's Save and a provider
/// materialising a default of its own both go through here, and either one replacing the row would
/// silently drop what the other decided.
/// </summary>
public class IntegrationSettingStoreTests
{
    /// <summary>2026-08-19T09:01:12Z. Any instant; nothing here reads a real clock.</summary>
    private static readonly Instant WrittenAt = Instant.FromUnixTimeSeconds(1_787_130_072);

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
        await using var context = database.CreateContext();

        var values = await NewStore(context).ReadAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);

        values.ShouldBeEmpty();
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
        await using var context = database.CreateContext();
        var store = NewStore(context);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((BusyCalendarsKey, Value(BusyCalendar))),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database);
        values[WriteCalendarKey].GetString().ShouldBe(WriteCalendar);
        values[BusyCalendarsKey].GetString().ShouldBe(BusyCalendar);
    }

    [Fact]
    public async Task WriteAsync_ForAKeyThatIsAlreadySet_ReplacesThatKeysValue()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var store = NewStore(context);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(OtherWriteCalendar))),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database);
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
        await using var context = database.CreateContext();
        var store = NewStore(context);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((WriteCalendarKey, Value(WriteCalendar)), (BusyCalendarsKey, Value(BusyCalendar))),
            TestContext.Current.CancellationToken);

        await store.WriteAsync(
            IntegrationProvider.Google,
            Map((BusyCalendarsKey, Nothing())),
            TestContext.Current.CancellationToken);

        var values = await ReadBackAsync(database);
        values.ShouldNotContainKey(BusyCalendarsKey);
        values[WriteCalendarKey].GetString().ShouldBe(WriteCalendar);
    }

    private static IntegrationSettingStore NewStore(KairosDbContext context) =>
        new(context, new FixedClock(WrittenAt));

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
        TestDatabase database)
    {
        await using var context = database.CreateContext();
        return await NewStore(context).ReadAsync(
            IntegrationProvider.Google, TestContext.Current.CancellationToken);
    }
}
