using System.Text.Json;

// ============================================================================
// 结构化错误协商：让 DBX 主动把本 agent 进程换掉
//
// 为什么需要它（2026-09-18 读上游源码后的结论，推翻方案的一个前提）
// ----------------------------------------------------------------
// 方案 N8 押注「DBX 是连接级进程隔离 ⇒ 一进程一引擎天然成立」。**这个押注已经不成立**：
//   crates/dbx-core/src/agent_runtime.rs `shared_runtime_key()`
//       = "{agent_key}|{program}|{args}|{working_dir}"      ← 不含连接 id、不含库路径
//   crates/dbx-core/src/connection.rs `spawn_routed_shared_agent_client()`
//       按这个 key 在 `manager.connection_runtimes` 里**缓存并复用** agent 进程。
// ⇒ 同一个 db_type 的**所有连接共用一个 agent.exe**。于是「一个进程先后遇到两个不同 ODS
//   的库」是**正常使用就会发生**的事，不再是理论风险。
//
// 但上游同时给了正解：`RecoveryPolicy`（crates/dbx-core/src/agent_recovery.rs）
// ----------------------------------------------------------------
//   error.data.sessionDisposition == "replace_runtime"
//       → AgentCallError::Legacy.hints.session_disposition = ReplaceRuntime
//       → RecoveryDecision::ReplaceRuntime
//       → connection.rs `replace_runtime_after_open_failure()`：
//             · 摘掉所有复用该 runtime 的连接池
//             · **kill 掉这个 agent 进程**
//             · 本次连接失败并报错；用户/DBX 重试时 → **全新进程**（引擎干净）
//
// ⇒ 这正是我们需要的逃生通道：把「同进程不能加载第二个引擎」这条硬限制，变成
//   「一次失败 + 自动换进程」的可恢复路径，而不是一个死错误。
//
// ⚠️ 代价必须写清楚：换进程会**连带杀掉同进程的其他 Firebird Embedded 连接**
//    （它们会被摘出连接池）。这是上游设计使然，不是我们引入的；文案里要说清
//    「重新打开即可」。
//
// 契约细节（crates/dbx-core/src/db/agent_driver.rs）
// ----------------------------------------------
// · `strict_structured_errors = handshake.capabilities.contains("structured_error_v1")`
//   —— 我们**没有**声明这个能力 ⇒ 走 legacy 分支：`error.data` 里的字段被**宽松**解析。
// · legacy 分支只认这几个键：category / retryable / sessionDisposition / stage /
//   operationOutcome / agentSessionId。所以下面同时写齐。
// · 顺带也满足结构化契约（`contractVersion: 1` 时 `valid_agent_error_combination` 要求）：
//   category=resource 允许 replace_runtime；stage=connect 要求 operationOutcome=not_started；
//   agentSessionId 必须是可见 ASCII 且与请求一致。两边都合法 ⇒ 将来上游启用严格模式
//   也不会退化成 ContractViolation。
// ============================================================================

/// <summary>与上游 <c>AgentErrorCategory</c> 对齐（serde snake_case）。</summary>
internal enum AgentFaultCategory
{
    Connection,
    Sql,
    Resource,
    Protocol,
    Timeout,
    Canceled,
}

/// <summary>与上游 <c>AgentErrorStage</c> 对齐（serde snake_case）。</summary>
internal enum AgentFaultStage
{
    Request,
    Checkout,
    Connect,
    Validate,
    Execute,
    Fetch,
    Cancel,
    Close,
}

internal enum AgentFaultOutcome
{
    NotStarted,
    Unknown,
}

internal enum AgentFaultDisposition
{
    Keep,
    Quarantine,
    ReplaceRuntime,
}

