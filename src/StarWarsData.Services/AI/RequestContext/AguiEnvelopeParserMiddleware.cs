using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.RequestContext;

/// <summary>
/// Reads the most recent user message from an AGUI POST body, parses the
/// bracketed envelope (<c>[CONTINUITY:][PAGE:][SUBJECT:][FACETS:]</c>), and
/// pins the parsed values onto the scoped <see cref="CurrentRequestContext"/>
/// so tools can read them without the agent having to pass parameters.
///
/// Wired in <c>ApiService/Program.cs</c> on the two AGUI streaming endpoints
/// (<c>/kernel/stream</c> and <c>/copilot/stream</c>). See ADR-008 +
/// Design-029.
/// </summary>
public static class AguiEnvelopeParserMiddleware
{
    static readonly Regex ContinuityRx = new(@"\[CONTINUITY:\s*([^\]]+?)\s*\]", RegexOptions.Compiled);
    static readonly Regex PageRx = new(@"\[PAGE:\s*([^\]]+?)\s*\]", RegexOptions.Compiled);
    static readonly Regex SubjectIdRx = new(@"\[SUBJECT:[^#]*#(\d+)", RegexOptions.Compiled);
    static readonly Regex RealmRx = new(@"\[REALM:\s*([^\]]+?)\s*\]", RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the request is a POST to an AGUI streaming endpoint
    /// we want to intercept. Used to gate the body-buffering cost.
    /// </summary>
    public static bool IsAguiPost(HttpContext context) =>
        context.Request.Method == "POST" && (context.Request.Path.StartsWithSegments("/kernel/stream") || context.Request.Path.StartsWithSegments("/copilot/stream"));

    /// <summary>
    /// Middleware delegate: peek the body, parse the envelope, populate the
    /// scoped context, then yield to the AGUI handler. Re-buffering means
    /// the AGUI deserializer reads the same bytes downstream.
    /// </summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAguiPost(context))
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        try
        {
            await ReadAndParseEnvelopeAsync(context);
        }
        catch
        {
            // Best-effort — parser failures must never block the request.
            // Tools just fall back to null context (today's behaviour).
        }
        finally
        {
            context.Request.Body.Position = 0;
        }

        await next(context);
    }

    static async Task ReadAndParseEnvelopeAsync(HttpContext context)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return;

        // Walk the messages array backwards to find the most recent user message.
        // It's the one carrying the envelope this turn.
        string? content = null;
        for (int i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.TryGetProperty("role", out var role) && role.GetString() == "user" && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                content = c.GetString();
                break;
            }
        }
        if (string.IsNullOrEmpty(content))
            return;

        var ctx = context.RequestServices.GetService<CurrentRequestContext>();
        if (ctx is null)
            return;

        if (
            ContinuityRx.Match(content) is { Success: true } cm
            && Enum.TryParse<Continuity>(cm.Groups[1].Value, ignoreCase: true, out var continuity)
            && continuity is not Models.Entities.Continuity.Both and not Models.Entities.Continuity.Unknown
        )
        {
            ctx.Continuity = continuity;
        }

        if (RealmRx.Match(content) is { Success: true } rm && Enum.TryParse<Realm>(rm.Groups[1].Value, ignoreCase: true, out var realm) && realm is not Models.Entities.Realm.Unknown)
        {
            ctx.Realm = realm;
        }

        if (PageRx.Match(content) is { Success: true } pm)
            ctx.Page = pm.Groups[1].Value;

        if (SubjectIdRx.Match(content) is { Success: true } sm && int.TryParse(sm.Groups[1].Value, out var sid))
            ctx.SubjectId = sid;
    }
}
