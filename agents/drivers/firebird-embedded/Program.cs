using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;

// DBX 原生 Firebird Embedded agent：JSON-RPC 2.0 over stdio（换行分隔），启动先输出 {"ready":true}。
//
// 骨架克隆自 agents/drivers/odbc/Program.cs（同一个 Agent Protocol v2 运行时），
// 差异有四处 + 一个总开关：
//   ① 连接层：OdbcConnection → FbConnection，连接串里带 ClientLibrary=所选引擎 DLL 的绝对路径；
//   ② ★ 引擎钉死（EnginePinning.cs）：首次 attach 决定引擎后永不变更，绝不同进程加载第二个；
//   ③ 库识别（FirebirdHeader.cs）：attach 前自读文件头拿 ODS + dialect，ODS 决定用哪套引擎；
//   ④ 元数据：ODBC 的 conn.GetSchema() → Firebird 的 RDB$ 系统表；
//   ⑤ ★★ DeclareMultiSession = false（见下）——不是进程模型的成因，而是对上游入口变化的防御。
//
// 契约（缺一不可，否则 DBX 会在握手后直接 kill 进程并报 Agent RPC error -32000）：
//   1. handshake 必须返回 protocolVersion >= 2 且 capabilities 为【数组】；
//      ⚠️ 本 agent **故意不含** multi_session —— 目的是把「共享 runtime」这条入口挡在门外，
//         与桌面端的「每连接一进程」无关（桌面走 legacy spawn，压根不看 capabilities）；
//   2. 必须实现 open_session / close_session / validate_session / cancel_session；
//      （桌面端不注入 agentSessionId，会话 id 回落到 __legacy__；共享入口才会用到 open_session）
//   3. 除 handshake / test_connection / shutdown 外，DBX 可能在 params 中注入 agentSessionId；
//   4. 不同会话的请求可并发到达，响应可乱序，回写 stdout 必须加锁。
//
// 参考：agents/docs/agent-protocol-v2.md、agents/common/src/main/resources/agent-protocol-v2.json、
//       agents/docs/firebird-embedded-plan.zh-CN.md §2.3

internal static class Program
{
    static readonly object StdoutLock = new();
    static StreamWriter _stdout = null!;
    static int _inFlight;
    static readonly ManualResetEventSlim Idle = new(true);

    static void Main()
    {
        // GBK(936) 在 .NET Core 上默认不可用，必须先挂 CodePages 提供程序，
        // 否则 NONE 字符集老库（如病例.g_b）的中文还原会直接抛异常。
        FirebirdText.Register();

        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Emit("{\"ready\":true}");

        // 多会话可并发执行，抬高线程池下限避免排队抖动。
        ThreadPool.GetMinThreads(out _, out int minIo);
        int minWorker = Math.Max(8, Environment.ProcessorCount);
        ThreadPool.SetMinThreads(minWorker, Math.Max(minIo, 8));

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var server = new FirebirdRuntimeServer();

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string request = line.Trim();

            if (IsShutdownRequest(request))
            {
                // 等在途请求收尾后再处理 shutdown，保证最后一条响应能干净写出。
                Idle.Wait();
                Emit(server.Handle(request));
                return;
            }

            Interlocked.Increment(ref _inFlight);
            Idle.Reset();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Emit(server.Handle(request)); }
                catch { /* Handle 内部已兜住异常，这里防御性吞掉，避免线程池崩溃 */ }
                finally
                {
                    if (Interlocked.Decrement(ref _inFlight) == 0) Idle.Set();
                }
            });
        }

        Idle.Wait();
    }

    static bool IsShutdownRequest(string request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request);
            return doc.RootElement.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                && method.GetString() == "shutdown";
        }
        catch
        {
            return false;
        }
    }

    internal static void Emit(string json)
    {
        lock (StdoutLock)
        {
            _stdout.WriteLine(json);
            _stdout.Flush();
        }
    }
}

/// <summary>
/// 进程级运行时：负责握手、会话生命周期，以及把连接级方法路由到对应会话。
/// </summary>
internal sealed class FirebirdRuntimeServer
{
    const int ProtocolVersion = 2;
    const string LegacySessionId = "__legacy__";
    const int MaxSessions = 256;