/// <summary>
/// 带协商信息的错误。只有需要 DBX 做特定恢复动作时才用；普通错误继续用 <see cref="Exception"/>。
/// </summary>
internal sealed class AgentFaultException : Exception
{
    public AgentFaultException(
        string message,
        AgentFaultCategory category,
        AgentFaultDisposition disposition,
        AgentFaultStage stage = AgentFaultStage.Connect,
        AgentFaultOutcome outcome = AgentFaultOutcome.NotStarted,
        bool retryable = false)
        : base(message)
    {
        Category = category;
        Disposition = disposition;
        Stage = stage;
        Outcome = outcome;
        Retryable = retryable;
    }

    public AgentFaultCategory Category { get; }
    public AgentFaultDisposition Disposition { get; }
    public AgentFaultStage Stage { get; }
    public AgentFaultOutcome Outcome { get; }
    public bool Retryable { get; }

    /// <summary>
    /// 「本进程已经不能再用了，请 DBX kill 掉我并换一个新进程」。
    /// DBX 侧对应 <c>RecoveryDecision::ReplaceRuntime</c>。
    /// </summary>
    public static AgentFaultException ReplaceRuntime(string message, bool retryable = true)
        => new(message, AgentFaultCategory.Resource, AgentFaultDisposition.ReplaceRuntime, retryable: retryable);
}

internal static class AgentFault
{
    static string Name(AgentFaultCategory category) => category switch
    {
        AgentFaultCategory.Connection => "connection",
        AgentFaultCategory.Sql => "sql",
        AgentFaultCategory.Resource => "resource",
        AgentFaultCategory.Protocol => "protocol",
        AgentFaultCategory.Timeout => "timeout",
        _ => "canceled",
    };

    static string Name(AgentFaultStage stage) => stage switch
    {
        AgentFaultStage.Request => "request",
        AgentFaultStage.Checkout => "checkout",
        AgentFaultStage.Connect => "connect",
        AgentFaultStage.Validate => "validate",
        AgentFaultStage.Execute => "execute",
        AgentFaultStage.Fetch => "fetch",
        AgentFaultStage.Cancel => "cancel",
        _ => "close",
    };

    static string Name(AgentFaultOutcome outcome)
        => outcome == AgentFaultOutcome.NotStarted ? "not_started" : "unknown";

    static string Name(AgentFaultDisposition disposition) => disposition switch
    {
        AgentFaultDisposition.Keep => "keep",
        AgentFaultDisposition.Quarantine => "quarantine",
        _ => "replace_runtime",
    };

    /// <summary>
    /// 组错误响应。带 fault 时在 <c>error.data</c> 里放协商字段。
    /// </summary>
    public static string Envelope(string idRaw, string message, AgentFaultException? fault, string? sessionId)
    {
        string encodedMessage = JsonSerializer.Serialize(message);

        if (fault == null)
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32000,\"message\":{encodedMessage}}}}}";

        var data = new Dictionary<string, object?>
        {
            ["contractVersion"] = 1,
            ["category"] = Name(fault.Category),
            ["retryable"] = fault.Retryable,
            ["sessionDisposition"] = Name(fault.Disposition),
            ["stage"] = Name(fault.Stage),
            ["operationOutcome"] = Name(fault.Outcome),
        };
        // agentSessionId 必须是可见 ASCII 且与请求中的一致，否则严格模式下会被判 ContractViolation。
        if (!string.IsNullOrEmpty(sessionId) && IsGraphicAscii(sessionId)) data["agentSessionId"] = sessionId;

        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32000,\"message\":{encodedMessage}," +
               $"\"data\":{JsonSerializer.Serialize(data)}}}}}";
    }

    static bool IsGraphicAscii(string value)
    {
        foreach (char c in value)
            if (c <= 0x20 || c > 0x7E) return false;
        return true;
    }

    /// <summary>在异常链里找 <see cref="AgentFaultException"/>（可能被包了一层）。</summary>
    public static AgentFaultException? Find(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
            if (e is AgentFaultException fault) return fault;
        return null;
    }
}
