using System.Collections.Concurrent;
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// DBX 原生 ODBC agent：JSON-RPC 2.0 over stdio（换行分隔），启动先输出 {"ready":true}。
//
// 本 agent 实现 Agent Protocol v2 的 multi_session 运行时：一个进程可承载多个逻辑会话，
// 每个会话独占一个 OdbcConnection、自己的游标表与手工事务。契约见 DBX 仓库：
//   agents/docs/agent-protocol-v2.md
//   agents/common/src/main/resources/agent-protocol-v2.json
// 参考实现：agents/drivers/neo4j-go/main.go
//
// 约定要点（缺一不可，否则 DBX 会在握手后直接 kill 进程并报 Agent RPC error -32000）：
//   1. handshake 必须返回 protocolVersion >= 2 且 capabilities 为【数组】并含 multi_session；
//   2. 必须实现 open_session / close_session / validate_session / cancel_session；
//   3. 除 handshake / test_connection / shutdown 外，DBX 会在 params 中注入 agentSessionId；
//   4. 不同会话的请求可并发到达，响应可乱序，回写 stdout 必须加锁。

internal static class Program
{
    static readonly object StdoutLock = new();
    static StreamWriter _stdout = null!;
    static int _inFlight;
    static readonly ManualResetEventSlim Idle = new(true);

