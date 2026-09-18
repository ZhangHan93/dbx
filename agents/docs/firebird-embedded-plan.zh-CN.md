# Firebird Embedded 自研 agent —— 可实施方案（施工单）

> **状态**：待开发 · 依据 `DBX离线包/DBX-FirebirdEmbedded驱动设计方案.md`（v11）+ 2026-09-17 两项真机实测
> **目标分支**：`my-agents` · **新增连接类型**：`firebird-embedded`（第三个自研类型，前两个是 `odbc` / `odbc32`）
> **本文件定位**：把 v11 的「设计」变成「照着做就行」的施工单。**每一处改动都带确切文件 + 行号 + 锚点文本**。
> **前置阅读**：`DBX离线包/DBX-自编驱动避坑手册.md`（★必读）+ 本文件 §4「ODBC 已踩过的坑 → 本方案规避」

---

## 0. TL;DR：决策与工作量

| 项 | 定案 | 依据 |
|---|---|---|
| agent 形态 | ★**单 exe**：`agent.exe` 进程内加载引擎，**不做 `fbhost.exe`**。靠**连接级进程隔离**（DBX 已实测）+ **引擎钉死纪律**（S4-2，必修）保证「一进程一引擎」 | 2026-09-18 决策；依据见 §2 |
| agent 位宽 | **只编 x86** | ODS 10 必须 32 位引擎（FB 2.5 无 x64 embedded）；fb30/fb50 也用 x86 引擎，同进程位宽才能共存 |
| 引擎 | `fb25`(2.5.9) / `fb30`(3.0.14) / `fb50`(5.0.4)，**全 Win32 x86**，共 **89 MB**；**fb40 不需要** | ODS 与引擎严格一一对应（非向下兼容） |
| 引擎随谁走 | **不进 git、不进 CI**；CI 只产 `agent.exe`，引擎在**打包/部署时**本地拷入 | 保持仓库轻、CI 快；引擎可直接从 firebirdsql.org 复现 |
| **文件路径存哪个字段** | ★**复用 `host`** —— **不新增 `file_path` 字段** | access / sqlite / duckdb 的既有先例；可**完全绕开「增传参数」整类坑**（见 §1.1） |
| `formKind` | ★**不写**（走默认 `standard`） | `formKind` 是**闭集**，写 `firebird-embedded` 会直接炸校验器（见 §1.2） |
| `localFile: true` | 要写 | 一行换到「文件路径输入框 + 浏览按钮 + 副标题 + 必填校验」全套泛化能力 |
| 打开策略 | **直连优先，副本兜底**（探测两路并联） | v11 §4.1/§4.3；`FileShare.None` 单独用有盲区 |
| 引擎选择 | 开库前**自读文件头**三判据 → 选定引擎 + Dialect | 2026-09-17 实测（13 真库全中 / 6 反例全排除） |
| 本机可编？ | ✅ **C# agent 分钟级本地可编**（dotnet 10.0.401 已装）⇒ 只有**前端/Rust**改动才要等 CI | 见 §5.2 |

**工作量分解（改动面）**：Rust **5 处**（其中 1 处漏了编译红、1 处漏了**静默算错**）· 前端 **4 处** · 配置 **2 个 yaml + 3 个生成物** · 新代码 **1 个 C# 工程** · CI **1 个 workflow**。

---

## 1. 必须先修正 v11 的 4 处（照抄会白烧一轮 CI ≈60 min）

### 1.1 ⭐ 最大简化：**不要新增 `file_path` 字段，复用 `host`**

v11 §3.3 原计划给 `ConnectionConfig` 加 `file_path?: string`。**经查证这不必要，而且会主动踩进 ODBC 最贵的那类坑。**

证据链（全部实测核对过源码）：

| # | 事实 | 位置 |
|---|---|---|
| 1 | `connectionFilePath()` 把**文件路径从 `host` 取**，已覆盖 `sqlite` / `duckdb` / `access` | `apps/desktop/src/lib/connection/connectionFile.ts:29-43` |
| 2 | 表单的本地文件输入**完全泛化**：`usesLocalFilePathInput = isLocalFileTypeDb(db_type) && …`，模板里是 `v-model="form.host"` | `ConnectionDialog.vue:3146`、`:6841-6844` |
| 3 | 副标题也**泛化**：`if (isLocalFilePresentationConnection(c)) return c.host \|\| c.database \|\| "local";` | `connectionPresentation.ts:44` |
| 4 | 「保存并连接」的兜底判据**已包含 `host`**：`return !!(form.value.host \|\| …)` | `ConnectionDialog.vue:3760` |
| 5 | `host` **原样送达 agent**：`params = json!({"host": agent_host, …})` | `crates/dbx-core/src/agent_connection.rs:123` |
| 6 | 先例：`access` / `h2` 都是「文件型 + agent」，**都把路径放 `host`** | `plugins/connection-types/access.yaml:15`、`h2.yaml:33` |

⇒ **复用 `host` 的收益**（不是省事，是**消坑**）：

- **零 Rust 参数链路改动** ⇒ 直接消掉手册 §3.2「给 agent 增传参数要动 **4 个地方**」+ 其中「第 3 处写成 `Self { }`，用类型名正则**永远扫不到**」+「**CI 绿 ≠ 参数通了**」——这三条是上一轮 ODBC **CI 假绿事故的根因**。
- 自动获得：文件路径输入框、浏览按钮、侧栏副标题、必填校验、`isLocalFileDb`（reveal）。
- `ConnectionConfigData` / `impl From` / `agent_connect_params_with_role` 白名单 **一律不动**。

**唯一代价**：`connectionFile.ts:31` 那个硬编码类型名的 `if` 要加一个分支（见 S3-1）。

> ⚠️ 注意别搞混：**「文件路径用 `host`」≠「不能用 `localFile: true`」**。`localFile: true` 正是打开上面那套泛化能力的开关，**必须写**。

### 1.2 `formKind` 必须省略，不能写 `firebird-embedded`

`formKind` 是**闭集**，校验器会直接抛错：

```js
// scripts/sync-connection-types.mjs:18
const formKinds = new Set(["standard", "jdbc", "mq", "mqtt", "nacos", "odbc"]);
//              :135-137  → throw new Error(`${location}: invalid formKind`)
```

⇒ 写 `formKind: firebird-embedded` 会让 `pnpm generate:connection-types` **当场失败**（而且 `pretypecheck` / `pretest` 都会先跑它）。
**正解：整个键省略**，落到默认 `standard`；文件输入框由 `localFile: true` 提供（先例 `access` / `h2` 同样省略）。

### 1.3 `database-drivers.manifest.json` 是**生成物，不能手改**

v11 §3.2 写「追加一条 `dbType: firebird-embedded`」——**错**。它是 `sync-connection-types.mjs` 的产物（`:10`、`:235-237`），手改会在 `--check` 时报 "out of date"。**只改 yaml，然后跑生成器。**

### 1.4 Rust 的 `DatabaseType` 枚举也是**生成的**，不要手写

`crates/dbx-core/build.rs:117-138` 从 yaml 的 `rustVariant` 生成枚举 + `ALL` + `as_str()`。
⇒ **不手改枚举**。v11 §3.2 那句「若脚本不覆盖 Rust 枚举，手动补」的疑虑**可以关闭**：`rustVariant: FirebirdEmbedded` 一写，枚举自动出现。
（编译期 `assert!` 只查 4 件事：`schemaVersion==1` / `dbType` 唯一 / `rustVariant` 唯一 / `order` 唯一 —— `build.rs:90-97`。）

---

## 2. 架构定案：**单 exe**（进程内加载引擎，不做 `fbhost`）

```
DBX (dbx.exe, x64)
 │  spawn，按 agentKey 找 <data>/agents/drivers/firebird-embedded/agent.exe
 ▼
agent.exe  (x86)   ← 一个连接 = 一个进程（DBX 已实测为连接级进程隔离）
 │   · handshake / 会话 / 元数据 / 查询 / 分页 / 取消 / 事务   ← 克隆 odbc 骨架
 │   · 自读文件头 → 选引擎 + 定 Dialect                        ← 纯托管 IO
 │   · 打开前探测 / 副本兜底 / ODS 前后断言
 │   · ★引擎钉死：本进程只允许加载一个引擎（S4-2，必修）
 │   · FbConnection（进程内）→ ClientLibrary = engines\<key>\<dll> 绝对路径
 │
 └── engines\fb25\  engines\fb30\  engines\fb50\   ← 三套引擎整目录，随 exe 一起打包
```

### 2.1 进程模型定案：桌面端「每连接一个 `agent.exe`」；`DeclareMultiSession = false` 是**防御性绊线**

**先给结论（2026-09-18 协议探针实测 + 上游源码双重确认）：**

- **桌面程序建立连接时，每个连接各起一个 `agent.exe`，与 agent 是否声明 `multi_session` 无关。**
- `DeclareMultiSession = false` **不是这个结果的原因** —— 它是针对「上游把入口改接到共享路径」的防御。
- 2026-09-17 那次真机 GUI 实测（两条 `odbc32` → 两个进程）**是对的，从未被推翻**。
  被推翻的是本施工单早先一版**静态读码结论**：那版把**共享路径**的代码当成了桌面路径。

#### 桌面端实际走的路径（唯一权威）