    readonly ConcurrentDictionary<string, FirebirdSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>处理一行请求，返回可直接写出的完整 JSON-RPC 响应。</summary>
    public string Handle(string request)
    {
        string idRaw = "null";
        string? sessionId = null;
        try
        {
            using var doc = JsonDocument.Parse(request);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) idRaw = idEl.GetRawText();

            string method = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
                ? methodEl.GetString()!
                : "";
            JsonElement p = root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object
                ? paramsEl
                : default;

            sessionId = Str(p, "agentSessionId");

            object? result = Dispatch(method, p);
            return ResultEnvelope(idRaw, result);
        }
        catch (Exception e)
        {
            // AgentFaultException 会带上 sessionDisposition 等协商字段，DBX 据此决定
            // 是保留本会话、隔离，还是【杀掉本 agent 进程换一个新的】。见 AgentFault.cs。
            return AgentFault.Envelope(idRaw, e.Message, AgentFault.Find(e), sessionId);
        }
    }

    object? Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "handshake":
                return Handshake();
            case "open_session":
                return OpenSession(p);
            case "close_session":
                return CloseSession(Str(p, "agentSessionId"));
            case "validate_session":
                return ValidateSession(p);
            case "cancel_session":
                // cancel 必须在会话锁之外执行，否则会被正在跑的查询挡住。
                return CancelSession(p);
            case "test_connection":
                return TestConnection(p);
            case "connect":
                // 遗留单会话路径：把连接挂到 __legacy__ 会话上。
                CloseSession(LegacySessionId);
                OpenSession(p, LegacySessionId);
                return new { ok = true };
            case "disconnect":
                return CloseSession(LegacySessionId);
            case "shutdown":
                return ShutdownAll();
            default:
                return RouteToSession(method, p);
        }
    }

    /// <summary>
    /// ★★ <c>multi_session</c> 声明开关 —— 对**未来**的防御性绊线，不是当前进程模型的成因。
    ///
    /// <para><b>取值 false。桌面程序根本不看这个开关（2026-09-18 实测修正）。</b></para>
    /// <para>
    /// 桌面端连接入口是 <c>connect_db</c>（<c>src-tauri/src/commands/connection.rs:1669</c>）
    /// → <c>db_type if is_agent_type(&amp;db_type)</c>（<c>:2034</c>）→ <c>connect_agent_pool</c>
    /// （<c>:170</c>）→ <c>agent_manager.spawn()</c> → <c>spawn_client_for_key</c>
    /// （<c>agent_runtime.rs:300</c>）⇒ **无条件新起一个进程、不查任何缓存、不看 capabilities**，
    /// 随后调 legacy 的 <c>connect</c>。所以「每连接一个 agent.exe」在桌面上是**既有事实**，
    /// 与本开关无关：2026-09-18 用协议探针（明确声明 <c>multi_session</c> 的假 agent）实测，
    /// 每条连接只收到 <c>handshake → connect → connection_info</c>，**从不出现
    /// <c>open_session</c>**，也无 kill/respawn 痕迹。
    /// </para>
    ///
    /// <para>
    /// <b>那它还有什么用：</b>共享 runtime 那条路
    /// （<c>AgentRuntimeClient::spawn</c> + <c>open_session</c>，缓存键
    /// <c>agent_key|program|args|working_dir</c>，<c>agent_runtime.rs::shared_runtime_key</c>，
    /// **与连接 id 无关**）属于**另一个入口**
    /// <c>ConnectionManager::get_or_create_pool_for_session_inner</c>（<c>connection.rs:2183</c>），
    /// 桌面 GUI 首次连接不走它（MCP / Web / 懒建会话池才走）。
    /// 万一哪天上游把 GUI 入口也接到那条路上，声明 <c>multi_session</c> 就会让同一 db_type 的
    /// **所有连接共用一个进程** —— 而一个进程只能钉住一套引擎 ⇒ 两个 ODS 不同的库无法同时打开
    /// （后开的触发 <c>replace_runtime</c>，把先开的连接一并换掉，即"音乐椅"）。
    /// <b>不声明就是针对这种未来的防御：届时仍走 legacy，一进程一引擎不受影响。</b>
    /// </para>
    ///
    /// <para><b>代价：桌面上为零。</b>该路径本来就不复用进程，不存在"多开进程"的额外开销；
    /// 也不存在"先起一个共享 runtime 再被 kill"的浪费（那只发生在共享路径上）。
    /// 唯一影响面是只走共享路径的入口（MCP / Web）：在那边本开关会拿内存换隔离。</para>
    /// </summary>
    /// <remarks>
    /// ⚠️ 仍然**保留 v2 与其余能力**，只是不声明 multi_session。
    /// 不要顺手把 <c>protocolVersion</c> 降成 1：那会让 capability 数组失去意义，
    /// 也会让握手之后的分支行为更难预测。
    ///
    /// 两个形态的 <c>engines/</c> 布局、文件头识别、方言处理、字符集还原**完全一样**。
    /// 验证方式见 <c>firebird_agent_probe.py</c>（<c>--legacy</c> 验生产路径、
    /// <c>--pin-test</c> 验同进程换引擎必须返回 <c>replace_runtime</c>）。
    /// </remarks>
    const bool DeclareMultiSession = false;

    static object Handshake() => new
    {
        protocolVersion = ProtocolVersion,
        agentProtocolVersion = ProtocolVersion,
        capabilities = DeclareMultiSession
            ? new[]
            {
                "connect", "test_connection", "metadata", "query", "paged_query", "transaction", "ddl",
                "multi_session"
            }
            : new[]
            {
                "connect", "test_connection", "metadata", "query", "paged_query", "transaction", "ddl"
            }
    };

    object OpenSession(JsonElement p) => OpenSession(p, Str(p, "agentSessionId"));

    object OpenSession(JsonElement p, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new Exception("agentSessionId is required");
        if (_sessions.ContainsKey(sessionId!)) throw new Exception($"agent session already exists: {sessionId}");
        if (_sessions.Count >= MaxSessions) throw new Exception($"agent session limit reached: {MaxSessions}");

        var session = new FirebirdSession(sessionId!);
        session.Open(p); // 连接失败要在此抛出，DBX 才能把失败归到 connect 阶段
        if (!_sessions.TryAdd(sessionId!, session))
        {
            session.Close();
            throw new Exception($"agent session already exists: {sessionId}");
        }
        return new { ok = true };
    }

    object CloseSession(JsonElement p) => CloseSession(Str(p, "agentSessionId"));

    object CloseSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new Exception("agentSessionId is required");
        if (_sessions.TryRemove(sessionId!, out var session)) session.Close();
        return new { ok = true };
    }

    object ValidateSession(JsonElement p) => RequireSession(p).Validate();

    object CancelSession(JsonElement p)
    {
        string? id = Str(p, "agentSessionId");
        if (string.IsNullOrEmpty(id) && _sessions.ContainsKey(LegacySessionId)) id = LegacySessionId;
        // cancel 是尽力而为：会话已消失时静默返回 ok，避免给用户刷无效报错。
        if (id != null && _sessions.TryGetValue(id, out var session)) session.CancelActiveCommand();
        return new { ok = true };
    }

    /// <summary>
    /// 「测试连接」按钮走这里：不建会话，只做一次 识别库 → 钉引擎 → 尝试 attach。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**故意不让护栏触发换进程**（<c>Pin(..., requestRuntimeReplacement: false)</c>）：
    /// 测试是用户随手点的动作，不该因为它把用户正在用的其他 Firebird Embedded 连接一起杀掉
    /// （上游换进程会摘掉所有复用该 runtime 的连接）。真正连不上时，
    /// 用户点「连接」走的 <c>open_session</c> 会自动换进程。
    /// </remarks>
    static object TestConnection(JsonElement p)
    {
        var request = FirebirdSession.ParseConnect(p);
        var header = FirebirdHeaderReader.Read(request.DatabasePath);
        if (!header.IsFirebird || header.EngineKey == null)
            throw new Exception(FirebirdHeaderReader.DescribeSupport(header));

        string clientLibrary = EnginePin.Pin(header.EngineKey, requestRuntimeReplacement: false);
        string? charset = FirebirdSession.ResolveCharset(request, header.EngineKey);
        string connectionString = FirebirdSession.ComposeConnectionString(request, header, clientLibrary, charset);
        using var conn = new FbConnection(connectionString);
        conn.Open();
        return new { ok = true, engine = header.EngineKey, ods = header.OdsLabel, dialect = header.Dialect, charset };
    }

    object ShutdownAll()
    {
        foreach (string key in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(key, out var session))
            {
                try { session.Close(); } catch { }
            }
        }
        return new { ok = true };
    }

    object? RouteToSession(string method, JsonElement p)
    {
        string? id = Str(p, "agentSessionId");
        if (string.IsNullOrEmpty(id)) id = LegacySessionId;
        if (!_sessions.TryGetValue(id, out var session)) throw new Exception($"agent session not found: {id}");
        return session.Dispatch(method, p);
    }

    FirebirdSession RequireSession(JsonElement p)
    {
        string? id = Str(p, "agentSessionId");
        if (string.IsNullOrEmpty(id)) id = LegacySessionId;
        if (!_sessions.TryGetValue(id, out var session)) throw new Exception($"agent session not found: {id}");
        return session;
    }

    internal static string ResultEnvelope(string idRaw, object? result)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{JsonSerializer.Serialize(result)}}}";

    /// <summary>
    /// 错误响应统一走 <see cref="AgentFault.Envelope"/> —— 只有它会在 <c>error.data</c> 里
    /// 带上 sessionDisposition 等协商字段（DBX 据此决定要不要换进程）。
    /// </summary>

    internal static string? Str(JsonElement p, string key)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    internal static int Int(JsonElement p, string key, int def = 0)
    {
        if (p.ValueKind != JsonValueKind.Object) return def;
        return p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : def;
    }

    internal static bool Bool(JsonElement p, string key)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(key, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.TryGetInt32(out int n) && n != 0;
            case JsonValueKind.String:
                return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(v.GetString(), "1", StringComparison.Ordinal);
            default: return false;
        }
    }
}

/// <summary>连接参数：从 DBX 送来的 JSON 里解出目标库文件与凭据。</summary>
internal sealed record FirebirdConnectRequest(
    string DatabasePath,
    string DatabaseAlias,
    string User,
    string Password,
    int ConnectTimeoutSecs,
    string? Charset);

