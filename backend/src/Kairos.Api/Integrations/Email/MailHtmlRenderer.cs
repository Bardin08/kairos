using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// <see cref="MailBody"/> as the <c>text/html</c> half, by rendering
/// <see cref="KairosMail"/> to a string.
/// </summary>
/// <remarks>
/// Razor rather than a template package because it costs no dependency — HtmlRenderer renders a
/// component outside any HTTP request, from the shared framework this application already
/// references — and because it is checked at build time, where a Liquid template that no longer
/// matches its model renders empty and ships.
///
/// <para>
/// Two things about HtmlRenderer that are not obvious, both measured on .NET 10 rather than
/// assumed. It ignores <c>@layout</c> — the directive only names a type and LayoutView is what
/// applies it, so a component rendered outside routing comes back unwrapped. That is settled
/// behaviour, not a pending fix: dotnet/aspnetcore#55068 is closed as by design. Hence one
/// component holding the whole skeleton rather than a layout and seven pages. And a render off
/// the renderer's own dispatcher throws "The current thread is not associated with the
/// Dispatcher", which is why this is async and why a caller cannot simply ask for a string.
/// </para>
/// </remarks>
public sealed class MailHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
{
    public async Task<string> RenderAsync(MailBody body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var renderer = new HtmlRenderer(services, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(KairosMail.Body)] = body });

            var output = await renderer.RenderComponentAsync<KairosMail>(parameters);
            return output.ToHtmlString();
        });
    }
}
