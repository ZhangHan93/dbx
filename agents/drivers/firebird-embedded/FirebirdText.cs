using System.Text;

// ============================================================================
// NONE 字符集数据库的文本还原（方案 §4.6 / 风险表「GBK 库当成 UTF8 → 中文乱码」）
//
// 问题
// ----
// 客户那批老库（如 `病例.g_b`）建库时用的是 `CHARACTER SET NONE`。Firebird 对 NONE 列
// **不做任何字符集转换**，而 FirebirdClient 在连接字符集也是 NONE 时，会把存储字节
// **1:1 当成字符**返回（Latin1 语义）——即 GBK 汉字会变成 `²¡Àý` 这种乱码。
// 这不是"解码错了"，而是**字节原样**：所以能无损还原。
//
// 还原方法（方案 §4.6 实测给出的配方）
// -----------------------------------
//   byte[] raw = Encoding.Latin1.GetBytes(str);   // 拿回原始存储字节
//   string gbk = Encoding.GetEncoding("GBK").GetString(raw);
//
// ⚠️ 只在「库字符集 = NONE」时才做这件事。UTF8 / WIN1252 等库由 provider 正常解码，
//    再套一层 Latin1 往返反而会**把正确的中文弄坏**。所以本类由 FirebirdSession 用
//    `RDB$DATABASE.RDB$CHARACTER_SET_NAME` 探到的字符集来开关（见 Program.cs Open）。
//
// ⚠️ .NET Core 起非 Unicode 代码页（936/GBK）默认不可用，必须显式注册
//    `CodePagesEncodingProvider`，否则 `Encoding.GetEncoding(936)` 直接抛
//    `ArgumentException: 'GBK' is not a supported encoding name`。已在 Main 里注册。
// ============================================================================

internal static class FirebirdText
{
    /// <summary>严格 UTF-8：遇到非法字节序列抛异常，用来做"是不是 UTF-8"的判定。</summary>
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static Encoding? _gbk;
    static bool _registered;

    /// <summary>注册 CodePages 提供程序并取到 GBK。失败也不致命（还原会退化成原样返回）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbk = Encoding.GetEncoding(936);
        }
        catch
        {
            _gbk = null;
        }
    }

    public static bool GbkAvailable => _gbk != null;

    /// <summary>
    /// 把「裸字节当字符」来的字符串还原成真实文本。
    /// </summary>
    /// <remarks>
    /// 判定顺序：纯 ASCII（怎么解都一样）→ 严格 UTF-8 → GBK。
    /// <para>
    /// 为什么 UTF-8 在前：GBK 汉字的字节序列（如 `B2 A1`）**绝大多数不是合法 UTF-8**
    /// （首字节 `B2` 落在续字节区间 ⇒ 严格解码抛错），于是会退到 GBK；反过来把 UTF-8
    /// 字节喂给 GBK 则是**静默产出乱码且不报错**。所以先试会抛错的那条，期望结果更好。
    /// </para>
    /// <para>
    /// 反过来，若字符串里出现 &gt; U+00FF 的字符，说明 provider 用的**不是** Latin1
    /// 语义（做了替换或真解码）⇒ 往返已经丢字节，此时原样返回，绝不二次加工。
    /// </para>
    /// </remarks>
    public static string Restore(string raw)
    {
        if (raw.Length == 0) return raw;

        bool asciiOnly = true;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c > '\u00FF') return raw;   // 不是 Latin1 语义 ⇒ 不做任何处理
            if (c > '\u007F') asciiOnly = false;
        }
        if (asciiOnly) return raw;

        byte[] bytes = Encoding.Latin1.GetBytes(raw);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8，继续往下试 GBK。
        }

        if (_gbk != null)
        {
            try { return _gbk.GetString(bytes); }
            catch { /* 落回原样 */ }
        }

        return raw;
    }

    /// <summary>按需还原一个结果值；非字符串原样返回。</summary>
    public static object? Restore(object? value) => value switch
    {
        string text => Restore(text),
        _ => value,
    };
}