/// <summary>
/// 一个逻辑数据库会话：独占一条 FbConnection、自己的游标表与手工事务。
/// 同一会话的请求串行执行（FbConnection 非并发安全）；跨会话并发由运行时调度。
/// </summary>
internal sealed class FirebirdSession
{
    readonly object _gate = new();
    readonly Dictionary<string, Cursor> _cursors = new();
    FbConnection? _conn;
    FbTransaction? _manualTx;
    FbCommand? _activeCommand;
    long _nextCursorId;
    string _databaseLabel = "database";

    /// <summary>库自身的字符集（读 <c>RDB$DATABASE.RDB$CHARACTER_SET_NAME</c>，NONE 时为 null）。</summary>
    string? _dbCharsetName;

    /// <summary>本次连接串里实际写的字符集（null ⇒ 没写，走 provider 默认）。</summary>
    string? _charset;

    /// <summary>库的 SQL 方言（1 或 3），来自文件头。决定标识符能不能用双引号（见 ConnectionInfo）。</summary>
    int _dialect = 3;

    /// <summary>我们**主动**要求了 <c>Charset=NONE</c>（此时文本必然需要还原）。</summary>
    bool _forcedNarrowText;

    /// <summary>
    /// 库字符集是 NONE ⇒ 文本要按 <see cref="FirebirdText"/> 还原（见该类注释）。
    /// 两个来源取或：① 我们自己要了 NONE；② 库本身就是 NONE（即使连接字符集是默认值，
    /// Firebird 对 NONE 列也不做转换，字节照样原样返回）。
    /// </summary>
    bool _legacyNarrowText;

    public FirebirdSession(string id) => Id = id;

    public string Id { get; }

    // ---------- 连接 ----------

    /// <summary>
    /// ★ 文件路径复用 DBX 的 <c>host</c> 字段（不新增 <c>file_path</c> 配置项）。
    /// <c>access</c> / <c>sqlite</c> / <c>duckdb</c> 都把本地文件路径放在 host，桌面端的
    /// 本地文件表单输入也是 <c>v-model="form.host"</c>，而 Rust 侧
    /// <c>agent_connect_params</c> 原样把 host 转发过来 —— 所以整条参数链路零改动，
    /// 也就不会重演 ODBC 那轮「加了字段但参数在中途被丢掉、CI 还是绿的」。
    /// </summary>
    public static FirebirdConnectRequest ParseConnect(JsonElement p)
    {
        string host = (FirebirdRuntimeServer.Str(p, "host") ?? "").Trim();
        string database = (FirebirdRuntimeServer.Str(p, "database") ?? "").Trim();
        string user = (FirebirdRuntimeServer.Str(p, "username") ?? "").Trim();
        string password = FirebirdRuntimeServer.Str(p, "password") ?? "";
        int timeout = FirebirdRuntimeServer.Int(p, "connectTimeoutSecs", 30);
        string? charset = FirebirdRuntimeServer.Str(p, "charset");

        string path = host;
        string alias = database;
        if (path.Length == 0)
        {
            // 兼容：个别调用方（脚本 / dbx-mcp）会把路径塞进 database。
            path = alias;
            alias = "";
        }
        if (path.Length == 0)
            throw new Exception("Firebird Embedded needs a database file path; the desktop form sends it in 'host'.");

        return new FirebirdConnectRequest(path, alias, user, password, timeout > 0 ? timeout : 30,
            string.IsNullOrWhiteSpace(charset) ? null : charset.Trim());
    }

    /// <summary>
    /// 决定连接字符集。**这是实测踩出来的，不是随手选的**：
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么 fb25 必须显式写 <c>NONE</c></b>：不写时 provider 会带上它自己的默认字符集
    /// （UTF8），而 Firebird 2.5.9 的 embedded 包在实测中**解析不了 UTF8**，attach 直接失败：
    /// <c>bad parameters on attach or create database / CHARACTER SET UTF8 is not defined</c>。
    /// 注意这与 root 解析无关：设 <c>FIREBIRD</c> 环境变量、把 CWD 切到引擎目录，
    /// 两种"修 root"的做法都试过，错误一模一样。
    /// （旁证：早期用裸 ctypes + DPB 打开同一个库是**成功**的，那个 DPB 里根本没有
    /// <c>isc_dpb_lc_ctype</c> —— 也就是"不指定字符集"这条路本来就是通的。）
    /// </para>
    /// <para>
    /// <b>为什么 <c>NONE</c> 在语义上也对</b>：ODS ≤ 11.2 的库绝大多数建库时就是
    /// <c>CHARACTER SET NONE</c>（中文现场的老库尤其如此）。NONE 库本来就不做字符集转换，
    /// 要 <c>NONE</c> 之外的东西没有意义。文本由 <see cref="FirebirdText"/> 负责还原。
    /// </para>
    /// <para>
    /// <b>为什么 fb30/fb50 保持 provider 默认</b>：那条路已实测可用（ODS 13 库正常打开），
    /// 而且保持库默认字符集时，用户在 SQL 里写中文条件也能正确编码。
    /// </para>
    /// <para>
    /// 用户显式传 <c>charset</c> 时**一律优先**（例如某个 UTF8 的 ODS 11 库）。
    /// </para>
    /// </remarks>
    public static string? ResolveCharset(FirebirdConnectRequest request, string engineKey)
    {
        if (!string.IsNullOrEmpty(request.Charset)) return request.Charset;
        return engineKey == "fb25" ? "NONE" : null;
    }

    /// <summary>
    /// 组连接串。三个要点：
    ///   · <c>ClientLibrary</c> 必须是**绝对路径**（裸名会命中 %WINDIR%\System32\fbclient.dll）；
    ///   · <c>Dialect</c> **必须显式写**（客户库是 dialect 1，不写会让 SELECT 7/2 得 3 而不是 3.5）；
    ///   · <c>ServerType=Embedded</c>，绝不设 FIREBIRD 环境变量。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**刻意不写 `Charset`**：让连接字符集跟随库默认值 —— UTF8 库照旧正常，
    ///    NONE 库才走 <see cref="FirebirdText"/> 还原（见 <see cref="Open"/> 里的探测）。
    ///    强行钉成 NONE/UTF8 会反过来把本来正常的库弄坏。
    /// ⚠️ `Database` 只认文件路径。DBX 表单里那个 `database` 字段对本驱动是**可选别名**，
    ///    只能用来当显示名（见下方 `_databaseLabel`），**绝不能覆盖 Database 属性** ——
    ///    否则会变成「打开一个叫 别名 的文件」，报一个与真实文件无关的 I/O 错。
    /// </remarks>
    public static string ComposeConnectionString(
        FirebirdConnectRequest request,
        FirebirdHeader header,
        string clientLibrary,
        string? charset)
    {
        var builder = new FbConnectionStringBuilder
        {
            Database = request.DatabasePath,
            UserID = string.IsNullOrWhiteSpace(request.User) ? "SYSDBA" : request.User,
            // 我们随包带了 security3.fdb / SECURITY5.FDB（全新库，SYSDBA 口令就是出厂值），
            // 用户没填时用出厂口令，避免「明明能连却报 authentication failed」。
            Password = string.IsNullOrEmpty(request.Password) ? "masterkey" : request.Password,
            Dialect = header.Dialect,
            ServerType = FbServerType.Embedded,
            ClientLibrary = clientLibrary,
            Pooling = false,
            ConnectionTimeout = request.ConnectTimeoutSecs,
        };

        // 不指定就整个属性留空，让 provider 用它的默认值（= 跟随库字符集）。
        if (!string.IsNullOrEmpty(charset)) builder.Charset = charset;

        return builder.ConnectionString;
    }

