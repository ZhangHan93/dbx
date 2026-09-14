using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// DBX 原生 ODBC agent：JSON-RPC 2.0 over stdio（换行分隔），启动先输出 {"ready":true}。
// 协议契约见 DBX 仓库 agents/docs/agent-protocol-v2.md 与 drivers/oracle-go/main.go。

class Program
{
    static void Main()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        stdout.WriteLine("{\"ready\":true}");

        var agent = new OdbcAgent();
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string method = "";
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string idRaw = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
                method = root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String
                    ? mEl.GetString()!
                    : "";
                JsonElement p = root.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object
                    ? pEl
                    : default;

                object? result = agent.Dispatch(method, p);
                string resultJson = JsonSerializer.Serialize(result);
                stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}");
            }
            catch (Exception e)
            {
                // 尽量带 id 回错误；解析失败时退化为 id=null
                string idRaw = "null";
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idEl)) idRaw = idEl.GetRawText();
                }
                catch { }
                stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32000,\"message\":{JsonSerializer.Serialize(e.Message)}}}}}");
            }

            if (method == "shutdown") break;
        }
    }
}

class OdbcAgent
{
    OdbcConnection? _conn;
    readonly Dictionary<string, Cursor> _cursors = new();
    long _nextCursorId;
    OdbcTransaction? _manualTx;

    const string V1Capabilities = "[\"connect\",\"test_connection\",\"metadata\",\"query\",\"paged_query\",\"transaction\",\"ddl\"]";