```
DBX GUI   connect_db                       src-tauri/src/commands/connection.rs:1669
   └─ db_type if is_agent_type(&db_type)                                (:2034)
        └─ connect_agent_pool                                            (:170)
             ├─ agent_manager.spawn(db_type, profile)   ← legacy，无条件
             │    └─ spawn_connection_client → spawn_first_available_client
             │         → spawn_client_for_key       agent_runtime.rs:300
             │              → AgentDriverClient::spawn(launch)   ← 新起进程，不查缓存
             └─ AgentMethod::Connect                    ← legacy connect，不注入 agentSessionId
```

`spawn_client_for_key` 的关键性质：**无条件新起进程、不查任何缓存、完全不看 capabilities**
⇒ 每个连接一个 `agent.exe`，与握手怎么声明无关。

#### 实测证据（2026-09-18，协议探针）

把 `odbc32/agent.exe` 换成一个**明确声明 `multi_session`** 的探针 agent（它把收到的每个请求写文件），
经真实 GUI 打开连接，每条连接收到的**全部**请求恰好是：

```
handshake        {appVersion:"0.6.15", supportedProtocolVersions:[2]}
connect          ← 不是 open_session；未注入 agentSessionId
connection_info
```

每个连接**恰好一个进程**，且**无 kill/respawn 痕迹**。
⇒ 一个「声明了 `multi_session`」的 agent 与「不声明」的 agent 在这条路径上被**完全同样对待**，因为它压根不问。

#### 共享 runtime 路径在哪里（它服务的是别的入口）

| 事实 | 代码位置 |
|---|---|
| runtime 缓存键 = `"{agent_key}\|{program}\|{args}\|{working_dir}"` —— **不含连接 id、不含库路径** | `crates/dbx-core/src/agent_runtime.rs` `shared_runtime_key()` |
| 该路径的调用者是 `get_or_create_pool_for_session_inner`，**不是**桌面首次连接 | `crates/dbx-core/src/connection.rs:2183`（共享客户端构造在 `:2600` 起，仅 ZooKeeper 例外） |
| native agent 的 launch spec **恒为** `{program: 固定路径, args: [], working_dir: driver_dir(driver_key)}` ⇒ 键与连接无关 | `crates/dbx-core/src/agent_manager.rs:892-905` |
| runtime 构造**硬要求**含 `multi_session`，否则 **kill 进程**并返回那句错误 | `crates/dbx-core/src/db/agent_driver.rs:101-104` |
| 被拒后 `connection.rs:2651-2681` 捕获该错误并改走 legacy `connect` | `crates/dbx-core/src/agent_runtime.rs:300-312` |

⇒ 共享 runtime 服务于 **MCP / Web / 懒建会话池** 这类入口；**桌面 GUI 首次连接不经过它**。
「音乐椅」（两条连接互相踢）在那条路上是真实的，在桌面路径上不会发生。

#### 两条入口对照

| | 桌面 GUI —— `connect_agent_pool` | 共享路径 —— `get_or_create_pool_for_session_inner` |
|---|---|---|
| agent 侧 API | `handshake` → **`connect`**（无会话 id） | `handshake` → **`open_session`**（注入 `agentSessionId`） |
| 每连接进程数 | **1**，恒定 | 每个 `db_type` **1 个**，所有连接共享 |
| 读 `capabilities` | **不读** | 读 —— 无 `multi_session` 即拒绝 |
| 两个 ODS 不同的连接同时开 | ✅ 各自独立进程 | ❌ 互相踢（见 §6 N8） |
| `replace_runtime` 逃生通道 | **不可达**（拒绝只表现为一次连接失败） | ✅ DBX kill 该 runtime 后重起 |

#### 🔴 定案（2026-09-18 用户拍板）：`DeclareMultiSession = false`

**它保证什么**：万一将来上游把桌面入口也接到共享路径上，**不声明 `multi_session` 仍会走 legacy spawn**
⇒ 依然是每连接一进程，「一进程一引擎」不受上游变化影响。

**它的代价**：**在桌面路径上为零**。该路径本来就不复用进程，不存在"多开进程"的额外开销；
也不存在"先起一个共享 runtime 再被 kill"的浪费（那只发生在共享路径上）。
唯一影响面是只走共享路径的入口（MCP / Web）：在那边本开关用内存换隔离。

> ⚠️ **本节的早先版本有两处错误，已作废**（2026-09-18 修正）：
> ① 称 09-17 实测「过期 / 被推翻」—— 实测是对的，错的是把共享路径当桌面路径的那份读码；
> ② 列了三条「代价」（每连接 2 进程 / 白起一个 runtime / 依赖旧版分支）—— 前两条只发生在共享路径上，
> 第三条表述也不准确：桌面路径本来就只用 legacy `spawn`，`false` 并未额外增加对旧分支的依赖。

### 2.2 为什么必须有「引擎钉死」：同进程第二个引擎**必炸**（S4-2）

> 桌面路径「每连接一进程」已使正常使用**不会**出现同进程第二个引擎（见 §2.1）。
> 但那层保证来自上游的一个**实现细节**，而不是协议承诺 ⇒ 本护栏必须**照常实现**：
> 它在桌面路径上是**安全网**（正常不触发），在共享路径上是**唯一防线**。
> 那句「绝不静默加载第二个引擎」的纪律不能省。

同进程加载**第二个**引擎**必炸**，2026-09-17 深夜 T4 实测，不可推翻：

| 实测项 | 结果 |
|---|---|
| `fb50 → fb30` 同进程 | ⛔ **必炸**（native 插件层 `err=127` —— 加载器按**基名**命中的是第一个进进程的 `fbclient`） |
| `fb30 → fb50` 同进程 | ⛔ **必炸** |
| 单个引擎 | **从未失败** |
| ALC `Unload()` + 3×GC 后 | `fbclient.dll` / `Engine13.DLL` **仍驻留进程**（引擎 attach 后起内部线程/共享内存） |

- **ALC 只治托管层**（provider 能力标志污染），**治不了 native 插件层**（中间隔着 P/Invoke 边界）。
- **子目录无用**：导入表里是**裸名** `fbclient.dll`，与磁盘路径无关。**改名更糟**：err 127 → **err 126 彻底打不开**。
- ⚠️ **假阳性陷阱**：`fb25→fb50→fb30` 顺序下三引擎**真能共存**（16 模块、查询全对），但共享 ALC 与独立 ALC 结果**完全相同** ⇒ 功劳在**顺序**不在隔离；且靠「FB3.0 的 `Engine12` 挂在 FB5.0 的 `fbclient` 上」这种**官方不支持的混版引擎**。
  ⇒ **别测出一次通过就当可行。**

⇒ 结论：单 exe 方案**必须**保留 **S4-2 的「引擎钉死」代码**。这是本方案**唯一的架构红线**。

**每条纪律（违反必炸）**：