    public void Open(JsonElement p)
    {
        lock (_gate)
        {
            var request = ParseConnect(p);

            var header = FirebirdHeaderReader.Read(request.DatabasePath);
            if (!header.IsFirebird || header.EngineKey == null)
                throw new Exception(FirebirdHeaderReader.DescribeSupport(header));

            // ★ 红线①：首次 attach 决定引擎，此后本进程永不变更。
            string clientLibrary = EnginePin.Pin(header.EngineKey);

            string? charset = ResolveCharset(request, header.EngineKey);
            string connectionString = ComposeConnectionString(request, header, clientLibrary, charset);

            // 连接字符集是 NONE ⇒ provider 会把存储字节 1:1 当字符返回 ⇒ 文本必须还原。
            _charset = charset ?? "(default)";
            _forcedNarrowText = string.Equals(charset, "NONE", StringComparison.OrdinalIgnoreCase);
            _dialect = header.Dialect;

            try
            {
                _conn?.Dispose();
            }
            catch { }

            var conn = new FbConnection(connectionString);
            try
            {
                conn.Open();
            }
            catch (Exception e)
            {
                // attach 阶段失败：native 层可能已经被拉进来了，进程不再干净。
                // 标记引擎不可用 —— 并且要求 DBX **换一个新进程**（ReplaceRuntime），
                // 这样用户重开这个连接就能拿到干净进程，而不是在这里重试。
                EnginePin.MarkFailed($"attach failed with engine '{header.EngineKey}': {e.Message}");
                try { conn.Dispose(); } catch { }
                throw AgentFaultException.ReplaceRuntime(
                    $"Failed to open '{request.DatabasePath}' with the Firebird {header.EngineKey} engine " +
                    $"({header.Describe()}): {e.Message}");
            }

            _conn = conn;

            // ★ 红线断言（方案 §4.5）：开库前后 ODS 必须相等。
            //   把「绝不升级原文件 ODS」从推理变成代码里的可观测不变量。
            //   只读 0x60 字节，不影响文件；读失败（例如恰好被独占）不阻断连接，
            //   但一旦**读到了且不相等**就必须立刻断开 —— 那说明引擎动了这个库。
            var after = FirebirdHeaderReader.Read(request.DatabasePath);
            if (after.IsFirebird && (after.OdsMajor != header.OdsMajor || after.OdsMinor != header.OdsMinor))
            {
                Disconnect();
                throw new Exception(
                    $"refusing to use this database: its ODS changed from {header.OdsLabel} to {after.OdsLabel} " +
                    $"while opening it with engine '{header.EngineKey}'. The engine upgraded the file, " +
                    "which means the original database may already be damaged for older tools.");
            }

            ProbeCharset();

            _databaseLabel = System.IO.Path.GetFileName(request.DatabasePath);
            if (string.IsNullOrWhiteSpace(_databaseLabel)) _databaseLabel = request.DatabasePath;
        }
    }

    /// <summary>
    /// 读库自身字符集，决定要不要做 NONE 老库的文本还原（见 <see cref="FirebirdText"/>）。
    /// </summary>
    /// <remarks>
    /// <c>RDB$DATABASE.RDB$CHARACTER_SET_NAME</c> 在 `CHARACTER SET NONE` 的库上是 **NULL**
    /// —— 正是我们随包支持的客户老库。此查询在 FB 2.0+ 全版本可用；万一失败，
    /// 退回「只看我们自己有没有要求 NONE」，宁可不还原，也不要拿正确的中文去做二次解码。
    /// </remarks>
    void ProbeCharset()
    {
        _dbCharsetName = null;
        try
        {
            var rows = QueryRows("SELECT RDB$CHARACTER_SET_NAME AS CS FROM RDB$DATABASE");
            if (rows.Count > 0) _dbCharsetName = AsString(rows[0], "CS");
        }
        catch
        {
            _dbCharsetName = null;
            _legacyNarrowText = _forcedNarrowText;
            return;
        }

        bool dbIsNarrow = string.IsNullOrEmpty(_dbCharsetName) || _dbCharsetName == "NONE";
        _legacyNarrowText = _forcedNarrowText || dbIsNarrow;
    }

    public void Close()
    {
        lock (_gate) { Disconnect(); }
    }

    /// <summary>校验会话可用性；按协议要求重连时清空手工事务与遗留游标。</summary>
    public object Validate()
    {
        lock (_gate)
        {
            EnsureConn();
            ReleaseResources();
            try { _conn!.Close(); } catch { }
            _conn!.Open();
            return new { ok = true };
        }
    }

    /// <summary>在会话锁之外调用，用于打断正在执行的语句。</summary>
    public void CancelActiveCommand()
    {
        FbCommand? cmd = Volatile.Read(ref _activeCommand);
        if (cmd == null) return;
        try { cmd.Cancel(); } catch { }
    }

    public object? Dispatch(string method, JsonElement p)
    {
        lock (_gate) { return DispatchLocked(method, p); }
    }

    object? DispatchLocked(string method, JsonElement p)
    {
        switch (method)
        {
            case "validate_connection":
                EnsureConn();
                return new { ok = true };
            case "connection_info":
                return ConnectionInfo();
            case "disconnect":
                Disconnect();
                return new { ok = true };
            case "list_databases":
                return ListDatabases();
            case "list_schemas":
                return ListSchemas();
            case "list_tables":
                return ListTables(p);
            case "list_objects":
                return ListTables(p); // Firebird 的对象类型由 TABLE_TYPE 表达，同一份结果即可
            case "list_data_types":
                return Array.Empty<object>();
            case "get_columns":
                return GetColumns(p);
            case "list_indexes":
                return ListIndexes(p);
            case "list_foreign_keys":
                return ListForeignKeys(p);
            case "list_constraints":
            case "list_triggers":
            case "list_partitions":
            case "list_subpartitions":
                return Array.Empty<object>();
            case "get_table_ddl":
            case "get_object_source":
                // 首版不还原 DDL：返回空串由 DBX 降级展示（supportLevel 也停在 browse）。
                return "";
            case "get_type_details":
                return null;
            case "get_explain_info":
                return new { plan = "", has_actual_stats = false };
            case "completion_assistant_search_v1":
                return new { candidates = Array.Empty<object>(), incomplete = false, fallback_used = false };
            case "execute_query":
                return ExecuteQuery(p);
            case "execute_query_page":
            case "start_table_read":
                return ExecuteQueryPage(p);
            case "fetch_query_page":
            case "fetch_table_read_page":
                return FetchQueryPage(p);
            case "close_query_session":
            case "close_table_read_session":
                return CloseQuerySession(p);
            case "execute_batch":
            case "execute_transaction":
                return ExecuteTransaction(p);
            case "begin_manual_transaction":
                return BeginManualTransaction();
            case "commit_manual_transaction":
                return CommitManualTransaction();
            case "rollback_manual_transaction":
                return RollbackManualTransaction();
            default:
                throw new Exception($"unknown method: {method}");
        }
    }

