using System.Net;

using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The seam itself, rather than anything Kairos does behind it. Every other Google suite reads its
/// verdict through this handler, so a handler that quietly drops a retry, cannot say "503", or
/// answers from whichever stub was registered first does not produce a wrong result — it produces
/// a green suite that proved nothing.
/// </summary>
/// <remarks>
/// The first two go through a real <see cref="CalendarService"/> because the client library is the
/// subject: the retry count and the gzipped request body are its behaviour, not ours. The last one
/// drives the handler directly, since matching a stub involves no library at all.
/// </remarks>
public class FakeGoogleHandlerTests
{
    private const string ApplicationName = "Kairos";

    private const string FreebusyPath = "/freeBusy";

    /// <summary>Google's alias for the signed-in account's own calendar.</summary>
    private const string PrimaryCalendarAlias = "primary";

    /// <summary>The general stub, and the specific one that sits inside it.</summary>
    private const string CalendarsPath = "/calendars/";

    private const string EventsPath = "/calendars/primary/events";

    private const string EventIdFromEventsStub = "event-from-the-events-stub";

    private const string EventIdFromCalendarsStub = "event-from-the-calendars-stub";

    /// <summary>Google's shape for a transient backend failure, which its library retries.</summary>
    private const string BackendError =
        """{"error":{"message":"Backend Error","status":"UNAVAILABLE"}}""";

    /// <summary>And for one it does not: a refusal no number of attempts changes.</summary>
    private const string PermissionDenied =
        """{"error":{"message":"Insufficient Permission","status":"PERMISSION_DENIED"}}""";

    /// <summary>
    /// A URI nothing stubs. Absolute because the handler is driven directly here, with no client
    /// library to build one.
    /// </summary>
    private const string UnstubbedUri = "https://www.googleapis.com/calendar/v3/colors";

    /// <summary>
    /// The whole of finding one and half of finding two. A 503 has to reach the client library as
    /// a response so its own retry loop runs, and every attempt has to arrive here with a readable
    /// body — the request message is re-sent, so a handler that consumed the request's stream on
    /// the first pass killed the second inside the harness and reported one attempt where the
    /// library made three.
    /// </summary>
    [Fact]
    public async Task Answering_WithAServiceUnavailable_RetriesTheLibrarysOwnCountWithEveryBodyReadable()
    {
        var handler = new FakeGoogleHandler()
            .Answering(FreebusyPath, BackendError, HttpStatusCode.ServiceUnavailable);

        using var service = CreateService(handler);

        var failure = await Should.ThrowAsync<GoogleApiException>(
            () => QueryFreebusyAsync(service));

        failure.HttpStatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Requests.Count.ShouldBe(service.HttpClient.MessageHandler.NumTries);
        handler.Requests.ShouldAllBe(request =>
            request.Body.Contains(PrimaryCalendarAlias, StringComparison.Ordinal));
        handler.UnstubbedUris.ShouldBeEmpty();
    }

    /// <summary>
    /// The other half of finding two: a status the library does not retry still arrives as that
    /// status. Answering 200 with an error body in it would have read as success to every caller
    /// and as a parse failure to none.
    /// </summary>
    [Fact]
    public async Task Answering_WithARefusalTheLibraryDoesNotRetry_AnswersItOnceWithThatStatus()
    {
        var handler = new FakeGoogleHandler()
            .Answering(FreebusyPath, PermissionDenied, HttpStatusCode.Forbidden);

        using var service = CreateService(handler);

        var failure = await Should.ThrowAsync<GoogleApiException>(
            () => QueryFreebusyAsync(service));

        failure.HttpStatusCode.ShouldBe(HttpStatusCode.Forbidden);
        handler.Requests.ShouldHaveSingleItem().Path.ShouldEndWith(FreebusyPath);
    }

    /// <summary>
    /// Finding four. Both stubs match an events URI, so before longest-fragment matching the
    /// answer was whichever the dictionary happened to yield first — and a suite that stubs a
    /// family of paths plus one member of it got a different Google depending on the order two
    /// lines were written in.
    /// </summary>
    [Fact]
    public async Task Answering_WithOverlappingFragments_AnswersFromTheLongestInEitherOrder()
    {
        var specificFirst = new FakeGoogleHandler()
            .Answering(EventsPath, EventJson(EventIdFromEventsStub))
            .Answering(CalendarsPath, EventJson(EventIdFromCalendarsStub));

        var generalFirst = new FakeGoogleHandler()
            .Answering(CalendarsPath, EventJson(EventIdFromCalendarsStub))
            .Answering(EventsPath, EventJson(EventIdFromEventsStub));

        (await InsertEventAsync(specificFirst)).Id.ShouldBe(EventIdFromEventsStub);
        (await InsertEventAsync(generalFirst)).Id.ShouldBe(EventIdFromEventsStub);
    }

    /// <summary>
    /// Finding three. The throw is caught here on purpose, because that is what the read path does
    /// to it in production — and a test whose subject swallows sees an empty answer rather than a
    /// missing stub. The list is the part that survives the swallow.
    /// </summary>
    [Fact]
    public async Task SendAsync_WithNoStubMatching_RecordsTheMissWhereASwallowingCallerCannotHideIt()
    {
        var handler = new FakeGoogleHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        try
        {
            await client.GetAsync(UnstubbedUri, TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The swallow, restated: GetBusyAsync catches every exception and returns nothing busy.
        }

        handler.UnstubbedUris.ShouldHaveSingleItem().ShouldBe(UnstubbedUri);
        handler.Requests.ShouldHaveSingleItem().Uri.ShouldBe(UnstubbedUri);
    }

    private static CalendarService CreateService(FakeGoogleHandler handler) =>
        new(new BaseClientService.Initializer
        {
            ApplicationName = ApplicationName,
            HttpClientFactory = new FakeGoogleHttpFactory(handler),
        });

    /// <summary>A POST, so there is a compressed request body for the retry to have to re-read.</summary>
    private static Task<FreeBusyResponse> QueryFreebusyAsync(CalendarService service) =>
        service.Freebusy
            .Query(new FreeBusyRequest
            {
                Items = [new FreeBusyRequestItem { Id = PrimaryCalendarAlias }],
            })
            .ExecuteAsync(TestContext.Current.CancellationToken);

    private static async Task<Event> InsertEventAsync(FakeGoogleHandler handler)
    {
        using var service = CreateService(handler);
        return await service.Events
            .Insert(new Event(), PrimaryCalendarAlias)
            .ExecuteAsync(TestContext.Current.CancellationToken);
    }

    private static string EventJson(string id) => $$"""{"id":"{{id}}"}""";
}
