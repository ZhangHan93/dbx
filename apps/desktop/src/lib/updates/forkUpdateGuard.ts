/**
 * FORK 自研落点（非上游文件）—— 「禁用应用内在线更新」的唯一真源。
 *
 * 背景：本 fork（ZhangHan93/dbx）由 CI 重新构建，应用内在线更新只会拉到
 * 上游 t8y2/dbx 的官方安装包，触发即覆盖自研能力（ODBC / odbc32 /
 * Firebird Embedded agent.exe）。且本 fork 常部署在内网，联网检查会卡顿
 * 或触发安全软件告警。
 *
 * 规格：零联网 / 零徽章 / 误触即提示。
 *
 * 约定：上游文件里只出现 FORK_ 前缀的引用，便于上游合并冲突时秒定位。
 * 设计依据见工作区 DBX-禁用更新-设计方案-20260919.md。
 */
export const FORK_UPDATES_DISABLED = true;
export const FORK_NO_ONLINE_UPDATE_NOTICE = "自开发分支版本，不支持在线更新";
