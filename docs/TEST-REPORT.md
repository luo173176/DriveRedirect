# DriveRedirect 测试报告

测试日期：2026-11-05  
测试机器：Windows 10 Home 22H2，build 19045，x64，150% DPI  
目标盘：D:（本地固定磁盘，测试时剩余约 260 GB）  
构建：.NET SDK 10.0.403，Release / win-x64 / self-contained

## 1. 构建与自动化测试

```text
dotnet build DriveRedirect.slnx -c Debug
已成功生成。0 个警告，0 个错误。

dotnet test tests\DriveRedirect.Tests\DriveRedirect.Tests.csproj -c Debug
已通过! 失败: 0，通过: 54，已跳过: 0，总计: 54。
```

### 单元/集成测试覆盖

| 类别 | 覆盖内容 |
|---|---|
| 路径映射 | Program Files、Program Files (x86)、ProgramData 的 C→D 层级保持 |
| 保护规则 | Program Files 根目录保护、系统目录穿越折叠 |
| 非 C 盘 | 源路径不在 C 盘时不重定向 |
| 用户路径 | 命令行已指定路径时不覆盖 |
| AppData | 全局开关关闭、无应用白名单、开关+白名单+映射三种分支 |
| 磁盘失败 | D 不存在、未就绪、不可写、非固定磁盘、空间不足 |
| 规则安全 | 目标不在 D 盘被拒绝；已存在非空源目录不迁移 |
| 安装器参数 | Inno `/DIR`、NSIS `/D`、MSI 目录属性、维护参数识别 |
| 名称识别 | Setup/Installer/Installation 后缀清理、卸载/更新程序过滤 |
| MSI 夹具 | 公共 INSTALLDIR 读取、私有 Directory 识别、MST 生成非空 |
| 数据库 | 默认规则/设置初始化、安装记录往返、设置与自定义规则持久化 |

## 2. 真实安装器端到端结果

所有结果来自安装后的 DriveRedirect 服务与启动器，不是模拟数据。测试安装包均为本项目生成的可控夹具。

| # | 安装器 | 启动参数 | 原始路径（推断） | 实际路径 | 状态 | 验证来源 | 兼容性 |
|---|---|---|---|---|---|---|---|
| 1 | WiX MSI，公共 `INSTALLDIR` | `/qn` | `C:\Program Files\AcmeExplicit` | `D:\Program Files\AcmeExplicit` | Redirected | filesystem | High |
| 2 | WiX MSI，私有 Directory + MST | `/qn` | `C:\Program Files\AcmePrivate` | `D:\Program Files\AcmePrivate` | Redirected | filesystem | Medium |
| 3 | Inno Setup 7，静默 | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | `C:\Program Files (x86)\Acme Inno` | `D:\Program Files (x86)\Acme Inno` | Redirected | filesystem | High |
| 4 | Inno Setup 7，交互默认安装 | 无参数，连续回车使用默认页面 | `C:\Program Files (x86)\Acme Inno` | `D:\Program Files (x86)\Acme Inno` | Redirected | filesystem | High |
| 5 | NSIS，静默 | `/S` | `C:\Program Files (x86)\AcmeNsis` | `D:\Program Files (x86)\AcmeNsis` | Redirected | filesystem | High |
| 6 | NSIS，经 EXE 文件关联启动 | `/S`（由 ShellExecute 传入） | `C:\Program Files (x86)\AcmeNsis` | `D:\Program Files (x86)\AcmeNsis` | Redirected | filesystem | High |

第 6 项专门验证“双击链路”：在提权进程中用 ShellExecute 启动 `AcmeNsisSetup.exe`，注册表 `exefile` 关联把调用转给 `DriveRedirect.Launcher --exe "<安装包>" /S`；启动器日志显示 `计划：redirect=True mode=Automatic target=D:\Program Files (x86)\AcmeNsis`，随后 NSIS 实际安装到 D 盘。数据库记录为 `status=redirected`、`verificationSource=filesystem`、`launchArguments=/S /D=D:\Program Files (x86)\AcmeNsis`。

### 核心验收：交互式 Inno 默认安装

这是最接近用户真实操作的场景：

1. 通过已安装的 `C:\Program Files\DriveRedirect\DriveRedirect.Launcher.exe --exe <夹具>` 启动安装器。
2. DriveRedirect 识别为 Inno Setup，自动追加 `/DIR="D:\Program Files (x86)\Acme Inno"`，没有加静默参数。
3. 安装向导「Select Destination Location」显示默认目录 `D:\Program Files (x86)\Acme Inno`（截图 `screenshots/08-inno-default-path-crop.png`）。
4. 用户不修改路径，连续按回车通过「Select Additional Tasks」「Ready to Install」。
5. 「Ready to Install」页显示 Destination location 为 `D:\Program Files (x86)\Acme Inno`。
6. 点击安装后，文件实际落盘到 `D:\Program Files (x86)\Acme Inno\AcmeInno.exe`。
7. 安装器退出码 0；服务记录 `status=Redirected`、`verificationSource=filesystem`、`actualPath=D:\Program Files (x86)\Acme Inno`。
8. 安装后的 `AcmeInno.exe` 可正常启动。

## 3. 安装包安装/卸载验证

安装包：`installer/DriveRedirect.msi`（约 53.3 MB，SHA-256 见下）

### 安装

```powershell
msiexec /i installer\DriveRedirect.msi /qn /L*v artifacts\install2.log
```

结果：退出码 0。

