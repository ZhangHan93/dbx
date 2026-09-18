using System.Buffers.Binary;

// ============================================================================
// Firebird 文件头识别（零依赖，不加载引擎）
//
// 判据于 2026-09-17 定稿并双向实测：13 个真实库全部命中，6 个反例全部排除。
//   ① 页类型   u16 @ +0x00 == 1
//   ② 页大小   u16 @ +0x10 ∈ {1024, 2048, 4096, 8192, 16384}
//   ③ ODS 版本 u8  @ +0x12（低字节 = major）、u16 @ +0x40（= minor）
//   ④ Dialect  u8  @ +0x2A 的 bit4（1 ⇒ dialect 3）
//
// ⚠️ 只读 ODS **一定假阳性**：任意文件在 +0x12 / +0x40 都必然有字节，撞进合法集合
//    就会把非 Firebird 文件认成 Firebird 库。所以三条必须串联，且①优先 ——
//    实测 6 个反例全都是被①首先拦下的，它们的「页类型」值恰好就是文件自身的魔术字
//    （MZ=23117、PK=19280、SQ=20819、UTF-8 BOM=48111、"# "=8227），判别力极强。
//
// ⚠️ +0x13 **不是** ODS minor：ODS 11 以后那里放的是 0x80 标志位。minor 在 +0x40。
// ============================================================================

internal sealed record FirebirdHeader
{
    public required bool IsFirebird { get; init; }
    public int PageType { get; init; }
    public int PageSize { get; init; }
    public int OdsMajor { get; init; }
    public int OdsMinor { get; init; }

    /// <summary>从文件头读出的 SQL dialect（1 或 3）。**必须写进连接串**。</summary>
    public int Dialect { get; init; }

    public string? RejectReason { get; init; }

    public string OdsLabel => $"{OdsMajor}.{OdsMinor}";

    public string? EngineKey => IsFirebird ? EnginePin.EngineKeyForOds(OdsMajor, OdsMinor) : null;

    /// <summary>给用户看的一句话。</summary>
    public string Describe() => IsFirebird
        ? $"Firebird ODS {OdsLabel} (page size {PageSize}, dialect {Dialect})"
        : $"not a Firebird database: {RejectReason}";
}

internal static class FirebirdHeaderReader
{
    const int HeaderSize = 0x60;
    const int MinimumUsefulSize = 0x42;

    static readonly HashSet<int> ValidPageSizes = new() { 1024, 2048, 4096, 8192, 16384 };

    public static FirebirdHeader Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Reject("no database file path was provided");

        byte[] head;
        try
        {
            if (!File.Exists(path))
                return Reject($"database file not found: {path}");

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // 客户现场常常是「Superserver 正开着这个库」⇒ 必须以共享方式打开，
                // 否则连读文件头都会因共享冲突失败（那会误报成「不是一个 Firebird 库」）。
                FileShare.ReadWrite | FileShare.Delete);

            head = new byte[HeaderSize];
            int read = stream.ReadAtLeast(head, HeaderSize, throwOnEndOfStream: false);
            if (read < MinimumUsefulSize)
                return Reject($"file is too small to be a Firebird database ({read} bytes)");
        }
        catch (Exception e)
        {
            return Reject($"cannot read the database header: {e.Message}");
        }

        int pageType = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x00, 2));
        int pageSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x10, 2));
        int odsMajor = head[0x12];
        int odsMinor = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x40, 2));
        int dialect = (head[0x2A] & 0x10) != 0 ? 3 : 1;

        // ① 页类型 —— 判别力最强的一条，放最前面。
        if (pageType != 1)
            return Reject($"header page type is {pageType}, expected 1", pageType, pageSize, odsMajor, odsMinor, dialect);

        // ② 页大小
        if (!ValidPageSizes.Contains(pageSize))
            return Reject($"page size {pageSize} is not a Firebird page size", pageType, pageSize, odsMajor, odsMinor, dialect);

        return new FirebirdHeader
        {
            IsFirebird = true,
            PageType = pageType,
            PageSize = pageSize,
            OdsMajor = odsMajor,
            OdsMinor = odsMinor,
            Dialect = dialect,
        };
    }

    /// <summary>
    /// 兜底提示：区分「根本不是 Firebird」与「是 Firebird 但版本没覆盖」。两者要给
    /// 用户完全不同的话术，否则 ODS 14 的库会被说成「这不是 Firebird 库」。
    /// </summary>
    public static string DescribeSupport(FirebirdHeader header)
    {
        if (!header.IsFirebird) return header.Describe();
        if (header.EngineKey != null) return header.Describe();

        string supported = string.Join("、", EnginePin.Engines.Select(e => $"{e.Key} ({e.OdsRange})"));
        return $"Firebird ODS {header.OdsLabel} is newer than this build supports. " +
               $"Supported: {supported}. The file is a Firebird database, just not one this agent can open yet.";
    }

    static FirebirdHeader Reject(
        string reason,
        int pageType = 0,
        int pageSize = 0,
        int odsMajor = 0,
        int odsMinor = 0,
        int dialect = 1) => new()
        {
            IsFirebird = false,
            PageType = pageType,
            PageSize = pageSize,
            OdsMajor = odsMajor,
            OdsMinor = odsMinor,
            Dialect = dialect,
            RejectReason = reason,
        };
}
