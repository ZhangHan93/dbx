// ============================================================================
// ★ S4-2「引擎钉死」—— Firebird Embedded agent 唯一的架构红线（必修）
//
// 为什么需要它
// ------------
// 单 exe 架构下「一个进程只加载一套引擎」**没有任何架构保证**，只有代码纪律。
//
// ⚠️⚠️ 2026-09-18 读上游源码后**推翻了方案 N8 的前提**：
//   方案押注「DBX 是连接级进程隔离 ⇒ 一进程一引擎天然成立」。**押注不成立**：
//     · crates/dbx-core/src/agent_runtime.rs  `shared_runtime_key()`
//         = "{agent_key}|{program}|{args}|{working_dir}"   ← 不含连接 id、不含库路径
//     · crates/dbx-core/src/connection.rs     `spawn_routed_shared_agent_client()`
//         按该 key 在 `manager.connection_runtimes` 里**缓存并复用** agent 进程
//   ⇒ 同一个 db_type 的**所有连接共用一个 agent.exe**。
//   所以「一个进程先后遇到两个不同 ODS 的库」是**正常使用就会发生**的事。
//
// 而「同进程多引擎」的后果是不可观测的：
//   · fb30 的 fbclient.dll 一进进程，fb50 的 engine13.dll 就按**基名**命中它
//     → err=127「找不到入口点」；
//   · 更坏的是 fb25 → fb50 → fb30 这个顺序下三引擎**能共存**（T4 实测 16 个模块、
//     查询结果全对）—— 但那靠的是「FB3.0 的 Engine12 挂在 FB5.0 的 fbclient 上」
//     这种**混版引擎**，官方不支持，且从模块列表里看不出来，查询结果不可信。
//   · AssemblyLoadContext 只治托管层（能力标志污染），**治不了 native 插件层**；
//     Unload() + 3×GC 之后 fb50 的 fbclient.dll / Engine13.DLL **仍驻留进程**。
//
// 所以本类把「一进程一引擎」强制成代码纪律，并且**不是报错了事**：
//   ① 首次 attach 决定引擎后，本进程**永不变更**；
//   ② 需要不同引擎 ⇒ 抛 `AgentFaultException.ReplaceRuntime`：
//        · **绝不静默加载第二个**（宁可失败，不要静默混版）；
//        · 同时请求 DBX **kill 本进程并换一个新的**（见 AgentFault.cs 与
//          upstream `replace_runtime_after_open_failure()`）⇒ 用户重开即成功。
//   ③ 加载失败 ⇒ 标记不可用 + 同样走 ReplaceRuntime。
//
// 逃生通道已**逐环节静态核验**（2026-09-18，全链无缺口）：
//   ① `agent_driver.rs:672` parse_agent_call_error —— 未声明 structured_error_v1
//      ⇒ strict_structured_errors=false ⇒ 直接 return Legacy 分支（**不查契约**）；
//   ② `agent_driver.rs:718` legacy_agent_hints —— 认的是 data 里的
//      category / retryable / sessionDisposition / stage / operationOutcome / agentSessionId
//      （camelCase），`"replace_runtime"` → Some(ReplaceRuntime)；
//   ③ `agent_recovery.rs:57` from_disposition —— Some(ReplaceRuntime) 命中**首臂**，
//      **不校验 category、不校验 RecoveryScope** ⇒ 任何调用点都能换进程；
//   ④ `connection.rs:736` replace_runtime_after_open_failure —— kill runtime
//      + 摘掉所有 uses_runtime(failed_runtime) 的连接池。
//   ⇒ 契约字段名错一个字符就静默退化成 KeepSession，所以 AgentFault.cs 里的常量
//     不要手改；`firebird_agent_probe.py` 每次都会断言 data 形状。
//
// ⚠️ ReplaceRuntime 的已知代价（上游设计使然，不是本驱动引入）：DBX 换进程时会
//    摘掉**所有复用该 runtime 的连接**（第④步 filter 用的是 uses_runtime，不是连接 id）。
//
// ⚠️ **已知限制：混合 ODS 的两个连接会互相踢（"音乐椅"）**。因为同一 db_type 只有
//    一个 runtime，所以：
//      · 连接1 = ODS 10（fb25）、连接2 = ODS 13（fb50）同时开着时 ——
//        谁后开谁赢；先开的那条连接池被摘掉 ⇒ UI 上表现为「另一条连接自己断了」。
//        用户重新打开它，又反过来把对方踢掉。**交替操作会持续重启 agent 进程**。
//      · 功能不坏（不会静默混版、不会算错），但**性能与体验都不好**。
//      · 这是上游缺少「按连接分进程」能力导致的（native agent 的
//        launch spec = {program: 固定路径, args: [], working_dir: driver_dir(driver_key)}，
//        见 agent_manager.rs:892-905 ⇒ runtime_key 与连接无关）。
//    ⇒ **缓解办法（未实施，备选）**：再注册 3 个 driver_key
//      （firebird-embedded-fb25/30/50），各自 launch config 指向同一个 agent.exe
//      但带不同 args（args 参与 runtime_key ⇒ 天然分进程），前端各加一个「引擎」
//      下拉（Auto / 强制 2.5 / 强制 3.0 / 强制 5.0）。仅当用户**必须**同时开混合
//      ODS 连接时才需要，且要改 yaml + Rust + 前端 ⇒ 走 CI（≈60 min）。
//      当前不出自动检测是更重要的能力，这条留作后续按需补。
// ============================================================================