    static void Main()
    {
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Emit("{\"ready\":true}");

        // 多会话可并发执行，抬高线程池下限避免排队抖动。
        ThreadPool.GetMinThreads(out _, out int minIo);
        int minWorker = Math.Max(8, Environment.ProcessorCount);
        ThreadPool.SetMinThreads(minWorker, Math.Max(minIo, 8));

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var server = new OdbcRuntimeServer();

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
internal sealed class OdbcRuntimeServer
{
    const int ProtocolVersion = 2;
    const string LegacySessionId = "__legacy__";
    const int MaxSessions = 256;

    readonly ConcurrentDictionary<string, OdbcSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>处理一行请求，返回可直接写出的完整 JSON-RPC 响应。</summary>
    public string Handle(string request)
    {
        string idRaw = "null";
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

            object? result = Dispatch(method, p);
            return ResultEnvelope(idRaw, result);
        }
        catch (Exception e)
        {
            return ErrorEnvelope(idRaw, e.Message);
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

    static object Handshake() => new
    {
        protocolVersion = ProtocolVersion,
        agentProtocolVersion = ProtocolVersion,
        capabilities = new[]
        {
            "connect", "test_connection", "metadata", "query", "paged_query", "transaction", "ddl",
            "multi_session"
        }
    };

    object OpenSession(JsonElement p) => OpenSession(p, Str(p, "agentSessionId"));

    object OpenSession(JsonElement p, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new Exception("agentSessionId is required");
        if (_sessions.ContainsKey(sessionId!)) throw new Exception($"agent session already exists: {sessionId}");
        if (_sessions.Count >= MaxSessions) throw new Exception($"agent session limit reached: {MaxSessions}");

        var session = new OdbcSession(sessionId!);
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

    static object TestConnection(JsonElement p)
    {
        string cs = OdbcSession.BuildConnectionString(p);
        using var conn = new OdbcConnection(cs);
        conn.Open();
        return new { ok = true };
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

    OdbcSession RequireSession(JsonElement p)
    {
        string? id = Str(p, "agentSessionId");
        if (string.IsNullOrEmpty(id)) id = LegacySessionId;
        if (!_sessions.TryGetValue(id, out var session)) throw new Exception($"agent session not found: {id}");
        return session;
    }

    internal static string ResultEnvelope(string idRaw, object? result)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{JsonSerializer.Serialize(result)}}}";

    internal static string ErrorEnvelope(string idRaw, string message)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32000,\"message\":{JsonSerializer.Serialize(message)}}}}}";

    internal static string? Str(JsonElement p, string key)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// 一个逻辑数据库会话：独占一条 OdbcConnection、自己的游标表与手工事务。
/// 同一会话的请求串行执行（ODBC 连接非并发安全）；跨会话并发由运行时调度。
/// </summary>
internal sealed class OdbcSession
{
    readonly object _gate = new();
    readonly Dictionary<string, Cursor> _cursors = new();
    OdbcConnection? _conn;
    OdbcTransaction? _manualTx;
    OdbcCommand? _activeCommand;
    long _nextCursorId;

    public OdbcSession(string id) => Id = id;

    public string Id { get; }

    // ---------- 生命周期 ----------

    public void Open(JsonElement p)
    {
        lock (_gate)
        {
            string cs = BuildConnectionString(p);
            try { _conn?.Dispose(); } catch { }
            var conn = new OdbcConnection(cs);
            conn.Open();
            _conn = conn;
        }
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
        OdbcCommand? cmd = Volatile.Read(ref _activeCommand);
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
                return ListTables(p); // 通用 ODBC 无独立对象类型，回退到表
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
                // 通用 ODBC 无法可靠还原目标库原生 DDL，返回空串由 DBX 降级展示。
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

    // ---------- 连接 ----------

    static internal string BuildConnectionString(JsonElement p)
    {
        string? dsn = OdbcRuntimeServer.Str(p, "dsn");
        string? cs = OdbcRuntimeServer.Str(p, "connection_string");
        string? user = OdbcRuntimeServer.Str(p, "username");
        string? pass = OdbcRuntimeServer.Str(p, "password");

        // ① 主路径：裸 DSN 名 + 表单凭据。UI 只提交裸名称（如 "interface"），
        //    "DSN=" 前缀与凭据全部在这里拼——agent 是唯一的拼接/规范化点。
        if (!string.IsNullOrWhiteSpace(dsn))
            return AppendCredentials("DSN=" + NormalizeDsn(dsn!), user, pass);

        // ② 兜底路径：调用方直接传整条连接串（dbx-mcp.exe / 脚本；UI 三字段不产生此形态）。
        //    仍追加表单凭据，避免"在 UI 改了密码却不生效"。
        if (!string.IsNullOrWhiteSpace(cs))
            return AppendCredentials(cs!.Trim().TrimEnd(';'), user, pass);

        throw new Exception("dsn or connection_string is required");
    }

    /// <summary>
    /// 串尾追加凭据。依据实测：
    ///   重复键「后者覆盖前者」→ 追加即覆盖，无需解析重组；
    ///   合并语义「逐字段独立」→ 某字段为空则不追加，该字段沿用串内已有值。
    /// </summary>
    static string AppendCredentials(string head, string? user, string? pass)
    {
        var sb = new StringBuilder(head);

        // 用户名：首尾空格无意义，可 trim
        if (!string.IsNullOrWhiteSpace(user))
            sb.Append(";UID=").Append(Escape(user!.Trim()));

        // 密码：⚠️ 空格是密码的一部分（驱动与 SQL Server 都不 trim），
        //       故此处【故意】用 IsNullOrEmpty 而非 IsNullOrWhiteSpace，
        //       否则 PWD=" " 这种纯空格密码会被静默丢弃，变成"密码明明是对的"。
        //       同样【故意】不 Trim()。review 时请不要顺手统一成上面的写法。
        if (!string.IsNullOrEmpty(pass))
            sb.Append(";PWD=").Append(Escape(pass!));

        return sb.Append(';').ToString();
    }

    /// <summary>
    /// 规范化 DSN 名。**不能复用 Escape()**：实测 `DSN={interface}` 报 IM002
    /// —— `{}` 转义对 DSN 关键字无效（只对 UID/PWD 的值有效）。
    /// </summary>
    static string NormalizeDsn(string raw)
    {
        // 驱动不 trim DSN 名：`DSN=interface ` 直接 IM002，所以这里必须 trim
        var name = raw.Trim();

        // 用户常顺手把前缀一起写进来（占位符写了"只填名称"也仍会有人写 DSN=x）。
        // 必须剥离，否则拼成 `DSN=DSN=x`，报的却是"未找到数据源名称"——
        // 会把排查方向带偏到注册表，而真因在拼串。
        if (name.StartsWith("DSN=", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(4).Trim();

        // 名称含 ; { } 时无任何转义手段（{} 对 DSN 无效，; 会截断值）
        // → 明确报错，绝不静默拼出一条必然失败的连接串
        if (name.IndexOfAny(new[] { ';', '{', '}' }) >= 0)
            throw new Exception("DSN name must not contain ';', '{' or '}'");

        if (name.Length == 0)
            throw new Exception("dsn is empty");

        return name;
    }

    /// <summary>
    /// ODBC 值转义：含 ; { } = 或首尾空白时用 {} 包裹，内部 } 双写。
    /// </summary>
    /// <remarks>
    /// 仅用于 UID / PWD 的值，**不可用于 DSN 名**（DSN 关键字不认 {} 包裹，见 NormalizeDsn）。
    /// </remarks>
    static string Escape(string v)
    {
        if (v.IndexOfAny(new[] { ';', '{', '}', '=' }) >= 0 || v != v.Trim())
            return "{" + v.Replace("}", "}}") + "}";
        return v;
    }

    void Disconnect()
    {
        ReleaseResources();
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
        return new { server_version = version, database };
    }

    // ---------- 元数据 ----------

    object ListDatabases()
    {
        EnsureConn();
        var list = new List<object>();
        foreach (DataRow r in SafeSchema("Catalogs").Rows)
        {
            string? name = Str(r, "CATALOG_NAME");
            if (!string.IsNullOrWhiteSpace(name)) list.Add(new { name });
        }
        if (list.Count == 0)
        {
            string name;
            try { name = _conn!.Database ?? "default"; } catch { name = "default"; }
            list.Add(new { name });
        }
        return list;
    }

    object ListSchemas()
    {
        EnsureConn();
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in SafeSchema("Schemata").Rows)
        {
            string? s = Str(r, "SCHEMA_NAME");
            if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        }
        if (set.Count == 0)
        {
            foreach (DataRow r in SafeSchema("Tables").Rows)
            {
                string? s = Str(r, "TABLE_SCHEM");
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
            }
        }
        if (set.Count == 0) set.Add("main");
        return set.ToList();
    }

    object ListTables(JsonElement p)
    {
        EnsureConn();
        string? schema = OdbcRuntimeServer.Str(p, "schema");
        bool includeSystem = Bool(p, "include_system_tables");
        var table = SafeSchema("Tables");
        var matchSchema = SchemaFilter(table, schema);
        var rows = table.Rows.Cast<DataRow>()
            .Where(matchSchema)
            .Where(r => includeSystem || !IsSystemObject(r))
            .OrderBy(r => Str(r, "TABLE_NAME") ?? "", StringComparer.OrdinalIgnoreCase);

        var list = new List<object>();
        foreach (DataRow r in rows)
        {
            // 同时输出 table_type 与 object_type：
            // 后端 list_tables 的反序列化目标 TableInfo 用 table_type；
            // list_objects（侧栏 TABLE/VIEW 分组节点展开时调用）的反序列化目标
            // ObjectInfo 用 object_type。二者都从同一个 agent 方法 ListTables 返回，
            // 任一字段缺失都会让该路径报 "missing field" 而渲染为空 —— 正是
            // 「双击 ODBC 库节点，分组里不出表」的根因（2026-09-16 真机 CDP 复现）。
            string tableType = Str(r, "TABLE_TYPE") ?? "TABLE";
            list.Add(new
            {
                name = Str(r, "TABLE_NAME") ?? "",
                table_type = tableType,
                object_type = tableType,
                comment = (string?)null
            });
        }
        return list;
    }

    object GetColumns(JsonElement p)
    {
        EnsureConn();
        string? schema = OdbcRuntimeServer.Str(p, "schema");
        string? table = OdbcRuntimeServer.Str(p, "table");
        var pkCols = PrimaryKeyColumns(schema, table);

        var list = new List<object>();
        var columnTable = SafeSchema("Columns");
        var matchSchema = SchemaFilter(columnTable, schema);
        var rows = columnTable.Rows.Cast<DataRow>()
            .Where(r => matchSchema(r) && MatchName(r, "TABLE_NAME", table))
            .OrderBy(r => IntFrom(r, "ORDINAL_POSITION"))
            .ToList();

        foreach (DataRow r in rows)
        {
            string typeName = Str(r, "TYPE_NAME") ?? Str(r, "DATA_TYPE") ?? "UNKNOWN";
            string isNullable = Str(r, "IS_NULLABLE") ?? (IntFrom(r, "NULLABLE") == 1 ? "YES" : "NO");
            string colName = Str(r, "COLUMN_NAME") ?? "";
            int? size = NullableInt(r, "COLUMN_SIZE");
            int? digits = NullableInt(r, "DECIMAL_DIGITS");

            bool numeric = IsNumericType(typeName);
            bool text = IsTextType(typeName);

            list.Add(new
            {
                name = colName,
                data_type = typeName,
                is_nullable = string.Equals(isNullable, "YES", StringComparison.OrdinalIgnoreCase),
                column_default = Str(r, "COLUMN_DEF"),
                is_primary_key = pkCols.Contains(colName),
                extra = (string?)null,
                comment = Str(r, "REMARKS"),
                numeric_precision = numeric ? size : (int?)null,
                numeric_scale = numeric && digits.HasValue ? digits : (int?)null,
                character_maximum_length = text ? size : (int?)null
            });
        }
        return list;
    }

    object ListIndexes(JsonElement p)
    {
        EnsureConn();
        string? schema = OdbcRuntimeServer.Str(p, "schema");
        string? table = OdbcRuntimeServer.Str(p, "table");

        var indexes = new Dictionary<string, (bool primary, bool unique, List<string> cols)>(StringComparer.OrdinalIgnoreCase);
        var indexTable = SafeSchema("Indexes");
        var matchSchema = SchemaFilter(indexTable, schema);
        foreach (DataRow r in indexTable.Rows)
        {
            if (!matchSchema(r) || !MatchName(r, "TABLE_NAME", table)) continue;
            string name = Str(r, "INDEX_NAME") ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!indexes.TryGetValue(name, out var e))
            {
                e = (BoolFrom(r, "PRIMARY_KEY"), BoolFrom(r, "UNIQUE"), new List<string>());
                indexes[name] = e;
            }
            string? col = Str(r, "COLUMN_NAME");
            if (!string.IsNullOrWhiteSpace(col)) e.cols.Add(col);
        }

        return indexes.Select(kv => new
        {
            name = kv.Key,
            columns = kv.Value.cols,
            is_unique = kv.Value.unique,
            is_primary = kv.Value.primary,
            filter = (string?)null,
            index_type = (string?)null,
            included_columns = Array.Empty<string>(),
            comment = (string?)null
        }).ToList();
    }

    object ListForeignKeys(JsonElement p)
    {
        EnsureConn();
        string? table = OdbcRuntimeServer.Str(p, "table");

        var list = new List<object>();
        foreach (DataRow r in SafeSchema("ForeignKeys").Rows)
        {
            if (!MatchName(r, "FK_TABLE_NAME", table)) continue;
            list.Add(new
            {
                name = Str(r, "FK_NAME") ?? "",
                column = Str(r, "FK_COLUMN_NAME") ?? "",
                ref_schema = Str(r, "PK_TABLE_SCHEM") ?? Str(r, "PK_SCHEMA_NAME") ?? "",
                ref_table = Str(r, "PK_TABLE_NAME") ?? "",
                ref_column = Str(r, "PK_COLUMN_NAME") ?? "",
                on_delete = Str(r, "DELETE_RULE") ?? ""
            });
        }
        return list;
    }

    // ---------- 查询 ----------

    object ExecuteQuery(JsonElement p)
    {
        EnsureConn();
        string sql = OdbcRuntimeServer.Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");

        int maxRows = Int(p, "maxRows", 1000);
        int limit = maxRows > 0 ? maxRows : 1000;
        int timeout = Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        using var reader = ExecuteReaderTracked(cmd);

        var columns = new List<string>();
        var colTypes = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
            colTypes.Add(reader.GetDataTypeName(i));
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
            column_types = colTypes,
            rows,
            affected_rows = affected,
            execution_time_ms = sw.ElapsedMilliseconds,
            truncated
        };
    }

    object ExecuteQueryPage(JsonElement p)
    {
        EnsureConn();
        int pageSize = Int(p, "pageSize", 200);
        if (pageSize <= 0) pageSize = 200;

        string sql = OdbcRuntimeServer.Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");
        int timeout = Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        OdbcDataReader reader;
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
        var colTypes = new List<string>();
        try
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
                colTypes.Add(reader.GetDataTypeName(i));
            }

            string sessionId = Interlocked.Increment(ref _nextCursorId).ToString();
            var cursor = new Cursor { Reader = reader, Command = cmd, Columns = columns, ColumnTypes = colTypes };
            _cursors[sessionId] = cursor;

            var rows = ReadPage(cursor, pageSize, out bool hasMore);
            long affected = reader.FieldCount == 0 ? Math.Max(0, reader.RecordsAffected) : 0;
            return new
            {
                columns,
                column_types = colTypes,
                rows,
                affected_rows = affected,
                execution_time_ms = sw.ElapsedMilliseconds,
                truncated = false,
                session_id = sessionId,
                has_more = hasMore
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
        string sessionId = OdbcRuntimeServer.Str(p, "sessionId") ?? "";
        int pageSize = Int(p, "pageSize", 200);
        if (pageSize <= 0) pageSize = 200;
        if (!_cursors.TryGetValue(sessionId, out var c)) throw new Exception($"query session not found: {sessionId}");

        var rows = ReadPage(c, pageSize, out bool hasMore);
        return new
        {
            columns = c.Columns,
            column_types = c.ColumnTypes,
            rows,
            affected_rows = 0L,
            execution_time_ms = 0L,
            truncated = false,
            session_id = sessionId,
            has_more = hasMore
        };
    }

    object CloseQuerySession(JsonElement p)
    {
        string sessionId = OdbcRuntimeServer.Str(p, "sessionId") ?? "";
        if (_cursors.Remove(sessionId, out var c)) c.Dispose();
        return new { ok = true };
    }

    object ExecuteTransaction(JsonElement p)
    {
        EnsureConn();
        var statements = StrArray(p, "statements");

        long affected = 0;
        var sw = Stopwatch.StartNew();

        // 通用 ODBC 会碰到不支持事务的驱动（dBase / Text / Excel 等）。
        // 这类驱动 BeginTransaction 直接抛 HYC00（可选功能未实现），
        // 此时退化为“逐条提交”而不是让整批语句失败。
        OdbcTransaction? tx = null;
        try
        {
            tx = _conn!.BeginTransaction();
        }
        catch (Exception e) when (e is OdbcException || e is InvalidOperationException || e is NotSupportedException)
        {
            tx = null;
        }

        try
        {
            foreach (string raw in statements)
            {
                string s = raw.Trim().TrimEnd(';').Trim();
                if (s.Length == 0) continue;
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
            truncated = false
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
    OdbcDataReader ExecuteReaderTracked(OdbcCommand cmd)
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

    void ApplyManualTx(OdbcCommand cmd)
    {
        if (_manualTx != null) cmd.Transaction = _manualTx;
    }

    void ReleaseResources()
    {
        foreach (var c in _cursors.Values) c.Dispose();
        _cursors.Clear();

        if (_manualTx == null) return;
        try { _manualTx.Rollback(); } catch { }
        try { _manualTx.Dispose(); } catch { }
        _manualTx = null;
    }

    DataTable SafeSchema(string collection)
    {
        try { return _conn!.GetSchema(collection) ?? new DataTable(); }
        catch { return new DataTable(); }
    }

    static List<object?[]> ReadPage(Cursor c, int pageSize, out bool hasMore)
    {
        var rows = new List<object?[]>();
        if (c.Pending != null) { rows.Add(c.Pending); c.Pending = null; }
        while (rows.Count < pageSize && c.Reader.Read()) rows.Add(ReadRow(c.Reader));
        if (c.Reader.Read()) { c.Pending = ReadRow(c.Reader); hasMore = true; }
        else hasMore = false;
        return rows;
    }

    static object?[] ReadRow(OdbcDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++) row[i] = Normalize(reader.GetValue(i));
        return row;
    }

    static object? Normalize(object v) => v switch
    {
        null or DBNull => null,
        byte[] b => Convert.ToBase64String(b),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset dto => dto.ToString("o"),
        Guid g => g.ToString(),
        char c => c.ToString(),
        _ => v
    };

    HashSet<string> PrimaryKeyColumns(string? schema, string? table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexTable = SafeSchema("Indexes");
        var matchSchema = SchemaFilter(indexTable, schema);
        foreach (DataRow r in indexTable.Rows)
        {
            if (!BoolFrom(r, "PRIMARY_KEY")) continue;
            if (!matchSchema(r) || !MatchName(r, "TABLE_NAME", table)) continue;
            string? col = Str(r, "COLUMN_NAME");
            if (!string.IsNullOrWhiteSpace(col)) set.Add(col);
        }
        return set;
    }

    /// <summary>
    /// 把 list_* 请求里的 <c>schema</c> 入参解析成行过滤器。ODBC 是通用桥接，
    /// 「schema 位」在不同调用方下含义不同，故按「先解析一次，再按行过滤」处理：
    ///
    /// 1. 入参为空 → 不过滤（侧栏分组走的就是空 schema；
    ///    <c>connectionObjectTreeQuerySchema</c> 在对象树模式下返回 ""）。
    /// 2. 入参在本集合里确实是某个 TABLE_SCHEM → 按 schema 过滤（用户显式选了 schema）。
    /// 3. 入参在本集合里是 TABLE_CAT（库/目录名）→ 按 catalog 过滤。
    ///    这是修复点：odbc/odbc32 的 traits 含 <c>databaseObjectTree</c>，前端在
    ///    对象树模式下会把**库名**塞进 schema 位（<c>ObjectBrowser.loadObjects</c>
    ///    的 <c>needsSchema ? ... : props.database</c>）。此时真实 TABLE_SCHEM 是
    ///    <c>dbo</c> 一类，永远匹配不上 → 主面板「没有找到对象」。侧栏分组因为走
    ///    空 schema 所以一直正常，这也是「树里有表、主面板空白」这个割裂现象的来源。
    /// 4. 两者都不是（入参既不是本集合的 schema 也不是 catalog）→ 返回空，保持既有行为。
    ///
    /// 注意：先判 schema 再判 catalog，顺序不可颠倒 —— 否则「库名恰好与某个 schema
    /// 同名」时会把整个库都放出来。集合内任一 TABLE_CAT/TABLE_SCHEM 为空（Oracle、
    /// SQLite 等无 catalog 概念的驱动）时，相应分支自然退化，行为不变。
    /// </summary>
    static Func<DataRow, bool> SchemaFilter(DataTable table, string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return static _ => true;

        bool Eq(string col, DataRow r) => string.Equals(Str(r, col), schema, StringComparison.OrdinalIgnoreCase);
        var rows = table.Rows.Cast<DataRow>();

        if (rows.Any(r => Eq("TABLE_SCHEM", r))) return r => Eq("TABLE_SCHEM", r);
        if (rows.Any(r => Eq("TABLE_CAT", r))) return r => Eq("TABLE_CAT", r);
        return static _ => false;
    }

    /// <summary>
    /// 系统对象判据。ODBC 的 SQLTables 会把系统表混在用户表里返回，并且**如实**标了
    /// <c>TABLE_TYPE='SYSTEM TABLE'</c>（Access/Jet 实测：11 个 MSys* 全是 SYSTEM TABLE，
    /// 另 3 个是 TABLE）——所以这不是驱动缺陷，是调用方原本没过滤。
    ///
    /// 为什么必须在 agent 侧过滤：DBX 前端只有「系统 Schema 可见性」这一个开关
    /// （<c>show_system_schemas</c>），它作用在 TABLE_SCHEM 上；而 Access / Excel /
    /// dBase 这类文件驱动的 TABLE_SCHEM **全空**，没得可关 —— MSys* 就会直接铺进
    /// 侧栏树和主面板（2026-09-17 用户截图复现）。
    ///
    /// 判据取 TABLE_TYPE 而非名字前缀（MSys*）：类型是驱动给的权威信息，前缀启发式
    /// 会误伤用户自建的、恰好叫 MSysXxx 的表。对 SQL Server 一类驱动的用户表
    /// （TABLE_TYPE='TABLE'）过滤是无操作。
    ///
    /// 需要看系统对象时在 params 内传 <c>include_system_tables=true</c>
    /// （前端目前不会传，留给调试与后续 UI 开关）。
    /// </summary>
    static bool IsSystemObject(DataRow r)
    {
        string type = (Str(r, "TABLE_TYPE") ?? "").Trim();
        return type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
    }

    static bool Bool(JsonElement p, string key)
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

    static bool MatchName(DataRow r, string col, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return string.Equals(Str(r, col), name, StringComparison.OrdinalIgnoreCase);
    }

    static bool BoolFrom(DataRow r, string col)
    {
        object v = r[col];
        if (v is bool b) return b;
        if (v is int i) return i != 0;
        if (v is short s) return s != 0;
        if (v is string str) return string.Equals(str, "true", StringComparison.OrdinalIgnoreCase) || str == "1";
        return false;
    }

    static int IntFrom(DataRow r, string col)
    {
        object v = r[col];
        if (v is int i) return i;
        if (v is short s) return s;
        if (v is long l) return (int)l;
        return 0;
    }

    static int? NullableInt(DataRow r, string col)
    {
        if (!r.Table.Columns.Contains(col)) return null;
        object v = r[col];
        if (v == null || v is DBNull) return null;
        return IntFrom(r, col);
    }

    static string? Str(DataRow r, string col)
    {
        if (!r.Table.Columns.Contains(col)) return null;
        object v = r[col];
        return v == null || v is DBNull ? null : v.ToString();
    }

    static int Int(JsonElement p, string key, int def = 0)
    {
        if (p.ValueKind != JsonValueKind.Object) return def;
        return p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : def;
    }

    static List<string> StrArray(JsonElement p, string key)
    {
        var list = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        }
        return list;
    }

    static bool IsNumericType(string type) => type.ToUpperInvariant() is var t &&
        (t.Contains("INT") || t.Contains("DEC") || t.Contains("NUM") || t.Contains("REAL") || t.Contains("FLOAT") || t.Contains("DOUBLE") || t.Contains("MONEY"));

    static bool IsTextType(string type) => type.ToUpperInvariant() is var t &&
        (t.Contains("CHAR") || t.Contains("TEXT") || t.Contains("CLOB") || t.Contains("STRING") || t.Contains("NCHAR") || t.Contains("GRAPHIC"));
}

/// <summary>分页游标：持有未读完的 reader、对应命令与一行的前瞻缓冲。</summary>
internal sealed class Cursor : IDisposable
{
    public OdbcDataReader Reader = null!;
    public OdbcCommand? Command;
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
