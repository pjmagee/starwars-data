using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace StarWarsData.Services;

/// <summary>
/// Function-calling middleware that caps the number of tool invocations per agent run
/// and emits diagnostics so we can see when (and which tools) blow the budget.
///
/// The framework already enforces <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/>
/// at the iteration (round-trip) level. This middleware adds a finer-grained cap at the
/// individual tool-call level, plus structured logging of every call so OTel-backed
/// dashboards can spot fan-out regressions.
///
/// Also exposes an agent-run wrapper (<see cref="RunStreamingAsync"/> / <see cref="RunAsync"/>)
/// that resets the per-run counter at the start of each agent invocation and logs an
/// end-of-turn summary at Information level: "Run summary — N tool calls: name1, name2, ...".
/// This gives observability into the tool-call sequence without requiring Debug-level logging
/// of every individual call.
/// </summary>
public sealed class ToolCallBudgetMiddleware
{
    readonly int _softWarnAt;
    readonly int _hardLimit;
    readonly ILogger _logger;

    static readonly AsyncLocal<RunCounter?> CurrentRun = new();

    public ToolCallBudgetMiddleware(int softWarnAt, int hardLimit, ILogger logger)
    {
        if (hardLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(hardLimit));
        if (softWarnAt < 1 || softWarnAt > hardLimit)
            throw new ArgumentOutOfRangeException(nameof(softWarnAt));
        _softWarnAt = softWarnAt;
        _hardLimit = hardLimit;
        _logger = logger;
    }

    /// <summary>
    /// Agent-run middleware (non-streaming): resets the AsyncLocal counter, runs the inner
    /// agent, then logs a summary of the tool-call sequence and total count.
    /// </summary>
    public async Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent innerAgent, CancellationToken cancellationToken)
    {
        CurrentRun.Value = new RunCounter();
        try
        {
            return await innerAgent.RunAsync(messages, session, options, cancellationToken);
        }
        finally
        {
            LogRunSummary();
        }
    }

    /// <summary>
    /// Agent-run middleware (streaming): same as <see cref="RunAsync"/> but for streaming runs.
    /// Logs the summary after the response stream completes.
    /// </summary>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        CurrentRun.Value = new RunCounter();
        try
        {
            await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            LogRunSummary();
        }
    }

    void LogRunSummary()
    {
        var counter = CurrentRun.Value;
        if (counter is null || counter.Count == 0)
            return;

        // Guard the string.Join cost — Info-level logs are typically enabled but be defensive.
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        _logger.LogInformation(
            "ToolCallBudget: run summary — {Count} tool call(s): [{Calls}]. Render tools: [{Renders}]",
            counter.Count,
            string.Join(", ", counter.Calls),
            string.Join(", ", counter.RenderToolsUsed)
        );
    }

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken
    )
    {
        var counter = CurrentRun.Value ??= new RunCounter();
        var count = ++counter.Count;
        var name = context.Function.Name;
        counter.Calls.Add(name);

        // Duplicate render guard: every visualization render tool is expected to be called at most
        // once per agent run. A second call with the same render_* name means the model is looping
        // on "did it render?" — we terminate and return a sentinel so it stops and writes the
        // final answer. Different render tools (e.g. render_markdown + render_data_table) are
        // still allowed together because each is keyed separately in the HashSet.
        if (name.StartsWith("render_", StringComparison.Ordinal))
        {
            if (!counter.RenderToolsUsed.Add(name))
            {
                _logger.LogWarning("ToolCallBudget: duplicate render tool {ToolName} blocked at call #{Count}. Calls so far: {Calls}", name, count, string.Join(",", counter.Calls));
                context.Terminate = true;
                return $"Duplicate render call blocked: '{name}' was already invoked this turn. The first call's output is already on screen. End the turn now — do not call any render tool again.";
            }
        }

        if (count > _hardLimit)
        {
            _logger.LogWarning(
                "ToolCallBudget: hard limit {Limit} exceeded by call to {ToolName}. Terminating function loop. Calls so far: {Calls}",
                _hardLimit,
                name,
                string.Join(",", counter.Calls)
            );
            context.Terminate = true;
            return $"Tool call budget exceeded ({_hardLimit}). The agent must answer with what it already has.";
        }

        if (count == _softWarnAt)
        {
            _logger.LogWarning("ToolCallBudget: soft threshold {Threshold} reached at {ToolName}. Calls so far: {Calls}", _softWarnAt, name, string.Join(",", counter.Calls));
        }

        _logger.LogDebug("ToolCall #{Count}: {ToolName}", count, name);

        return await next(context, cancellationToken);
    }

    sealed class RunCounter
    {
        public int Count;
        public List<string> Calls { get; } = [];
        public HashSet<string> RenderToolsUsed { get; } = [];
    }
}

public static class ToolCallBudgetExtensions
{
    /// <summary>
    /// Caps tool invocations per agent run, blocks duplicate render_* calls, and logs an
    /// Information-level run summary at the end of every turn so the tool-call sequence is
    /// visible in OTel structured logs without enabling Debug-level logging.
    /// <paramref name="hardLimit"/> terminates the function loop; <paramref name="softWarnAt"/>
    /// emits a warning log without stopping.
    /// </summary>
    public static AIAgentBuilder UseToolCallBudget(this AIAgentBuilder builder, int softWarnAt, int hardLimit, ILogger logger)
    {
        var middleware = new ToolCallBudgetMiddleware(softWarnAt, hardLimit, logger);
        // Two layers stacked: agent-run middleware (resets counter, logs end-of-turn summary)
        // wraps the function-call middleware (per-call enforcement of budget + duplicate guard).
        return builder.Use(runFunc: middleware.RunAsync, runStreamingFunc: middleware.RunStreamingAsync).Use(middleware.InvokeAsync);
    }
}