internal enum EngineState
{
    Unpinned,
    Pinned,
    Failed,
}

internal sealed record EngineDescriptor(
    string Key,
    string DirectoryName,
    string ClientLibraryFileName,
    string OdsRange);

internal static class EnginePin
{
    /// <summary>
    /// 引擎目录名与磁盘布局一致。<c>engines/</c> **不进 git**（每套 12–51 MB，合计约
    /// 89 MB），在打包/部署时本地拷入；CI 只产 <c>agent.exe</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ FB 2.5 的入口 DLL 是 <c>fbembed.dll</c>，**不要改名为 fbclient.dll**。
    ///    engine1x 对 fbclient 的依赖是**裸名**（与磁盘路径无关），改名只会让
    ///    err 127「找不到入口点」退化成 err 126「彻底打不开」。
    /// ⚠️ FB 3.0+ 必须用 <c>fbclient.dll</c>：引擎实体在 plugins/engine1x.dll，由它
    ///    按相对路径拉起；把 ClientLibrary 指向 engine1x.dll 是错的。
    /// </remarks>
    static readonly EngineDescriptor[] Catalog =
    {
        new("fb25", "fb25_x86", "fbembed.dll", "ODS 10.0 - 11.2"),
        new("fb30", "fb30_x86", "fbclient.dll", "ODS 12.0 - 12.1"),
        new("fb50", "fb50_x86", "fbclient.dll", "ODS 13.0 - 13.1"),
    };

    static readonly object Gate = new();
    static EngineState _state = EngineState.Unpinned;
    static string? _key;
    static string? _clientLibrary;
    static string? _failure;

    /// <summary>
    /// engines/ 与 agent.exe 同目录。single-file 发布下 AppContext.BaseDirectory 就是
    /// exe 所在目录，因此部署时 <c>engines/</c> 必须与 <c>agent.exe</c> 平级。
    /// </summary>
    public static string EngineRoot => Path.Combine(AppContext.BaseDirectory, "engines");

    public static bool IsPinned
    {
        get { lock (Gate) return _state == EngineState.Pinned; }
    }

    public static string? PinnedKey
    {
        get { lock (Gate) return _key; }
    }

    public static string? PinnedClientLibrary
    {
        get { lock (Gate) return _clientLibrary; }
    }

    public static IReadOnlyList<EngineDescriptor> Engines => Catalog;

    public static EngineDescriptor? Describe(string? key) =>
        key == null ? null : Catalog.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// ODS → 引擎。**严格一一对应，不是向下兼容**（已实测）：10.0–11.2 只能 fb25、
    /// 12.x 只能 fb30、13.0–13.1 只能 fb50。fb40 不在表内：ODS 13.x 由 fb50 覆盖，
    /// 不需要 4.0 引擎。
    /// </summary>
    public static string? EngineKeyForOds(int major, int minor) => (major, minor) switch
    {
        (10, 0) or (10, 1) => "fb25",
        (11, 0) or (11, 1) or (11, 2) => "fb25",
        (12, 0) or (12, 1) => "fb30",
        (13, 0) or (13, 1) => "fb50",
        _ => null,
    };

