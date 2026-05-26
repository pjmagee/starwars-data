using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace StarWarsData.Services;

/// <summary>
/// Agent-run middleware that bridges AGUI frontend (client-side) tools through
/// <see cref="FunctionInvokingChatClient"/> safely. Solves the "orphan FCC" bug where
/// FIC terminates the function-call loop on the FIRST declaration-only tool it sees in
/// an iteration, dropping any server-tool invocations queued in the SAME iteration —
/// which leaks unpaired FCCs to the browser and breaks the next round-trip's OpenAI
/// call ("No tool output found for function call call_X").
///
/// <para>
/// AGUI hosting (1.6.2) advertises browser-side tools to the LLM as
/// <see cref="AIFunctionDeclaration"/>s — declarations without bodies — via
/// <c>RunAgentInput.Tools.AsAITools()</c>. When OpenAI emits FCCs for one server tool
/// AND one client tool in the same iteration, M.E.AI's FIC sees the declaration-only
/// client tool, calls <c>ShouldTerminateLoopBasedOnHandleableFunctions</c> → true, and
/// breaks WITHOUT invoking the server tool. Both FCCs are yielded to the AGUI stream;
/// the AGUI hosting filter
/// (<c>FilterServerToolsFromMixedToolInvocationsAsync</c>) only strips server FCCs
/// when they appear in the SAME update as a client FCC, but the M.E.AI OpenAI adapter
/// emits each FCC as its own update — so the server FCC sails through. The browser
/// then re-POSTs a history with the orphan server FCC, and OpenAI rejects on the next
/// turn.
/// </para>
///
/// <para>
/// Fix: at the agent-run boundary we substitute every declaration-only tool in
/// <see cref="ChatOptions.Tools"/> with an INVOCABLE no-op <see cref="AIFunction"/>
/// that flips <see cref="FunctionInvocationContext.Terminate"/> to <c>true</c> and
/// returns a sentinel. FIC now happily invokes the no-op alongside any server tools
/// in the iteration (we keep <c>AllowConcurrentInvocation = true</c> on the chat
/// client so all FCCs in one iteration run in parallel — see <see cref="CopilotAgent"/>
/// — which means a server tool will always get invoked before the loop breaks).
/// After the inner agent stream completes, we filter out any FRC update whose result
/// equals the sentinel so the browser never sees the synthetic tool result; the
/// original FCC for the client tool still streams to the browser, where the AGUI
/// client invokes it locally and round-trips a real FRC on the next POST.
/// </para>
///
/// <para>
/// Registration: chain into the AGUI-served agent's builder. The middleware is a
/// no-op if a request supplies no declaration-only tools, so it's safe to leave on
/// the <see cref="AskAIAgent"/> too (kernel/stream surface) even though that surface
/// doesn't currently use frontend tools.
/// </para>
/// </summary>
public sealed class ClientToolBridgeMiddleware
{
    /// <summary>
    /// Sentinel that the synthetic client-tool stub returns as its
    /// <see cref="FunctionResultContent.Result"/>. Used to identify and strip the
    /// synthetic FRC out of the outbound AGUI stream so the browser never sees it.
    /// Kept long and namespaced so accidental collision with a real tool result
    /// is essentially impossible.
    /// </summary>
    public const string Sentinel = "__starwars_data_client_tool_stub_handled_on_client__";

    readonly ILogger _logger;