    void Disconnect()
    {
        ReleaseResources();
        // 系统表列集合是**按库**探出来的，换库必须重探。
        _relationColumns.Clear();
        try { _conn?.Close(); } catch { }
        try { _conn?.Dispose(); } catch { }
        _conn = null;
    }

    object ConnectionInfo()
    {
        EnsureConn();
        string version;
        try { version = _conn!.ServerVersion ?? ""; } catch { version = ""; }
        string database;
        try { database = _conn!.Database ?? ""; } catch { database = ""; }
        return new
        {
            server_version = version,
            database,
            database_label = _databaseLabel,
            // 排查现场问题时这几项最有信息量：库字符集 vs 连接字符集决定中文会不会乱码。
            charset = _dbCharsetName,
            connection_charset = _charset,
            engine = EnginePin.PinnedKey ?? "",

            // ★ 上报标识符引号，DBX 侧对应 AgentConnectionInfo.identifierQuote ——
            //   前端 quoteTableDataIdentifier / Rust uses_connection_identifier_quote 会用它。
            //   **方言 1 必须回空串**：在 dialect 1 里双引号是【字符串定界符】，不是标识符定界符，
            //   `SELECT * FROM "PTNTBL"` 会直接语法错（实测：Token unknown，列 27）。
            //   方言 3 回双引号，此时 `"NAME"` 才是合法的定界标识符。
            //   空串会让 DBX 不加引号地引用（Firebird 未加引号建的对象的真实名字就是大写，
            //   所以不引号永远是对的）。
            identifierQuote = _dialect == 1 ? "" : "\"",
        };
    }

    // ---------- 元数据 ----------
    //
    // Firebird 没有 schema 概念：表、视图、索引等对象都挂在一个库里，元数据全部来自
    // RDB$ 系统表。RDB$ 里的名字是 CHAR 且右侧补空格，读出来必须 TRIM()。
    //
    // ⚠️⚠️ RDB$ 系统表的**列集合随 ODS 版本不同**，硬编码任何"较新"的列都会在老库上炸。
    //   实测（病例.g_b，ODS 10.0 / Firebird 2.5.9 引擎打开）：
    //     · RDB$RELATIONS 只有 16 列，**没有 RDB$RELATION_TYPE**（FB 2.0 才加）
    //     · 但 RDB$VIEW_BLR（判表/视图）、RDB$SYSTEM_FLAG 都在
    //     · RDB$FIELDS 反而**有** RDB$FIELD_PRECISION / RDB$CHARACTER_LENGTH（InterBase 6 起就有）
    //   所以凡是要用"可能有也可能没有"的列，先用 ColumnsOf() 问数据库，再决定拼不拼。

