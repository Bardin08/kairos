using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using Kairos.Api.Integrations;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Kairos.Api.Features.Integrations;

/// <summary>One field as the settings panel renders it. No vendor name is in any of it.</summary>
/// <param name="Kind">
/// The wire spelling <see cref="IntegrationSettingsReader"/> mints, never the enum: the panel
/// branches on this string, and the enum's own name is not a contract with anybody.
/// </param>
public sealed record SettingFieldResponse(
    string Key,
    string Kind,
    string Label,
    string? Help,
    IReadOnlyList<SettingOption>? Options,
    int? MaxValues,
    bool Required,
    JsonElement Value);

/// <summary>
/// One provider's whole panel. <see cref="IIntegrationSettings.EnabledKey"/> is always the first
/// field, and every connected provider has it.
/// </summary>
public sealed record IntegrationSettingsResponse(
    string Provider,
    IReadOnlyList<SettingFieldResponse> Fields);

public sealed record SaveIntegrationSettingsRequest
{
    /// <summary>
    /// The whole map, not a patch: the panel has one Save and sends back what it is showing. An
    /// explicit null is refused here rather than reaching the writer, for the reason
    /// <see cref="Admin.CreateInviteRequest.Prefill"/> gives — System.Text.Json ignores the
    /// nullable annotation, so nothing else would.
    /// </summary>
    [Required]
    public required IReadOnlyDictionary<string, JsonElement> Values { get; init; }
}

/// <summary>
/// Two routes and the shapes they answer in, on the admin group like every other operator route.
/// Everything either of them does is <see cref="IntegrationSettingsReader"/>'s or
/// <see cref="IntegrationSettingsWriter"/>'s.
/// </summary>
public static class IntegrationSettingsEndpoints
{
    public static RouteGroupBuilder MapIntegrationSettingsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/integrations/{provider}/settings", ReadAsync)
            .WithName("ReadIntegrationSettings");

        group.MapPut("/integrations/{provider}/settings", SaveAsync)
            .WithName("SaveIntegrationSettings");

        return group;
    }

    private static Task<IntegrationSettingsResponse> ReadAsync(
        string provider,
        IntegrationSettingsReader reader,
        CancellationToken cancellationToken) =>
        reader.ReadAsync(provider, cancellationToken);

    private static async Task<NoContent> SaveAsync(
        string provider,
        SaveIntegrationSettingsRequest request,
        IntegrationSettingsWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.SaveAsync(provider, request.Values, cancellationToken);
        return TypedResults.NoContent();
    }
}