| # | 纪律 | 理由 |
|---|---|---|
| ① | 永远用**绝对路径** `ClientLibrary=<所选引擎 DLL 的绝对路径>` | 裸名会命中 `%WINDIR%\System32\fbclient.dll` → 报 `xnet://Global\FIREBIRD`，**误导性极强** |
| ② | **绝不设 `FIREBIRD` 环境变量** | 进程级，会覆盖该进程内引擎的 root |
| ③ | 每套引擎目录**自带完整依赖** | fb25：`fbembed.dll`+`intl\`+`icu*30.dll`+`firebird.msg`；fb30：`fbclient.dll`+`plugins\engine12.dll`+`intl\`+`icu*52*`+`firebird.conf`；fb50：同 fb30 换 `engine13`/`icu*63`+`tzdata\`+`plugins\{srp,legacy_auth,chacha,udr_engine}.dll` |
| ④ | **FB 2.5 的 DLL 保持原名 `fbembed.dll`**，不要改名为 `fbclient.dll` | 有 `ClientLibrary` 就不必给裸名让路；改名无任何收益 |
| ⑤ | fb30 / fb50 需 **`ServerMode = SuperClassic`** | 3.0+ 默认 `Super`＝独占，会挡住现场服务（见 §6 N1） |

### 2.3 逃生通道：护栏拒绝 ≠ 死路（2026-09-18 逐环节静态核验）

护栏拒绝的**唯一价值**在于「拒绝之后 DBX 会换一个干净的进程」。这条链路已按代码逐环节核
验，**环节③是命门：字段名错一个字符就静默退化成 `KeepSession`**，所以 `AgentFault.cs`
里的常量不要手改。

| # | 环节 | 位置 | 关键点 |
|---|---|---|---|
| ① | 分支选择 | `agent_driver.rs:670-674` | **未声明 `structured_error_v1` ⇒ `strict=false` ⇒ 直接 return `Legacy` 分支**（不看契约） |
| ② | 字段解析 | `agent_driver.rs:718-732` `legacy_agent_hints` | 认 `error.data` 里的 `category` / `retryable` / `sessionDisposition` / `stage` / `operationOutcome` / `agentSessionId`（**camelCase**）；`"replace_runtime"` → `Some(ReplaceRuntime)` |
| ③ | 决策 | `agent_recovery.rs:57` `from_disposition` | `Some(ReplaceRuntime)` 命中**首臂**，**不校验 category、不校验 `RecoveryScope`** ⇒ 任何调用点都能换进程 |
| ④ | 执行 | `connection.rs:736-752` | `kill()` runtime + 摘掉所有 `uses_runtime(failed_runtime)` 的连接池，并 `finish_detach` 通知 UI |

⚠️ **不要声明 `structured_error_v1`**：一旦声明就切到严格分支，
`valid_agent_error_combination()` 会校验 category/stage/outcome 组合，
不合就变成 `ContractViolation` → `QuarantineSession`，**逃生通道直接失效**。
`firebird_agent_probe.py` 每次都会断言这一点。

`test_connection` 故意传 `requestRuntimeReplacement: false`：护栏是在**任何东西被加载之前**
拒绝的，进程还干净，不该因为用户随手点一下"测试连接"就把他在用的其他 Firebird 连接一起杀掉。
（定案取 `false` 后这条更少被触发，但**语义不变**——安全网不该有副作用。）

### 2.4 S4 真机验证结果（2026-09-18，两轮）

**第一轮：`DeclareMultiSession = true`（后被否决的形态）** —— 产物 md5 `b431ca08…`。

| 用例 | 结果 |
|---|---|
| 握手 | `protocolVersion=2` + 数组 + **含** `multi_session` + **未**声明 `structured_error_v1` ✔ |
| ODS 13.1 库 | `open_session ok` |
| 非 Firebird 文件（zip） | `not a Firebird database: header page type is 19280, expected 1` ✔ |
| **客户老库 `病例.g_b`（ODS 10.0 / dialect 1）** | `open_session ok`；`server_version = WI-V2.5.9.27139 Firebird 2.5`；`connection_charset="NONE"`；`engine="fb25"`；`identifierQuote=""` |
| `list_tables` | `['PTNTBL','TMPTBL','USERTBL']`，**`RDB$*` 泄漏 = 0** |
| `get_columns('PTNTBL')` | 12 列全对（`RECID INTEGER pk` / `PTNNM VARCHAR(25)` / …） |
| **方言语义对照（决定性）** | `dia1.fdb`（头 dialect 1）→ `7/2` = **3.5**；`dia3.fdb`（头 dialect 3）→ **3**。⇒ **两端相反，文件头 dialect 必须写进连接串**，不写会**静默算错** |
| 双引号归一化 | dialect 1 库上 `SELECT … FROM "PTNTBL"` 从 `SQL error -104` 变为可执行 ✔ |
| **引擎钉死（决定性）** | 同进程 `fb25 → fb50` 与 `fb50 → fb25` 均返回 `sessionDisposition=replace_runtime`；**会话 A 复查仍可用**（护栏不破坏已有会话）✔ |
| 同引擎共存 | 同进程两个 ODS13 库（不同文件）、以及**同一个文件开两个会话**，均 `ok` ✔ |

**第二轮：`DeclareMultiSession = false`（✅ 定案形态）** —— 产物 md5 `4424e635…`，PE `0x014C`(x86)。

| 用例 | 结果 |
|---|---|
| 握手 | `capabilities = ['connect','test_connection','metadata','query','paged_query','transaction','ddl']` —— **故意不含 `multi_session`** ✔ |
| **legacy 调用形态 · ODS 10.0 老库**（⚠️ 直连 agent 验证，**未经 DBX**） | `connect` / `connection_info` / `list_tables` / `execute_query` / `disconnect` **全部不带 `agentSessionId`** 均 `ok`；`{"engine":"fb25","charset":null,"connection_charset":"NONE","identifierQuote":""}`；`tables=3`、`RDB$*` 泄漏 **0** |
| **legacy 调用形态 · ODS 13.0**（⚠️ 直连 agent 验证） | `{"engine":"fb50","charset":"NONE","connection_charset":"(default)","identifierQuote":"\""}`；`tables=1`（`T1`）、泄漏 **0** |
| **方言对照（同一探针，两条库）** | od10 `SELECT 7/2` = **`3.5`**；ods13 = **`3`** ✔ —— 与第一轮结论一致 |
| **安全网 pin-test** | 同进程 `fb50 → fb25` 仍返回 `sessionDisposition=replace_runtime`，会话 A 复查 `ok` ✔（证明护栏在 legacy 路径上照常工作） |
| 反例 | `fake.zip` → `connect: ERROR not a Firebird database: header page type is 19280, expected 1` ✔ |
| **★ N10 闭环实验（协议探针，2026-09-18）** | 把 `odbc32/agent.exe` 换成**明确声明 `multi_session`** 的探针 agent（它将收到的每个请求写入文件）：每条连接收到的**全部**请求恒为 `handshake → connect → connection_info`，**从不出现 `open_session`**、**从不注入 `agentSessionId`**；每连接**恰好 1 个进程**，5ms 粒度采样下**无 kill/respawn** ⇒ **桌面路径压根不看 `multi_session`**，09-17 实测正确（结论与代码路径详见 §2.1） |

⚠️ **两个尚未闭环的验证**：
1. **中文字符集还原未在真机验证到** —— `病例.g_b` 的文字列（`PTNNM`/`PTNSEX`）真实值多为
   `NULL`，`FirebirdText` 只做到编译级验证。要闭环得找一个真有 GBK 文本的 NONE 字符集库。
2. **测试 adapter 的宽度断言是"假绿"** —— `apps/desktop/src-tauri/tests/adapters/dataGridSql.rs`
   与 `crates/dbx-core/src/sql_dialect/capabilities.rs` 里都有 `SELECT 7/2`，
   但**只有 dialect 3 的库**，所以「dialect 没写进连接串」这个坑在现有单测里**检测不到**。
   要扩成"双方言库各验一次"，否则永远是绿的。

---

## 3. 施工单

> 顺序即依赖顺序。**S0→S3 是「一次改全、一次推 CI」的最小集合**；S4（agent）可本地并行。

### S0 · 准备

1. 确认在 `my-agents` 分支、工作区干净：
   ```bash
   cd "D:/ProgramData/ClawWorkspace/WorkBuddy/DBX离线包/dbx-src"
   git -c core.hooksPath=/dev/null status -sb      # 期望：## my-agents...origin/my-agents
   ```
2. **废弃 Java 版草稿**（与 C# 决策矛盾，且 yaml 非法）：
   `DBX离线包/dbx-agent-firebird-embedded/`（`firebird-embedded.yaml` + `src/main/java/.../OdsDetector.java`）
   → 不采用。它缺 `schemaVersion`/`order`/`label`/`supportLevel`/`capabilities`，且有非法顶层键 `driverManagement`，**照抄必炸校验器**。
3. 建目录骨架：
   ```
   agents/drivers/firebird-embedded/
   ├── dbx-agent-firebird-embedded.csproj   # 单工程（照抄 ../odbc/dbx-agent-odbc.csproj + FirebirdClient 包）
   ├── Program.cs                           # 克隆 ../odbc/Program.cs + S4-2 引擎钉死
   ├── build.ps1
   ├── README.md
   ├── .gitignore                           # bin/ obj/ publish/ engines/
   └── engines/                             # 【不进 git】fb25|fb30|fb50 三套，打包/部署时本地拷入
   ```

### S1 · 连接类型注册（源 → 生成物）

**S1-1 新建 `plugins/connection-types/firebird-embedded.yaml`**

照抄 `access.yaml`（**最近亲先例**：agent + localFile + 省略 formKind），逐字如下 —— 已按 `sync-connection-types.mjs` 的校验规则逐条核对：

```yaml
schemaVersion: 1
order: 773                          # 已核实 773-779 空闲（现用最大值 790）
dbType: firebird-embedded           # ^[a-z0-9]+(?:-[a-z0-9]+)*$ ✓
rustVariant: FirebirdEmbedded       # ^[A-Z][A-Za-z0-9]*$        ✓
label: Firebird Embedded
dialect: Firebird                   # 已核实 plugins/dialects/firebird.yaml → dialect.name: "Firebird"
runtimeMode: agent
mcpMode: bridge
agentKey: firebird-embedded         # → <data>/agents/drivers/firebird-embedded/agent.exe
driverStoreVisible: false           # 自研，不在上游 registry（同 odbc/odbc32）
singleConnectionPool: true
metadataConnectionScoped: false
skipTcpProbe: true                  # 本地文件，无 host:port 可探
localFile: true                     # ★ 一行换到「文件路径 UI + 副标题 + 必填校验」全套泛化能力
# formKind: 省略 —— 走默认 standard。★ 不可写 firebird-embedded（闭集，会炸）
traits:
  singleDatabase: true              # 一个文件 = 一个库（同 access）
supportLevel: browse
capabilities:                       # 16 个键必须全部出现（requireAll=true）
  queryExecution: true
  metadataBrowse: true
  objectBrowser: true
  objectSource: false
  schemaSearch: false               # Firebird 无 schema 概念
  diagram: false
  tableDataEdit: false              # 首版只读浏览；后续实现 DML 再开（需同时升 supportLevel）
  tableStructureEdit: false
  tableImport: false
  dataTransfer: false
  sqlFileExecution: true
  databaseCreate: false
  fieldLineage: false
  sqlExplain: false
  userAdmin: false
  driverManagement: false