    readonly Dictionary<string, HashSet<string>> _relationColumns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 问数据库：某张表（可以是 RDB$ 系统表）到底有哪些列。结果缓存，连接断开时清空。
    /// </summary>
    /// <remarks>
    /// 这是比"读文件头推断 ODS 再查表"更可靠的做法：**数据库自己就是权威**，
    /// 不用维护「哪个版本加了哪一列」的对照表（那种表迟早会漏）。
    /// </remarks>
    HashSet<string> ColumnsOf(string relation)
    {
        if (_relationColumns.TryGetValue(relation, out var cached)) return cached;

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in QueryRows(
                "SELECT TRIM(RDB$FIELD_NAME) AS N FROM RDB$RELATION_FIELDS " +
                "WHERE TRIM(RDB$RELATION_NAME) = ?", relation))
            {
                string? name = AsString(row, "N");
                if (!string.IsNullOrEmpty(name)) columns.Add(name!);
            }
        }
        catch
        {
            // 查不到就当作"什么都没有"，调用方会退到最保守的分支。
        }

        _relationColumns[relation] = columns;
        return columns;
    }

    /// <summary>
    /// 嵌入式模式下「库」就是用户选的那一个文件。前端在 singleDatabase 下只用它做节点名，
    /// 所以直接回文件基名。
    /// </summary>
    object ListDatabases()
    {
        EnsureConn();
        return new[] { new { name = _databaseLabel } };
    }

    /// <summary>
    /// Firebird 没有 schema 层。沿用 ODBC agent 对「无 schema 的驱动」的兜底值 <c>main</c>，
    /// 目的是让前端拿到非空集合 —— 侧栏与对象树都假设至少有一个 schema 才会加载对象。
    /// <c>list_tables</c> / <c>get_columns</c> 因此**忽略** schema 入参（见各自实现）。
    /// </summary>
    object ListSchemas() => new[] { "main" };

    object ListTables(JsonElement p)
    {
        EnsureConn();
        bool includeSystem = FirebirdRuntimeServer.Bool(p, "include_system_tables");

        // RDB$SYSTEM_FLAG = 1 是系统表（RDB$*）；普通用户表为 0 或 NULL。
        // 不按名字前缀过滤：用户完全可以自建一张叫 RDB$FOO 的表，类型标志才是权威信息。
        //
        // ⚠️ 这里**刻意忽略 schema 入参**：Firebird 无 schema 层，前端在对象树模式下会把
        //    库名塞进 schema 位，若按它过滤就会「树里有表、主面板空白」。
        //
        // ⚠️ 视图判据**不能硬编码 RDB$RELATION_TYPE**（那是 Firebird 2.0/ODS 11 才加的列）：
        //    ODS 10 的老库上这条 SQL 直接 `SQL error code = -206 / Column unknown`。
        //    实测（病例.g_b，ODS 10.0）：RDB$RELATIONS 只有 16 列，有 RDB$VIEW_BLR、没有
        //    RDB$RELATION_TYPE。所以先问数据库自己有哪些列（见 ColumnsOf），再拼 SQL。
        string viewExpression = ColumnsOf("RDB$RELATIONS").Contains("RDB$RELATION_TYPE")
            ? "COALESCE(RDB$RELATION_TYPE, 0)"                                    // ODS 11+：权威
            : "CASE WHEN RDB$VIEW_BLR IS NULL THEN 0 ELSE 1 END";                 // ODS 10：万能兜底

        string sql =
            "SELECT TRIM(RDB$RELATION_NAME) AS NAME, " + viewExpression + " AS REL_TYPE, " +
            "COALESCE(RDB$SYSTEM_FLAG, 0) AS SYS_FLAG " +
            "FROM RDB$RELATIONS " +
            "WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 " +
            "ORDER BY RDB$RELATION_NAME";

        var list = new List<object>();
        foreach (var row in QueryRows(sql))
        {
            string name = AsString(row, "NAME") ?? "";
            if (name.Length == 0) continue;
            if (!includeSystem && AsInt(row, "SYS_FLAG") != 0) continue;

            // 1 = VIEW，其余（0 = TABLE / 2-4 = GTT / 5 = external）在前端一律按表渲染。
            string tableType = AsInt(row, "REL_TYPE") == 1 ? "VIEW" : "TABLE";

            // 同时输出 table_type 与 object_type：后端 list_tables 的反序列化目标 TableInfo 用
            // table_type；list_objects（侧栏分组展开时调用）的目标 ObjectInfo 用 object_type。
            // 二者都从同一个 agent 方法返回，任一字段缺失都会让该路径报 "missing field"。
            list.Add(new
            {
                name,
                table_type = tableType,
                object_type = tableType,
                comment = (string?)null,
            });
        }
        return list;
    }

    object GetColumns(JsonElement p)
    {
        EnsureConn();
        string? table = FirebirdRuntimeServer.Str(p, "table");
        if (string.IsNullOrWhiteSpace(table)) return Array.Empty<object>();

        string relation = NormalizeIdentifier(table!);
        var primaryKeys = PrimaryKeyColumns(relation);

        // 同 ListTables：每个"较新"的系统列都先确认存在，不存在就退成常量。
        var fieldColumns = ColumnsOf("RDB$RELATION_FIELDS");
        var domainColumns = ColumnsOf("RDB$FIELDS");

        string defaultExpr = fieldColumns.Contains("RDB$DEFAULT_SOURCE") ? "rf.RDB$DEFAULT_SOURCE" : "NULL";
        string descriptionExpr = fieldColumns.Contains("RDB$DESCRIPTION") ? "rf.RDB$DESCRIPTION" : "NULL";
        string precisionExpr = domainColumns.Contains("RDB$FIELD_PRECISION")
            ? "COALESCE(f.RDB$FIELD_PRECISION, 0)"
            : "0";

        // 联结 RDB$RELATION_FIELDS（列归属）与 RDB$FIELDS（域定义）。
        string sql =
            "SELECT TRIM(rf.RDB$FIELD_NAME) AS COLUMN_NAME, " +
            "       COALESCE(f.RDB$FIELD_TYPE, 0) AS FIELD_TYPE, " +
            "       COALESCE(f.RDB$FIELD_SUB_TYPE, 0) AS FIELD_SUB_TYPE, " +
            "       COALESCE(f.RDB$FIELD_LENGTH, 0) AS FIELD_LENGTH, " +
            precisionExpr + " AS FIELD_PRECISION, " +
            "       COALESCE(f.RDB$FIELD_SCALE, 0) AS FIELD_SCALE, " +
            "       COALESCE(rf.RDB$NULL_FLAG, 0) AS NULL_FLAG, " +
            defaultExpr + " AS DEFAULT_SOURCE, " +
            descriptionExpr + " AS DESCRIPTION " +
            "FROM RDB$RELATION_FIELDS rf " +
            "JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE " +
            "WHERE TRIM(rf.RDB$RELATION_NAME) = ? " +
            "ORDER BY rf.RDB$FIELD_POSITION";

        var list = new List<object>();
        foreach (var row in QueryRows(sql, relation))
        {
            string columnName = AsString(row, "COLUMN_NAME") ?? "";
            if (columnName.Length == 0) continue;

            int fieldType = AsInt(row, "FIELD_TYPE");
            int subType = AsInt(row, "FIELD_SUB_TYPE");
            int length = AsInt(row, "FIELD_LENGTH");
            int precision = AsInt(row, "FIELD_PRECISION");
            int scale = AsInt(row, "FIELD_SCALE");

            string typeName = FieldTypeName(fieldType, subType, length, precision, scale);

            bool isNullable = AsInt(row, "NULL_FLAG") == 0;
            bool numeric = IsNumericFieldType(fieldType);
            bool text = fieldType is 14 or 37 or 40; // CHAR / VARCHAR / CSTRING

            list.Add(new
            {
                name = columnName,
                data_type = typeName,
                is_nullable = isNullable,
                column_default = AsString(row, "DEFAULT_SOURCE"),
                is_primary_key = primaryKeys.Contains(columnName),
                extra = (string?)null,
                comment = AsString(row, "DESCRIPTION"),
                numeric_precision = numeric && precision > 0 ? precision : (int?)null,
                numeric_scale = numeric && precision > 0 ? Math.Abs(scale) : (int?)null,
                character_maximum_length = text && length > 0 ? length : (int?)null,
            });
        }
        return list;
    }

    object ListIndexes(JsonElement p)
    {
        EnsureConn();
        string? table = FirebirdRuntimeServer.Str(p, "table");
        if (string.IsNullOrWhiteSpace(table)) return Array.Empty<object>();

        string relation = NormalizeIdentifier(table!);

        const string sql =
            "SELECT TRIM(i.RDB$INDEX_NAME) AS INDEX_NAME, " +
            "       COALESCE(i.RDB$UNIQUE_FLAG, 0) AS UNIQUE_FLAG, " +
            "       COALESCE(i.RDB$INDEX_INACTIVE, 0) AS INACTIVE_FLAG, " +
            "       TRIM(s.RDB$FIELD_NAME) AS COLUMN_NAME, " +
            "       rc.RDB$CONSTRAINT_TYPE AS CONSTRAINT_TYPE " +
            "FROM RDB$INDICES i " +
            "JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = i.RDB$INDEX_NAME " +
            "LEFT JOIN RDB$RELATION_CONSTRAINTS rc ON rc.RDB$INDEX_NAME = i.RDB$INDEX_NAME " +
            "WHERE TRIM(i.RDB$RELATION_NAME) = ? " +
            "ORDER BY i.RDB$INDEX_NAME, s.RDB$FIELD_POSITION";

        var indexes = new Dictionary<string, (bool primary, bool unique, List<string> columns)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in QueryRows(sql, relation))
        {
            string name = AsString(row, "INDEX_NAME") ?? "";
            if (name.Length == 0) continue;

            string? constraint = AsString(row, "CONSTRAINT_TYPE");
            bool isPrimary = string.Equals(constraint, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase);

            if (!indexes.TryGetValue(name, out var entry))
            {
                entry = (isPrimary, AsInt(row, "UNIQUE_FLAG") != 0, new List<string>());
                indexes[name] = entry;
            }

            string? column = AsString(row, "COLUMN_NAME");
            if (!string.IsNullOrWhiteSpace(column)) entry.columns.Add(column!);
        }

        return indexes.Select(kv => new
        {
            name = kv.Key,
            columns = kv.Value.columns,
            is_unique = kv.Value.unique,
            is_primary = kv.Value.primary,
            filter = (string?)null,
            index_type = (string?)null,
            included_columns = Array.Empty<string>(),
            comment = (string?)null,
        }).ToList();
    }

    object ListForeignKeys(JsonElement p)
    {
        EnsureConn();
        string? table = FirebirdRuntimeServer.Str(p, "table");
        if (string.IsNullOrWhiteSpace(table)) return Array.Empty<object>();

        string relation = NormalizeIdentifier(table!);

        // Firebird 的外键定义分两处：RDB$RELATION_CONSTRAINTS 给出外键本身，
        // RDB$REF_CONSTRAINTS 用 RDB$CONST_NAME_UQ 指回被引用的唯一约束。
        //
        // ⚠️ RDB$DELETE_RULE / RDB$UPDATE_RULE 在 **RDB$REF_CONSTRAINTS** 上，
        //    不在 RDB$RELATION_CONSTRAINTS 上（实测列清单证实；写成 rc.RDB$DELETE_RULE
        //    会直接 `SQL error -206 Column unknown`，而且在任何 ODS 版本上都错）。
        string deleteRuleExpr = ColumnsOf("RDB$REF_CONSTRAINTS").Contains("RDB$DELETE_RULE")
            ? "COALESCE(refc.RDB$DELETE_RULE, 0)"
            : "0";

        string sql =
            "SELECT TRIM(rc.RDB$CONSTRAINT_NAME) AS FK_NAME, " +
            "       TRIM(src.RDB$FIELD_NAME) AS FK_COLUMN, " +
            "       TRIM(tgtRel.RDB$RELATION_NAME) AS REF_TABLE, " +
            "       TRIM(tgt.RDB$FIELD_NAME) AS REF_COLUMN, " +
            deleteRuleExpr + " AS DELETE_RULE " +
            "FROM RDB$RELATION_CONSTRAINTS rc " +
            "JOIN RDB$REF_CONSTRAINTS refc ON refc.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME " +
            "JOIN RDB$INDEX_SEGMENTS src ON src.RDB$INDEX_NAME = rc.RDB$INDEX_NAME " +
            "JOIN RDB$INDEX_SEGMENTS tgt ON tgt.RDB$INDEX_NAME = refc.RDB$CONST_NAME_UQ " +
            "                          AND tgt.RDB$FIELD_POSITION = src.RDB$FIELD_POSITION " +
            "JOIN RDB$RELATION_CONSTRAINTS tgtRc ON tgtRc.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ " +
            "JOIN RDB$RELATIONS tgtRel ON tgtRel.RDB$RELATION_NAME = tgtRc.RDB$RELATION_NAME " +
            "WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND TRIM(rc.RDB$RELATION_NAME) = ?";

        var list = new List<object>();
        foreach (var row in QueryRows(sql, relation))
        {
            list.Add(new
            {
                name = AsString(row, "FK_NAME") ?? "",
                column = AsString(row, "FK_COLUMN") ?? "",
                ref_schema = "",
                ref_table = AsString(row, "REF_TABLE") ?? "",
                ref_column = AsString(row, "REF_COLUMN") ?? "",
                on_delete = DeleteRuleName(AsInt(row, "DELETE_RULE")),
            });
        }
        return list;
    }

    HashSet<string> PrimaryKeyColumns(string relation)
    {
        const string sql =
            "SELECT TRIM(s.RDB$FIELD_NAME) AS COLUMN_NAME " +
            "FROM RDB$INDEX_SEGMENTS s " +
            "JOIN RDB$RELATION_CONSTRAINTS rc ON rc.RDB$INDEX_NAME = s.RDB$INDEX_NAME " +
            "WHERE rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY' AND TRIM(rc.RDB$RELATION_NAME) = ? " +
            "ORDER BY s.RDB$FIELD_POSITION";

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in QueryRows(sql, relation))
        {
            string? column = AsString(row, "COLUMN_NAME");
            if (!string.IsNullOrWhiteSpace(column)) set.Add(column!);
        }
        return set;
    }

    // ---------- 查询 ----------

    object ExecuteQuery(JsonElement p)
    {
        EnsureConn();
        string sql = FirebirdRuntimeServer.Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");

        // dialect 1 里双引号是字符串定界符，DBX 生成的 "NAME" 会语法错（见 FirebirdSqlDialect.cs）。
        sql = FirebirdSqlDialect.NormalizeForDialect(sql, _dialect);

        int maxRows = FirebirdRuntimeServer.Int(p, "maxRows", 1000);
        int limit = maxRows > 0 ? maxRows : 1000;
        int timeout = FirebirdRuntimeServer.Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        using var reader = ExecuteReaderTracked(cmd);

        var columns = new List<string>();
        var columnTypes = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
            columnTypes.Add(reader.GetDataTypeName(i));
        }

        var rows = new List<object?[]>();
        bool truncated = false;
        int n = 0;
        while (n < limit && reader.Read())
        {
            rows.Add(ReadRow(reader));
            n++;
        }
        if (reader.Read()) truncated = true;

        long affected = reader.FieldCount == 0 ? Math.Max(0, reader.RecordsAffected) : 0;

        return new
        {
            columns,
            column_types = columnTypes,
            rows,
            affected_rows = affected,
            execution_time_ms = sw.ElapsedMilliseconds,
            truncated,
        };
    }

    object ExecuteQueryPage(JsonElement p)
    {
        EnsureConn();
        int pageSize = FirebirdRuntimeServer.Int(p, "pageSize", 200);
        if (pageSize <= 0) pageSize = 200;

        string sql = FirebirdRuntimeServer.Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");
        sql = FirebirdSqlDialect.NormalizeForDialect(sql, _dialect);
        int timeout = FirebirdRuntimeServer.Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        FbDataReader reader;
        try
        {
            reader = ExecuteReaderTracked(cmd);
        }
        catch
        {
            cmd.Dispose();
            throw;
        }

        var columns = new List<string>();
        var columnTypes = new List<string>();
        try
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
                columnTypes.Add(reader.GetDataTypeName(i));
            }

            string sessionId = Interlocked.Increment(ref _nextCursorId).ToString();
            var cursor = new Cursor { Reader = reader, Command = cmd, Columns = columns, ColumnTypes = columnTypes };
            _cursors[sessionId] = cursor;

            var rows = ReadPage(cursor, pageSize, out bool hasMore);
            long affected = reader.FieldCount == 0 ? Math.Max(0, reader.RecordsAffected) : 0;
            return new
            {
                columns,
                column_types = columnTypes,
                rows,
                affected_rows = affected,
                execution_time_ms = sw.ElapsedMilliseconds,
                truncated = false,
                session_id = sessionId,
                has_more = hasMore,
            };
        }
        catch
        {
            try { reader.Dispose(); } catch { }
            try { cmd.Dispose(); } catch { }
            throw;
        }
    }

    object FetchQueryPage(JsonElement p)
    {
        string sessionId = FirebirdRuntimeServer.Str(p, "sessionId") ?? "";
        int pageSize = FirebirdRuntimeServer.Int(p, "pageSize", 200);
        if (pageSize <= 0) pageSize = 200;
        if (!_cursors.TryGetValue(sessionId, out var cursor)) throw new Exception($"query session not found: {sessionId}");

        var rows = ReadPage(cursor, pageSize, out bool hasMore);
        return new
        {
            columns = cursor.Columns,
            column_types = cursor.ColumnTypes,
            rows,
            affected_rows = 0L,
            execution_time_ms = 0L,
            truncated = false,
            session_id = sessionId,
            has_more = hasMore,
        };
    }

    object CloseQuerySession(JsonElement p)
    {
        string sessionId = FirebirdRuntimeServer.Str(p, "sessionId") ?? "";
        if (_cursors.Remove(sessionId, out var cursor)) cursor.Dispose();
        return new { ok = true };
    }

    object ExecuteTransaction(JsonElement p)
    {
        EnsureConn();
        var statements = StrArray(p, "statements");

        long affected = 0;
        var sw = Stopwatch.StartNew();

        FbTransaction? tx;
        try
        {
            tx = _conn!.BeginTransaction();
        }
        catch (Exception e) when (e is FbException || e is InvalidOperationException || e is NotSupportedException)
        {
            tx = null; // 退化为逐条提交，而不是让整批语句失败
        }

        try
        {
            foreach (string raw in statements)
            {
                string s = raw.Trim().TrimEnd(';').Trim();
                if (s.Length == 0) continue;
                s = FirebirdSqlDialect.NormalizeForDialect(s, _dialect);
                using var cmd = _conn!.CreateCommand();
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText = s;
                affected += cmd.ExecuteNonQuery();
            }
            tx?.Commit();
        }
        catch
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        finally
        {
            try { tx?.Dispose(); } catch { }
        }

        return new
        {
            columns = Array.Empty<string>(),
            column_types = Array.Empty<string>(),
            rows = Array.Empty<object[]>(),
            affected_rows = affected,
            execution_time_ms = sw.ElapsedMilliseconds,
            truncated = false,
        };
    }

    object BeginManualTransaction()
    {
        EnsureConn();
        if (_manualTx != null) throw new Exception("manual transaction already open");
        _manualTx = _conn!.BeginTransaction();
        return new { ok = true };
    }

    object CommitManualTransaction()
    {
        if (_manualTx == null) throw new Exception("no manual transaction open");
        _manualTx.Commit();
        _manualTx.Dispose();
        _manualTx = null;
        return new { ok = true };
    }

    object RollbackManualTransaction()
    {
        if (_manualTx == null) throw new Exception("no manual transaction open");
        _manualTx.Rollback();
        _manualTx.Dispose();
        _manualTx = null;
        return new { ok = true };
    }

    // ---------- 内部辅助 ----------

    void EnsureConn()
    {
        if (_conn == null) throw new Exception("Not connected");
    }

    /// <summary>登记当前命令，供 cancel_session 从其他线程打断。</summary>
    FbDataReader ExecuteReaderTracked(FbCommand cmd)
    {
        Volatile.Write(ref _activeCommand, cmd);
        try
        {
            return cmd.ExecuteReader();
        }
        finally
        {
            Volatile.Write(ref _activeCommand, null);
        }
    }

    void ApplyManualTx(FbCommand cmd)
    {
        if (_manualTx != null) cmd.Transaction = _manualTx;
    }

    void ReleaseResources()
    {
        foreach (var cursor in _cursors.Values) cursor.Dispose();
        _cursors.Clear();

        if (_manualTx == null) return;
        try { _manualTx.Rollback(); } catch { }
        try { _manualTx.Dispose(); } catch { }
        _manualTx = null;
    }

    /// <summary>跑一条只读元数据查询，按列名取值（Firebird 列名大小写不敏感比较）。</summary>
    List<Dictionary<string, object?>> QueryRows(string sql, params object?[] args)
    {
        var results = new List<Dictionary<string, object?>>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        ApplyManualTx(cmd);
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.Add(new FbParameter("p" + i, args[i] ?? DBNull.Value));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            results.Add(row);
        }
        return results;
    }

    List<object?[]> ReadPage(Cursor cursor, int pageSize, out bool hasMore)
    {
        var rows = new List<object?[]>();
        if (cursor.Pending != null)
        {
            rows.Add(cursor.Pending);
            cursor.Pending = null;
        }
        while (rows.Count < pageSize && cursor.Reader.Read()) rows.Add(ReadRow(cursor.Reader));
        if (cursor.Reader.Read())
        {
            cursor.Pending = ReadRow(cursor.Reader);
            hasMore = true;
        }
        else
        {
            hasMore = false;
        }
        return rows;
    }

    object?[] ReadRow(FbDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            object? value = reader.GetValue(i);
            // NONE 字符集的老库：provider 会把存储字节 1:1 当字符返回 ⇒ 中文是乱码，
            // 这里按需还原（见 FirebirdText）。其他字符集的库不动，避免把正确的文本弄坏。
            if (_legacyNarrowText) value = FirebirdText.Restore(value);
            row[i] = Normalize(value);
        }
        return row;
    }

    static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset dto => dto.ToString("o"),
        Guid g => g.ToString(),
        char c => c.ToString(),
        _ => value,
    };

    static List<string> StrArray(JsonElement p, string key)
    {
        var list = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        }
        return list;
    }

    /// <summary>
    /// Firebird 把未加引号的标识符统一存成大写，所以查询 RDB$ 时要把用户给的名字折成大写。
    /// （用引号建的、大小写敏感的对象名首版不支持 —— 那类名字在整个 DBX 里都很少见。）
    /// </summary>
    static string NormalizeIdentifier(string raw) => raw.Trim().ToUpperInvariant();

    static string? AsString(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return null;
        string text = value.ToString() ?? "";
        return text.Trim();
    }

    static int AsInt(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return 0;
        return value switch
        {
            int i => i,
            short s => s,
            long l => (int)l,
            byte b => b,
            decimal d => (int)d,
            string str => int.TryParse(str.Trim(), out int parsed) ? parsed : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// RDB$FIELD_TYPE → 可读类型名。数值取自 Firebird 的 blrtable（rdb$types.h）。
    /// CHAR/VARCHAR 带上字节长度；NUMERIC/DECIMAL 由「整数基类型 + 负 scale」表达。
    /// </summary>
    static string FieldTypeName(int fieldType, int subType, int length, int precision, int scale)
    {
        if (scale < 0 && fieldType is 7 or 8 or 16)
        {
            int effectivePrecision = precision > 0 ? precision : 9;
            return $"NUMERIC({effectivePrecision}, {-scale})";
        }

        return fieldType switch
        {
            7 => "SMALLINT",
            8 => "INTEGER",
            9 => "INT64",
            10 => "FLOAT",
            11 => "DOUBLE PRECISION",
            12 => "DATE",
            13 => "TIME",
            14 => length > 0 ? $"CHAR({length})" : "CHAR",
            16 => "BIGINT",
            23 => "BOOLEAN",
            24 => "DECFLOAT(16)",
            25 => "DECFLOAT(34)",
            26 => "DECFLOAT",
            27 => "DOUBLE PRECISION",
            28 => "TIMESTAMP",
            35 => "TIMESTAMP",
            37 => length > 0 ? $"VARCHAR({length})" : "VARCHAR",
            40 => length > 0 ? $"CSTRING({length})" : "CSTRING",
            261 => subType == 1 ? "BLOB SUB_TYPE TEXT" : "BLOB",
            _ => $"UNKNOWN_TYPE_{fieldType}",
        };
    }

    static bool IsNumericFieldType(int fieldType) => fieldType is 7 or 8 or 9 or 10 or 11 or 16 or 24 or 25 or 26 or 27;

    /// <summary>RDB$DELETE_RULE：0 = NO ACTION、1 = CASCADE、2 = SET NULL、3 = SET DEFAULT。</summary>
    static string DeleteRuleName(int rule) => rule switch
    {
        1 => "CASCADE",
        2 => "SET NULL",
        3 => "SET DEFAULT",
        _ => "NO ACTION",
    };
}

/// <summary>分页游标：持有未读完的 reader、对应命令与一行的前瞻缓冲。</summary>
internal sealed class Cursor : IDisposable
{
    public FbDataReader Reader = null!;
    public FbCommand? Command;
    public List<string> Columns = new();
    public List<string> ColumnTypes = new();
    public object?[]? Pending;

    public void Dispose()
    {
        try { Reader?.Close(); } catch { }
        try { Reader?.Dispose(); } catch { }
        try { Command?.Dispose(); } catch { }
    }
}