    /// <summary>
    /// ★ 钉死引擎并返回 <c>ClientLibrary</c> 的**绝对路径**。
    /// </summary>
    /// <param name="engineKey">"fb25" | "fb30" | "fb50"</param>
    /// <param name="requestRuntimeReplacement">
    /// 护栏拒绝时是否要求 DBX 换进程。
    /// <para>
    /// <c>open_session</c> 传 true：这个进程永远服务不了这个连接 ⇒ 必须换。
    /// </para>
    /// <para>
    /// <c>test_connection</c> 传 false：护栏是**在任何东西被加载之前**拒绝的，进程还干净，
    /// 在用连接毫发无伤 ⇒ 只报错即可。否则「点一下测试连接」会把用户正在用的其他
    /// Firebird Embedded 连接一起杀掉（上游换进程会摘掉所有复用该 runtime 的连接）。
    /// </para>
    /// </param>
    /// <remarks>
    /// 必须绝对路径：裸名 <c>fbclient.dll</c> 会命中 <c>%WINDIR%\System32\fbclient.dll</c>
    /// （客户机只要装过 Firebird 就有），随后报 <c>xnet://Global\FIREBIRD</c> 一类与
    /// 真实原因完全无关的错误，误导性极强。
    ///
    /// 另外**绝不设置 FIREBIRD 环境变量**：它是进程级的，会覆盖所有引擎的 root
    /// 推断，正是「多引擎互相污染」的另一条入口。
    /// </remarks>
    public static string Pin(string engineKey, bool requestRuntimeReplacement = true)
    {
        var descriptor = Describe(engineKey)
            ?? throw new InvalidOperationException($"unknown Firebird engine: {engineKey}");

        lock (Gate)
        {
            if (_state == EngineState.Failed)
            {
                string reason = $"Firebird engine is unusable in this agent process: {_failure}.";
                throw requestRuntimeReplacement
                    ? AgentFaultException.ReplaceRuntime(reason + " Databases needing it cannot be opened here.")
                    : new InvalidOperationException(reason);
            }

            if (_state == EngineState.Pinned)
            {
                // ★ 红线②：绝不静默加载第二个引擎 —— 静默混版是最坏的结局。
                if (!string.Equals(_key, engineKey, StringComparison.Ordinal))
                {
                    string reason =
                        $"This agent process already opened a database with the Firebird {_key} engine, " +
                        $"and it cannot load the {engineKey} engine on top (Windows resolves the engine plugin " +
                        "by base name, so a second engine would silently mix versions and return untrustworthy " +
                        "results).";
                    throw requestRuntimeReplacement
                        // DBX 会 kill 本进程并摘掉其他 Firebird Embedded 连接，用户重开即成功。
                        ? AgentFaultException.ReplaceRuntime(reason + " Reopen this connection to get a fresh process for it.")
                        // 进程还干净（这次调用什么都没加载）：只说明这一次测试做不了。
                        : new InvalidOperationException(
                            reason + " Open the connection directly instead: DBX will start a fresh process for it.");
                }
                return _clientLibrary!;
            }

            string directory = Path.Combine(EngineRoot, descriptor.DirectoryName);
            string dll = Path.Combine(directory, descriptor.ClientLibraryFileName);
            if (!File.Exists(dll))
            {
                // ★ 红线③：加载失败即整进程标记不可用，绝不回退到别的引擎。
                _state = EngineState.Failed;
                _failure = $"engine '{engineKey}' is missing {descriptor.ClientLibraryFileName} under {directory}";
                throw AgentFaultException.ReplaceRuntime(_failure + ". The bundled engines are incomplete.");
            }

            _state = EngineState.Pinned;
            _key = engineKey;
            _clientLibrary = dll;
            return dll;
        }
    }

    /// <summary>
    /// 引擎已在进程内初始化失败（例如 attach 成功后 native DLL 被占用/替换）时调用。
    /// 之后本进程不再接受任何 attach，直到 DBX 断开连接把进程杀掉。
    /// </summary>
    public static void MarkFailed(string reason)
    {
        lock (Gate)
        {
            _state = EngineState.Failed;
            _failure = reason;
        }
    }

    /// <summary>给错误信息用：当前钉死状态的一句话描述。</summary>
    public static string StatusLine()
    {
        lock (Gate)
        {
            return _state switch
            {
                EngineState.Pinned => $"pinned to '{_key}' ({_clientLibrary})",
                EngineState.Failed => $"failed: {_failure}",
                _ => "no engine loaded yet",
            };
        }
    }
}