- `C:\Program Files\DriveRedirect` 下 306 个文件。
- `zh-Hans` WPF 卫星资源目录及 12 个资源文件按原目录结构安装（未扁平化）。
- `DriveRedirect Service` 已创建为 `AUTO_START`、`LocalSystem`、`WIN32_OWN_PROCESS`，并自动处于 RUNNING。
- 服务路径：`"C:\Program Files\DriveRedirect\DriveRedirect.Service.exe"`。
- 开始菜单和公共桌面快捷方式均指向 `C:\Program Files\DriveRedirect\DriveRedirect.Desktop.exe`。
- EXE/MSI 双击关联指向 `C:\Program Files\DriveRedirect\DriveRedirect.Launcher.exe`。
- 提权 CLI `ping` 返回 `monitoring=true, ipc=true`；`dashboard` 返回 C/D 盘容量、服务状态和安装历史。
- 桌面程序可启动并正常渲染六个页面，浅色/深色主题均正常。

### 卸载

```powershell
msiexec /x installer\DriveRedirect.msi /qn /L*v artifacts\uninstall.log
```

结果：退出码 0。

- `C:\Program Files\DriveRedirect` 已删除。
- 开始菜单和公共桌面快捷方式已删除。
- `DriveRedirect Service` 已停止并删除。
- `exefile` 与 `Msi.Package` 关联恢复为系统默认命令。
- `C:\ProgramData\DriveRedirect` 数据目录按设计保留。

随后使用修复后的安装包重新安装，再次确认服务自动启动和关联切换正常。

最终打包前又发现并修复了一个打包问题：旧 `generate-files.ps1` 会把 `zh-Hans` 卫星资源目录扁平化到安装根目录。已改为生成 WiX `<Directory>` 树，最终包安装后 `C:\Program Files\DriveRedirect\zh-Hans` 下存在 12 个资源文件；并用最终包完成了一次卸载→重装循环（卸载退出码 0，安装退出码 0，服务自动运行）。

## 4. 桌面 UI 验证

使用 UI Automation 切换页面并截图，截图位于 `docs/screenshots/`：

| 文件 | 页面 |
|---|---|
| `01-dashboard.png` | 首页（浅色） |
| `02-live.png` | 实时监控 |
| `03-history.png` | 安装记录 |
| `04-rules.png` | 路径规则（开关完整显示） |
| `05-compatibility.png` | 兼容性中心 |
| `06-settings.png` | 设置 |
| `07-dashboard-dark.png` | 首页（深色） |

已修复的实机问题：

- ProgressBar 对只读属性使用默认 TwoWay 绑定导致启动崩溃 → 改为 `OneWay`。
- 150% DPI 下窗口超出屏幕 → 改为 1120×680 并按显示器工作区/DPI 自动缩放居中。
- ComboBox 显示类型名 → 为选项模型实现 `ToString()`。
- 安装记录搜索框缺少占位提示 → 增加叠加提示。
- 路径规则启用列被裁切 → 调整列宽并移除固定尺寸。

## 5. 后台监控噪声验证

加入系统路径跳过规则后重启服务，测试期间仅记录真实测试安装器（Acme Inno/NSIS/MSI），未再新增 TrustedInstaller、WindowsApps、winget 等系统进程的安装记录。早期数据库中的系统进程记录是规则加入前的测试噪声，交付前已清理。

## 6. 未实测/未完全覆盖的场景

以下项目没有足够证据，不能声称通过：

| 场景 | 状态 | 说明 |
|---|---|---|
| 真实 InstallShield 安装包 | 未实测 | 仅有 Basic MSI 适配代码；无商业样本，默认 Medium，可能走辅助/放行 |
| 真实厂商 MSIX 重定向 | 按设计不支持 | 由 Windows 部署子系统管理，安全放行 |
| 未知自定义 EXE 自动重定向 | 按设计不支持 | 无验证配置时禁止注入参数 |
| 软件在线更新/升级 | 未做真实厂商更新器测试 | 维护调用会放行；厂商更新器是否保留 D 盘位置取决于厂商实现 |
| 安装中途取消 | 代码按退出码 1602/1223 标记 Cancelled，未单独实机脚本化验证 | 取消流程依赖安装器退出码 |
| D 盘物理移除/掉线 | 仅单元测试覆盖不存在/未就绪/不可写/空间不足 | 未在实机热拔盘验证 |
| 普通用户无 UAC 场景 | 部分覆盖 | 桌面端固定要求管理员；安装器自身 UAC 不被绕过 |
| Windows 11 | 未实机测试 | UI 和 API 按 Windows 10/11 兼容实现 |
| ARM64 | 未支持 | 仅发布 win-x64 |
| 多用户并发安装 | 未测试 | IPC 和数据库按单机串行场景设计 |

## 7. 产物校验

```text
installer\DriveRedirect.msi
SHA-256: CD82E4A22102B233D14FB1DAC3504D47DC7799ED1A33B13CD506BB4F351C5E1B
大小: 53,302,667 bytes
```

测试夹具 SHA-256 前缀：

| 夹具 | SHA-256 |
|---|---|
| AcmeExplicit.msi | `8824D4D34B0373C91D915164157CE354F2D0A76AC5DB84B25FA01CD0C9094319` |
| AcmePrivate.msi | `F15AF5B770F8AADD17F83E78BA3E3468FD79A040290E4D38ECCAB7731535B09A` |
| AcmeInnoSetup.exe | `3AF799BB36D32FFA89A83B894D1D3ADDAE309319749D53291415A18EF9ACCCD0` |
| AcmeNsisSetup.exe | `85BF19B81330F564805047F4F6556CFB030D03D0598C843D97F3B9AC57536FF7` |

