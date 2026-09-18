// ============================================================================
// ★ S4-2「引擎钉死」—— Firebird Embedded agent 的第二道防线
//
// 为什么需要它
// ------------
// 「一个进程只加载一套引擎」在**桌面连接路径**上已由架构保证（每连接一个 agent.exe），
// 但那条保证来自上游的一个实现细节，而不是协议承诺 ⇒ 必须有代码兜底。
//
// ⚠️ 2026-09-18 两次修正后的准确图景（此前两个版本都写错过，以此为准）：
//
//   桌面程序走 `connect_db`（src-tauri/src/commands/connection.rs:1669）
//     → `db_type if is_agent_type(&db_type)`（:2034）→ `connect_agent_pool`（:170）
//     → `agent_manager.spawn()` → `spawn_client_for_key`（agent_runtime.rs:300）
//     ⇒ **无条件新起进程、不查任何缓存、不看 capabilities** ⇒ 每连接一个 agent.exe。
//   （2026-09-18 协议探针实测：每条连接只收到 handshake → connect → connection_info，
//     从不出现 open_session，也无 kill/respawn 痕迹；2026-09-17 真机同结论。）
//
//   而「共享 runtime」是**另一条入口**：
//     · crates/dbx-core/src/agent_runtime.rs `shared_runtime_key()`
//         = "{agent_key}|{program}|{args}|{working_dir}"   ← 不含连接 id、不含库路径
//     · crates/dbx-core/src/connection.rs:2183 `get_or_create_pool_for_session_inner()`
//         按该 key 在 `manager.connection_runtimes` 里**缓存并复用** agent 进程
//     ⇒ 那条路上同一 db_type 的**所有连接共用一个 agent.exe**。
//   桌面 GUI 首次连接不走它（MCP / Web / 懒建会话池才走）。
//
//   ⇒ 本类的定位：桌面路径上是**安全网**（正常不触发）；
//     共享路径上是**唯一防线**（那条路上一个进程真的会先后遇到不同 ODS 的库）。
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
// ✅ 形态已定（2026-09-18）：`Program.cs::DeclareMultiSession = false`
//    ⇒ 把共享 runtime 入口挡在门外：即便将来上游把桌面入口也接到共享路径上，
//      本 agent 也不声明 multi_session，因而仍走 legacy spawn ⇒ 仍是每连接一进程。
//    所以本类退化为**安全网**：正常使用不会触发，触发即说明前提被破坏
//    （上游改了进程模型、有人把开关改回 true、或某条入口强制走 open_session）。
//
// ⚠️ 逃生通道（ReplaceRuntime）的可达性**因入口而异**：
//    · 共享路径：`open_session` 收到本类的 replace_runtime ⇒ DBX kill 该 runtime 并重起
//      （`connection.rs:736 replace_runtime_after_open_failure`，逐环节静态核验见下）；
//    · 桌面路径：agent 由 legacy `connect` 驱动，而 `connect_agent_pool` 只会对 Oracle
//      描述符做重试（`connection.rs:204`）⇒ 此时 replace_runtime 表现为**一条连接失败提示**，
//      不会自动换进程。这不是实际缺口：桌面路径每连接一进程，本类不会触发。
//
// ⚠️ 如果哪天把 `DeclareMultiSession` 改回 true：走共享路径的入口会变成
//    同一 db_type 的所有连接共用**一个**进程，于是出现"音乐椅" ——
//    连接1 = ODS 10（fb25）与连接2 = ODS 13（fb50）谁后开谁赢，
//    先开的那条连接池被摘掉（UI 上表现为「另一条连接自己断了」），
//    重新打开它又反过来把对方踢掉。**功能不坏**（不会静默混版、不会算错数据），
//    但体验与性能都差。根因是 upstream 的 runtime 缓存键不含连接 id
//    （`agent_manager.rs:892-905` ⇒ launch spec 恒为 {固定 program, args:[], driver_dir}）。
//    真要同时开混合 ODS 又不想每连接两进程，才需要"多 driver_key + 引擎下拉"那套
//    （改 yaml + Rust + 前端 ⇒ 走 CI ≈60 min），当前不必要。
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
