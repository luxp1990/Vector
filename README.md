# Vector — 快捷键与手势贴图工具

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0--windows-purple.svg)](https://dotnet.microsoft.com/)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](https://www.microsoft.com/windows)
[![Release: 2.0.2](https://img.shields.io/badge/Release-v2.0.2-green.svg)](https://github.com/luxp1990/Vector/releases)

> 一个常驻系统托盘的 Windows 桌面工具：全局快捷键管理 + 手势贴图 + 快捷键贴图。
> 技术栈：.NET 8 (WPF) + WinForms NotifyIcon，C#。


---

## 一、软件功能

### 1. 快捷键管理
- **本地快捷键**：为每条项设置一个全局热键（如 `Ctrl+Alt+1`），按下后立刻执行对应动作。
- **全局开关热键**：一个独立的"监听开关"热键（如 `Ctrl+Alt+V`），暂停 / 恢复当前在界面中手动启用的快捷键功能与截图功能；界面手动关闭的功能不会被快捷键重新打开。
- **独立功能开关**：快捷键管理与截图贴图可分别在界面中启停；全局开关只临时暂停/恢复已手动启用的功能，不改变各自的持久化开关。
- 支持 **Ctrl / Alt / Shift / Win** 四种修饰键任意组合，UI 在录入时实时捕获并显示（如 `Ctrl+Shift+A`），自动过滤纯修饰键与非法组合。

### 2. 四种目标类型
| 类型 | 说明 | 执行方式 |
|------|------|----------|
| 程序 | `.exe` 等可执行文件 | ShellExecute 启动 |
| 文件夹 | 本地目录 | `explorer.exe` 打开 |
| 网页 | `http(s)://` 网址 | 默认浏览器打开 |
| 文件 | 文档等任意文件 | ShellExecute 打开 |

- **自动类型识别**：输入或拖入目标后，自动根据路径/URL 推断类型并切换下拉框。
- **快捷方式支持**：`.lnk`（WScript 解析真实目标）/ `.url`（读取 `URL=` 行）会被自动解开为真实目标。

### 3. 手势与快捷键贴图
- **手势贴图**：按住右键画近似矩形，松开后直接把包围区域变成置顶贴图。
- **框选贴图**：默认 `Ctrl+Alt+A`，也可在 Vector 的“贴图工具”窗口中修改。
- **截图后自动复制**：贴图设置中可开启，默认关闭；启用后框选与手势截图会同时复制到剪贴板。
- **剪贴板贴图**：默认 `Ctrl+Alt+S`，复制图片后按热键即可贴出。
- **贴图交互**：左键拖动、滚轮调透明度、右键/双击/Esc/Delete 关闭。
- **统一生命周期**：手势、贴图、快捷键、托盘和配置均在同一个 Vector 进程内，不依赖 Python 或外部 StickLite。

### 4. 交互与体验
- **系统托盘常驻**：最小化 / 关闭窗口默认缩到托盘（非真正退出），双击托盘图标恢复窗口；托盘菜单含"显示、暂停/恢复监听、切换深/浅色、以管理员模式启动、退出"。
- **监听状态指示**：窗口与托盘图标实时显示"监听中（紫）/ 已暂停（红）"，托盘图标颜色随状态切换。
- **导入 / 导出**：配置以 JSON 形式导入导出（`config.json`），方便备份与迁移。
- **撤销（Ctrl+Z）**：对新增 / 编辑 / 删除动作提供单步撤销，并提示本次撤销了什么。
- **类型排序**：点击"类型"列头可在"按原始顺序 / 按类型排序（程序→文件夹→网页→文件）"间切换。
- **深 / 浅色主题**：两套 XAML 主题资源，选择持久化到配置。

### 5. 健壮性与安全细节
- **注册失败提示**：热键被系统或其他程序占用时，统一弹窗列出失败项，不会静默吞掉。
- **系统保留键黑名单**：`Ctrl+C`、`F5`、`Alt+F4` 等系统/编辑器保留键会被拦截并提示不可用。
- **UIPI 提权（管理员模式）**：普通进程的热键无法穿透到高权限窗口（如全屏游戏）。可选"以管理员模式启动"，下次运行时自举 UAC 提权，使热键可作用于管理员程序。
- **脏数据清理**：粘贴/拖入的路径自动去除引号、换行、首尾空格与控制字符，保证配置干净且不会令 `File.Exists` 抛异常。
- **单实例**：通过命名 Mutex + EventWaitHandle 保证全局只有一个 Vector，重复启动时唤醒已有实例而非新开窗口。
- **脚本执行反馈**：`.bat/.cmd/.ps1/.vbs` 脚本以对应解释器运行，非 0 退出或产生 stderr 时通过托盘气泡通知用户。
- **快捷键诊断**：注册、热键分发和执行失败会静默写入 `Vector.log`；可在主界面临时开启详细诊断，记录消息到达、分发和目标启动阶段，默认关闭。
- **全局异常兜底**：UI 与域级未处理异常均写入 `Vector.log`，避免程序"凭空消失"。
- **低延迟手势链路**：低级鼠标钩子只入队，UI 每 33ms 批量消费并限制轨迹重绘频率；截图在后台线程完成，并限制排队数量。
- **资源清理**：退出时释放鼠标钩子、全局热键、GDI 对象、轨迹层和全部贴图窗口。

---

## 二、实现方式

### 1. 整体架构
- **WPF 主界面**：`MainWindow.xaml(.cs)` 负责 UI、列表展示、录入表单、托盘与热键回调。
- **App.xaml(.cs)**：应用入口，使用 **Generic Host + 依赖注入**（Microsoft.Extensions.Hosting / DependencyInjection）注册所有服务，并负责单实例锁、提权自举、全局异常捕获。
- **Services 层**：业务逻辑接口化；贴图由 `PinFeatureService` 编排，底层为 `MouseGestureService`、`GestureMatcher` 与 `ScreenCaptureService`。
- **Models 层**：`AppSettings` 同时包含快捷键、主题、提权偏好和 `PinSettings`。

### 2. 全局热键（核心）
- 通过 `RegisterHotKey` / `UnregisterHotKey`（P/Invoke `user32.dll`）向 Windows 注册。
- 本地、全局开关和贴图快捷键均使用动态分配的 Windows 热键 ID；逻辑映射按组合键统一做冲突检查。
- 窗口注册 `HwndSource.AddHook(WndProc)`，在 `WM_HOTKEY (0x0312)` 消息中根据 `wParam` 分派：
  - 全局开关 ID → 触发 `ToggleListening`（暂停/恢复手动启用的功能）；
  - 本地快捷键 ID → 触发 `ExecuteShortcut`，回调 `ExecutorService.Execute`；贴图快捷键 ID 分别触发框选截图或剪贴板贴图。
- **热键解析**：自实现 `ParseModifierKey` 把 `Ctrl+Shift+1` 拆成修饰符位掩码 + 虚拟键码；对 `=` `-` `[` 等符号键、数字键、`Win` 组合做了专门映射与黑名单校验，规避 `Enum.TryParse` 静默失败导致的"注册返回 false 但系统没占用"类难排查问题。

### 3. 配置存储
- 路径：`%AppData%/Vector/config.json`（由 `SettingsService` 管理）。
- 使用 `System.Text.Json` 序列化 `AppSettings`；`SaveAsync` 写盘、`LoadAsync` 读盘，失败均记日志并返回安全默认值。
- 运行日志写入同目录 `Vector.log`，写操作经 `SemaphoreSlim` 串行化，避免并发 `IOException`。

### 4. 执行引擎
- `ExecutorService.Execute` 在线程池 (`Task.Run`) 中执行，避免阻塞 UI。
- **命令行解析**：调用 Windows 原生 `CommandLineToArgvW` API 把目标串拆成 `FileName` + `Arguments`，正确处理带引号/空格路径，再智能恢复被空格拆错的含空格路径。
- **类型分支**：
  - 网页 → 直接交给浏览器；
  - 文件夹 → `explorer.exe` 显式打开（规避 ShellExecute 在部分中文路径静默失败）；
  - 脚本（`.bat/.cmd/.ps1/.vbs`）→ 指定解释器（`cmd/cscript/powershell`），隐藏窗口并捕获 stdout/stderr，非 0 退出即通知用户；
  - 其余 → `UseShellExecute` 启动。

### 5. 主题与外观
- `Themes/` 下 `Dark.xaml` / `Light.xaml` 定义画刷与颜色，`Styles.xaml` 定义控件样式。
- `ThemeService` 在 `Application.Resources.MergedDictionaries` 中动态插入/移除主题字典（保留 Styles），切换即时生效并持久化。

### 6. 撤销机制
- `UndoService` 用栈保存操作前快照（JSON 深拷贝 `ShortcutConfig` 列表，上限 50 步）。
- 撤销时整体替换 `_settings.LocalShortcuts` 实例、重注册所有热键并持久化；状态变化通过 `StateChanged` 事件刷新按钮可用态与 ToolTip。

### 7. 单实例与提权
- **单实例**：`Mutex("Local\Vector_SingleInstance")`。已存在实例时，新进程通过 `EventWaitHandle("Local\Vector_WakeupEvent")` 通知旧实例在 `Dispatcher` 中恢复窗口（避免直接 Win32 操作造成的"幽灵窗口"）。
- **提权自举**：若配置 `RunAsAdmin=true` 且当前未提权，启动时以 `runas` 重新拉起自身（带 `--elevated` 标记防递归），由用户 UAC 确认后接管。

---

## 三、目录结构

```
Vector/
├─ App.xaml(.cs)            应用入口 / DI / 单实例 / 提权 / 异常兜底
├─ MainWindow.xaml(.cs)     主窗口 UI 与逻辑
├─ Vector.csproj            项目文件（net8.0-windows, WPF+WinForms）
├─ Models/                  AppSettings / PinSettings / ShortcutConfig / UndoEntry
├─ Services/                热键 / 手势 / 截图 / 贴图编排 / 设置 / 执行 / 主题
├─ Windows/                 贴图设置 / 轨迹层 / 框选层 / 贴图窗口
├─ Themes/                  Dark.xaml / Light.xaml / Styles.xaml
├─ Properties/app.manifest  UAC / DPI 清单
└─ Resources/               托盘图标 (app.ico / app_paused.ico) 与预览图、make_icon.py
```

## 四、构建与运行

```powershell
# 还原依赖并编译（Debug / Release）
dotnet build -c Release

# 运行（需 Windows，且首次建议以管理员身份获得完整热键穿透能力）
dotnet run -c Release
```

> 依赖 nuget：`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Hosting`、`System.Drawing.Common`。
> 配置与日志位于：`%AppData%\Vector\`。

### 5. 回归测试

```powershell
# 全量回归（编译 + 契约断言 + 真实进程集成验证）
python tests\run_all.py

# 只验证某个已发布的绿色版单文件
python tests\run_all.py --exe release\Vector-2.0.1\Vector.exe

# 跳过编译，只复验现有产物（清理构建中间件之后用，不会重新生成 obj/）
python tests\run_all.py --no-build --exe release\Vector-2.0.1\Vector.exe
```

`tests/Vector.RegressionTests` 是独立的 C# 测试工程，直接驱动 `GestureOverlayWindow`
断言其「空闲停车 / 手势铺屏 / 坐标换算 / 清轨迹 / 系统状态恢复」契约——守卫
下面这条历史 bug 不复发：**铺满屏幕的常驻顶层透明窗会被 Windows 当成"有全屏程序在跑"
（`SHQueryUserNotificationState` 返回 `QUNS_BUSY`），从而让其它软件的"全屏时抑制自身"
逻辑误判，例如吞掉全局快捷键。**

> 注：`tests/` 已从主工程的默认源文件通配中排除（见 `Vector.csproj`），不会被编进 `Vector.exe`。

---

## MCP 集成（Model Context Protocol）

Vector 托盘主进程内置了可选的 MCP 命名管道服务端（管道名 `Vector.Mcp`），并通过 `Vector.exe --mcp-bridge` 提供零额外依赖的 stdio 原始字节桥接模式，供 Claude Desktop、Claude Code 等 MCP 客户端接入。

### 1. 客户端配置示例（stdio 桥）

先启动 Vector 主程序并在设置卡片中勾选 **MCP**；当主程序未运行或未开启 MCP 时，`--mcp-bridge` 会输出 JSON-RPC 错误并直接退出（不会自动拉起主程序）。

**Claude Desktop（`claude_desktop_config.json`）：**

```json
{
  "mcpServers": {
    "vector": {
      "command": "C:\\Path\\To\\Vector.exe",
      "args": ["--mcp-bridge"]
    }
  }
}
```

**Claude Code（`.mcp.json` 或用户级 MCP 配置）：**

```json
{
  "mcpServers": {
    "vector": {
      "type": "stdio",
      "command": "C:\\Path\\To\\Vector.exe",
      "args": ["--mcp-bridge"]
    }
  }
}
```

### 2. 工具清单与开关门控

| 工具名称 | 类别 | 所需开关 | 需传 `token` | 说明 |
| --- | --- | --- | --- | --- |
| `list_shortcuts` | 只读 | `McpEnabled` | 否 | 列出所有本地快捷键配置 |
| `get_status` | 只读 | `McpEnabled` | 否 | 获取监听状态与设置摘要 |
| `export_config` | 只读 | `McpEnabled` | 否 | 导出当前配置 JSON |
| `add_shortcut` | 写操作 | `McpEnabled` + `McpAllowWrites` | 是 | 新增本地快捷键 |
| `update_shortcut` | 写操作 | `McpEnabled` + `McpAllowWrites` | 是 | 按索引更新本地快捷键 |
| `delete_shortcut` | 写操作 | `McpEnabled` + `McpAllowWrites` | 是 | 按索引删除本地快捷键 |
| `set_listening` | 写操作 | `McpEnabled` + `McpAllowWrites` | 是 | 开启或暂停全局热键监听 |
| `import_config` | 写操作 | `McpEnabled` + `McpAllowWrites` | 是 | 导入并应用配置 JSON |
| `execute_shortcut` | 高危执行 | `McpEnabled` + `McpAllowExecute` | 是 | 按索引或热键直接执行目标 |

### 3. 安全说明

- **默认全关**：`McpEnabled`、`McpAllowWrites`、`McpAllowExecute` 默认均为 `false`，`McpToken` 默认为空字符串；默认状态下不监听管道，也不暴露任何写/执行工具。
- **仅当前用户 SID 可连**：命名管道 `Vector.Mcp` 通过 `PipeSecurity` 显式限制仅当前 Windows 用户 SID 可访问。
- **令牌双保险（`McpToken`）**：所有写工具与 `execute_shortcut` 每次调用都必须传入与设置中一致的 `token`；`McpToken` 为空时拒绝一切写/执行调用。可在设置卡片中点击「生成/重置令牌」生成高熵随机令牌并自动复制到剪贴板。
- **主线程调度与审计日志**：所有工具调用均经 WPF `Dispatcher.InvokeAsync` 回到主线程操作状态，调用记录与异常统一写入 `Vector.log`（不记录令牌明文）。

---

## 五、开源协议

本项目采用 [MIT License](LICENSE) 开源协议。

---

## 六、版本历史

- **v2.0.2** (2026-11-05)
  - 【多显示器跨屏手势轨迹完整显示】：放宽 Win32 `WM_GETMINMAXINFO` 限制，同步窗口与 Canvas 至虚拟桌面全域，修复多屏缝隙轨迹截断缺陷。
  - 【原位精准贴图】：框选或鼠标手势截图后，贴图窗口自动严丝合缝固定在截图原始矩形坐标。
  - 【高分屏像素级对齐】：优化 125%~250% 缩放下的亚像素漂移，剪贴板贴图自适应屏幕 DPI。
  - 【DWM 截屏残影与线程安全】：引入 `DwmFlush` 帧同步与单飞原子守卫，彻底消除截入轨迹残影。
- **v2.0.1** (2026-09-29)
  - 修复：手势遮罩窗空闲态未离屏，导致系统被误判为全屏/忙碌从而压制全局快捷键的问题。