```

> **核对清单**（对应校验器行号）：`descriptorKeys` 白名单 ✓（`localFile` 在 `:38`）· `capabilities` 全 16 键 ✓ · `traits` 合法键 ✓（`singleDatabase` 在 `:67`）· `runtimeMode: agent` 且有 `agentKey` ✓ · `driverStoreVisible: false` ⇒ 不需要 `driverStoreOrder` ✓ · 至少一个 capability 为 true ✓。

**S1-2 `plugins/connection-types/profiles/catalog.yaml`** —— 在 `firebird` 条目（`:272-278`）附近加，`category` 必须取自闭集（`sql|analytics|domestic|...`）：

```yaml
  - id: firebird-embedded
    dbType: firebird-embedded
    label: Firebird Embedded
    icon: firebird                  # public/icons/database/firebird.svg 已存在 ✓
    port: 0
    user: SYSDBA
    category: sql
```

**S1-3 跑生成器**（**绝不手改产物**）：

```bash
NODE="C:/Users/ZhangHan/.workbuddy/binaries/node/versions/22.22.2-3/node.exe"
SRC="D:/ProgramData/ClawWorkspace/WorkBuddy/DBX离线包/dbx-src"
cd "$SRC" && "$NODE" scripts/sync-connection-types.mjs
```
产出 3 个**必须一起提交**的文件：
- `crates/dbx-core/assets/database-drivers.manifest.json`
- `apps/desktop/src/types/generated/databaseTypes.ts`
- `apps/desktop/src/types/generated/connectionProfiles.ts`

自查：`"$NODE" scripts/sync-connection-types.mjs --check` 必须静默通过（秒级）。

### S2 · Rust：**5 处**（用「grep 兄弟变体」法穷尽，别靠记忆）

> 方法：`grep -rn "Odbc32" --include=*.rs .` —— 凡兄弟变体出现处即为落点。**下列 5 处已逐个核对行号**。

| # | 文件:行 | 锚点（改前） | 改成 | 漏了会怎样 |
|---|---|---|---|---|
| **R1** | `crates/dbx-core/src/connection.rs:322-323` | `\| DatabaseType::Odbc`<br>`\| DatabaseType::Odbc32`<br>`};` | 追加 `\| DatabaseType::FirebirdEmbedded` | **编译红**（宏被当 match arm 用，不穷举） |
| **R2** | `crates/dbx-core/src/models/connection.rs:1212` | `DatabaseType::Odbc \| DatabaseType::Odbc32 => "odbc:<redacted>".to_string(),` | 追加 `DatabaseType::FirebirdEmbedded => "firebird-embedded:<redacted>".to_string(),` | **编译红**（该 match 无 `_`） |
| **R3** | `crates/dbx-core/src/models/connection.rs:1496-1498` | `DatabaseType::Odbc \| DatabaseType::Odbc32 => {` … `}` | 加一条返回文件路径（嵌入式无 host:port；取 `self.host`，回退 `"firebird-embedded:"`） | **编译红**（同 R2） |
| **R4** ★ | `crates/dbx-core/src/sql_dialect/capabilities.rs:116` | `Some(DatabaseType::Odbc \| DatabaseType::Odbc32) => TablePaginationStrategy::AgentMaxRows,` | 在 `:139` 的 `Some(DatabaseType::Firebird)` 旁边加 `Some(DatabaseType::FirebirdEmbedded) => TablePaginationStrategy::FirebirdRows,` | **不报错、静默算错** ⚠️ 掉进 `_ => LimitOffset` → Firebird 被注入 `LIMIT 100` → 语法错误。**症状与"没改"一模一样，最易误判** |
| **R5** | `crates/dbx-core/src/sql_dialect/ddl_profile.rs:815` | `… \| Jdbc \| Odbc \| Odbc32 => {`<br>`conservative_ansi(db_type)` | 该 or-pattern 加 `\| FirebirdEmbedded` | **编译红**（该 match 应该无 `_`；若实际有 `_` 则静默归类错） |

**可选（不阻塞编译）**：

- **R6** `crates/dbx-core/src/sql_dialect/table_select.rs:1084` —— 在 `#[test] fn odbc_table_preview_omits_dialect_specific_limit()` 的 `for database_type in [Odbc, Odbc32]` 里加一项，断言 Firebird 也不出现 `LIMIT`。
  ⚠️ 它在 `#[test]` 里 ⇒ **不改也不会红**（`build-my-agents.yml` 根本没有 `cargo test`）⇒ 但**期望值必须真机实测**，禁止手推（见 §4 坑 13）。

**不用改**（已核实，避免多余改动）：

- `quote_table_identifier`（`sql_dialect/identifiers.rs`）：`Firebird` 不在显式名单里 ⇒ 落 `_` arm ⇒ **双引号**，而双引号正是 Firebird 的合法定界符 ⇒ **语义正确，无需改**。
- `src-tauri/src/commands/connection.rs` 的 `test_connection` / 建池分派：有 `db_type if database_capabilities::is_agent_type(&db_type) =>` 的 guard + 兜底 arm ⇒ **只要 `runtimeMode: agent` + `agentKey` 正确就零改动**。
- agent 启动/查找（`agent_runtime.rs` → `database_capabilities.rs` → `agent_catalog.rs` → `database_manifest.rs` → `agent_manager.rs:769-780`）：**全 manifest 驱动**，按 `agentKey` 找 `drivers/<agentKey>/agent.exe` ⇒ **无任何白名单要改**。

### S3 · 前端：**4 处**

| # | 文件:行 | 改什么 | 漏了会怎样 |
|---|---|---|---|
| **F1** | `apps/desktop/src/lib/database/databaseNamespaceCreation.ts`（表 `:19-103`，`firebird` 在 `:59`、`odbc32` 在 `:97`） | 加 `"firebird-embedded": { deferred: "Firebird embedded has no CREATE DATABASE target" },` | **TS2740 → 右键菜单整体不弹**（Vue 渲染期 `TypeError` 被 `console.error` 吞掉，无异常事件） |
| **F2** | `apps/desktop/src/lib/database/databasePropertyEditing.ts`（同结构） | 同上加键 | TS2740 |
| **F3** | `apps/desktop/src/lib/connection/connectionAttemptTimeout.ts:11-61`（`DRIVER_STARTUP_FLOOR_TYPES`，`firebird` 在 `:23`、`odbc/odbc32` 在 `:31-32`） | 加 `"firebird-embedded"` | **UI 先报超时、agent 其实还在连**（self-contained 单文件 exe 冷启动要解压+载运行时，默认只给 10s） |
| **F4** | `apps/desktop/src/lib/connection/connectionFile.ts:31` | `if (dbType === "sqlite" \|\| dbType === "duckdb" \|\| dbType === "access")` → 追加 `\|\| dbType === "firebird-embedded"` | 「在文件管理器中显示」失效、`isLocalFileDb` 判 false |

**F5（打磨，可不做也能跑）** —— `ConnectionDialog.vue` 两处硬编码：

- `:6110` 文件对话框过滤器：`browseDbFilePath()` 里加一支 `form.value.db_type === "firebird-embedded" ? [{ name: "Firebird", extensions: ["fdb", "gdb"] }] : …`
- `:3189-3194` 占位符：`if (form.value.db_type === "firebird-embedded") return "/path/to/database.fdb";`

**明确不用改**（这些都是泛化驱动，已核实）：

- `usesLocalFilePathInput`（`ConnectionDialog.vue:3146`，由 `isLocalFileTypeDb` 驱动）✓
- 文件路径输入模板（`:6841-6869`，`v-model="form.host"`）✓
- 副标题 `connectionEndpointLabel` / `connectionRedactedEndpointLabel`（`connectionPresentation.ts:44` / `:95`，走 `isLocalFilePresentationConnection`）✓ —— **手册 §8.4 那个坑对本方案不成立**
- 「保存并连接」校验兜底（`ConnectionDialog.vue:3760` 已含 `form.host`）✓ —— **手册 §8.1 那个坑对本方案不成立**
- 图标：`DatabaseIcon.vue:12` 的 `assetIcons`（`firebird: "firebird"` 已在 `:92`）+ `firebird.svg` 已存在 ✓
- 驱动商店分类（`driver-category-definitions.ts`）：`driverStoreVisible: false` ⇒ **不进入该断言** ✓

**i18n**：**本方案不需要新增任何文案**（文件路径标签复用 `connection.filePath`，用户名/密码复用既有 key）。
> 若后续要加提示文案：必须改 **`en.ts` + `zh-CN.ts`** 两份（其余 8 个语言包靠 `fallbackLocale: "en"` 兜底），且**裸 `{` `}` 必须转义为 `{'{'}` / `{'}'}`**，改完跑 `validate-i18n.mjs`（见 §4 坑 3）。

### S4 · agent 本体（C#，**本地分钟级可编**，不占 CI）

**S4-1 `dbx-agent-firebird-embedded.csproj`**（照抄 odbc 的 csproj，换名 + 加包）：

```xml
<TargetFramework>net8.0</TargetFramework>
<AssemblyName>dbx-agent-firebird-embedded</AssemblyName>
<RootNamespace>DbxAgentFirebirdEmbedded</RootNamespace>
<!-- 其余同 agents/drivers/odbc/dbx-agent-odbc.csproj -->
<PackageReference Include="FirebirdSql.Data.FirebirdClient" Version="10.*" />
```
> 单 exe 方案下 **agent 自己就要用 `FbConnection`** ⇒ 必须引用 provider（不同于双 exe 的"调度层零 native"）。
> provider 是**纯托管**的，P/Invoke 按需触发 ⇒ 引用它**不会**在启动时就碰引擎。

**S4-2 ★★★ 引擎钉死 —— 单 exe 方案下**唯一的架构红线（必修，不是可选）

架构不再是「每个引擎一个进程」，所以**必须由代码保证同一个 `agent.exe` 进程只碰一个引擎**。

```csharp
// 进程级状态：一旦非 null，永不变更
static string? _pinnedEngine = null;   // "fb25" | "fb30" | "fb50"
static string? _pinnedDllAbs = null;

static void PinEngine(string engineKey, string dllAbsPath) {
    if (_pinnedEngine is null) { _pinnedEngine = engineKey; _pinnedDllAbs = dllAbsPath; return; }
    if (_pinnedEngine != engineKey)
        throw new InvalidOperationException(
            $"引擎已钉死为 {_pinnedEngine}，本进程内不允许再加载 {engineKey}。请断开该连接后重新连接。");
}
```

| # | 硬性要求 | 为什么 |
|---|---|---|
| 1 | **首次 attach 决定引擎，此后永不加载第二个** | native 插件层无解（T4 实测）；第二次加载会**静默**命中第一个 `fbclient` ⇒ 混版引擎 ⇒ 行为了不可预测 |
| 2 | **需要不同引擎 ⇒ 明确报错，绝不尝试加载** | 静默加载 = 必炸且**不可观测**（模块列表看不出 `Engine12` 实际跑在哪个 `fbclient` 上） |
| 3 | **加载失败 ⇒ 本进程标记「引擎不可用」，后续请求直接返回同一错误** | 堵死「失败 → 重试 → 换个引擎加载」这条最危险的路径 |

**错误文案必须给出恢复路径**（单 exe 下用户只有这一条路可走）：

```
Firebird 引擎加载失败（fb50）: <原始错误>
本进程内无法切换引擎，请【断开该连接】后重新连接。
```

这与 DBX 行为天然契合：实测 `disconnect` **立即杀 agent.exe**（`t=+0.00s`）⇒
用户「断开重连」拿到的是**全新进程**，污染自动清零，**不需要任何额外机制**。

**S4-3 握手 —— 逐字照抄 ODBC（三种写错都会被杀进程）**

```csharp
// 启动第一行必须是： {"ready":true}
static object Handshake() => new {
    protocolVersion = 2,
    agentProtocolVersion = 2,          // ★ 必须与 protocolVersion 同时给
    capabilities = new[] {             // ★ 必须是数组；★ 故意【不含】multi_session
        "connect","test_connection","metadata","query","paged_query","transaction","ddl"
    }
};
```
🔴 **本 agent 故意不声明 `multi_session`**（见 §2.1 定案）。这不是笔误，但必须**理解它挡的是哪条路**：
- **桌面路径不受它影响**：桌面走 `connect_db → connect_agent_pool → agent_manager.spawn()`，
  该路径是 legacy、**完全不读 capabilities** ⇒ 声明与否都是每连接一进程（协议探针实测，见 §2.4）。
- **它挡的是共享路径**：`agent_driver.rs:101-104` 硬校验 `protocolVersion >= 2` + `capabilities` 是数组 + **含 `multi_session`**；
  缺 `multi_session` 时它 kill 掉共享 runtime 并抛 `Agent runtime does not support multi_session protocol v2`，
  该错误被 `connection.rs:2651-2681` 捕获后改走 legacy（`spawn_client_for_key`，无条件新起进程）。
  ⇒ 于是**即便上游哪天把桌面入口接到共享路径，本 agent 仍会退到 legacy**，一进程一引擎不受影响。
- ⚠️ 若把 `multi_session` 加回去：**桌面行为不变**（同前），但共享路径会变成
  "同一 `db_type` 的所有连接共用一个进程" ⇒ 那条路上混合 ODS 互踢。改这一处必须同步改 §2.1 与 README。
- ⚠️ 仍然**不要声明 `structured_error_v1`**（见 §2.3：声明后逃生通道失效）。
- `firebird_agent_probe.py` 每次都会断言这两点（含 `connect`、**不含** `multi_session`、**不含** `structured_error_v1`）。

**S4-4 会话契约**：必须实现 `open_session` / `close_session` / `validate_session` / `cancel_session`（除 `handshake`/`test_connection`/`shutdown` 外，DBX 会自动注入 `agentSessionId`）。
- 不同会话**可并发、响应可乱序**（按 `id` 关联）⇒ **回写 stdout 必须加锁**；同一会话内**串行**。
- 上限 256 会话；最后一个会话关闭后 **30s 宽限**退出。
- **`cancel_session` 必须在会话锁之外处理**（DBX 只给 5s），否则被正在跑的查询挡住。

**S4-5 ⚠️ 共享方法的返回字段必须取并集**（自编 agent 最易漏、只在特定 UI 路径暴露）：

DBX 把不同语义的命令路由到同一个 agent 方法，而 Rust 侧**反序列化目标结构体的必填字段不同**：

| 后端命令 | 目标结构体 | **非 `Option` 必填** |
|---|---|---|
| `list_tables` | `TableInfo` | `name` + **`table_type`** |
| `list_objects` | `ObjectInfo` | `name` + **`object_type`** |

症状：侧栏**能看到** TABLE/VIEW 分组节点，但**展开分组是空的**（`list_objects` 是展开才调的）⇒ 用户视角是"双击库没出表"，**极易误判成数据层没数据**。

```csharp
string tableType = Str(r, "TABLE_TYPE") ?? "TABLE";
list.Add(new {
    name        = Str(r, "TABLE_NAME") ?? "",
    table_type  = tableType,   // ← list_tables  → TableInfo
    object_type = tableType,   // ← list_objects → ObjectInfo（侧栏分组展开）
    comment     = (string?)null
});
```

> **通用自检（写 agent 前必做）**：实现每个 `list_*` / `get_*` 前，回 Rust 侧把该命令的**反序列化目标结构体**找出来，确认哪些字段**没有 `Option` 包裹** —— 那些就是硬性必填。
> `grep -n "pub struct ObjectInfo" -A 25 crates/dbx-core/src/types.rs`
> ⚠️ **别照抄参考实现**：别人不需要的字段，你这里可能就是必填。

**S4-6 文件头自读（引擎选择 + Dialect，三判据）** ★2026-09-17 真机实测定稿：

```
① 页类型  u16 LE @ +0x00 == 1
② 页大小  u16 LE @ +0x10 ∈ {1024, 2048, 4096, 8192, 16384}
③ ODS     major = u8  @ +0x12 的【低字节】；minor = u16 LE @ +0x40
          → 查表得引擎
```

| ODS | 引擎 | | ODS | 引擎 |
|---|---|---|---|---|
| 10.0 / 10.1 / 11.0 / 11.1 / 11.2 | **fb25** | | 13.0 / 13.1 | **fb50** |
| 12.0 / 12.1 | **fb30** | | 其它 | 未覆盖 |

- **必须三判据串联**：ODS 只是文件头里的普通字节，**任意文件**在 `+0x12`/`+0x40` 都有值 ⇒ **只看 ODS 会假阳性**。
- 判别力实测：13 真库 **13/13 全中**；6 个反例（`.exe`/`.zip`/`.md`/`.ps1`/`.db`）**全部被第①条首先拦下**，且它们的"页类型"值**恰是各文件自身魔术字**（`MZ`=23117 / `PK`=19280 / `SQ`=20819 / UTF-8 BOM=48111 / `# `=8227）。
- **两个偏移历史坑**：`+0x00` 是**页类型**（header=1）**不是** pageno（曾用 `==0` 判据 ⇒ 全部真库误判为非 FB）；`+0x13` 在 ODS 11+ 是 `0x80` **标志位**，**不是** minor。
- **Dialect**：`+0x2A` u16 的 **bit4** 置位 ⇒ dialect 3，否则 1。**必须显式写进连接串**（客户库 dialect 1，不写吃 provider 默认 3 ⇒ `SELECT 7/2` 得 **3** 而非 **3.5**，**不报错但算错数**）。
- 读 `0x60` 字节即可，**不需要引擎**。
⇒ **选定引擎后立刻 `PinEngine(engineKey, dllAbs)`（S4-2），把选择钉死在本进程上。**

**S4-7 兜底要分两档**（错误提示才有指导性）：
`① / ②` 不过 ⇒ **「不是 Firebird 数据库」**；`③` ODS 不在表内 ⇒ **「是 Firebird，但版本 N.M 未覆盖」**。

**S4-8 打开策略（直连优先，副本兜底）**

```csharp
enum OpenPlan { Direct, Copy }
OpenPlan Decide(string path, out string why) {
    try {
        // 独占探测：只要有【其它】进程持有该文件，这里必然失败
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        why = "free"; return OpenPlan.Direct;
    } catch (UnauthorizedAccessException) {          // 文件带 R 属性（实测）
        why = "readonly-attribute"; return OpenPlan.Copy;
    } catch (IOException) {                          // 已被其它引擎持有（实测）
        why = "file-in-use"; return OpenPlan.Copy;
    }
}
if (plan == Copy) {
    target = Path.Combine(copiesDir, sessionId, Path.GetFileName(file_path));
    File.Copy(file_path, target, overwrite: true);
    // ⚠️ File.Copy 会继承 ReadOnly 属性 → 必须显式清掉，否则引擎照样打不开
    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
}
// 红线断言：开库前后各读一次文件头，ODS 必须不变
var h0 = ReadHeader(target);
var dllAbs = Path.Combine(enginesRoot, EngineDir(engineKey), EngineDll(engineKey));  // 纪律①：绝对路径
PinEngine(engineKey, dllAbs);                          // S4-2：钉死；第二次换引擎会抛，绝不静默加载
var reply = OpenInProcess(target, user, pwd, dialect: h0.Dialect, clientLibrary: dllAbs);
var h1 = ReadHeader(target);
if ((h1.Major, h1.Minor) != (h0.Major, h0.Minor))
    throw new InvalidOperationException($"ODS changed {h0.Major}.{h0.Minor} -> {h1.Major}.{h1.Minor}");
// finally: close → 直连则无需恢复任何东西；副本则删除
```

**⚠️ 探测必须两路并联**（`FileShare.None` 单独用有**盲区**）：`LINGER` 默认 0 且仅 SuperServer 适用 ⇒ **运行中但零连接的 Superserver 不持有文件** ⇒ 独占探测**误报"无人占用"**。
⇒ 必须再加一路 **WMI 查 `fbserver.exe` / `fb_inet_server.exe`** 并读其 `firebird.conf` 的 `ServerMode`（回答"待会儿"）。
⇒ 配套位宽坑：**x86 agent 用 `Process.MainModule` 读 x64 进程会 `Access denied`** ⇒ **必须走 WMI** `Win32_Process.ExecutablePath`；**读不到 conf ⇒ 保守按 `Super`**。

**S4-9 系统表过滤**（ODBC 踩过：agent 拿到类型却不按类型过滤 ⇒ 冒出 `MSys*`）：
Firebird 侧对应的是 `RDB$*` 系统表 + 少量视图 ⇒ 按 `RDB$SYSTEM_FLAG` / 表名前缀滤掉，**并在源码里注释说明**。

**S4-10 `build.ps1`（只编 x86，单工程）**

```powershell
$ErrorActionPreference = "Stop"
dotnet publish dbx-agent-firebird-embedded.csproj -c Release -r win-x86 `
    --self-contained true -p:PublishSingleFile=true -o publish
Copy-Item "publish/dbx-agent-firebird-embedded.exe" "publish/agent.exe" -Force
# 三套引擎【整目录】拷进 publish —— 原生 DLL 不会被单文件打包
# ⚠️ CI 上 engines/ 不存在（不进 git）⇒ 必须加守卫，否则 CI 直接红
if (Test-Path "engines") {
    foreach ($k in 'fb25_x86','fb30_x86','fb50_x86') {
        Copy-Item "engines/$k/*" "publish/engines/$k/" -Recurse -Force
    }
} else {
    Write-Host "engines/ not found - CI mode, skipping engine copy"
}
```

- 🚨 **绝不 AnyCPU**：native DLL 位宽绑定，AnyCPU 被 x64 DBX 加载仍会 P/Invoke x64。
- 产物 `publish/` 即 `data/agents/drivers/firebird-embedded/` 的内容（`agent.exe` + `engines/`），解包约 **90 MB**。
- 引擎根定位：`enginesRoot = Path.Combine(AppContext.BaseDirectory, "engines")`
  （单文件 exe 下 `AppContext.BaseDirectory` = exe 所在目录 ✅）。

**S4-11 引擎来源（可复现）**：从 firebirdsql.org 取三个 **Win32(x86)** 包，解出后**整目录**放 `engines/`：

| key | 版本 | 本地已有 | 精简后 |
|---|---|---|---|
| `fb25_x86` | 2.5.9 | `2026-09-14-17-12-45/engines/fb25_x86` | 12 MB |
| `fb30_x86` | 3.0.14 | 同上 | 26 MB |
| `fb50_x86` | 5.0.4 | 同上 | 51 MB |

> **fb40 不需要**（ODS 13.x 由 fb50 覆盖；v11 §1.1 已定）。
> `engines/` **加进 `.gitignore`**（89 MB 不进仓库）；CI **不产引擎**，引擎在打包/部署时本地拷入。

### S5 · CI（扩展既有 workflow，不新建）

改 `.github/workflows/build-my-agents.yml`（**已于 2026-09-18 由 `build-odbc.yml` 改名**：这个 workflow 现在产 dbx.exe + odbc x64/x86 + firebird-embedded 三个 agent，`build-odbc` 这个名字已经过时。⚠️ 改名必须与 `odbc-build-output/manage_fork_workflows.py` 的 `DEFAULT_KEEP` **同一次完成**，否则新 workflow 会被 `--apply` 当白名单外禁用 ⇒ **push 再也跑不起 CI 且毫无提示**）：

1. 加两步（紧跟现有两个 ODBC agent 构建步骤之后）：

```yaml
      - name: Build Firebird Embedded agent (32-bit)
        working-directory: agents/drivers/firebird-embedded
        shell: pwsh
        run: ./build.ps1
```
   ✅ 引擎拷贝的 `Test-Path` 守卫**已写进 S4-10 的 `build.ps1`** ⇒ CI 只产 `agent.exe`，本地打包时才有引擎。

2. `Upload artifacts` 的 `path:` 追加：
```yaml
            agents/drivers/firebird-embedded/publish/agent.exe
```

3. **保留** `Typecheck frontend` 步骤（`build-my-agents.yml:53-59` 那段注释就是为「穷举表漏键」踩坑后加的）—— F1/F2 靠它兜底。

**CI 只做一次**：S1+S2+S3 一起推。**agent 改动走本地 `build.ps1`，不占 CI**。
⚠️ `build-my-agents.yml` **无 `concurrency` 组** ⇒ 连推两次会有两个 run 并行（互不阻塞，无需 cancel）。⚠️ **无 `cargo test`** ⇒ CI 绿 ≠ 单测过。

### S6 · 离线包 + 部署

1. `Build-DBX-Win10-OfflinePackage.ps1`：`$DriverWhitelist`（`:25`）**不要把 key 加进去**（那份逻辑是给上游 registry 用的，自研 agent 不在 registry ⇒ 会命中 `:208`「不在离线驱动 registry 中，已跳过」）。
   ⇒ 改成**显式本地拷贝**（v11 §3.6）：
   ```powershell
   $emb = Join-Path $baseDir "data\agents\drivers\firebird-embedded"
   New-Item -ItemType Directory -Force -Path $emb | Out-Null
   Copy-Item "<本地构建输出>\firebird-embedded\publish\*" "$emb\" -Recurse -Force
   ```
   `Build-DBX-Win7-OfflinePackage.ps1` 同加（Win7 版**无颜色**，两包目录**勿互拷**；且需验证 Win7 上 .NET 8 自包含 exe 能否跑）。
2. 本机部署（复用既有的 `odbc-build-output/deploy.py`：幂等、`.bak.exe` 备份语义、覆盖占用时退避重试）：
   目标 `D:\ProgramData\DBX\agents\drivers\firebird-embedded\{agent.exe, engines\...}`。
   ⚠️ 引擎目录 89 MB，**首次部署慢**；`deploy.py` 目前的文件清单需扩展为「目录级同步」。
   ⚠️ `engines\` 里的 DLL 是**只读数据不是运行中的 exe** ⇒ 直接覆盖即可；只有 `agent.exe` 被占用时才走退避重试。

---

## 4. ★ ODBC 已踩过的坑 → 本方案如何规避

> 用户明确要求。左列每条都在上一轮 ODBC 开发中**真实踩到过**。

### 4.1 因「本方案做了不同选择」而**整条消失**的坑（这是 §1.1 的价值）

| # | ODBC 踩过的坑 | 症状 | 本方案为何不适用 |
|---|---|---|---|
| 1 | **给 agent 增传参数要动 4 个地方**（`ConnectionConfig` / `ConnectionConfigData` / `impl From` / `agent_connect_params` 白名单） | 参数被**静默丢弃**，且 **CI 照样绿** | ★**本方案零参数改动**：文件路径复用既有 `host` 字段，白名单里本来就有 |
| 2 | 其中第 3 处写成 **`Self { … }`**，用 `ConnectionConfig {` 正则**永远扫不到** | 字段恒为默认值 | 同上，不存在该文件/该 `impl From` 的改动 |
| 3 | 「**CI 绿 ≠ 参数通了**」：34 处结构体字面量全在 `#[cfg(test)]`，`cargo build` 不编译 | 事故级假绿 | 同上 |
| 4 | **新增表单字段漏改编辑态 hydrate 字面量** | 「**存得进、重编辑看不到**」，一保存把原值**覆盖成空** | ★**不新增表单字段** ⇒ 无需碰那个上百行的 hydrate 字面量 |
| 5 | **漏加校验豁免** ⇒「保存并连接」永久禁用 | 测试按钮能点、保存按钮灰着 | 校验兜底 `return !!(form.value.host \|\| …)` **已含 `host`** ⇒ 自带豁免 |
| 6 | **副标题为空** ⇒ 侧栏只显示光秃秃类型名 | 多连接无法区分 | `isLocalFilePresentationConnection` 泛化分支 ⇒ 直接显示文件路径 |
| 7 | **位宽 DSN 判据 / DSN 名规范化 / 连接串转义与凭据优先级（§6 全章）** | 各种 `IM002`/`IM014` | ODBC 专有；Firebird 用 `FbConnectionStringBuilder` 结构化拼装，**不写连接串解析器** |
| 8 | **`odbcad32.exe` 命名陷阱**、WOW64 重定向 | 位宽判断错 | 无 DSN、无注册表参与 |
| 9 | **驱动商店分类断言**（`driver-category-definitions.ts`） | 漏了抛异常 | `driverStoreVisible: false` ⇒ 不进入该断言 |

### 4.2 仍然适用、**必须主动处理**的坑

| # | 坑 | 症状 | 本方案的规避动作 | 何时验 |
|---|---|---|---|---|
| 10 | ★ **`pagination_strategy` 的 or-pattern 掉进 `LimitOffset`** | **不报错**，被注入 `LIMIT` → Firebird 语法错误。**症状与"完全没改"一样，最易误判** | S2-**R4** 显式加 `FirebirdEmbedded => FirebirdRows` | 本地静态核对 + 真机预览 SQL |
| 11 | **or-pattern / `matches!` 漏 variant 不报错** | 静默 `false` | 用 **「grep 兄弟变体 `Odbc32`」** 法穷尽（§7），而非靠记忆；能遍历就 `DatabaseType::ALL` | push 前 |
| 12 | **2 张 `satisfies Record<DatabaseType, …>` 穷举表漏键** | **TS2740 → 右键菜单整体不弹**（Vue 用 `console.error` 吞渲染期异常，`Runtime.exceptionThrown` **抓不到**） | S3-**F1/F2**；`on.name=773`… 并由 `pnpm typecheck` 兜底 | 本地 `vue-tsc`（≈60s）+ CI |
| 13 | **单测期望值不能手推**（`quote_table_identifier` 的 `_` arm 会加双引号） | 错误 `assert_eq!` 静默存在（CI 不跑 `cargo test`） | R6 的期望值**必须真机探针取值**；Firebird 同落 `_` arm ⇒ 预期双引号，但**仍以实测为准** | 写 R6 时 |
| 14 | **i18n 保留字符**（裸 `{` `}` `@` `\|` `$`） | **整块表单 body 消失**（外壳/按钮都在，中间不在 DOM） | 本方案**不新增文案**；若加则转义 `{'{'}` 并跑 `validate-i18n.mjs` | 若加文案 |
| 15 | **self-contained 单文件 exe 冷启动慢** | UI 先报超时、agent 其实还在连 | S3-**F3** 加进 `DRIVER_STARTUP_FLOOR_TYPES`（30s 下限） | 真机首连 |
| 16 | **共享 agent 方法返回字段必须取并集** | 侧栏能看到分组节点、**展开是空的**（`list_objects` 报 `missing field 'object_type'`） | S4-5：实现前回 Rust 侧核对**非 `Option`** 字段 | 写 agent 时 + 真机展开分组 |
| 17 | **握手不合规 ⇒ agent 被 kill**（⚠️ **仅共享路径**） | 之后**所有**调用 `-32000` | S4-3 逐字照抄（`protocolVersion:2` + `agentProtocolVersion:2` + **数组**）。⚠️ **桌面路径不做这项校验**（`agent_manager.spawn` 走 legacy，`try_optional_handshake` 对握手完全宽容）⇒ 本行症状**不会在桌面上出现** | 首个 agent 版本 |
| 18 | **`cancel_session` 放在会话锁内** | 被正在跑的查询挡住（DBX 只给 5s） | S4-4：**必须在会话锁之外** | 写 agent 时 |
| 19 | **stdout 回写不加锁** | 并发会话响应交错、JSON 撕裂 | S4-4：回写加锁 | 写 agent 时 |
| 20 | **CI 产物路径写错**（`target/release/dbx.exe` 在 **workspace 根**，不是 `src-tauri/target/`） | run **绿色成功**但 zip 里只有 agent | 取产物后**必须下载核对内容** | 每次取产物 |
| 21 | **探针没照抄前端真实入参** | 探到了"没问题的那条路"，差点得出错误结论 | 同一命令的**每条 UI 路径**各打一遍并对照（侧栏 `schema=""` vs 主面板 `schema=<库名>`） | 每次探针 |
| 22 | **别点 DBX 的「更新」** | 覆盖 `dbx.exe`，自研 `list_odbc_dsns` 等消失 | 部署后禁用/忽略更新提示；判身份**扫符号**而非时间戳/版本号 | 部署后 |
| 23 | **本机不能跑 `cargo`**（铁则） | —— | Rust 改动只做**静态阅读 + 源码编辑**，编译交 CI | 全程 |
| 24 | **CDP 验证硬约束** | ①命令结束整棵进程树被 Job Object 连坐杀 ②CI 产物里 `el.__vueParentComponent` **不存在** ⇒ 读不到 `setupState` ③启动 DBX 必须走 `schtasks` | 走 `schtasks`；验证一律**纯 DOM 路径**；抓 Vue 渲染异常必须**挂 console 钩子** | 验证期 |
| 25 | **中文 Windows `tasklist` 输出是 GBK** | `UnicodeDecodeError`，且**线程内异常导致脚本后半段整块丢失**（假失败） | `r.stdout.decode('gbk','replace')`；取 PID 用 `/FO CSV`（MSYS `ps` 的 PID ≠ Windows PID） | 写采样脚本时 |
| 26 | **CDP 脏运行态污染交互** | 双击无反应（`hasFocus` 正常、监听器在） | CDP 实验前**先重启 DBX 取干净基线** | 验证期 |
| 27 | ★ **（本方案新引入）单 exe 下没有进程兜底**：同进程加载第二个引擎必炸（`err=127`），且**加载失败后无法清理**（ALC/`Free` 都无效） | 一个进程里 `Engine12` 静默挂在另一版本的 `fbclient` 上 ⇒ **查询结果不可信** | **S4-2 引擎钉死**：首次决定引擎后永不变更；需要不同引擎 ⇒ **报错**；加载失败 ⇒ 标记不可用 + 提示断开重连 | 写 agent 时（**必修**） |

---

## 5. 验证计划

### 5.1 分层验证（别指望一层通就全通）

| 层 | 手段 | 判据 |
|---|---|---|
| **协议** | 裸 spawn `agent.exe` 直发 JSON-RPC（`odbc-build-output/agent_rpc_probe.py`，**改 `--exe` 参数即可复用**） | `handshake` 返回 v2 + 数组；`open_session` → `list_tables` 有行 |
| **引擎调度** | 起**独立**的 `agent.exe` 分别连三种 ODS 的库（`agent_rpc_probe.py --exe`） | 各自都能选对引擎；把 ODS 10 的库交给只应认 ODS 13 的进程会失败 |
| **参数链路** | 从 DBX 真连，看 agent 收到的 `host` | **决定性判据**：让路径指向一个不存在的 `.fdb` ⇒ 报 Firebird 的「文件打不开」**而非**「未提供路径」⇒ 证明 `host` 送达 |
| **UI 渲染** | CDP 连运行中的 WebView2 | 纯 DOM：表单出现**文件路径输入框 + 浏览按钮**；右键菜单**有项** |
| **端到端** | 真连客户库 `病例.g_b`（ODS 10.0 / dialect 1） | 能列出表；`SELECT 7/2` 得 **3.5**（证明 Dialect=1 写进去了） |

### 5.2 本地预检（**改前端后 push 前必跑**，别盲等 60 分钟）

```bash
NODE="C:/Users/ZhangHan/.workbuddy/binaries/node/versions/22.22.2-3/node.exe"
SRC="D:/ProgramData/ClawWorkspace/WorkBuddy/DBX离线包/dbx-src"
cd "$SRC"
"$NODE" scripts/sync-connection-types.mjs --check                                    # 秒级：生成物同步
"$NODE" "$SRC/node_modules/vue-tsc/bin/vue-tsc.js" --noEmit --project "$SRC/apps/desktop/tsconfig.json"   # ≈60s：TS2740
```
> 已实证：`vue-tsc` 确实会报 `TS2740`（**别把"无输出"当"检查没用"**）。CI 的 `pnpm tauri build` 走 `vite build`，**不做类型检查**。

### 5.3 agent 本地迭代（分钟级，**不占 CI**）

1. `build.ps1` → `publish/{agent.exe, engines/}`
2. `agent_rpc_probe.py --exe publish/agent.exe …` 打协议层
3. 通过后再覆盖到 `D:\ProgramData\DBX\agents\drivers\firebird-embedded\`

### 5.4 ★ 决定性判据（至少构造一条）

思路：**不要看"成功/失败"，要构造一个只可能由"该条件成立"才会出现的结果**。

| 待证 | 决定性判据 |
|---|---|
| 文件路径真的到了 agent | 路径指向不存在的文件 ⇒ 报 Firebird **文件级**错误，而非"缺参数" |
| Dialect 真的写进去了 | 客户库上 `SELECT 7/2` = **3.5**（不写 Dialect 会是 **3**） |
| 引擎选对了 | 对 ODS 10.0 的库，只有 fb25 能开；在**独立进程**里用 fb30/fb50 试会报 `unsupported on-disk structure` |
| **一进程一引擎真的成立** | 同时开 ODS10（`病例.g_b`）与 ODS13 两个连接 ⇒ 是两个 `agent.exe`，且**各自模块列表里只有自己那套引擎的 DLL**（`Get-Process -Module` / Process Explorer 核对 `fbembed.dll` vs `engine13.dll`） |
| ★★ **引擎钉死真的生效** | 用 `agent_rpc_probe.py` 在**同一个** agent 进程里先后请求两个不同 ODS 的库 ⇒ **第二次必须报错，绝不能成功**（成功 = 静默混版 = 最危险，且不可观测） |
| 没污染原文件 | 开库前后 `sha256` 对比；**但注意引擎只跑 `SELECT` 也会改 mtime/hash**（引擎必然写原文件）⇒ 判据应为"**ODS 不变**"而非"文件不变" |

---

## 6. 风险与未决项

| # | 项 | 状态 | 处理 |
|---|---|---|---|
| **N1** | fb30/fb50 的 `ServerMode = SuperClassic` 落地方式（`firebird.conf` vs provider 的 `ServerType`）与生效性 | **未实测** | S4 首个可运行版本上实测；同步测「与客户 **Classic/SuperClassic** 服务共存」。**机制内核已明：冲突只看各方 `ServerMode`，与谁先打开无关；任一方为 `Super` ⇒ 只有一方能开** |
| **N2** | ⚠️ **单 exe 下引擎加载失败后 agent 自己无法恢复**（不能重启自己，也不能卸载已进进程的 native 模块） | **已接受**（2026-09-18 架构决策） | 报错 + 提示「断开该连接后重连」；实测 `disconnect` **立即杀 agent.exe** ⇒ 重连即得干净进程。**若真机发现频繁出现，再引入 `fbhost.exe` 子进程池**（届时只需替换引擎访问实现，其余代码不动） |
| **N3** | 离线包体积（引擎 89 MB，两包各 +90 MB） | 未决 | 先量化；可选精简（去 `tzdata`/多余 `plugins`/`icudt*`） |
| **N4** | Win7 上 .NET 8 自包含 exe 可跑性 | 未验 | 与 Win7 离线包一起验 |
| **N5** | x86 agent 的 2 GB 地址空间上限（大库/大结果集） | 未评估 | 首版观察；必要时再议 x64 引擎分支 |
| **N6** | `list_databases` 在 `singleDatabase: true` 下的预期行为 | 未确认 | 照抄 access/odbc 的行为，真机确认树形态（是否出现数据库层级） |
| **N7** | 上游 MCP（0.4.89）**看不见 `odbc32`**（静默丢弃未知 `db_type`）⇒ 新增 `firebird-embedded` **同样看不见** | 已确认 | **自研类型的功能验证只能走桌面程序**；若需 MCP 支持要同步改 MCP server 的已知类型表 |
| **N8** | 运行时**不是**协议级连接隔离：**共享路径**上同一 `db_type` 的连接共用一个 `agent.exe`（见 §2.1）。**桌面路径**上则每连接一个进程 ⇒ 混合 ODS 可共存 | ✅ **已闭环（2026-09-18 协议探针实测）**：桌面路径实测序列恒为 `handshake → connect → connection_info`，每连接 **1 个进程、无 kill/respawn** ⇒ **桌面不会出现"音乐椅"**。共享路径上的 `replace_runtime` 行为（`open_session B` 返回 `sessionDisposition=replace_runtime`、且 A 的池被一并摘掉）来自**直连 agent + 静态读码**，仅在 MCP / Web 入口生效 | 无需动作（定案形态下桌面已满足）。若将来桌面入口被上游改接到共享路径，由 `Program.cs::DeclareMultiSession = false` 兜住 |
| **N9** | 是否声明 `multi_session` | ✅ **已定案（2026-09-18 用户拍板）= 不声明** | 桌面路径**不读**该能力 ⇒ 对桌面进程模型**零影响**（实测：声明了的探针 agent 与不声明的待遇完全相同）。它的价值是**防御**：上游若把桌面入口改接到共享路径，不声明仍走 legacy ⇒ 仍每连接一进程。代价在桌面上为零 —— 详见 §2.1 |
| **N10** | 「声明 `multi_session` ⇒ 每 `db_type` 一进程」与 09-17 GUI 实测（2 × `odbc32` → 2 进程）的矛盾 | ✅ **已闭环（2026-09-18）＝ 早先的静态读码读错了入口** | 根因：`connection.rs:2600` 那套共享路径属于 `get_or_create_pool_for_session_inner`（`:2183`），**桌面 GUI 首次连接不走它**。桌面走 `connect_db` → `connect_agent_pool`（`src-tauri/src/commands/connection.rs:170`）→ `agent_manager.spawn()`（legacy，**不看 capabilities**）。协议探针实测：一个**声明了 `multi_session`** 的 agent 仍只收到 `connect`，每连接 1 个进程。⇒ **09-17 实测是正确的，早先的"证伪"作废** |

---

## 7. 附录

### 7.1 落点全景（用「grep 兄弟变体」法得来，可直接复跑复核）

```bash
cd <repo>
grep -rn "Odbc32" --include=*.rs --include=*.ts --include=*.vue --include=*.yaml \
  . | grep -v node_modules | grep -v /dist/ | grep -v /target/ | grep -v generated/
```
上一轮输出 = 下列**全部**落点（多出来的两处 `table_select.rs` 在 `#[test]` 内）：

| 文件 | 性质 |
|---|---|
| `crates/dbx-core/src/connection.rs:323` | Rust 穷举（宏，漏⇒编译红） |
| `crates/dbx-core/src/models/connection.rs:1212` | Rust 穷举（漏⇒编译红） |
| `crates/dbx-core/src/models/connection.rs:1496` | Rust 穷举（漏⇒编译红） |
| `crates/dbx-core/src/sql_dialect/capabilities.rs:116` | Rust 语义（漏⇒**静默 LIMIT**） ★ |
| `crates/dbx-core/src/sql_dialect/ddl_profile.rs:815` | Rust 穷举 |
| `crates/dbx-core/src/sql_dialect/table_select.rs:1084` | 仅 `#[test]`（可选） |
| `apps/desktop/src/lib/database/databaseNamespaceCreation.ts:97` | 前端穷举（漏⇒菜单不弹） |
| `apps/desktop/src/lib/database/databasePropertyEditing.ts:97` | 前端穷举 |
| `apps/desktop/src/lib/connection/connectionAttemptTimeout.ts:32` | 前端语义（漏⇒误报超时） |
| `apps/desktop/src/lib/connection/connectionPresentation.ts:27,92` | 前端表现层（**本方案走泛化分支，不用改**） |
| `apps/desktop/src/components/connection/ConnectionDialog.vue:3159` | ODBC 位宽特判（**不适用**） |
| `plugins/connection-types/odbc32.yaml` + `profiles/catalog.yaml` + 3 个生成物 | 配置 |

### 7.2 已核实「**不需要改**」的清单（省得白改）

- `crates/dbx-core/src/sql_dialect/identifiers.rs`（双引号 = Firebird 合法定界符，落 `_` arm 正确）
- `src-tauri/src/commands/connection.rs` 的 `test_connection` / 建池分派（有 agent-type guard + 兜底）
- `src-tauri/src/lib.rs`（无新 Tauri 命令；`list_odbc_dsns` 是 ODBC 专有）
- agent 启动/解析/安装全链（`agent_runtime.rs` → `agent_manager.rs:769-780`，**全 manifest 驱动**，按 `agentKey` 找 `agent.exe`）
- `ConnectionConfig` / `ConnectionConfigData` / `impl From` / `agent_connect_params_with_role`（**零参数改动**）
- 图标（`firebird.svg` 已存在）、驱动商店分类（`driverStoreVisible: false`）
- `agents/settings.gradle` / `build.gradle` / `versions.json`（那些是 **Java agent** 的登记处，本方案是 C# agent，**不涉及**）

### 7.3 相关文档

| 文档 | 用途 |
|---|---|
| `DBX离线包/DBX-FirebirdEmbedded驱动设计方案.md`（v11） | 技术设计与实测依据（本文件是其施工化版本） |
| `DBX离线包/DBX-自编驱动避坑手册.md` | ★写驱动前必读（本文件 §4 与之互补） |
| `DBX离线包/DBX-agent进程模型与Firebird识别-实测报告-20260917.md` | agent 连接级进程模型 + 文件头判据的原始证据 |
| `DBX离线包/DBX-Firebird-T4-多引擎同进程实测-20260917.md` | 「一引擎一进程」为何不可推翻的实测矩阵 |
| `.workbuddy/memory/DBX-PITFALLS.md` | 综合踩坑手册（§3 调试 / §5 架构） |

---

## 8. 施工顺序速查（贴墙用）

```
S0 准备（分支 / 废弃草稿 / 目录；放 engines/fb25|fb30|fb50）
 └─ S1 yaml ×2（firebird-embedded.yaml + catalog.yaml）+ 跑生成器
     └─ S2 Rust 5 处（R1 R2 R3 编译红 / R4 ★静默算错 / R5）
         └─ S3 前端 4 处（F1 F2 菜单 / F3 超时 / F4 文件路径）
             └─ ★ 本地预检：sync --check + vue-tsc 0 error
                 └─ ★ push 一次 → 等 CI（≈60 min）→ 产物下载核对
                     └─ S4 agent 本体（本地 build.ps1，分钟级，与 CI 并行）
                         ├─ S4-2 ★引擎钉死 —— 先写这条，它是唯一红线
                         ├─ 协议层：agent_rpc_probe.py
                         ├─ 引擎层：三种 ODS 各起独立 agent.exe 验
                         └─ 真机：病例.g_b（ODS 10.0 / dialect 1）
                             └─ S6 离线包 + 部署（.bak.exe 回滚点 + 引擎目录级同步）
```
