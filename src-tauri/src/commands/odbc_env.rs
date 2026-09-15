//! ODBC 环境探测：枚举本机已配置的 DSN。
//!
//! 为什么读注册表而不是问 agent：
//!   - ODBC 在 DBX 里是**原生类型**（不走 `PluginHost`），现成的 `invoke_plugin` 通用通道用不上；
//!   - agent 方法在 Rust 侧是**硬编码枚举**（`dbx-core/src/db/agent_driver.rs` 的 `AgentMethod`），
//!     加方法要动核心协议文件，收益不成比例；
//!   - agent 是懒启动的，表单阶段未必有进程；只为开一个下拉就去起进程（数十 MB + 延迟）不值；
//!   - DSN 本来就是注册表数据，跟「连上某个数据库」无关。
//!
//! **位宽语义（实测，见 DBX-PITFALLS.md §5.4.5）**：
//!   - 系统 DSN 在 `HKLM` 下**严格按位宽重定向**：64 位进程看不到 32 位建的 DSN，反之亦然；
//!   - 用户 DSN 在 `HKCU` 下**不重定向**，两个位宽读到的是同一份。
//! 因此本命令一次性返回**两个位宽视图**的全部 DSN，由前端按连接类型过滤：
//! 这样即使将来位宽映射改了，也只是「少显示」而不会「显示错」——顺手把
//! `IM014 驱动程序和应用程序之间的体系结构不匹配` 这个坑从根上堵死。

use serde::Serialize;

/// 一条已配置的 ODBC DSN。
#[derive(Debug, Clone, Serialize)]
pub struct OdbcDsnEntry {
    /// DSN 名称（裸名，不含 `DSN=` 前缀）
    pub name: String,
    /// 驱动注册名，如 `SQL Server` / `ODBC Driver 17 for SQL Server`
    pub driver: String,
    /// 该 DSN 对哪个位宽可见：`x64` | `x86`
    pub view: String,
    /// 作用域：`user`（HKCU）| `system`（HKLM）
    pub scope: String,
}

/// 列出本机 ODBC DSN（含 x64 / x86 两个位宽视图）。
///
/// 前端按连接类型过滤：`odbc` → `view == "x64"`，`odbc32` → `view == "x86"`。
#[tauri::command]
pub async fn list_odbc_dsns() -> Result<Vec<OdbcDsnEntry>, String> {
    tauri::async_runtime::spawn_blocking(enumerate)
        .await
        .map_err(|err| err.to_string())?
}

#[cfg(target_os = "windows")]
fn enumerate() -> Result<Vec<OdbcDsnEntry>, String> {
    use windows_sys::Win32::Foundation::{ERROR_MORE_DATA, ERROR_NO_MORE_ITEMS, ERROR_SUCCESS};
    use windows_sys::Win32::System::Registry::{
        RegCloseKey, RegEnumValueW, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER,
        HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY,
    };

    /// DSN 注册表位置（HKLM / HKCU 共用同一子键路径）
    const ODBC_DATA_SOURCES: &str = r"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources";

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// 读某个根键 / 位宽视图下的全部「DSN 名 -> 驱动名」。
    /// `flags` 传 `KEY_WOW64_64KEY` / `KEY_WOW64_32KEY` 选择位宽视图，0 表示不重定向（HKCU）。
    fn read_view(root: HKEY, flags: u32) -> Vec<(String, String)> {
        let mut out = Vec::new();
        let sub = wide(ODBC_DATA_SOURCES);
        let mut key: HKEY = std::ptr::null_mut();

        // SAFETY: sub 是本函数内在栈上构造的以 NUL 结尾的宽字符串；
        // 成功时系统把新句柄写入 key，失败时 key 保持空指针（下方不会关闭它）。
        let rc = unsafe { RegOpenKeyExW(root, sub.as_ptr(), 0, KEY_READ | flags, &mut key) };
        if rc != ERROR_SUCCESS {
            // 该位宽下从未建过系统 DSN 时键不存在，属正常情况 → 空表，不报错
            return out;
        }

        let mut index = 0u32;
        loop {
            let mut name_buf = vec![0u16; 512];
            let mut name_len = name_buf.len() as u32;
            let mut data_buf = [0u8; 1024];
            let mut data_len = data_buf.len() as u32;
            let mut value_type = 0u32;

            // SAFETY: name_len / data_len 均初始化为对应缓冲区的真实容量；
            // 缓冲区在循环内独占存在，指针在调用期间有效。
            let rc = unsafe {
                RegEnumValueW(
                    key,
                    index,
                    name_buf.as_mut_ptr(),
                    &mut name_len,
                    std::ptr::null(),
                    &mut value_type,
                    data_buf.as_mut_ptr(),
                    &mut data_len,
                )
            };

            if rc == ERROR_NO_MORE_ITEMS {
                break;
            }
            if rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA {
                break;
            }

            // ERROR_MORE_DATA 时 data_len 是「所需大小」，可能大于缓冲区 → 必须夹紧，否则切片越界
            let got = (data_len as usize).min(data_buf.len());
            let name = String::from_utf16_lossy(&name_buf[..(name_len as usize).min(name_buf.len())]);

            // 值是 UTF-16（REG_SZ），按字节缓冲反解并在第一个 NUL 处截断
            let units: Vec<u16> = data_buf[..got]
                .chunks_exact(2)
                .map(|c| u16::from_ne_bytes([c[0], c[1]]))
                .collect();
            let units = match units.iter().position(|&u| u == 0) {
                Some(pos) => &units[..pos],
                None => &units[..],
            };
            let driver = String::from_utf16_lossy(units);

            if !name.is_empty() {
                out.push((name, driver));
            }
            index += 1;
        }

        // SAFETY: key 由上面成功的 RegOpenKeyExW 返回，且仅在此处释放一次。
        unsafe { RegCloseKey(key) };
        out
    }

    let mut entries = Vec::new();

    // 用户 DSN：不参与 WOW64 重定向，两个位宽读到同一份 → 一份数据登记到两个视图
    for (name, driver) in read_view(HKEY_CURRENT_USER, 0) {
        for view in ["x64", "x86"] {
            entries.push(OdbcDsnEntry {
                name: name.clone(),
                driver: driver.clone(),
                view: view.to_string(),
                scope: "user".to_string(),
            });
        }
    }

    // 系统 DSN：严格按位宽重定向 → 分开读两个视图
    for (flags, view) in [(KEY_WOW64_64KEY, "x64"), (KEY_WOW64_32KEY, "x86")] {
        for (name, driver) in read_view(HKEY_LOCAL_MACHINE, flags) {
            entries.push(OdbcDsnEntry {
                name,
                driver,
                view: view.to_string(),
                scope: "system".to_string(),
            });
        }
    }

    // 稳定顺序：用户 DSN 优先（多数人给自己建），同名再按视图
    entries.sort_by(|a, b| {
        a.scope
            .cmp(&b.scope)
            .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
            .then_with(|| a.view.cmp(&b.view))
    });
    Ok(entries)
}

#[cfg(not(target_os = "windows"))]
fn enumerate() -> Result<Vec<OdbcDsnEntry>, String> {
    // ODBC DSN 是 Windows 注册表概念，其它平台返回空表。
    Ok(Vec::new())
}