    public object? Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "handshake":
                return new { protocolVersion = 1, agentProtocolVersion = 1, capabilities = V1Capabilities };
            case "connect":
                return Connect(p);
            case "test_connection":
                return TestConnection(p);
            case "validate_connection":
                EnsureConn();
                return new { ok = true };
            case "connection_info":
                return ConnectionInfo();
            case "disconnect":
                return Disconnect();
            case "shutdown":
                return Shutdown();
            case "list_databases":
                return ListDatabases();
            case "list_schemas":
                return ListSchemas(p);
            case "list_tables":
                return ListTables(p);
            case "list_objects":
                return ListTables(p); // 通用 ODBC 无独立对象类型，回退到表
            case "get_columns":
                return GetColumns(p);
            case "list_indexes":
                return ListIndexes(p);
            case "list_foreign_keys":
                return ListForeignKeys(p);
            case "list_constraints":
                return Array.Empty<object>();
            case "list_triggers":
                return Array.Empty<object>();
            case "get_table_ddl":
                return GetTableDdl(p);
            case "get_object_source":
                return "";
            case "get_explain_info":
                return new { plan = "", has_actual_stats = false };
            case "completion_assistant_search_v1":
                return new { candidates = Array.Empty<object>(), incomplete = false, fallback_used = false };
            case "execute_query":
                return ExecuteQuery(p, false, 0);
            case "execute_query_page":
                return ExecuteQueryPage(p);
            case "fetch_query_page":
                return FetchQueryPage(p);
            case "close_query_session":
                return CloseQuerySession(p);
            case "execute_batch":
            case "execute_transaction":
                return ExecuteTransaction(p);
            case "begin_manual_transaction":
                return BeginManualTransaction(p);
            case "commit_manual_transaction":
                return CommitManualTransaction();
            case "rollback_manual_transaction":
                return RollbackManualTransaction();
            default:
                throw new Exception($"unknown method: {method}");
        }
    }

    // ---------- 连接 ----------

    object Connect(JsonElement p)
    {
        string cs = BuildConnectionString(p);
        try { _conn?.Dispose(); } catch { }
        _conn = new OdbcConnection(cs);
        _conn.Open();
        return new { ok = true };
    }

    object TestConnection(JsonElement p)
    {
        string cs = BuildConnectionString(p);
        using var c = new OdbcConnection(cs);
        c.Open();
        return new { ok = true };
    }

    static string BuildConnectionString(JsonElement p)
    {
        string? cs = Str(p, "connection_string");
        if (!string.IsNullOrWhiteSpace(cs)) return cs;

        string? dsn = Str(p, "dsn");
        string? user = Str(p, "username");
        string? pass = Str(p, "password");

        if (!string.IsNullOrWhiteSpace(dsn))
        {
            var sb = new OdbcConnectionStringBuilder { Dsn = dsn };
            if (!string.IsNullOrWhiteSpace(user)) sb["UID"] = user;
            if (!string.IsNullOrWhiteSpace(pass)) sb["PWD"] = pass;
            return sb.ConnectionString;
        }

        throw new Exception("connection_string or dsn is required");
    }

    object Disconnect()
    {
        CloseAllCursors();
        RollbackManualTxQuiet();
        try { _conn?.Close(); } catch { }
        try { _conn?.Dispose(); } catch { }
        _conn = null;
        return new { ok = true };
    }

    object Shutdown()
    {
        try { Disconnect(); } catch { }
        return new { ok = true };
    }

    object ConnectionInfo()
    {
        EnsureConn();
        return new { server_version = _conn!.ServerVersion, database = _conn.Database ?? "" };
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
            string name = _conn!.Database ?? "default";
            list.Add(new { name });
        }
        return list;
    }

    object ListSchemas(JsonElement p)
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
        string? schema = Str(p, "schema");
        var rows = SafeSchema("Tables").Rows.Cast<DataRow>()
            .Where(r => MatchSchema(r, schema))
            .OrderBy(r => Str(r, "TABLE_NAME") ?? "", StringComparer.OrdinalIgnoreCase);

        var list = new List<object>();
        foreach (DataRow r in rows)
        {
            list.Add(new
            {
                name = Str(r, "TABLE_NAME") ?? "",
                table_type = Str(r, "TABLE_TYPE") ?? "TABLE",
                comment = (string?)null
            });
        }
        return list;
    }

    object GetColumns(JsonElement p)
    {
        EnsureConn();
        string? schema = Str(p, "schema");
        string? table = Str(p, "table");
        var pkCols = PrimaryKeyColumns(schema, table);

        var list = new List<object>();
        var rows = SafeSchema("Columns").Rows.Cast<DataRow>()
            .Where(r => MatchSchema(r, schema) && MatchName(r, "TABLE_NAME", table))
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
        string? schema = Str(p, "schema");
        string? table = Str(p, "table");

        var indexes = new Dictionary<string, (bool primary, bool unique, List<string> cols)>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in SafeSchema("Indexes").Rows)
        {
            if (!MatchSchema(r, schema) || !MatchName(r, "TABLE_NAME", table)) continue;
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
        string? schema = Str(p, "schema");
        string? table = Str(p, "table");

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

    object GetTableDdl(JsonElement p)
    {
        // 通用 ODBC 无法可靠还原目标库原生 DDL，返回空串由 DBX 降级展示。
        return "";
    }

    // ---------- 查询 ----------

    object ExecuteQuery(JsonElement p, bool page, int pageSize)
    {
        EnsureConn();
        string sql = Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");

        int maxRows = Int(p, "maxRows", 1000);
        int limit = maxRows > 0 ? maxRows : 1000;
        int timeout = Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        using var reader = cmd.ExecuteReader();

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
        int pageSize = Int(p, "pageSize", 200);
        if (pageSize <= 0) pageSize = 200;
        var q = ExecuteQueryPageInternal(p, pageSize);
        return new
        {
            q.columns,
            q.column_types,
            q.rows,
            q.affected_rows,
            q.execution_time_ms,
            q.truncated,
            session_id = q.sessionId,
            has_more = q.hasMore
        };
    }

    object FetchQueryPage(JsonElement p)
    {
        string sessionId = Str(p, "sessionId") ?? "";
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
        string sessionId = Str(p, "sessionId") ?? "";
        if (_cursors.Remove(sessionId, out var c))
        {
            try { c.Reader.Close(); } catch { }
            try { c.Reader.Dispose(); } catch { }
        }
        return new { ok = true };
    }

    (List<string> columns, List<string> column_types, List<object?[]> rows, long affected_rows, long execution_time_ms, bool truncated, string sessionId, bool hasMore)
        ExecuteQueryPageInternal(JsonElement p, int pageSize)
    {
        EnsureConn();
        string sql = Str(p, "sql") ?? "";
        if (string.IsNullOrWhiteSpace(sql)) throw new Exception("sql is required");
        int timeout = Int(p, "timeoutSecs", 0);

        var sw = Stopwatch.StartNew();
        var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout > 0 ? timeout : 0;
        ApplyManualTx(cmd);

        var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        var colTypes = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
            colTypes.Add(reader.GetDataTypeName(i));
        }

        string sessionId = (Interlocked.Increment(ref _nextCursorId)).ToString();
        var cursor = new Cursor { Reader = reader, Columns = columns, ColumnTypes = colTypes };
        _cursors[sessionId] = cursor;

        var rows = ReadPage(cursor, pageSize, out bool hasMore);
        long affected = reader.FieldCount == 0 ? Math.Max(0, reader.RecordsAffected) : 0;
        return (columns, colTypes, rows, affected, sw.ElapsedMilliseconds, false, sessionId, hasMore);
    }

    object ExecuteTransaction(JsonElement p)
    {
        EnsureConn();
        var statements = StrArray(p, "statements");
        string? schema = Str(p, "schema");

        long affected = 0;
        var sw = Stopwatch.StartNew();
        using var tx = _conn!.BeginTransaction();
        foreach (string raw in statements)
        {
            string s = raw.Trim().TrimEnd(';').Trim();
            if (s.Length == 0) continue;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = s;
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();

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

    object BeginManualTransaction(JsonElement p)
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

    void ApplyManualTx(OdbcCommand cmd)
    {
        if (_manualTx != null) cmd.Transaction = _manualTx;
    }

    void RollbackManualTxQuiet()
    {
        if (_manualTx == null) return;
        try { _manualTx.Rollback(); } catch { }
        try { _manualTx.Dispose(); } catch { }
        _manualTx = null;
    }

    void CloseAllCursors()
    {
        foreach (var c in _cursors.Values)
        {
            try { c.Reader.Close(); } catch { }
            try { c.Reader.Dispose(); } catch { }
        }
        _cursors.Clear();
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
        foreach (DataRow r in SafeSchema("Indexes").Rows)
        {
            if (!BoolFrom(r, "PRIMARY_KEY")) continue;
            if (!MatchSchema(r, schema) || !MatchName(r, "TABLE_NAME", table)) continue;
            string? col = Str(r, "COLUMN_NAME");
            if (!string.IsNullOrWhiteSpace(col)) set.Add(col);
        }
        return set;
    }

    static bool MatchSchema(DataRow r, string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return true;
        return string.Equals(Str(r, "TABLE_SCHEM"), schema, StringComparison.OrdinalIgnoreCase);
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

    static string? Str(JsonElement p, string key)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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

class Cursor
{
    public OdbcDataReader Reader = null!;
    public List<string> Columns = new();
    public List<string> ColumnTypes = new();
    public object?[]? Pending;
}