    public ClientToolBridgeMiddleware(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Streaming agent-run middleware. Rewrites declaration-only tools in the request's
    /// <see cref="ChatOptions.Tools"/> as invocable no-ops, runs the inner agent, then
    /// strips synthetic FRC updates from the outbound stream.
    /// </summary>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var rewrittenOptions = RewriteOptions(options);

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, rewrittenOptions, cancellationToken))
        {
            if (IsSyntheticFrcUpdate(update))
            {
                _logger.LogDebug("Stripped synthetic client-tool FRC from outbound AGUI stream.");
                continue;
            }
            yield return update;
        }
    }

    /// <summary>
    /// Non-streaming variant — same rewrite, same filter, applied post-hoc on the
    /// response's Messages collection. Wired for completeness but the AGUI hosting
    /// path is always streaming.
    /// </summary>
    public async Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent innerAgent, CancellationToken cancellationToken)
    {
        var rewrittenOptions = RewriteOptions(options);
        var response = await innerAgent.RunAsync(messages, session, rewrittenOptions, cancellationToken);

        // Best-effort scrub: drop FRCs whose Result is the sentinel. Mutating in-place
        // because we own the response object at this layer.
        foreach (var msg in response.Messages)
        {
            for (int i = msg.Contents.Count - 1; i >= 0; i--)
            {
                if (msg.Contents[i] is FunctionResultContent frc && IsSentinel(frc.Result))
                {
                    msg.Contents.RemoveAt(i);
                }
            }
        }

        return response;
    }

    /// <summary>
    /// Returns a <see cref="ChatClientAgentRunOptions"/> identical to <paramref name="options"/>
    /// but with every declaration-only tool in <see cref="ChatOptions.Tools"/> swapped for
    /// an invocable no-op stub. Server-side AIFunctions (e.g. GraphRAG, KGAnalytics) pass
    /// through unchanged.
    /// </summary>
    static AgentRunOptions? RewriteOptions(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions ccaro || ccaro.ChatOptions?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        List<AITool>? rewritten = null;
        for (int i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            if (tool is AIFunctionDeclaration decl && tool is not AIFunction)
            {
                rewritten ??= [.. tools];
                rewritten[i] = CreateClientToolStub(decl);
            }
        }

        if (rewritten is null)
        {
            return options;
        }

        // Clone the run-options shape (ChatClientAgentRunOptions doesn't expose a public Clone
        // that returns its concrete type, so we mint a fresh one with the same ChatClientFactory).
        var clonedChatOptions = ccaro.ChatOptions.Clone();
        clonedChatOptions.Tools = rewritten;
        var cloned = new ChatClientAgentRunOptions(clonedChatOptions)
        {
            ChatClientFactory = ccaro.ChatClientFactory,
            ResponseFormat = ccaro.ResponseFormat,
            AllowBackgroundResponses = ccaro.AllowBackgroundResponses,
            AdditionalProperties = ccaro.AdditionalProperties,
        };
        // ContinuationToken is [Experimental] on AgentRunOptions in this preview; suppress
        // the warning rather than skip it — losing this on the cloned options would silently
        // break resumable streaming when the upstream feature flips stable.
#pragma warning disable MEAI001
        cloned.ContinuationToken = ccaro.ContinuationToken;
#pragma warning restore MEAI001
        return cloned;
    }

    /// <summary>
    /// Build a real <see cref="AIFunction"/> that preserves the original declaration's
    /// name / description / JSON schema but, when invoked, flips
    /// <see cref="FunctionInvocationContext.Terminate"/> = <c>true</c> and returns the
    /// sentinel. The arguments are ignored — the browser's local invocation re-runs
    /// the tool with the real arguments on the next AGUI round-trip.
    /// </summary>
    static AIFunction CreateClientToolStub(AIFunctionDeclaration declaration) => new ClientToolStub(declaration);

    /// <summary>
    /// Invocable AIFunction whose body is a no-op. Wraps an
    /// <see cref="AIFunctionDeclaration"/> so the LLM sees the same name / description /
    /// JSON schema the frontend advertised, but FIC can invoke it through the normal
    /// pipeline. Setting <see cref="FunctionInvocationContext.Terminate"/> = <c>true</c>
    /// ends the function-call loop AFTER all FCCs in the current iteration have been
    /// invoked (with <see cref="FunctionInvokingChatClient.AllowConcurrentInvocation"/>
    /// = <c>true</c> they run in parallel), so any sibling server-tool invocations
    /// still complete and the LLM never sees a partial-iteration outcome.
    /// </summary>
    sealed class ClientToolStub : AIFunction
    {
        readonly AIFunctionDeclaration _inner;

        public ClientToolStub(AIFunctionDeclaration inner)
        {
            _inner = inner;
        }

        public override string Name => _inner.Name;
        public override string Description => _inner.Description;
        public override JsonElement JsonSchema => _inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // CurrentContext is the FIC's AsyncLocal — null in tests but always non-null
            // in the production agent pipeline. Setting Terminate ends the function loop
            // after this iteration's invocations finish.
            if (FunctionInvokingChatClient.CurrentContext is { } ctx)
            {
                ctx.Terminate = true;
            }
            return new ValueTask<object?>(Sentinel);
        }
    }

    /// <summary>
    /// True when <paramref name="update"/> contains ONLY <see cref="FunctionResultContent"/>
    /// items and ALL of them carry the sentinel value. We want to drop the whole update in
    /// that case so the AGUI text/tool-call event stream doesn't surface it to the browser.
    /// Mixed updates (real FRC + synthetic FRC, or text + FRC) are preserved — we strip the
    /// synthetic FRC item in place.
    /// </summary>
    static bool IsSyntheticFrcUpdate(AgentResponseUpdate update)
    {
        var contents = update.Contents;
        if (contents.Count == 0)
            return false;

        bool anySynthetic = false;
        for (int i = contents.Count - 1; i >= 0; i--)
        {
            if (contents[i] is FunctionResultContent frc && IsSentinel(frc.Result))
            {
                anySynthetic = true;
                contents.RemoveAt(i);
            }
        }

        // If we removed all of them and nothing else is present, this is a synthetic-only
        // update — caller should drop it entirely.
        return anySynthetic && contents.Count == 0;
    }

    static bool IsSentinel(object? result) =>
        result switch
        {
            string s => s == Sentinel,
            JsonElement je => je.ValueKind == JsonValueKind.String && je.GetString() == Sentinel,
            _ => false,
        };
}

public static class ClientToolBridgeExtensions
{
    /// <summary>
    /// Bridges AGUI frontend tools through M.E.AI's <see cref="FunctionInvokingChatClient"/>
    /// by swapping each <see cref="AIFunctionDeclaration"/> in <c>ChatOptions.Tools</c> with
    /// an invocable no-op stub. Required on any agent served via
    /// <c>app.MapAGUI(...)</c> that mixes server-side tools with client-side tools — without
    /// it, FIC terminates the loop on the first declaration-only tool and any server-tool
    /// FCC in the same iteration leaks to the browser unpaired, which breaks the next
    /// OpenAI round-trip with HTTP 400 "No tool output found for function call call_X".
    /// </summary>
    public static AIAgentBuilder UseClientToolBridge(this AIAgentBuilder builder, ILogger logger)
    {
        var middleware = new ClientToolBridgeMiddleware(logger);
        return builder.Use(runFunc: middleware.RunAsync, runStreamingFunc: middleware.RunStreamingAsync);
    }
}
