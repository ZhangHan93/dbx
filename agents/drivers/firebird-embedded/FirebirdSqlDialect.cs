// ============================================================================
// SQL dialect 1 归一化
//
// 问题（2026-09-18 真机实测）
// -------------------------
// 客户老库 `病例.g_b` 是 **SQL dialect 1**（文件头 +0x2A bit4 = 0）。在 dialect 1 里：
//     `"` 是【字符串定界符】，不是标识符定界符。
// 于是 `SELECT * FROM "PTNTBL"` 直接语法错：
//     Dynamic SQL Error / SQL error code = -104 / Token unknown - line 1, column 27 "PTNTBL"
// 而 DBX 生成 SQL 时是按 **db_type** 决定引号的（`quoteTableIdentifier` 的 default 分支
// 给双引号），**不知道 dialect** —— 方言是「库」的属性，不是「驱动类型」的属性，
// 那个映射天生表达不了它。实测对比（同一台机、同一个 agent）：
//     dialect 1：`FROM PTNTBL` ✅    `FROM "PTNTBL"` ❌
//     dialect 3：`FROM "PTNTBL"` ✅
//
// 为什么在 agent 里修，而不是改前端/Rust
// ------------------------------------
// · 前端 `tableSelectSql.ts` 与 Rust `sql_dialect/*` 都是**按 db_type** 决定引号的，
//   要改成 dialect 感知就得把「连接后才知道的方言」回灌进两处生成器，链路长；
// · 而**执行**必然经过 agent —— 在这里做一处归一化，就同时覆盖前端、Rust、
//   MCP、批量执行、导出等所有路径，不会有漏网的生成器。
// · 前端那边仍顺手把 `firebird-embedded` 的静态引号改成"不加引号"（见
//   `quoteTableIdentifier`），让界面上显示的 SQL 也是对的。
//
// 归一化做什么（**只在 dialect 1 生效**）
// ------------------------------------
//   把 `"IDENT"` 变成 `IDENT`。因为 dialect 1 根除没有「带引号的标识符」这个概念，
//   而 Firebird 把未加引号建的标识符一律存成大写 —— 所以去掉引号永远是安全的。
//
// 安全性：只认结构，不做正则硬替换
// ------------------------------
//   逐字符扫描，**跳过**：`'...'` 单引号字符串（含 `''` 转义）、`--` 行注释、`/* */` 块注释。
//   只有处于"代码"状态的 `"` 才会被当作标识符定界符处理。
//   例外：dialect 1 里用户理论上可以用 `"..."` 写字符串字面量。这种写法极少见
//   （正常都写单引号），而且代价是「老库整表打不开」，两相权衡取前者。
// ============================================================================

internal static class FirebirdSqlDialect
{
    /// <summary>
    /// 按库的 SQL 方言归一化 SQL。<paramref name="dialect"/> 3（或未知）时原样返回。
    /// </summary>
    public static string NormalizeForDialect(string sql, int dialect)
        => dialect == 1 ? StripIdentifierQuotes(sql) : sql;

    /// <summary>把处于代码状态的 <c>"IDENT"</c> 转成 <c>IDENT</c>。</summary>
    static string StripIdentifierQuotes(string sql)
    {
        if (sql.IndexOf('"') < 0) return sql;   // 绝大多数语句走这条快路

        var output = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        int n = sql.Length;

        while (i < n)
        {
            char c = sql[i];

            // 单引号字符串：整体复制，处理 '' 转义
            if (c == '\'')
            {
                output.Append(c);
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'')
                    {
                        output.Append('\'');
                        i++;
                        if (i < n && sql[i] == '\'') { output.Append('\''); i++; continue; }
                        break;
                    }
                    output.Append(sql[i]);
                    i++;
                }
                continue;
            }

            // 行注释：复制到行尾
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') { output.Append(sql[i]); i++; }
                continue;
            }

            // 块注释：复制到 */
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                output.Append("/*");
                i += 2;
                while (i < n)
                {
                    if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/')
                    {
                        output.Append("*/");
                        i += 2;
                        break;
                    }
                    output.Append(sql[i]);
                    i++;
                }
                continue;
            }

            // 标识符定界符：丢掉两端的引号，内容原样保留
            if (c == '"')
            {
                i++;
                while (i < n && sql[i] != '"') { output.Append(sql[i]); i++; }
                if (i < n) i++;   // 跳过收尾引号
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }
}
